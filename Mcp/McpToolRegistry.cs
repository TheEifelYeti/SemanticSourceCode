using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;

namespace SemanticSourceCode.Mcp;

/// <summary>
/// Tool delegate: takes the raw MCP params JsonElement + IServiceProvider and
/// returns a <see cref="JsonRpcToolResult"/>. Tools are responsible for
/// deserializing params and reporting errors via the isError flag.
/// </summary>
public delegate Task<JsonRpcToolResult> McpToolHandler(
    JsonElement? parameters,
    IServiceProvider services,
    ILogger logger);

/// <summary>
/// In-memory registry of tools the MCP server exposes.
/// Tools are registered manually (no source-generator attribute scanning) so
/// we don't depend on the still-preview ModelContextProtocol NuGet package.
/// </summary>
public class McpToolRegistry
{
    private readonly Dictionary<string, (McpToolDescriptor Descriptor, McpToolHandler Handler)> _tools = new();

    public IReadOnlyCollection<McpToolDescriptor> List() => _tools.Values.Select(t => t.Descriptor).ToList();

    public bool TryGet(string name, out McpToolDescriptor descriptor, out McpToolHandler handler)
    {
        if (_tools.TryGetValue(name, out var entry))
        {
            descriptor = entry.Descriptor;
            handler = entry.Handler;
            return true;
        }
        descriptor = null!;
        handler = null!;
        return false;
    }

    public void Register(string name, string description, JsonElement inputSchema, McpToolHandler handler)
    {
        _tools[name] = (new McpToolDescriptor
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema
        }, handler);
    }
}

/// <summary>
/// Built-in tools registered by <see cref="McpServer"/>.
/// </summary>
public static class BuiltInMcpTools
{
    public const string SearchCode = "search_code";
    public const string GetChunkById = "get_chunk_by_id";

    public static void RegisterAll(McpToolRegistry registry)
    {
        registry.Register(
            SearchCode,
            "Search the indexed code base semantically. Returns up to `limit` (default 3) " +
            "code chunks ranked by hybrid score (semantic similarity + keyword match). " +
            "Use namespace/class/filePattern to scope the search.",
            McpSchemas.SearchCode,
            SearchCodeHandler);

        registry.Register(
            GetChunkById,
            "Fetch a single indexed code chunk by its ID. Returns null if no chunk with that ID exists.",
            McpSchemas.GetChunkById,
            GetChunkByIdHandler);
    }

    private static async Task<JsonRpcToolResult> SearchCodeHandler(
        JsonElement? parameters,
        IServiceProvider services,
        ILogger logger)
    {
        if (parameters == null
            || !parameters.Value.TryGetProperty("query", out var queryElem)
            || queryElem.ValueKind != JsonValueKind.String)
        {
            return ErrorResult("Missing or invalid 'query' parameter.");
        }

        var query = queryElem.GetString()!;
        if (string.IsNullOrWhiteSpace(query))
        {
            return ErrorResult("'query' must not be empty.");
        }

        // Optional parameters
        string? ns = null, className = null, filePattern = null;
        int? limit = null;
        if (parameters.Value.TryGetProperty("namespace", out var nsElem) && nsElem.ValueKind == JsonValueKind.String)
            ns = nsElem.GetString();
        if (parameters.Value.TryGetProperty("class", out var classElem) && classElem.ValueKind == JsonValueKind.String)
            className = classElem.GetString();
        if (parameters.Value.TryGetProperty("filePattern", out var fpElem) && fpElem.ValueKind == JsonValueKind.String)
            filePattern = fpElem.GetString();
        if (parameters.Value.TryGetProperty("limit", out var limElem) && limElem.ValueKind == JsonValueKind.Number)
            limit = limElem.GetInt32();

        // Build a SearchFilter if any filter was provided
        SearchFilter? filter = null;
        if (ns != null || className != null || filePattern != null)
        {
            filter = new SearchFilter
            {
                Namespace = ns,
                ClassName = className,
                FilePathPattern = filePattern
            };
        }

        try
        {
            // Pull the same dependencies SearchCommand.RunAsync uses
            var config = services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var embeddingService = services.GetRequiredService<IEmbeddingService>();
            var database = services.GetRequiredService<IVectorDatabase>();
            var queryExpander = services.GetRequiredService<IQueryExpander>();
            var querySuggester = services.GetRequiredService<IQuerySuggester>();
            var adaptiveThreshold = services.GetRequiredService<IAdaptiveThreshold>();
            var hybridSearch = services.GetRequiredService<IHybridSearchService>();
            var keywordIndex = services.GetRequiredService<IKeywordIndex>();

            await database.InitializeAsync();
            if (!await database.IsInitializedAsync())
            {
                return ErrorResult("Database not initialized. Run index mode first.");
            }

            var searchOptions = config.GetSection("Search").Get<SearchOptions>() ?? new SearchOptions();
            bool hasFilter = filter != null;
            int searchTopK = hasFilter ? Math.Max(searchOptions.TopK, 50) : searchOptions.TopK;
            int displayLimit = limit ?? searchOptions.DisplayCount;

            var result = await SemanticSourceCode.Cli.SearchCommand.ExecuteSearchAsync(
                query, services, queryExpander, embeddingService, database,
                hybridSearch, keywordIndex, adaptiveThreshold, querySuggester,
                searchOptions, filter, searchTopK, displayLimit, logger);

            var dtos = result.Results.Select(McpCodeChunkDto.FromHybridResult).ToList();
            var json = JsonSerializer.Serialize(new
            {
                query = result.Query,
                expandedQuery = result.ExpandedQuery,
                effectiveThreshold = result.EffectiveThreshold,
                resultCount = dtos.Count,
                suggestions = result.Suggestions,
                results = dtos
            }, new JsonSerializerOptions { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

            return new JsonRpcToolResult
            {
                Content = new List<JsonRpcContent> { new() { Type = "text", Text = json } }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in search_code tool");
            return ErrorResult($"Search failed: {ex.Message}");
        }
    }

    private static async Task<JsonRpcToolResult> GetChunkByIdHandler(
        JsonElement? parameters,
        IServiceProvider services,
        ILogger logger)
    {
        if (parameters == null
            || !parameters.Value.TryGetProperty("id", out var idElem)
            || idElem.ValueKind != JsonValueKind.String)
        {
            return ErrorResult("Missing or invalid 'id' parameter.");
        }

        var id = idElem.GetString()!;
        if (string.IsNullOrWhiteSpace(id))
        {
            return ErrorResult("'id' must not be empty.");
        }

        try
        {
            var database = services.GetRequiredService<IVectorDatabase>();
            await database.InitializeAsync();
            if (!await database.IsInitializedAsync())
            {
                return ErrorResult("Database not initialized. Run index mode first.");
            }

            var allChunks = await database.GetAllChunksAsync();
            var match = allChunks.FirstOrDefault(c => c.Id == id);
            var dto = McpCodeChunkDto.FromCodeChunk(match);
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            return new JsonRpcToolResult
            {
                Content = new List<JsonRpcContent> { new() { Type = "text", Text = json } }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in get_chunk_by_id tool");
            return ErrorResult($"Lookup failed: {ex.Message}");
        }
    }

    private static JsonRpcToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = new List<JsonRpcContent> { new() { Type = "text", Text = message } }
    };
}
