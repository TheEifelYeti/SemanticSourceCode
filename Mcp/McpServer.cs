using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SemanticSourceCode.Mcp;

/// <summary>
/// Minimal MCP (Model Context Protocol) server over stdio JSON-RPC 2.0.
///
/// Reads one JSON-RPC request per line from stdin, writes one response per
/// line to stdout, and routes notifications without id to the discard pile.
/// Status messages go to stderr so the JSON-RPC channel on stdout stays clean.
///
/// We implement JSON-RPC manually rather than depending on the still-preview
/// ModelContextProtocol NuGet package. This keeps the build stable and gives
/// us full control over the wire format.
/// </summary>
public static class McpServer
{
    public const string ServerName = "semantic-source-code";
    public const string ServerVersion = "1.0.0";

    /// <summary>
    /// Runs the MCP server until stdin closes (EOF) or the cancellation token fires.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var registry = new McpToolRegistry();
        BuiltInMcpTools.RegisterAll(registry);

        logger.LogInformation("MCP server '{Name} v{Version}' starting (tools: {ToolCount})",
            ServerName, ServerVersion, registry.List().Count);
        await Console.Error.WriteLineAsync($"MCP server ready. Tools: {string.Join(", ", registry.List().Select(t => t.Name))}");

        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput());

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (line == null)
                {
                    // EOF — client closed the pipe
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonRpcResponse? response;
                try
                {
                    response = await HandleLineAsync(line, registry, services, logger);
                }
                catch (JsonException jex)
                {
                    response = new JsonRpcResponse
                    {
                        Id = null,
                        Error = new JsonRpcError
                        {
                            Code = -32700, // Parse error
                            Message = "Invalid JSON",
                            Data = jex.Message
                        }
                    };
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error while handling MCP request");
                    response = new JsonRpcResponse
                    {
                        Id = null,
                        Error = new JsonRpcError
                        {
                            Code = -32603, // Internal error
                            Message = ex.Message
                        }
                    };
                }

                // Notifications have no id and expect no response.
                if (response == null)
                {
                    continue;
                }

                var responseJson = JsonSerializer.Serialize(response, GetJsonOptions());
                await Console.Out.WriteLineAsync(responseJson);
                await Console.Out.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal MCP server error");
            return 1;
        }

        logger.LogInformation("MCP server stopped.");
        return 0;
    }

    /// <summary>
    /// Dispatches a single JSON-RPC line to the appropriate handler.
    /// Returns null for notifications (no id), so the caller can skip writing a response.
    /// </summary>
    public static async Task<JsonRpcResponse?> HandleLineAsync(
        string line,
        McpToolRegistry registry,
        IServiceProvider services,
        ILogger logger)
    {
        var request = JsonSerializer.Deserialize<JsonRpcRequest>(line, GetJsonOptions());
        if (request == null)
        {
            return new JsonRpcResponse
            {
                Id = null,
                Error = new JsonRpcError { Code = -32700, Message = "Empty request" }
            };
        }

        var response = JsonRpcResponse.FromId(request.Id);
        bool isNotification = request.Id == null
            || (request.Id.Value.ValueKind == JsonValueKind.Null);

        try
        {
            switch (request.Method)
            {
                case "initialize":
                    response.Result = new
                    {
                        protocolVersion = "2024-11-05",
                        serverInfo = new { name = ServerName, version = ServerVersion },
                        capabilities = new { tools = new { } }
                    };
                    break;

                case "notifications/initialized":
                    // Client acknowledgment; no response
                    return null;

                case "tools/list":
                    response.Result = new { tools = registry.List() };
                    break;

                case "tools/call":
                    if (request.Params == null
                        || !request.Params.Value.TryGetProperty("name", out var nameElem)
                        || nameElem.ValueKind != JsonValueKind.String)
                    {
                        response.Error = new JsonRpcError { Code = -32602, Message = "Missing 'name' in tools/call params" };
                        break;
                    }
                    var toolName = nameElem.GetString()!;
                    if (!registry.TryGet(toolName, out _, out var handler))
                    {
                        response.Error = new JsonRpcError
                        {
                            Code = -32601,
                            Message = $"Unknown tool: {toolName}"
                        };
                        break;
                    }
                    JsonElement? args = null;
                    if (request.Params.Value.TryGetProperty("arguments", out var argsElem))
                    {
                        args = argsElem;
                    }
                    var toolResult = await handler(args, services, logger);
                    response.Result = toolResult;
                    break;

                case "ping":
                    response.Result = new { };
                    break;

                default:
                    response.Error = new JsonRpcError
                    {
                        Code = -32601,
                        Message = $"Method not found: {request.Method}"
                    };
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in MCP method {Method}", request.Method);
            response.Error = new JsonRpcError
            {
                Code = -32603,
                Message = ex.Message
            };
        }

        return isNotification ? null : response;
    }

    private static JsonSerializerOptions GetJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
