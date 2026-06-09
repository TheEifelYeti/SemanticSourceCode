using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticSourceCode.Mcp;

/// <summary>
/// JSON-RPC 2.0 request envelope. See https://www.jsonrpc.org/specification.
/// We accept both id=null (notification) and id=string/number (request).
/// </summary>
public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 response envelope. For successful results we return a
/// <see cref="JsonRpcToolResult"/> (the MCP "tool call" response shape).
/// For errors we set Error and leave Result null.
/// </summary>
public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse FromId(object? id) => new() { Id = id as JsonElement? };
}

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// MCP tool result wrapper. Per the MCP spec, a tool call result contains
/// an array of content blocks plus an optional isError flag.
/// </summary>
public class JsonRpcToolResult
{
    [JsonPropertyName("content")]
    public List<JsonRpcContent> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }
}

public class JsonRpcContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Description of a single tool, returned by `tools/list`.
/// </summary>
public class McpToolDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; set; }
}

/// <summary>
/// JSON Schema for a tool's input. We hardcode a small set of named types
/// here; an MCP client only needs to see a valid JSON Schema to send args.
/// </summary>
public static class McpSchemas
{
    public static readonly JsonElement SearchCode = JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""query"": { ""type"": ""string"", ""description"": ""The search query (semantic + keyword)."" },
            ""namespace"": { ""type"": ""string"", ""description"": ""Optional: filter to chunks in this namespace."" },
            ""class"": { ""type"": ""string"", ""description"": ""Optional: filter to chunks in this class."" },
            ""filePattern"": { ""type"": ""string"", ""description"": ""Optional: filter to files matching this glob pattern."" },
            ""limit"": { ""type"": ""integer"", ""description"": ""Optional: max number of results (default 3)."" }
        },
        ""required"": [""query""]
    }").RootElement.Clone();

    public static readonly JsonElement GetChunkById = JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""id"": { ""type"": ""string"", ""description"": ""The chunk ID (semantic SHA-256 hash from Issue #2)."" }
        },
        ""required"": [""id""]
    }").RootElement.Clone();
}
