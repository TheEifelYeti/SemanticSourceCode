namespace SemanticSourceCode.Models;

/// <summary>
/// Configuration options for chunking code into searchable segments.
/// </summary>
public class ChunkingOptions
{
    /// <summary>
    /// Maximum number of lines per chunk. Methods longer than this will be split.
    /// Default: 50
    /// </summary>
    public int MaxMethodChunkLines { get; set; } = 50;

    /// <summary>
    /// Number of overlapping lines between consecutive chunks.
    /// Helps with context preservation for searches.
    /// Default: 10
    /// </summary>
    public int OverlapLines { get; set; } = 10;
}
