using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;

namespace SemanticSourceCode.Cli;

public static class SearchCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration configuration, ILogger logger, string[] args)
    {
        var embeddingService = services.GetRequiredService<IEmbeddingService>();
        var database = services.GetRequiredService<IVectorDatabase>();
        var queryExpander = services.GetRequiredService<IQueryExpander>();
        var querySuggester = services.GetRequiredService<IQuerySuggester>();
        var adaptiveThreshold = services.GetRequiredService<IAdaptiveThreshold>();
        var hybridSearch = services.GetRequiredService<IHybridSearchService>();

        await database.InitializeAsync();

        if (!await database.IsInitializedAsync())
        {
            Console.WriteLine("Database not initialized. Run index mode first.");
            return 1;
        }

        var searchOptions = configuration.GetSection("Search").Get<SearchOptions>() ?? new SearchOptions();

        // Parse filter arguments from CLI
        var filter = ParseFilterFromArgs(args);
        bool hasFilter = filter != null && !filter.IsEmpty;

        // When filtering, we can use a larger topK to ensure enough results after filtering
        int searchTopK = hasFilter ? Math.Max(searchOptions.TopK, 50) : searchOptions.TopK;

        if (hasFilter)
        {
            Console.WriteLine($"Active filters: {DescribeFilter(filter!)}");
        }

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

                List<(CodeChunk Chunk, float Similarity)> resultsWithScores;

                if (hasFilter)
                {
                    resultsWithScores = await database.SearchSimilarWithScoresAsync(queryEmbedding, filter!, topK: searchTopK);
                }
                else
                {
                    resultsWithScores = await database.SearchSimilarWithScoresAsync(queryEmbedding, topK: searchTopK);
                }

                var filteredResults = resultsWithScores
                    .Where(r => r.Similarity >= searchOptions.MinimumSimilarity)
                    .Take(searchOptions.DisplayCount)
                    .ToList();

                // Compute adaptive threshold if enabled
                var scores = resultsWithScores.Select(r => r.Similarity).ToList();
                var adaptiveMinScore = adaptiveThreshold.Compute(scores, searchOptions.AdaptiveThreshold, query);
                
                // Override with adaptive threshold if it's more restrictive than the configured minimum
                var effectiveThreshold = Math.Max(searchOptions.MinimumSimilarity, adaptiveMinScore);
                
                var finalResults = resultsWithScores
                    .Where(r => r.Similarity >= effectiveThreshold)
                    .Take(searchOptions.DisplayCount)
                    .ToList();

                var avgScore = resultsWithScores.Count > 0 ? resultsWithScores.Average(r => r.Similarity) : 0;
                var maxScore = resultsWithScores.Count > 0 ? resultsWithScores.Max(r => r.Similarity) : 0;

                if (finalResults.Count == 0 && maxScore >= searchOptions.WeakMatchThreshold)
                {
                    // Try to suggest alternative queries
                    if (!hasFilter)
                    {
                        var allChunks = await database.GetAllChunksAsync();
                        var suggestions = querySuggester.Suggest(query, allChunks);
                        if (suggestions.Count > 0)
                        {
                            Console.WriteLine($"Meintest du: {string.Join(", ", suggestions)}?");
                            Console.WriteLine();
                        }
                    }

                    var weakMatches = resultsWithScores.Take(3).ToList();
                    Console.WriteLine($"Weak matches found (similarity < {effectiveThreshold:F2}):");
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

                Console.WriteLine($"\nFound {finalResults.Count} results (filtered by similarity >= {effectiveThreshold:F2}):");
                Console.WriteLine(new string('=', 80));

                for (int i = 0; i < finalResults.Count; i++)
                {
                    var (result, score) = finalResults[i];
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

    private static SearchFilter? ParseFilterFromArgs(string[] args)
    {
        var filter = new SearchFilter();
        bool hasAny = false;

        var ns = CliArguments.GetValue(args, "--namespace");
        if (!string.IsNullOrWhiteSpace(ns)) { filter.Namespace = ns; hasAny = true; }

        var className = CliArguments.GetValue(args, "--class");
        if (!string.IsNullOrWhiteSpace(className)) { filter.ClassName = className; hasAny = true; }

        var httpMethod = CliArguments.GetValue(args, "--http-method");
        if (!string.IsNullOrWhiteSpace(httpMethod)) { filter.HttpMethod = httpMethod; hasAny = true; }

        var filePattern = CliArguments.GetValue(args, "--file-pattern");
        if (!string.IsNullOrWhiteSpace(filePattern)) { filter.FilePathPattern = filePattern; hasAny = true; }

        if (hasAny) return filter;
        return null;
    }

    private static string DescribeFilter(SearchFilter filter)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter.Namespace)) parts.Add($"namespace={filter.Namespace}");
        if (!string.IsNullOrWhiteSpace(filter.ClassName)) parts.Add($"class={filter.ClassName}");
        if (!string.IsNullOrWhiteSpace(filter.HttpMethod)) parts.Add($"http={filter.HttpMethod}");
        if (!string.IsNullOrWhiteSpace(filter.FilePathPattern)) parts.Add($"file={filter.FilePathPattern}");
        return string.Join(", ", parts);
    }
}
