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

        // Add logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        // Parse arguments
        if (args.Length == 0)
        {
            ShowUsage();
            return 1;
        }

        var mode = args[0].ToLowerInvariant();

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
                else if (subMode == "search")
                {
                    return await SearchCommand.RunAsync(serviceProvider, configuration, logger, args);
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
        Console.WriteLine("  SemanticSourceCode --mode search");
        Console.WriteLine();
        Console.WriteLine("Modes:");
        Console.WriteLine("  index   - Index C# files in the specified directory");
        Console.WriteLine("  search  - Start interactive semantic search");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  SemanticSourceCode --mode index --path ./src");
        Console.WriteLine("  SemanticSourceCode --mode index --path /home/user/projects/MyApp");
        Console.WriteLine("  SemanticSourceCode --mode search");
    }
}
