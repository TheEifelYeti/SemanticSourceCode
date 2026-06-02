namespace SemanticSourceCode.Search;

/// <summary>
/// Re-ranks search results using structural signals and metadata.
/// </summary>
public interface IResultRanker
{
    /// <summary>
    /// Re-ranks hybrid search results based on structural signals.
    /// </summary>
    List<HybridResult> Rank(List<HybridResult> results, string query, RankerOptions options);
}
