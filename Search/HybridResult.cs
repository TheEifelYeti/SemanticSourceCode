using SemanticSourceCode.Models;

namespace SemanticSourceCode.Search;

/// <summary>
/// A search result combining semantic, keyword, and re-ranked scores.
/// </summary>
public class HybridResult
{
    public CodeChunk Chunk { get; set; } = null!;
    public float SemanticScore { get; set; }
    public float KeywordScore { get; set; }
    public float HybridScore { get; set; }
    public float FinalScore { get; set; }
}
