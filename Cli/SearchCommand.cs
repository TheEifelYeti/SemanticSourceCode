using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;

namespace SemanticSourceCode.Cli;

public static class SearchCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        var embeddingService = services.GetRequiredService<IEmbeddingService>();
        var database = services.GetRequiredService<IVectorDatabase>();
        var queryExpander = services.GetRequiredService<IQueryExpander>();

        await database.InitializeAsync();

        if (!await database.IsInitializedAsync())
        {
            Console.WriteLine("Database not initialized. Run index mode first.");
            return 1;
        }

        var searchOptions = configuration.GetSection("Search").Get<SearchOptions>() ?? new SearchOptions();

        Console.WriteLine("Semantic Code Search");
        Console.WriteLine("====================");

        while (true)
        {
            Console.WriteLine();
            Console.Write("Enter search query (or 'quit' to exit): ");
            var query = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(query) || query.ToLowerInvariant() == "quit")
                break;

            var expandedQuery = queryExpander.Expand(query);
            Console.WriteLine($"Searching for: {expandedQuery}");

            try
            {
                var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(expandedQuery);

                var resultsWithScores = await database.SearchSimilarWithScoresAsync(queryEmbedding, topK: searchOptions.TopK);

                var filteredResults = resultsWithScores
                    .Where(r => r.Similarity >= searchOptions.MinimumSimilarity)
                    .Take(searchOptions.DisplayCount)
                    .ToList();

                var avgScore = resultsWithScores.Count > 0 ? resultsWithScores.Average(r => r.Similarity) : 0;
                var maxScore = resultsWithScores.Count > 0 ? resultsWithScores.Max(r => r.Similarity) : 0;

                if (filteredResults.Count == 0 && maxScore >= searchOptions.WeakMatchThreshold)
                {
                    var weakMatches = resultsWithScores.Take(3).ToList();
                    Console.WriteLine($"Weak matches found (similarity < {searchOptions.MinimumSimilarity:F2}):");
                    Console.WriteLine();
                    for (int i = 0; i < weakMatches.Count; i++)
                    {
                        var (result, score) = weakMatches[i];
                        Console.WriteLine($"{i + 1}. {result.NamespaceName}.{result.ClassName}.{result.MemberName}");
                        Console.WriteLine($"   Similarity: {score:F4}");
                    }
                    Console.WriteLine();
                    Console.WriteLine($"Tip: Results may be less relevant. Try a more specific query.");
                    continue;
                }

                Console.WriteLine($"\nFound {filteredResults.Count} results (filtered by similarity >= {searchOptions.MinimumSimilarity:F2}):");
                Console.WriteLine(new string('=', 80));

                for (int i = 0; i < filteredResults.Count; i++)
                {
                    var (result, score) = filteredResults[i];
                    Console.WriteLine($"\n{i + 1}. {result.NamespaceName}.{result.ClassName}.{result.MemberName}");
                    Console.WriteLine($"   Similarity: {score:F4}");
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
                logger.LogError(ex, "Error during search");
            }
        }

        Console.WriteLine("Goodbye!");
        return 0;
    }
}
