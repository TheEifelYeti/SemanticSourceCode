using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SemanticSourceCode.Models;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Tests for Issue #5: Watch mode database support.
/// </summary>
public class WatchModeDatabaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteVssDatabase _db;

    public WatchModeDatabaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ssc-watch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath
            })
            .Build();
        _db = new SqliteVssDatabase(config, Mock.Of<ILogger<SqliteVssDatabase>>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task DeleteChunksByFilePathAsync_RemovesAllChunksForFile()
    {
        await _db.InitializeAsync();

        // Insert 3 chunks for Foo.cs and 2 for Bar.cs
        await _db.InsertChunkAsync(MakeChunk("/src/Foo.cs", "Foo", "Method"));
        await _db.InsertChunkAsync(MakeChunk("/src/Foo.cs", "Bar", "Method"));
        await _db.InsertChunkAsync(MakeChunk("/src/Foo.cs", "Baz", "Property"));
        await _db.InsertChunkAsync(MakeChunk("/src/Bar.cs", "Qux", "Method"));
        await _db.InsertChunkAsync(MakeChunk("/src/Bar.cs", "Zap", "Method"));

        var all = await _db.GetAllChunksAsync();
        Assert.Equal(5, all.Count);

        // Act: remove Foo.cs chunks
        var removed = await _db.DeleteChunksByFilePathAsync("/src/Foo.cs");

        // Assert
        Assert.Equal(3, removed);
        var remaining = await _db.GetAllChunksAsync();
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, c => Assert.Equal("/src/Bar.cs", c.FilePath));
    }

    [Fact]
    public async Task DeleteChunksByFilePathAsync_NoChunksForFile_ReturnsZero()
    {
        await _db.InitializeAsync();
        await _db.InsertChunkAsync(MakeChunk("/src/Foo.cs", "Foo", "Method"));

        var removed = await _db.DeleteChunksByFilePathAsync("/src/Nonexistent.cs");

        Assert.Equal(0, removed);
        var remaining = await _db.GetAllChunksAsync();
        Assert.Single(remaining);
    }

    [Fact]
    public async Task DeleteChunksByFilePathAsync_EmptyPath_ReturnsZero()
    {
        await _db.InitializeAsync();
        await _db.InsertChunkAsync(MakeChunk("/src/Foo.cs", "Foo", "Method"));

        Assert.Equal(0, await _db.DeleteChunksByFilePathAsync(""));
        Assert.Equal(0, await _db.DeleteChunksByFilePathAsync("   "));
        Assert.Equal(0, await _db.DeleteChunksByFilePathAsync(null!));
    }

    [Fact]
    public async Task DeleteChunksByFilePathAsync_RemovesContentHash()
    {
        await _db.InitializeAsync();

        var chunk = MakeChunk("/src/Foo.cs", "Foo", "Method");
        chunk.ContentHash = "abc123";
        await _db.InsertChunkAsync(chunk);

        // Hash should be in the map
        var hashesBefore = await _db.GetAllContentHashesAsync();
        Assert.Contains("abc123", hashesBefore.Values);

        await _db.DeleteChunksByFilePathAsync("/src/Foo.cs");

        // Hash should be gone
        var hashesAfter = await _db.GetAllContentHashesAsync();
        Assert.DoesNotContain("abc123", hashesAfter.Values);
    }

    [Fact]
    public async Task DeleteChunksByFilePathAsync_CaseSensitive()
    {
        await _db.InitializeAsync();

        // SQLite is case-sensitive by default for TEXT comparisons
        await _db.InsertChunkAsync(MakeChunk("/src/Foo.cs", "Foo", "Method"));

        var removed = await _db.DeleteChunksByFilePathAsync("/src/foo.cs");

        Assert.Equal(0, removed);
        var remaining = await _db.GetAllChunksAsync();
        Assert.Single(remaining);
    }

    private static CodeChunk MakeChunk(string filePath, string memberName, string memberType)
    {
        return new CodeChunk
        {
            Id = $"{filePath}|{memberName}|{Guid.NewGuid():N}",
            FilePath = filePath,
            MemberName = memberName,
            MemberType = memberType,
            ClassName = "TestClass",
            NamespaceName = "TestNs",
            Content = "test content",
            Signature = $"public void {memberName}()",
            ChunkIndex = 0,
            ContentHash = Guid.NewGuid().ToString("N")
        };
    }
}
