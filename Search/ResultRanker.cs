namespace SemanticSourceCode.Search;

/// <summary>
/// Re-ranks search results using structural signals from code metadata.
/// Purely functional and testable — no external dependencies.
/// </summary>
public class ResultRanker : IResultRanker
{
    public List<HybridResult> Rank(List<HybridResult> results, string query, RankerOptions options)
    {
        if (results.Count == 0)
            return results;

        var queryTerms = TokenizeQuery(query);

        foreach (var result in results)
        {
            float boost = 1.0f;

            // Boost for ClassName/MemberName matches
            if (queryTerms.Any(term =>
                result.Chunk.ClassName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                result.Chunk.MemberName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                boost *= options.ClassNameBoost;
            }

            // Boost for controllers, services, middleware
            if (result.Chunk.IsController)
                boost *= options.ControllerBoost;
            if (result.Chunk.IsService)
                boost *= options.ServiceBoost;
            if (result.Chunk.IsMiddleware)
                boost *= options.MiddlewareBoost;

            // Boost for documentation
            if (!string.IsNullOrWhiteSpace(result.Chunk.Documentation))
                boost *= options.DocumentationBoost;

            // Penalty for very small files (often private helpers)
            var lineCount = result.Chunk.EndLine - result.Chunk.StartLine + 1;
            if (lineCount <= options.SmallFileLineThreshold)
                boost *= options.SmallFilePenalty;

            // Document-length boost: favor focused, shorter chunks
            var contentLength = result.Chunk.Content.Length;
            if (contentLength > 0)
            {
                // Normalize to a reasonable range and boost shorter docs slightly
                var lengthBoost = Math.Min(1.0f, 500f / contentLength);
                boost *= Math.Max(0.8f, lengthBoost);
            }

            result.FinalScore = result.HybridScore * boost;
        }

        return results
            .OrderByDescending(r => r.FinalScore)
            .ToList();
    }

    private static List<string> TokenizeQuery(string query)
    {
        return query
            .Split(new[] { ' ', '_', '.', '/', '(', ')', '[', ']', '{', '}', ';', ',', ':', '"', '\'', '\t', '\n', '\r', '<', '>', '=', '+', '-', '*', '&', '|', '!', '?', '@', '#', '$', '%', '^', '~', '`' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToList();
    }
}
