using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SemanticSourceCode.Mcp;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Mcp;

/// <summary>
/// Tests for Issue #6: MCP server.
/// Covers JSON-RPC 2.0 dispatch and the two built-in tools (search_code, get_chunk_by_id).
/// </summary>
public class McpServerTests
{
    // ---------- Helpers ----------

    private static (ServiceProvider sp, IConfiguration config) BuildServiceProvider(
        IEmbeddingService embeddingService,
        IVectorDatabase database,
        IHybridSearchService? hybridSearch = null,
        IKeywordIndex? keywordIndex = null,
        IQueryExpander? queryExpander = null,
        IQuerySuggester? querySuggester = null,
        IAdaptiveThreshold? adaptiveThreshold = null)
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
        services.AddLogging();
        return (services.BuildServiceProvider(), config);
    }

    private static CodeChunk CreateChunk(string memberName, float score, string idSuffix = "")
    {
        return new CodeChunk
        {
            Id = $"/src/Test.cs|Test|{memberName}|Method|0|{idSuffix}",
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
        };
    }

    // ---------- Registry / dispatch tests ----------

    [Fact]
    public void Registry_RegistersAllBuiltInTools()
    {
        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);

        Assert.True(registry.TryGet(BuiltInMcpTools.SearchCode, out _, out _));
        Assert.True(registry.TryGet(BuiltInMcpTools.GetChunkById, out _, out _));
    }

    [Fact]
    public void List_ReturnsBothToolsWithSchemas()
    {
        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);

        var tools = registry.List().ToList();
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == BuiltInMcpTools.SearchCode);
        Assert.Contains(tools, t => t.Name == BuiltInMcpTools.GetChunkById);

        var searchTool = tools.First(t => t.Name == BuiltInMcpTools.SearchCode);
        // Schema should mention "query" as a required property
        var schemaStr = searchTool.InputSchema.GetRawText();
        Assert.Contains("query", schemaStr);
        Assert.Contains("required", schemaStr);
    }

    [Fact]
    public async Task HandleLineAsync_Initialize_ReturnsServerInfo()
    {
        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);

        var line = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), Mock.Of<IVectorDatabase>());

        var response = await McpServer.HandleLineAsync(line, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        var resultJson = JsonSerializer.Serialize(response.Result);
        Assert.Contains("semantic-source-code", resultJson);
        Assert.Contains("protocolVersion", resultJson);
    }

    [Fact]
    public async Task HandleLineAsync_ToolsList_ReturnsRegisteredTools()
    {
        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);

        var line = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), Mock.Of<IVectorDatabase>());

        var response = await McpServer.HandleLineAsync(line, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains(BuiltInMcpTools.SearchCode, json);
        Assert.Contains(BuiltInMcpTools.GetChunkById, json);
    }

    [Fact]
    public async Task HandleLineAsync_UnknownMethod_ReturnsErrorMinus32601()
    {
        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);

        var line = """{"jsonrpc":"2.0","id":3,"method":"nonexistent","params":{}}""";
        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), Mock.Of<IVectorDatabase>());

        var response = await McpServer.HandleLineAsync(line, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.NotNull(response!.Error);
        Assert.Equal(-32601, response.Error!.Code);
    }

    [Fact]
    public async Task HandleLineAsync_Notification_ReturnsNullNoResponse()
    {
        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);

        // No "id" field — this is a notification (per JSON-RPC 2.0)
        var line = """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""";
        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), Mock.Of<IVectorDatabase>());

        var response = await McpServer.HandleLineAsync(line, registry, sp, new Mock<ILogger>().Object);

        // Notifications should not produce a response
        Assert.Null(response);
    }

    [Fact]
    public async Task HandleLineAsync_MalformedJson_ReturnsParseError()
    {
        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);

        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), Mock.Of<IVectorDatabase>());

        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await McpServer.HandleLineAsync("not valid json", registry, sp, new Mock<ILogger>().Object);
        });
    }

    [Fact]
    public async Task HandleLineAsync_Ping_ReturnsEmptyObject()
    {
        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);

        var line = """{"jsonrpc":"2.0","id":99,"method":"ping","params":{}}""";
        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), Mock.Of<IVectorDatabase>());

        var response = await McpServer.HandleLineAsync(line, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        Assert.NotNull(response.Result);
    }

    // ---------- search_code tool tests ----------

    [Fact]
    public async Task SearchCodeTool_WithQuery_ReturnsResults()
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
                new() { Chunk = CreateChunk("Alpha", 0.9f, "alpha"), HybridScore = 0.9f, SemanticScore = 0.9f, FinalScore = 0.9f },
                new() { Chunk = CreateChunk("Beta", 0.7f, "beta"), HybridScore = 0.7f, SemanticScore = 0.7f, FinalScore = 0.7f }
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

        // Build a tools/call request for search_code
        var callLine = """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"search_code","arguments":{"query":"test"}}}""";

        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);
        var response = await McpServer.HandleLineAsync(callLine, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains("Alpha", json);
        Assert.Contains("Beta", json);
        Assert.Contains("resultCount", json);
    }

    [Fact]
    public async Task SearchCodeTool_MissingQuery_ReturnsToolError()
    {
        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), Mock.Of<IVectorDatabase>());

        var callLine = """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"search_code","arguments":{}}}""";

        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);
        var response = await McpServer.HandleLineAsync(callLine, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.NotNull(response!.Result);
        // The tool result is on Result, and isError should be true
        var result = response.Result as JsonRpcToolResult;
        Assert.NotNull(result);
        Assert.True(result!.IsError);
    }

    [Fact]
    public async Task SearchCodeTool_NoResults_ReturnsEmptyList()
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

        var (sp, _) = BuildServiceProvider(embeddingMock.Object, database.Object, hybrid.Object,
            keyword.Object,
            new Mock<IQueryExpander>().Object,
            new Mock<IQuerySuggester>().Object,
            new Mock<IAdaptiveThreshold>().Object);

        var callLine = """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"search_code","arguments":{"query":"nothing"}}}""";

        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);
        var response = await McpServer.HandleLineAsync(callLine, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains("resultCount", json);
        Assert.Contains("0", json);  // zero results
    }

    // ---------- get_chunk_by_id tool tests ----------

    [Fact]
    public async Task GetChunkByIdTool_ValidId_ReturnsChunk()
    {
        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.InitializeAsync()).Returns(Task.CompletedTask);
        var chunks = new List<CodeChunk>
        {
            CreateChunk("FindMe", 0f, "findme-id"),
            CreateChunk("OtherOne", 0f, "other-id")
        };
        database.Setup(d => d.GetAllChunksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<CodeChunk>)chunks);

        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), database.Object);

        // Use the full chunk ID (as stored in DB) to look up
        var fullId = chunks[0].Id;
        var callLine = "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"get_chunk_by_id\",\"arguments\":{\"id\":\"" + fullId + "\"}}}";

        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);
        var response = await McpServer.HandleLineAsync(callLine, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains("FindMe", json);
        Assert.DoesNotContain("OtherOne", json);
    }

    [Fact]
    public async Task GetChunkByIdTool_UnknownId_ReturnsNull()
    {
        var database = new Mock<IVectorDatabase>();
        database.Setup(d => d.IsInitializedAsync()).ReturnsAsync(true);
        database.Setup(d => d.InitializeAsync()).Returns(Task.CompletedTask);
        database.Setup(d => d.GetAllChunksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<CodeChunk>)new List<CodeChunk> { CreateChunk("Only", 0f, "only-id") });

        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), database.Object);

        var callLine = """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_chunk_by_id","arguments":{"id":"definitely-does-not-exist"}}}""";

        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);
        var response = await McpServer.HandleLineAsync(callLine, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        // Result should be a JsonRpcToolResult with text "null"
        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains("null", json);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsMethodNotFoundError()
    {
        var (sp, _) = BuildServiceProvider(Mock.Of<IEmbeddingService>(), Mock.Of<IVectorDatabase>());

        var callLine = """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"nonexistent_tool","arguments":{}}}""";

        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);
        var response = await McpServer.HandleLineAsync(callLine, registry, sp, new Mock<ILogger>().Object);

        Assert.NotNull(response);
        Assert.NotNull(response!.Error);
        Assert.Equal(-32601, response.Error!.Code);
    }
}
