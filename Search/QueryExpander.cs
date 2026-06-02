using Microsoft.Extensions.Configuration;

namespace SemanticSourceCode.Search;

public class QueryExpander : IQueryExpander
{
    private readonly Dictionary<string, string[]> _expansions;

    public QueryExpander(IConfiguration? configuration = null)
    {
        _expansions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        // Try to load from configuration
        var configSection = configuration?.GetSection("QueryExpansion");
        if (configSection?.GetChildren().Any() == true)
        {
            foreach (var child in configSection.GetChildren())
            {
                var value = child.Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var synonyms = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                        .Select(s => s.Trim())
                                        .Where(s => !string.IsNullOrEmpty(s))
                                        .ToArray();
                    if (synonyms.Length > 0)
                    {
                        _expansions[child.Key] = synonyms;
                    }
                }
            }
        }

        // Fallback: apply hardcoded defaults for keys not in config
        var defaults = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["db"] = new[] { "database", "sql", "entity framework" },
            ["http"] = new[] { "web", "api", "rest", "endpoint" },
            ["async"] = new[] { "asynchronous", "task", "background" },
            ["sensor"] = new[] { "ultrasonic", "distance", "color", "gyro" },
            ["file"] = new[] { "io", "read", "write", "stream" }
        };

        foreach (var (key, synonyms) in defaults)
        {
            if (!_expansions.ContainsKey(key))
            {
                _expansions[key] = synonyms;
            }
        }
    }

    public string Expand(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Select(w => w.ToLowerInvariant())
                         .ToArray();
        var expanded = new List<string>(words);

        foreach (var word in words)
        {
            if (_expansions.TryGetValue(word, out var synonyms))
            {
                expanded.AddRange(synonyms);
            }
        }

        return string.Join(" ", expanded.Distinct());
    }
}
