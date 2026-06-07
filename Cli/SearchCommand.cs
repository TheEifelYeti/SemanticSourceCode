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
        var keywordIndex = services.GetRequiredService<IKeywordIndex>();

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

                // Decide whether to use hybrid search based on keyword index availability
                bool useHybrid = await keywordIndex.IsAvailableAsync();

                List<HybridResult> hybridResults;
                if (useHybrid)
                {
                    hybridResults = await hybridSearch.SearchAsync(
                        queryEmbedding, expandedQuery, topK: searchTopK, filter: filter ?? new SearchFilter());
                }
                else
                {
                    // Fallback: pure vector search wrapped into HybridResult
                    var semanticResults = hasFilter
                        ? await database.SearchSimilarWithScoresAsync(queryEmbedding, filter!, topK: searchTopK)
                        : await database.SearchSimilarWithScoresAsync(queryEmbedding, topK: searchTopK);

                    hybridResults = semanticResults.Select(r => new HybridResult
                    {
                        Chunk = r.Chunk,
                        SemanticScore = r.Similarity,
                        KeywordScore = 0f,
                        HybridScore = r.Similarity,
                        FinalScore = r.Similarity
                    }).ToList();
                }

                var hybridScores = hybridResults.Select(r => r.HybridScore).ToList();
                var adaptiveMinScore = adaptiveThreshold.Compute(hybridScores, searchOptions.AdaptiveThreshold, query);

                var effectiveThreshold = Math.Max(searchOptions.MinimumSimilarity, adaptiveMinScore);

                var finalResults = hybridResults
                    .Where(r => r.HybridScore >= effectiveThreshold)
                    .Take(searchOptions.DisplayCount)
                    .ToList();

                var avgScore = hybridResults.Count > 0 ? hybridResults.Average(r => r.HybridScore) : 0;
                var maxScore = hybridResults.Count > 0 ? hybridResults.Max(r => r.HybridScore) : 0;

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

                    var weakMatches = hybridResults.Take(3).ToList();
                    Console.WriteLine($"Weak matches found (score < {effectiveThreshold:F2}):");
                    Console.WriteLine();
                    for (int i = 0; i < weakMatches.Count; i++)
                    {
                        var result = weakMatches[i];
                        Console.WriteLine($"{i + 1}. {result.Chunk.NamespaceName}.{result.Chunk.ClassName}.{result.Chunk.MemberName}");
                        Console.WriteLine($"   Score: {result.HybridScore:F4} (semantic={result.SemanticScore:F4}, keyword={result.KeywordScore:F4})");
                    }
                    Console.WriteLine();
                    Console.WriteLine($"Tip: Results may be less relevant. Try a more specific query.");
                    continue;
                }

                Console.WriteLine($"\nFound {finalResults.Count} results (filtered by score >= {effectiveThreshold:F2}):");
                Console.WriteLine(new string('=', 80));

                for (int i = 0; i < finalResults.Count; i++)
                {
                    var result = finalResults[i];
                    Console.WriteLine($"\n{i + 1}. {result.Chunk.NamespaceName}.{result.Chunk.ClassName}.{result.Chunk.MemberName}");
                    Console.WriteLine($"   Score: {result.HybridScore:F4} (semantic={result.SemanticScore:F4}, keyword={result.KeywordScore:F4})");
                    Console.WriteLine($"   Type: {result.Chunk.MemberType}");
                    Console.WriteLine($"   File: {result.Chunk.FilePath} (lines {result.Chunk.StartLine}-{result.Chunk.EndLine})");
                    Console.WriteLine($"   Signature: {result.Chunk.Signature.Split('\n')[0]}");

                    if (!string.IsNullOrWhiteSpace(result.Chunk.Documentation))
                    {
                        var doc = result.Chunk.Documentation.Replace("\n", " ").Replace("///", "").Trim();
                        if (doc.Length > 100)
                            doc = doc[..100] + "...";
                        Console.WriteLine($"   Documentation: {doc}");
                    }

                    Console.WriteLine($"   Content Preview:");
                    var contentPreview = result.Chunk.Content.Replace("\n", " ").Replace("\r", "");
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
