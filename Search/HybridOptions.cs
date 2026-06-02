namespace SemanticSourceCode.Search;

/// <summary>
/// Configuration options for hybrid search (semantic + keyword).
/// </summary>
public class HybridOptions
{
    /// <summary>
    /// Weight for semantic (embedding) score. Default: 0.7.
    /// Keyword weight is implicitly (1 - SemanticWeight).
    /// </summary>
    public float SemanticWeight { get; set; } = 0.7f;

    /// <summary>
    /// Weight for keyword score. Computed as (1 - SemanticWeight).
    /// </summary>
    public float KeywordWeight => 1.0f - SemanticWeight;
}
