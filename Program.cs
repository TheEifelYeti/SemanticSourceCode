using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Cli;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;

namespace SemanticSourceCode;

public class Program
{
    static async Task<int> Main(string[] args)
    {
        // Setup configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        // Setup dependency injection
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTransient<ICodeAnalyzer, CodeAnalyzer>();
        services.AddTransient<IEmbeddingService>(provider =>
        {
            var factory = new EmbeddingServiceFactory(
                provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<ILoggerFactory>());
            return factory.CreateEmbeddingServiceAsync().GetAwaiter().GetResult();
        });
        services.AddTransient<IVectorDatabase, SqliteVssDatabase>();
        services.AddTransient<IQueryExpander, QueryExpander>();
        services.AddTransient<IQuerySuggester, QuerySuggester>();
        services.AddTransient<IHybridSearchService, HybridSearchService>();
        services.AddTransient<IKeywordIndex, KeywordIndexService>();
        services.AddTransient<IResultRanker, ResultRanker>();
        services.AddTransient<IAdaptiveThreshold, AdaptiveThreshold>();

        // Parse arguments
        if (args.Length == 0)
        {
            ShowUsage();
            return 1;
        }

        var mode = args[0].ToLowerInvariant();

        // Detect MCP mode early so we can route logging to stderr (keep stdout clean for JSON-RPC).
        bool isMcp = mode == "--mode" && args.Length >= 2 && args[1].Equals("mcp", StringComparison.OrdinalIgnoreCase);

        // Add logging (with stderr routing in MCP mode to keep stdout clean for JSON-RPC)
        services.AddLogging(builder =>
        {
            builder.AddConsole(opts =>
            {
                if (isMcp)
                {
                    opts.LogToStandardErrorThreshold = LogLevel.Trace;
                }
            });
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        switch (mode)
        {
            case "--mode":
            case "-m":
                if (args.Length < 2)
                {
                    ShowUsage();
                    return 1;
                }

                var subMode = args[1].ToLowerInvariant();

                if (subMode == "index")
                {
                    var pathIndex = Array.IndexOf(args, "--path");
                    if (pathIndex == -1 || pathIndex + 1 >= args.Length)
                    {
                        logger.LogError("--path is required for index mode");
                        return 1;
                    }
                    var path = args[pathIndex + 1];
                    return await IndexCommand.RunAsync(serviceProvider, path, logger);
                }
                else if (subMode == "watch")
                {
                    var pathIndex = Array.IndexOf(args, "--path");
                    if (pathIndex == -1 || pathIndex + 1 >= args.Length)
                    {
                        logger.LogError("--path is required for watch mode");
                        return 1;
                    }
                    var path = args[pathIndex + 1];
                    var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        cts.Cancel();
                    };
                    return await WatchCommand.RunAsync(serviceProvider, path, logger, cts.Token);
                }
                else if (subMode == "search")
                {
                    return await SearchCommand.RunAsync(serviceProvider, configuration, logger, args);
                }
                else if (subMode == "mcp")
                {
                    var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        cts.Cancel();
                    };
                    return await McpCommand.RunAsync(serviceProvider, logger, cts.Token);
                }
                else
                {
                    ShowUsage();
                    return 1;
                }

            default:
                ShowUsage();
                return 1;
        }
    }

    static void ShowUsage()
    {
        Console.WriteLine("SemanticSourceCode - Semantic Code Search Tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  SemanticSourceCode --mode index --path <directory>");
        Console.WriteLine("  SemanticSourceCode --mode watch --path <directory>");
        Console.WriteLine("  SemanticSourceCode --mode search --query <text>     [non-interactive]");
        Console.WriteLine("  SemanticSourceCode --mode search                     [interactive]");
        Console.WriteLine("  SemanticSourceCode --mode mcp                        [for AI agents]");
        Console.WriteLine();
        Console.WriteLine("Modes:");
        Console.WriteLine("  index   - Index C# files in the specified directory (one-shot, incremental)");
        Console.WriteLine("  watch   - Watch directory for *.cs changes and re-index live (Ctrl+C to stop)");
        Console.WriteLine("  search  - Search the indexed code base (interactive, or one-shot with --query)");
        Console.WriteLine("  mcp     - Run as an MCP (Model Context Protocol) JSON-RPC server over stdio");
        Console.WriteLine();
        Console.WriteLine("Search flags (with --query for one-shot):");
        Console.WriteLine("  --query, -q       <text>  The search query (triggers non-interactive mode)");
        Console.WriteLine("  --format, -f      text|json|quiet  Output format (default: text)");
        Console.WriteLine("  --limit, -l       <N>    Max results to display (default from config)");
        Console.WriteLine("  --quiet                   Shorthand for --format quiet");
        Console.WriteLine("  --namespace       <ns>    Filter to chunks in this namespace");
        Console.WriteLine("  --class           <name> Filter to chunks in this class");
        Console.WriteLine("  --http-method     <verb> Filter to controller methods with this HTTP verb");
        Console.WriteLine("  --file-pattern    <patt> Filter to files matching this glob pattern");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  SemanticSourceCode --mode index --path ./src");
        Console.WriteLine("  SemanticSourceCode --mode watch --path ./src");
        Console.WriteLine("  SemanticSourceCode --mode search --query \"arithmetic calculation\"");
        Console.WriteLine("  SemanticSourceCode --mode search -q \"database connection\" --format json");
        Console.WriteLine("  SemanticSourceCode --mode search -q \"sum\" --quiet");
        Console.WriteLine("  SemanticSourceCode --mode mcp   # connect from Claude Code, Cursor, etc.");
    }
}
