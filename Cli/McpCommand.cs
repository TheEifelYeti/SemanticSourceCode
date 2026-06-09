using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Mcp;

namespace SemanticSourceCode.Cli;

/// <summary>
/// Entry point for `--mode mcp`. Starts the MCP server and blocks until
/// the client closes the connection or Ctrl+C is pressed.
/// </summary>
public static class McpCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting MCP server mode");
        return await McpServer.RunAsync(services, logger, cancellationToken);
    }
}
