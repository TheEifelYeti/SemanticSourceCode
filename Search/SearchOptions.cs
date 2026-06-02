namespace SemanticSourceCode.Search;

public class SearchOptions
{
    public float MinimumSimilarity { get; set; } = 0.70f;
    public int TopK { get; set; } = 20;
    public int DisplayCount { get; set; } = 5;
    public float WeakMatchThreshold { get; set; } = 0.30f;
}
