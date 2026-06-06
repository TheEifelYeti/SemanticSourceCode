using SemanticSourceCode.Models;
using System.Globalization;

namespace SemanticSourceCode.Search;

/// <summary>
/// Suggests alternative queries based on Levenshtein distance and index analysis.
/// </summary>
public interface IQuerySuggester
{
    IReadOnlyList<string> Suggest(string query, IReadOnlyList<CodeChunk> index);
}

/// <summary>
/// Default implementation of query suggestions using Levenshtein distance.
/// </summary>
public class QuerySuggester : IQuerySuggester
{
    private readonly Dictionary<string, int> _termFrequencies = new();
    private DateTime _lastCacheUpdate = DateTime.MinValue;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    public IReadOnlyList<string> Suggest(string query, IReadOnlyList<CodeChunk> index)
    {
        if (string.IsNullOrWhiteSpace(query) || index.Count == 0)
            return Array.Empty<string>();

        var normalizedQuery = query.Trim().ToLowerInvariant();
        var queryTerms = Tokenize(normalizedQuery);
        var queryTokens = SplitCamelCase(query.Trim()).Select(t => t.ToLowerInvariant()).ToArray(); // Split CamelCase from original query
        queryTerms = queryTerms.Concat(queryTokens).Distinct().Where(t => t.Length >= 2).ToArray();

        // Build candidate list from known names
        var candidates = new HashSet<string>();
        foreach (var chunk in index)
        {
            if (!string.IsNullOrWhiteSpace(chunk.ClassName))
                candidates.Add(chunk.ClassName);
            if (!string.IsNullOrWhiteSpace(chunk.MemberName))
                candidates.Add(chunk.MemberName);
            if (!string.IsNullOrWhiteSpace(chunk.NamespaceName))
                candidates.Add(chunk.NamespaceName);
        }

        // Score candidates by Levenshtein distance
        var scored = new List<(string Term, int Distance, int LengthDiff)>();
        foreach (var candidate in candidates)
        {
            var normCandidate = candidate.Trim().ToLowerInvariant();
            
            // Skip exact matches for full candidate name
            if (normCandidate == normalizedQuery) continue;

            // Compute minimum distance over all tokenizations
            int distance = ComputeLevenshteinDistance(normalizedQuery, normCandidate);
            
            // Also check CamelCase tokens of candidate against the FULL query (not query tokens)
            // This handles "DataBase" vs "DatabaseService" where query matches "database" token
            foreach (var token in SplitCamelCase(candidate.Trim()))
            {
                var lowerToken = token.ToLowerInvariant();
                int tokenDistance = ComputeLevenshteinDistance(normalizedQuery, lowerToken);
                if (tokenDistance < distance) distance = tokenDistance;
            }

            int lengthDiff = Math.Abs(normalizedQuery.Length - normCandidate.Length);

            // Only suggest if reasonably close
            int maxAllowedDistance = Math.Max(2, normalizedQuery.Length / 3);
            if (distance <= maxAllowedDistance)
            {
                scored.Add((candidate, distance, lengthDiff));
            }
        }

        // Return top 3, sorted by distance then by length similarity
        return scored
            .OrderBy(s => s.Distance)
            .ThenBy(s => s.LengthDiff)
            .Take(3)
            .Select(s => s.Term)
            .ToList();
    }

    /// <summary>
    /// Tokenizes a string into words, including CamelCase splitting.
    /// </summary>
    private static string[] Tokenize(string text)
    {
        return text
            .Replace("_", " ")
            .Replace(".", " ")
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(SplitCamelCase)
            .ToArray();
    }

    private static IEnumerable<string> SplitCamelCase(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var words = new List<string>();
        int start = 0;
        for (int i = 1; i < text.Length; i++)
        {
            if (char.IsUpper(text[i]) && i > 0 && char.IsLower(text[i - 1]))
            {
                words.Add(text[start..i].ToLowerInvariant());
                start = i;
            }
        }
        words.Add(text[start..].ToLowerInvariant());

        foreach (var word in words)
        {
            if (word.Length >= 2) yield return word;
        }
    }

    /// <summary>
    /// Computes Levenshtein distance between two strings.
    /// </summary>
    public static int ComputeLevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 0 : b.Length;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var distances = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++) distances[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) distances[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + cost);
            }
        }

        return distances[a.Length, b.Length];
    }
}
