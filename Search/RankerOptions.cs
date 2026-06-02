namespace SemanticSourceCode.Search;

public class RankerOptions
{
    public float ClassNameBoost { get; set; } = 1.3f;
    public float MemberNameBoost { get; set; } = 1.0f;
    public float ControllerBoost { get; set; } = 1.1f;
    public float ServiceBoost { get; set; } = 1.1f;
    public float MiddlewareBoost { get; set; } = 1.1f;
    public float DocumentationBoost { get; set; } = 1.05f;
    public int SmallFileLineThreshold { get; set; } = 10;
    public float SmallFilePenalty { get; set; } = 0.9f;
}
