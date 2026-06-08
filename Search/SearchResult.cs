using System.Text.Json.Serialization;

namespace SemanticSourceCode.Search;

/// <summary>
/// Container for the outcome of a single semantic search run.
/// Used by both interactive and non-interactive (one-shot) modes
/// and serializable to JSON via <see cref="JsonSerializerDefaults"/>.
/// </summary>
public class SearchResult
{
    /// <summary>
    /// The original user query (as passed in).
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// The query after expansion (synonyms, related terms).
    /// </summary>
    public string ExpandedQuery { get; set; } = string.Empty;

    /// <summary>
    /// The effective similarity threshold that was applied.
    /// </summary>
    public float EffectiveThreshold { get; set; }

    /// <summary>
    /// True if at least one result was found above the effective threshold.
    /// </summary>
    [JsonIgnore]
    public bool HasResults => Results.Count > 0;

    /// <summary>
    /// Number of results that were returned.
    /// </summary>
    [JsonIgnore]
    public int ResultCount => Results.Count;

    /// <summary>
    /// The search results that passed the threshold filter.
    /// </summary>
    public List<HybridResult> Results { get; set; } = new();

    /// <summary>
    /// Suggestions for alternative queries (only populated when no results were found
    /// and no structural filter is active).
    /// </summary>
    public List<string> Suggestions { get; set; } = new();
}
