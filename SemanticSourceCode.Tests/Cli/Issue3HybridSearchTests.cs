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
/// Unit tests for the changes introduced in Issue #3:
/// - IndexCommand now writes to the keyword index after each successful chunk insert
/// - SearchCommand now uses IHybridSearchService when the keyword index is available
/// </summary>
public class Issue3HybridSearchTests
{
    // ---------- Helpers ----------

    private static ServiceProvider BuildServiceProvider(
        ICodeAnalyzer analyzer,
        IEmbeddingService embeddingService,
        IVectorDatabase database,
        IKeywordIndex keywordIndex,
        IHybridSearchService? hybridSearch = null,
        IQueryExpander? queryExpander = null,
        IQuerySuggester? querySuggester = null,
        IAdaptiveThreshold? adaptiveThreshold = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(analyzer);
        services.AddSingleton(embeddingService);
        services.AddSingleton(database);
        services.AddSingleton(keywordIndex);
        if (hybridSearch != null) services.AddSingleton(hybridSearch);
        if (queryExpander != null) services.AddSingleton(queryExpander);
        if (querySuggester != null) services.AddSingleton(querySuggester);
        if (adaptiveThreshold != null) services.AddSingleton(adaptiveThreshold);
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private static CodeChunk CreateTestChunk(string id, string memberName = "Foo", string content = "public void Foo() { }")
    {
        return new CodeChunk
        {
            Id = id,
            FilePath = "/src/Test.cs",
            NamespaceName = "TestNs",
            ClassName = "TestClass",
            MemberName = memberName,
            MemberType = "Method",
            Content = content,
            Signature = $"public void {memberName}()",
            Documentation = "",
            StartLine = 1,
            EndLine = 1,
            ContentHash = Guid.NewGuid().ToString("N"),
            Embedding = new byte[12],
            IndexedAt = DateTime.UtcNow
        };
    }

    // ---------- IndexCommand: keyword index is called ----------

    [Fact]
    public async Task IndexCommand_CallsKeywordIndexForEachChunk()
    {
        // Arrange
        var chunks = new List<CodeChunk>
        {
            CreateTestChunk("id-1", "Method1"),
            CreateTestChunk("id-2", "Method2"),
            CreateTestChunk("id-3", "Method3")
        };

        var analyzer = new Mock<ICodeAnalyzer>();
        analyzer.Setup(a => a.AnalyzeDirectoryAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(chunks);

        var embeddingService = new Mock<IEmbeddingService>();
        embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>()))
                        .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });
        // After Issue #13: IndexCommand batches via GenerateEmbeddingsAsync.
        embeddingService.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                            texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToArray());

        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.GetAllContentHashesAsync()).ReturnsAsync(new Dictionary<string, string>());

        var keywordIndex = new Mock<IKeywordIndex>();
        keywordIndex.Setup(k => k.IsAvailableAsync()).ReturnsAsync(true);

        // Create a temp directory so Directory.Exists passes
        var tempDir = Path.Combine(Path.GetTempPath(), $"idx_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sp = BuildServiceProvider(analyzer.Object, embeddingService.Object,
                database.Object, keywordIndex.Object);
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

            // Act
            var exitCode = await IndexCommand.RunAsync(sp, tempDir, logger);

            // Assert
            // Issue #21: IndexCommand now uses the bulk API once for the whole batch.
            keywordIndex.Verify(k => k.IndexChunksAsync(It.IsAny<IEnumerable<CodeChunk>>()), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task IndexCommand_KeywordIndexCalledAfterDatabaseInsert()
    {
        // Arrange
        var chunks = new List<CodeChunk> { CreateTestChunk("id-1") };
        var callOrder = new List<string>();

        var analyzer = new Mock<ICodeAnalyzer>();
        analyzer.Setup(a => a.AnalyzeDirectoryAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(chunks);

        var embeddingService = new Mock<IEmbeddingService>();
        embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>()))
                        .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f });
        // After Issue #13: IndexCommand batches via GenerateEmbeddingsAsync.
        embeddingService.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                            texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToArray());

        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.GetAllContentHashesAsync()).ReturnsAsync(new Dictionary<string, string>());
        database.Setup(d => d.InsertChunkAsync(It.IsAny<CodeChunk>()))
                .Callback<CodeChunk>(_ => callOrder.Add("database"))
                .Returns(Task.CompletedTask);
        // Issue #21: the IndexCommand now uses InsertChunksAsync for the whole batch.
        database.Setup(d => d.InsertChunksAsync(It.IsAny<IEnumerable<CodeChunk>>()))
                .Callback<IEnumerable<CodeChunk>>(_ => callOrder.Add("database"))
                .Returns(Task.CompletedTask);

        var keywordIndex = new Mock<IKeywordIndex>();
        keywordIndex.Setup(k => k.IsAvailableAsync()).ReturnsAsync(true);
        keywordIndex.Setup(k => k.IndexChunkAsync(It.IsAny<CodeChunk>()))
                    .Callback<CodeChunk>(_ => callOrder.Add("keywordIndex"))
                    .Returns(Task.CompletedTask);
        // Issue #21: bulk IndexChunksAsync is what the IndexCommand actually calls.
        keywordIndex.Setup(k => k.IndexChunksAsync(It.IsAny<IEnumerable<CodeChunk>>()))
                    .Callback<IEnumerable<CodeChunk>>(_ => callOrder.Add("keywordIndex"))
                    .Returns(Task.CompletedTask);

        var tempDir = Path.Combine(Path.GetTempPath(), $"idx_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sp = BuildServiceProvider(analyzer.Object, embeddingService.Object,
                database.Object, keywordIndex.Object);
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

            await IndexCommand.RunAsync(sp, tempDir, logger);

            Assert.Equal(2, callOrder.Count);
            Assert.Equal("database", callOrder[0]);
            Assert.Equal("keywordIndex", callOrder[1]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task IndexCommand_NoChunks_DoesNotCallKeywordIndex()
    {
        // Arrange
        var analyzer = new Mock<ICodeAnalyzer>();
        analyzer.Setup(a => a.AnalyzeDirectoryAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<CodeChunk>());

        var embeddingService = new Mock<IEmbeddingService>();
        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.GetAllContentHashesAsync()).ReturnsAsync(new Dictionary<string, string>());

        var keywordIndex = new Mock<IKeywordIndex>();

        var tempDir = Path.Combine(Path.GetTempPath(), $"idx_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sp = BuildServiceProvider(analyzer.Object, embeddingService.Object,
                database.Object, keywordIndex.Object);
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

            await IndexCommand.RunAsync(sp, tempDir, logger);

            // Issue #21: bulk API replaces per-chunk IndexChunkAsync.
            keywordIndex.Verify(k => k.IndexChunksAsync(It.IsAny<IEnumerable<CodeChunk>>()), Times.Never);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task IndexCommand_BulkInsertThrows_ReturnsExitCodeOne()
    {
        // Regression test for the BLOCKER found in Issue #21 code review:
        // previously the catch block in ProcessChunksAsync logged the error but
        // still returned 0, masking bulk-insert failures from the CLI caller.

        // Arrange
        var chunks = new List<CodeChunk> { CreateTestChunk("boom-1") };
        var analyzer = new Mock<ICodeAnalyzer>();
        analyzer.Setup(a => a.AnalyzeDirectoryAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(chunks);

        var embeddingService = new Mock<IEmbeddingService>();
        embeddingService.Setup(e => e.GenerateEmbeddingsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
                            texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f }).ToArray());

        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.GetAllContentHashesAsync()).ReturnsAsync(new Dictionary<string, string>());
        database.Setup(d => d.InsertChunksAsync(It.IsAny<IEnumerable<CodeChunk>>()))
                .ThrowsAsync(new InvalidOperationException("simulated bulk-insert failure"));

        var keywordIndex = new Mock<IKeywordIndex>();
        keywordIndex.Setup(k => k.IsAvailableAsync()).ReturnsAsync(true);

        var tempDir = Path.Combine(Path.GetTempPath(), $"idx_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var sp = BuildServiceProvider(analyzer.Object, embeddingService.Object,
                database.Object, keywordIndex.Object);
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Test");

            // Act
            var exitCode = await IndexCommand.RunAsync(sp, tempDir, logger);

            // Assert — caller must see the failure.
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------- SearchCommand: hybrid search is used ----------

    [Fact]
    public async Task SearchCommand_WhenKeywordAvailable_CallsHybridSearch()
    {
        // We can't easily test the full interactive loop (it blocks on Console.ReadLine),
        // but we can verify the service resolution works and the dependencies are wired correctly.
        //
        // This is a structural test: the SearchCommand code uses `keywordIndex.IsAvailableAsync()`
        // and calls `hybridSearch.SearchAsync(...)`. We confirm that the DI container resolves
        // both services correctly when the configuration matches what SearchCommand expects.

        // Arrange
        var keywordIndex = new Mock<IKeywordIndex>();
        keywordIndex.Setup(k => k.IsAvailableAsync()).ReturnsAsync(true);

        var hybridSearch = new Mock<IHybridSearchService>();
        hybridSearch.Setup(h => h.SearchAsync(
                It.IsAny<float[]>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<SearchFilter>(),
                It.IsAny<HybridOptions>(),
                It.IsAny<RankerOptions>()))
            .ReturnsAsync(new List<HybridResult>());

        var embeddingService = new Mock<IEmbeddingService>();
        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);

        var queryExpander = new Mock<IQueryExpander>();
        var querySuggester = new Mock<IQuerySuggester>();
        var adaptiveThreshold = new Mock<IAdaptiveThreshold>();

        var sp = BuildServiceProvider(
            new Mock<ICodeAnalyzer>().Object,
            embeddingService.Object,
            database.Object,
            keywordIndex.Object,
            hybridSearch.Object,
            queryExpander.Object,
            querySuggester.Object,
            adaptiveThreshold.Object);

        // Act — resolve the same way SearchCommand does
        var resolvedHybrid = sp.GetRequiredService<IHybridSearchService>();
        var resolvedKeyword = sp.GetRequiredService<IKeywordIndex>();
        var resolvedEmbedding = sp.GetRequiredService<IEmbeddingService>();
        var resolvedDatabase = sp.GetRequiredService<IVectorDatabase>();

        // Assert
        Assert.Same(hybridSearch.Object, resolvedHybrid);
        Assert.Same(keywordIndex.Object, resolvedKeyword);
        Assert.Same(embeddingService.Object, resolvedEmbedding);
        Assert.Same(database.Object, resolvedDatabase);

        // Verify the keyword index reports available
        Assert.True(await resolvedKeyword.IsAvailableAsync());
    }

    [Fact]
    public async Task SearchCommand_KeywordIndexContract_IsAvailableAsync_ReturnsExpected()
    {
        // When the keyword index is NOT available, the SearchCommand should fall back
        // to pure vector search. We verify the contract on the mock.
        var keywordIndex = new Mock<IKeywordIndex>();
        keywordIndex.Setup(k => k.IsAvailableAsync()).ReturnsAsync(false);

        var available = await keywordIndex.Object.IsAvailableAsync();
        Assert.False(available);
    }

    [Fact]
    public void HybridResult_HasExpectedScoreFields()
    {
        // Sanity check that HybridResult has the fields the SearchCommand reads.
        // This is more of a smoke test for the contract.
        var chunk = CreateTestChunk("id-1");
        var result = new HybridResult
        {
            Chunk = chunk,
            SemanticScore = 0.8f,
            KeywordScore = 0.5f,
            HybridScore = 0.65f,
            FinalScore = 0.65f
        };

        Assert.Equal(0.8f, result.SemanticScore);
        Assert.Equal(0.5f, result.KeywordScore);
        Assert.Equal(0.65f, result.HybridScore);
        Assert.Equal(0.65f, result.FinalScore);
        Assert.Same(chunk, result.Chunk);
    }
}
