using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;

namespace SemanticSourceCode.Cli;

public static class SearchCommand
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Entry point for `--mode search [--query Q]`.
    /// With `--query` (or `-q`), runs one round of search and exits (non-interactive / one-shot).
    /// Without it, starts the interactive read-eval-print loop.
    /// </summary>
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
            Console.Error.WriteLine("Database not initialized. Run index mode first.");
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
            Console.Error.WriteLine($"Active filters: {DescribeFilter(filter!)}");
        }

        // Parse one-shot query and output format
        var oneShotQuery = CliArguments.GetValue(args, "--query") ?? CliArguments.GetValue(args, "-q");
        bool isOneShot = !string.IsNullOrWhiteSpace(oneShotQuery);

        var outputFormat = ParseFormat(args);
        if (outputFormat == null)
        {
            Console.Error.WriteLine($"Invalid --format value. Supported: text, json, quiet");
            return 1;
        }
        int displayLimit = ParseLimit(args) ?? searchOptions.DisplayCount;

        if (isOneShot)
        {
            // Non-interactive: one round, print, exit.
            var result = await ExecuteSearchAsync(
                oneShotQuery!, services, queryExpander, embeddingService, database,
                hybridSearch, keywordIndex, adaptiveThreshold, querySuggester,
                searchOptions, filter, searchTopK, displayLimit, logger);

            PrintResults(result, outputFormat, displayLimit);
            return result.HasResults ? 0 : 1;
        }

        // Interactive mode
        Console.WriteLine("Semantic Code Search");
        Console.WriteLine("====================");

        while (true)
        {
            Console.WriteLine();
            Console.Write("Enter search query (or 'quit' to exit): ");
            var query = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(query) || query.ToLowerInvariant() == "quit")
                break;

            var result = await ExecuteSearchAsync(
                query, services, queryExpander, embeddingService, database,
                hybridSearch, keywordIndex, adaptiveThreshold, querySuggester,
                searchOptions, filter, searchTopK, displayLimit, logger);

            PrintInteractive(result);
        }

        Console.WriteLine("Goodbye!");
        return 0;
    }

    /// <summary>
    /// Runs one full search cycle for a single query and returns a <see cref="SearchResult"/>.
    /// Used by both the interactive loop and the one-shot mode.
    /// </summary>
    public static async Task<SearchResult> ExecuteSearchAsync(
        string query,
        IServiceProvider services,
        IQueryExpander queryExpander,
        IEmbeddingService embeddingService,
        IVectorDatabase database,
        IHybridSearchService hybridSearch,
        IKeywordIndex keywordIndex,
        IAdaptiveThreshold adaptiveThreshold,
        IQuerySuggester querySuggester,
        SearchOptions searchOptions,
        SearchFilter? filter,
        int searchTopK,
        int displayLimit,
        ILogger logger)
    {
        var result = new SearchResult { Query = query };
        bool hasFilter = filter != null && !filter.IsEmpty;

        try
        {
            result.ExpandedQuery = queryExpander.Expand(query);

            var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(result.ExpandedQuery);

            // Decide whether to use hybrid search based on keyword index availability
            bool useHybrid = await keywordIndex.IsAvailableAsync();

            List<HybridResult> hybridResults;
            if (useHybrid)
            {
                hybridResults = await hybridSearch.SearchAsync(
                    queryEmbedding, result.ExpandedQuery, topK: searchTopK, filter: filter ?? new SearchFilter());
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
            result.EffectiveThreshold = effectiveThreshold;

            result.Results = hybridResults
                .Where(r => r.HybridScore >= effectiveThreshold)
                .Take(displayLimit)
                .ToList();

            // If we found no results, try to suggest alternative queries (only when no filter)
            if (result.Results.Count == 0 && hybridResults.Count > 0
                && hybridResults.Max(r => r.HybridScore) >= searchOptions.WeakMatchThreshold
                && !hasFilter)
            {
                var allChunks = await database.GetAllChunksAsync();
                result.Suggestions = querySuggester.Suggest(query, allChunks).ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during search");
        }

        return result;
    }

    /// <summary>
    /// Prints the result using the requested output format. Stdout-friendly (status messages
    /// go to stderr, so JSON/quiet output can be piped cleanly).
    /// </summary>
    public static void PrintResults(SearchResult result, string format, int limit)
    {
        switch (format.ToLowerInvariant())
        {
            case "json":
                PrintJson(result);
                break;
            case "quiet":
                PrintQuiet(result);
                break;
            case "text":
            default:
                PrintText(result, limit, writeToStdout: true);
                break;
        }
    }

    private static void PrintInteractive(SearchResult result)
    {
        // For interactive mode, mimic the original behaviour:
        //  - Use Console.WriteLine so the user sees the output
        //  - If weak matches and suggestions exist, print them too
        PrintText(result, displayLimit: result.ResultCount, writeToStdout: true, includeWeakMatchHint: true);
    }

    private static void PrintText(SearchResult result, int displayLimit, bool writeToStdout, bool includeWeakMatchHint = false)
    {
        var writer = writeToStdout ? Console.Out : Console.Error;

        writer.WriteLine();
        writer.WriteLine($"Found {result.ResultCount} result(s) for \"{result.Query}\" (score >= {result.EffectiveThreshold:F2}):");
        writer.WriteLine(new string('=', 80));

        if (result.ResultCount == 0)
        {
            writer.WriteLine("(no results)");
            if (result.Suggestions.Count > 0 && includeWeakMatchHint)
            {
                writer.WriteLine();
                writer.WriteLine($"Meintest du: {string.Join(", ", result.Suggestions)}?");
            }
            return;
        }

        for (int i = 0; i < result.Results.Count; i++)
        {
            var r = result.Results[i];
            writer.WriteLine();
            writer.WriteLine($"{i + 1}. {r.Chunk.NamespaceName}.{r.Chunk.ClassName}.{r.Chunk.MemberName}");
            writer.WriteLine($"   Score: {r.HybridScore:F4} (semantic={r.SemanticScore:F4}, keyword={r.KeywordScore:F4})");
            writer.WriteLine($"   Type: {r.Chunk.MemberType}");
            writer.WriteLine($"   File: {r.Chunk.FilePath} (lines {r.Chunk.StartLine}-{r.Chunk.EndLine})");
            writer.WriteLine($"   Signature: {r.Chunk.Signature.Split('\n')[0]}");

            if (!string.IsNullOrWhiteSpace(r.Chunk.Documentation))
            {
                var doc = r.Chunk.Documentation.Replace("\n", " ").Replace("///", "").Trim();
                if (doc.Length > 100)
                    doc = doc[..100] + "...";
                writer.WriteLine($"   Documentation: {doc}");
            }

            writer.WriteLine($"   Content Preview:");
            var contentPreview = r.Chunk.Content.Replace("\n", " ").Replace("\r", "");
            if (contentPreview.Length > 200)
                contentPreview = contentPreview[..200] + "...";
            writer.WriteLine($"   {contentPreview}");
        }
    }

    private static void PrintJson(SearchResult result)
    {
        // Use a thin DTO for serialization so we don't leak the HybridResult internals
        var dto = new
        {
            query = result.Query,
            expandedQuery = result.ExpandedQuery,
            effectiveThreshold = result.EffectiveThreshold,
            resultCount = result.ResultCount,
            suggestions = result.Suggestions,
            results = result.Results.Select(r => new
            {
                memberName = r.Chunk.MemberName,
                className = r.Chunk.ClassName,
                namespaceName = r.Chunk.NamespaceName,
                memberType = r.Chunk.MemberType,
                filePath = r.Chunk.FilePath,
                startLine = r.Chunk.StartLine,
                endLine = r.Chunk.EndLine,
                signature = r.Chunk.Signature,
                content = r.Chunk.Content,
                score = r.HybridScore,
                semanticScore = r.SemanticScore,
                keywordScore = r.KeywordScore
            }).ToArray()
        };

        Console.WriteLine(JsonSerializer.Serialize(dto, _jsonOptions));
    }

    private static void PrintQuiet(SearchResult result)
    {
        if (result.ResultCount == 0)
        {
            // No top-1 to print, but still exit cleanly
            return;
        }
        var top = result.Results[0];
        Console.WriteLine($"{top.Chunk.MemberName} — {top.Chunk.FilePath}:{top.Chunk.StartLine} (score: {top.HybridScore:F3})");
    }

    private static string? ParseFormat(string[] args)
    {
        var format = CliArguments.GetValue(args, "--format") ?? CliArguments.GetValue(args, "-f");
        if (format == null)
        {
            // Check --quiet shorthand
            if (CliArguments.HasFlag(args, "--quiet"))
            {
                return "quiet";
            }
            return "text";
        }
        return format.ToLowerInvariant() switch
        {
            "text" or "json" or "quiet" => format.ToLowerInvariant(),
            _ => null
        };
    }

    private static int? ParseLimit(string[] args)
    {
        var limitStr = CliArguments.GetValue(args, "--limit") ?? CliArguments.GetValue(args, "-l");
        if (limitStr == null) return null;
        return int.TryParse(limitStr, out var n) && n > 0 ? n : null;
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
