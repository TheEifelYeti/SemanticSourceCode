using System.Text.Json;
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
/// Tests for Issue #7: Non-interactive search mode.
/// Covers the new --query / --format / --limit / --quiet flags and the
/// extracted ExecuteSearchAsync / PrintResults helpers.
/// </summary>
public class SearchCommandNonInteractiveTests
{
    // ---------- Helpers ----------

    private static (ServiceProvider sp, IConfiguration config) BuildServiceProvider(
        IEmbeddingService embeddingService,
        IVectorDatabase database,
        IHybridSearchService? hybridSearch = null,
        IKeywordIndex? keywordIndex = null,
        IQueryExpander? queryExpander = null,
        IQuerySuggester? querySuggester = null,
        IAdaptiveThreshold? adaptiveThreshold = null,
        ICodeAnalyzer? analyzer = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Search:MinimumSimilarity"] = "0.50",
                ["Search:TopK"] = "5",
                ["Search:DisplayCount"] = "3",
                ["Search:WeakMatchThreshold"] = "0.30"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(embeddingService);
        services.AddSingleton(database);
        if (hybridSearch != null) services.AddSingleton(hybridSearch);
        if (keywordIndex != null) services.AddSingleton(keywordIndex);
        if (queryExpander != null) services.AddSingleton(queryExpander);
        if (querySuggester != null) services.AddSingleton(querySuggester);
        if (adaptiveThreshold != null) services.AddSingleton(adaptiveThreshold);
        if (analyzer != null) services.AddSingleton(analyzer);
        services.AddLogging();
        return (services.BuildServiceProvider(), config);
    }

    private static CodeChunk CreateChunk(string memberName, float similarity)
    {
        return new CodeChunk
        {
            Id = $"/src/Test.cs|Test|{memberName}|Method|0|{Guid.NewGuid():N}",
            FilePath = "/src/Test.cs",
            MemberName = memberName,
            MemberType = "Method",
            ClassName = "Test",
            NamespaceName = "TestNs",
            Signature = $"public void {memberName}()",
            Content = $"public void {memberName}() {{ /* {memberName} */ }}",
            StartLine = 1,
            EndLine = 5,
            Embedding = new byte[16]
        }.WithFinalScore(similarity);
    }

    private static SearchOptions MakeOptions() => new()
    {
        MinimumSimilarity = 0.5f,
        TopK = 5,
        DisplayCount = 3,
        WeakMatchThreshold = 0.3f
    };

    // ---------- ExecuteSearchAsync tests ----------

