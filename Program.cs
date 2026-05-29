using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Models;
using SemanticSourceCode.Services;

namespace SemanticSourceCode;

class Program
{
    static async Task Main(string[] args)
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
            return factory.CreateEmbeddingService();
        });
        services.AddTransient<IVectorDatabase, SqliteVssDatabase>();
        
        // Add logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var serviceProvider = services.BuildServiceProvider();

        // Parse arguments
        if (args.Length == 0)
        {
            ShowUsage();
            return;
        }

        var mode = args[0].ToLowerInvariant();
        
        switch (mode)
        {
            case "--mode":
            case "-m":
                if (args.Length < 2)
                {
                    ShowUsage();
                    return;
                }
                
                var subMode = args[1].ToLowerInvariant();
                
                if (subMode == "index")
                {
                    var pathIndex = Array.IndexOf(args, "--path");
                    if (pathIndex == -1 || pathIndex + 1 >= args.Length)
                    {
                        Console.WriteLine("Error: --path is required for index mode");
                        return;
                    }
                    var path = args[pathIndex + 1];
                    await RunIndexMode(serviceProvider, path);
                }
                else if (subMode == "search")
                {
                    await RunSearchMode(serviceProvider);
                }
                else
                {
                    ShowUsage();
                }
                break;
                
            default:
                ShowUsage();
                break;
        }
    }

    static async Task RunIndexMode(IServiceProvider services, string path)
    {
        Console.WriteLine($"Indexing code in: {path}");
        
        var analyzer = services.GetRequiredService<ICodeAnalyzer>();
        var embeddingService = services.GetRequiredService<IEmbeddingService>();
        var database = services.GetRequiredService<IVectorDatabase>();

        await database.InitializeAsync();

        if (!Directory.Exists(path))
        {
            Console.WriteLine($"Error: Directory not found: {path}");
            return;
        }

        // Analyze files
        Console.WriteLine("Analyzing C# files...");
        var chunks = await analyzer.AnalyzeDirectoryAsync(path);
        Console.WriteLine($"Found {chunks.Count} code chunks");

        if (chunks.Count == 0)
        {
            Console.WriteLine("No C# files found to index.");
            return;
        }

        // Generate embeddings
        Console.WriteLine("Generating embeddings (this may take a while)...");
        var processed = 0;
        foreach (var chunk in chunks)
        {
            try
            {
                var embedding = await embeddingService.GenerateEmbeddingAsync(chunk.Content);
                
                if (embedding == null || embedding.Length == 0)
                {
                    Console.WriteLine($"WARNING: Empty embedding for {chunk.FilePath} - {chunk.MemberName}. Skipping.");
                    continue;
                }
                
                chunk.Embedding = ConvertFloatArrayToByteArray(embedding);
                await database.InsertChunkAsync(chunk);
                
                processed++;
                if (processed % 10 == 0)
                {
                    Console.WriteLine($"Processed {processed}/{chunks.Count} chunks...");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing {chunk.FilePath} - {chunk.MemberName}: {ex.Message}");
            }
        }

        Console.WriteLine($"Successfully indexed {processed} code chunks.");
    }

    static async Task RunSearchMode(IServiceProvider services)
    {
        Console.WriteLine("Semantic Code Search");
        Console.WriteLine("====================");
        
        var embeddingService = services.GetRequiredService<IEmbeddingService>();
        var database = services.GetRequiredService<IVectorDatabase>();

        await database.InitializeAsync();

        if (!await database.IsInitializedAsync())
        {
            Console.WriteLine("Database not initialized. Run index mode first.");
            return;
        }

        while (true)
        {
            Console.WriteLine();
            Console.Write("Enter search query (or 'quit' to exit): ");
            var query = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(query) || query.ToLowerInvariant() == "quit")
                break;

            Console.WriteLine("Searching...");
            
            try
            {
                var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(query);
                var results = await database.SearchSimilarAsync(queryEmbedding, topK: 5);

                if (results.Count == 0)
                {
                    Console.WriteLine("No results found.");
                    continue;
                }

                Console.WriteLine($"\nFound {results.Count} results:");
                Console.WriteLine(new string('=', 80));

                for (int i = 0; i < results.Count; i++)
                {
                    var result = results[i];
                    Console.WriteLine($"\n{i + 1}. {result.NamespaceName}.{result.ClassName}.{result.MemberName}");
                    Console.WriteLine($"   Type: {result.MemberType}");
                    Console.WriteLine($"   File: {result.FilePath} (lines {result.StartLine}-{result.EndLine})");
                    Console.WriteLine($"   Signature: {result.Signature.Split('\n')[0]}");
                    
                    if (!string.IsNullOrWhiteSpace(result.Documentation))
                    {
                        var doc = result.Documentation.Replace("\n", " ").Replace("///", "").Trim();
                        if (doc.Length > 100)
                            doc = doc[..100] + "...";
                        Console.WriteLine($"   Documentation: {doc}");
                    }
                    
                    Console.WriteLine($"   Content Preview:");
                    var contentPreview = result.Content.Replace("\n", " ").Replace("\r", "");
                    if (contentPreview.Length > 200)
                        contentPreview = contentPreview[..200] + "...";
                    Console.WriteLine($"   {contentPreview}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during search: {ex.Message}");
            }
        }

        Console.WriteLine("Goodbye!");
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

    static byte[] ConvertFloatArrayToByteArray(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
