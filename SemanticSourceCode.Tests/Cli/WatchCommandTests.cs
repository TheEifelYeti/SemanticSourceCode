using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SemanticSourceCode.Cli;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Cli;

/// <summary>
/// Integration-style tests for Issue #5: Watch command end-to-end behaviour.
///
/// These tests do NOT drive the real FileSystemWatcher event loop end-to-end (the loop
/// runs until cancellation, which is hard to assert on synchronously in xUnit). Instead
/// they exercise the smaller, deterministic pieces of the watch pipeline:
///   - ProcessChunksAsync (the shared embed+persist loop)
///   - File-deleted handling (chunks removed via DeleteChunksByFilePathAsync)
///   - The full WatchCommand path with the file system: a chunk is created, the index
///     picks it up, the file is removed, the chunk is gone again.
/// </summary>
public class WatchCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteVssDatabase _db;
    private readonly Mock<IEmbeddingService> _embedding;
    private readonly Mock<IKeywordIndex> _keywordIndex;
    private readonly ServiceProvider _services;

    public WatchCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ssc-watch-cmd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "watch.db");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath
            })
            .Build();

        _db = new SqliteVssDatabase(config, Mock.Of<ILogger<SqliteVssDatabase>>());

        // Deterministic fake embedding: 4-dim vector where every value is 1.0
        _embedding = new Mock<IEmbeddingService>();
        _embedding
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1.0f, 1.0f, 1.0f, 1.0f });

        _keywordIndex = new Mock<IKeywordIndex>();
        _keywordIndex
            .Setup(k => k.IndexChunkAsync(It.IsAny<CodeChunk>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IVectorDatabase>(_db);
        services.AddSingleton<IEmbeddingService>(_embedding.Object);
        services.AddSingleton<IKeywordIndex>(_keywordIndex.Object);
        services.AddLogging();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ProcessChunksAsync_WithNoExistingHashes_EmbedsAllChunks()
    {
        await _db.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            MakeChunk("/src/A.cs", "Foo"),
            MakeChunk("/src/A.cs", "Bar"),
            MakeChunk("/src/B.cs", "Baz")
        };
        var logger = new Mock<ILogger>().Object;

        var result = await IndexCommand.ProcessChunksAsync(
            chunks, new Dictionary<string, string>(),
            _embedding.Object, _db, _keywordIndex.Object, logger);

        Assert.Equal(0, result);
        var all = await _db.GetAllChunksAsync();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task ProcessChunksAsync_WithMatchingHashes_SkipsEmbeddingCalls()
    {
        await _db.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            MakeChunk("/src/A.cs", "Foo"),
            MakeChunk("/src/A.cs", "Bar")
        };
        // Pre-populate existing hashes that match
        var existing = chunks.ToDictionary(c => c.Id, c => c.ContentHash);

        var logger = new Mock<ILogger>().Object;

        var result = await IndexCommand.ProcessChunksAsync(
            chunks, existing, _embedding.Object, _db, _keywordIndex.Object, logger);

        Assert.Equal(0, result);
        _embedding.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessChunksAsync_WithOneChangedChunk_EmbedsOnlyThat()
    {
        await _db.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            MakeChunk("/src/A.cs", "Foo"),
            MakeChunk("/src/A.cs", "Bar")
        };
        // Only the first chunk matches its existing hash
        var existing = new Dictionary<string, string>
        {
            [chunks[0].Id] = chunks[0].ContentHash,
            [chunks[1].Id] = "stale-hash"
        };

        var logger = new Mock<ILogger>().Object;

        var result = await IndexCommand.ProcessChunksAsync(
            chunks, existing, _embedding.Object, _db, _keywordIndex.Object, logger);

        Assert.Equal(0, result);
        _embedding.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteChunksByFilePath_AfterIndex_RemovesChunksForThatFileOnly()
    {
        await _db.InitializeAsync();
        // Seed two files
        var foo = MakeChunk("/src/Foo.cs", "Foo");
        var bar = MakeChunk("/src/Bar.cs", "Bar");
        foo.Embedding = ToBytes(new float[] { 1, 1, 1, 1 });
        bar.Embedding = ToBytes(new float[] { 1, 1, 1, 1 });
        await _db.InsertChunkAsync(foo);
        await _db.InsertChunkAsync(bar);
        await _keywordIndex.Object.IndexChunkAsync(foo);
        await _keywordIndex.Object.IndexChunkAsync(bar);

        Assert.Equal(2, (await _db.GetAllChunksAsync()).Count);

        // Simulate the watch Deleted handler
        var removed = await _db.DeleteChunksByFilePathAsync("/src/Foo.cs");

        Assert.Equal(1, removed);
        var remaining = await _db.GetAllChunksAsync();
        Assert.Single(remaining);
        Assert.Equal("/src/Bar.cs", remaining[0].FilePath);
    }

    [Fact]
    public async Task WatchCommand_InitialIndex_ThenFileDelete_LeavesEmptyIndex()
    {
        // The first call to WatchCommand.RunAsync will:
        //   1. Build a real CodeAnalyzer
        //   2. Run the full directory index
        //   3. Watch for changes (we cancel immediately via the token)
        // We can't easily short-circuit the watch loop, so we exercise only the
        // pieces manually here: initial index + delete cleanup.

        // Create one .cs file in the temp dir
        var sourceFile = Path.Combine(_tempDir, "Hello.cs");
        await File.WriteAllTextAsync(sourceFile, "namespace Test { public class Hello { public void Greet() { } } }");

        // Build a real analyzer pointing at the temp dir
        var analyzer = new CodeAnalyzer(Mock.Of<ILogger<CodeAnalyzer>>());
        var chunks = await analyzer.AnalyzeDirectoryAsync(_tempDir);
        Assert.NotEmpty(chunks);

        // Simulate the initial index run
        await _db.InitializeAsync();
        var logger = new Mock<ILogger>().Object;
        await IndexCommand.ProcessChunksAsync(
            chunks, new Dictionary<string, string>(),
            _embedding.Object, _db, _keywordIndex.Object, logger);

        var all = await _db.GetAllChunksAsync();
        Assert.NotEmpty(all);

        // Simulate the file being deleted (the watcher Deleted handler)
        var filePath = chunks[0].FilePath;
        File.Delete(filePath);
        var removed = await _db.DeleteChunksByFilePathAsync(filePath);

        Assert.NotEqual(0, removed);
        Assert.Empty(await _db.GetAllChunksAsync());
    }

    [Fact]
    public async Task WatchCommand_RunAsync_NonexistentDirectory_ReturnsOne()
    {
        var logger = new Mock<ILogger>().Object;
        var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel immediately so we don't actually wait

        var result = await WatchCommand.RunAsync(_services, "/nonexistent/path/12345", logger, cts.Token);

        Assert.Equal(1, result);
    }

    private static CodeChunk MakeChunk(string filePath, string memberName)
    {
        var id = $"{filePath}|TestClass|{memberName}|Method|0|{Guid.NewGuid():N}";
        return new CodeChunk
        {
            Id = id,
            FilePath = filePath,
            MemberName = memberName,
            MemberType = "Method",
            ClassName = "TestClass",
            NamespaceName = "TestNs",
            Content = $"public void {memberName}() {{ }}",
            Signature = $"public void {memberName}()",
            ChunkIndex = 0,
            ContentHash = Guid.NewGuid().ToString("N")
        };
    }

    private static byte[] ToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