    [Fact]
    public async Task ExecuteSearchAsync_EmbedsQueryAndReturnsResults()
    {
        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1f, 0f, 0f, 0f });

        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.SearchSimilarWithScoresAsync(It.IsAny<float[]>(), It.IsAny<SearchFilter>(), It.IsAny<int>()))
            .ReturnsAsync(new List<(CodeChunk, float)>
            {
                (CreateChunk("Alpha", 0.9f), 0.9f),
                (CreateChunk("Beta", 0.7f), 0.7f),
                (CreateChunk("Gamma", 0.4f), 0.4f)  // below 0.5 threshold
            });

        var hybrid = new Mock<IHybridSearchService>();
        hybrid.Setup(h => h.SearchAsync(It.IsAny<float[]>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchFilter>()))
            .ReturnsAsync(new List<HybridResult>
            {
                new() { Chunk = CreateChunk("Alpha", 0.9f), HybridScore = 0.9f, SemanticScore = 0.9f, FinalScore = 0.9f },
                new() { Chunk = CreateChunk("Beta", 0.7f), HybridScore = 0.7f, SemanticScore = 0.7f, FinalScore = 0.7f },
                new() { Chunk = CreateChunk("Gamma", 0.4f), HybridScore = 0.4f, SemanticScore = 0.4f, FinalScore = 0.4f }
            });

        var keyword = new Mock<IKeywordIndex>();
        keyword.Setup(k => k.IsAvailableAsync()).ReturnsAsync(true);

        var expander = new Mock<IQueryExpander>();
        expander.Setup(q => q.Expand(It.IsAny<string>())).Returns<string>(s => s);

        var suggester = new Mock<IQuerySuggester>();
        var threshold = new Mock<IAdaptiveThreshold>();
        threshold.Setup(t => t.Compute(It.IsAny<List<float>>(), It.IsAny<AdaptiveThresholdOptions>(), It.IsAny<string>()))
            .Returns(0.5f);

        var (sp, _) = BuildServiceProvider(embeddingMock.Object, database.Object, hybrid.Object,
            keyword.Object, expander.Object, suggester.Object, threshold.Object);

        var result = await SearchCommand.ExecuteSearchAsync(
            "test query", sp, expander.Object, embeddingMock.Object, database.Object,
            hybrid.Object, keyword.Object, threshold.Object, suggester.Object,
            MakeOptions(), filter: null, searchTopK: 5, displayLimit: 3,
            new Mock<ILogger>().Object);

        Assert.Equal("test query", result.Query);
        Assert.Equal(2, result.ResultCount);  // Alpha + Beta (Gamma below threshold)
        Assert.Contains(result.Results, r => r.Chunk.MemberName == "Alpha");
        Assert.Contains(result.Results, r => r.Chunk.MemberName == "Beta");
    }

    [Fact]
    public async Task ExecuteSearchAsync_NoResults_ReturnsEmptyResult()
    {
        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1f, 0f, 0f, 0f });

        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);

        var hybrid = new Mock<IHybridSearchService>();
        hybrid.Setup(h => h.SearchAsync(It.IsAny<float[]>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchFilter>()))
            .ReturnsAsync(new List<HybridResult>());

        var keyword = new Mock<IKeywordIndex>();
        keyword.Setup(k => k.IsAvailableAsync()).ReturnsAsync(true);

        var expander = new Mock<IQueryExpander>();
        expander.Setup(q => q.Expand(It.IsAny<string>())).Returns<string>(s => s);
        var suggester = new Mock<IQuerySuggester>();
        var threshold = new Mock<IAdaptiveThreshold>();

        var (sp, _) = BuildServiceProvider(embeddingMock.Object, database.Object, hybrid.Object,
            keyword.Object, expander.Object, suggester.Object, threshold.Object);

        var result = await SearchCommand.ExecuteSearchAsync(
            "no match", sp, expander.Object, embeddingMock.Object, database.Object,
            hybrid.Object, keyword.Object, threshold.Object, suggester.Object,
            MakeOptions(), filter: null, searchTopK: 5, displayLimit: 3,
            new Mock<ILogger>().Object);

        Assert.False(result.HasResults);
        Assert.Equal(0, result.ResultCount);
    }

    // ---------- PrintResults tests ----------

    [Fact]
    public void PrintResults_JsonOutput_ProducesValidJson()
    {
        var result = new SearchResult
        {
            Query = "test",
            ExpandedQuery = "test",
            EffectiveThreshold = 0.5f,
            Results = new List<HybridResult>
            {
                new()
                {
                    Chunk = CreateChunk("Alpha", 0.9f),
                    HybridScore = 0.9f,
                    SemanticScore = 0.85f,
                    KeywordScore = 0.5f
                }
            }
        };

        // Redirect Console.Out to capture
        var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        try
        {
            SearchCommand.PrintResults(result, "json", 3);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }

        var output = sw.ToString();
        // Must be valid JSON
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("test", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("resultCount").GetInt32());
        var firstResult = doc.RootElement.GetProperty("results")[0];
        Assert.Equal("Alpha", firstResult.GetProperty("memberName").GetString());
        Assert.Equal(0.9f, firstResult.GetProperty("score").GetSingle(), precision: 3);
    }

    [Fact]
    public void PrintResults_QuietOutput_PrintsOnlyTop1()
    {
        var result = new SearchResult
        {
            Query = "test",
            Results = new List<HybridResult>
            {
                new() { Chunk = CreateChunk("TopOne", 0.95f), HybridScore = 0.95f, SemanticScore = 0.95f, FinalScore = 0.95f },
                new() { Chunk = CreateChunk("Second", 0.8f), HybridScore = 0.8f, SemanticScore = 0.8f, FinalScore = 0.8f }
            }
        };

        var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        try
        {
            SearchCommand.PrintResults(result, "quiet", 3);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }

        var output = sw.ToString().Trim();
        // Single line, contains the top-1 chunk's name and not the second
        Assert.Single(output.Split('\n'));
        Assert.Contains("TopOne", output);
        Assert.DoesNotContain("Second", output);
    }

    [Fact]
    public void PrintResults_QuietNoResults_PrintsNothing()
    {
        var result = new SearchResult { Query = "x", Results = new List<HybridResult>() };

        var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        try
        {
            SearchCommand.PrintResults(result, "quiet", 3);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }

        Assert.Equal(string.Empty, sw.ToString());
    }

    [Fact]
    public void PrintResults_TextOutput_ContainsQueryAndResults()
    {
        var result = new SearchResult
        {
            Query = "arithmetic",
            ExpandedQuery = "arithmetic math",
            EffectiveThreshold = 0.5f,
            Results = new List<HybridResult>
            {
                new() { Chunk = CreateChunk("Add", 0.9f), HybridScore = 0.9f, SemanticScore = 0.9f, FinalScore = 0.9f }
            }
        };

        var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        try
        {
            SearchCommand.PrintResults(result, "text", 3);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }

        var output = sw.ToString();
        Assert.Contains("arithmetic", output);
        Assert.Contains("Add", output);
    }

    // ---------- RunAsync with --query (one-shot) tests ----------

    [Fact]
    public async Task RunAsync_WithQuery_ExecutesOneShotAndExits()
    {
        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1f, 0f, 0f, 0f });

        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.InitializeAsync()).Returns(Task.CompletedTask);

        var hybrid = new Mock<IHybridSearchService>();
        hybrid.Setup(h => h.SearchAsync(It.IsAny<float[]>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchFilter>()))
            .ReturnsAsync(new List<HybridResult>
            {
                new() { Chunk = CreateChunk("Result1", 0.9f), HybridScore = 0.9f, FinalScore = 0.9f }
            });

        var keyword = new Mock<IKeywordIndex>();
        keyword.Setup(k => k.IsAvailableAsync()).ReturnsAsync(true);

        var expander = new Mock<IQueryExpander>();
        expander.Setup(q => q.Expand(It.IsAny<string>())).Returns<string>(s => s);
        var suggester = new Mock<IQuerySuggester>();
        var threshold = new Mock<IAdaptiveThreshold>();
        threshold.Setup(t => t.Compute(It.IsAny<List<float>>(), It.IsAny<AdaptiveThresholdOptions>(), It.IsAny<string>()))
            .Returns(0.5f);

        var (sp, config) = BuildServiceProvider(embeddingMock.Object, database.Object, hybrid.Object,
            keyword.Object, expander.Object, suggester.Object, threshold.Object);

        // Capture stdout to make sure we don't deadlock on Console.ReadLine
        var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new System.IO.StringReader(string.Empty));  // no interactive input
        try
        {
            var args = new[] { "--mode", "search", "--query", "test" };
            int exitCode = await SearchCommand.RunAsync(sp, config, new Mock<ILogger>().Object, args);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        }

        // Output should mention the result
        Assert.Contains("Result1", sw.ToString());
    }

    [Fact]
    public async Task RunAsync_WithQueryNoResults_ReturnsExitCode1()
    {
        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1f, 0f, 0f, 0f });

        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.InitializeAsync()).Returns(Task.CompletedTask);

        var hybrid = new Mock<IHybridSearchService>();
        hybrid.Setup(h => h.SearchAsync(It.IsAny<float[]>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchFilter>()))
            .ReturnsAsync(new List<HybridResult>());

        var keyword = new Mock<IKeywordIndex>();
        keyword.Setup(k => k.IsAvailableAsync()).ReturnsAsync(true);

        var expander = new Mock<IQueryExpander>();
        expander.Setup(q => q.Expand(It.IsAny<string>())).Returns<string>(s => s);
        var suggester = new Mock<IQuerySuggester>();
        var threshold = new Mock<IAdaptiveThreshold>();
        threshold.Setup(t => t.Compute(It.IsAny<List<float>>(), It.IsAny<AdaptiveThresholdOptions>(), It.IsAny<string>()))
            .Returns(0.5f);

        var (sp, config) = BuildServiceProvider(embeddingMock.Object, database.Object, hybrid.Object,
            keyword.Object, expander.Object, suggester.Object, threshold.Object);

        var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new System.IO.StringReader(string.Empty));
        try
        {
            var args = new[] { "--mode", "search", "--query", "nothing-matches" };
            int exitCode = await SearchCommand.RunAsync(sp, config, new Mock<ILogger>().Object, args);
            Assert.Equal(1, exitCode);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        }
    }

    [Fact]
    public async Task RunAsync_QuietWithQuery_OutputsSingleLine()
    {
        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 1f, 0f, 0f, 0f });

        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.InitializeAsync()).Returns(Task.CompletedTask);

        var hybrid = new Mock<IHybridSearchService>();
        hybrid.Setup(h => h.SearchAsync(It.IsAny<float[]>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchFilter>()))
            .ReturnsAsync(new List<HybridResult>
            {
                new() { Chunk = CreateChunk("OnlyOne", 0.95f), HybridScore = 0.95f, FinalScore = 0.95f }
            });

        var keyword = new Mock<IKeywordIndex>();
        keyword.Setup(k => k.IsAvailableAsync()).ReturnsAsync(true);

        var expander = new Mock<IQueryExpander>();
        expander.Setup(q => q.Expand(It.IsAny<string>())).Returns<string>(s => s);
        var suggester = new Mock<IQuerySuggester>();
        var threshold = new Mock<IAdaptiveThreshold>();
        threshold.Setup(t => t.Compute(It.IsAny<List<float>>(), It.IsAny<AdaptiveThresholdOptions>(), It.IsAny<string>()))
            .Returns(0.5f);

        var (sp, config) = BuildServiceProvider(embeddingMock.Object, database.Object, hybrid.Object,
            keyword.Object, expander.Object, suggester.Object, threshold.Object);

        var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new System.IO.StringReader(string.Empty));
        try
        {
            var args = new[] { "--mode", "search", "--query", "x", "--quiet" };
            int exitCode = await SearchCommand.RunAsync(sp, config, new Mock<ILogger>().Object, args);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        }

        var output = sw.ToString().Trim();
        Assert.Single(output.Split('\n'));
        Assert.Contains("OnlyOne", output);
    }
}

/// <summary>
/// Internal helper extension to attach a FinalScore/SemanticScore to a CodeChunk's HybridResult test fixture.
/// </summary>
internal static class CodeChunkTestExtensions
{
    public static CodeChunk WithFinalScore(this CodeChunk chunk, float score)
    {
        // Used to give the chunk a deterministic content hash for testing
        chunk.ContentHash = Guid.NewGuid().ToString("N");
        return chunk;
    }
}
