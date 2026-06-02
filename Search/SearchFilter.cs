namespace SemanticSourceCode.Search;

/// <summary>
/// Filter options for refining search results by structural code properties.
/// </summary>
public class SearchFilter
{
    /// <summary>
    /// Namespace pattern (LIKE query, e.g. "Api.Controllers").
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Class name (exact or LIKE match).
    /// </summary>
    public string? ClassName { get; set; }

    /// <summary>
    /// HTTP method (GET, POST, PUT, DELETE, etc.).
    /// </summary>
    public string? HttpMethod { get; set; }

    /// <summary>
    /// File path glob pattern (e.g. "*/Controllers/*").
    /// </summary>
    public string? FilePathPattern { get; set; }

    /// <summary>
    /// Specific chunk type to filter by.
    /// </summary>
    public string? ChunkType { get; set; }

    /// <summary>
    /// Whether to include only controller chunks.
    /// </summary>
    public bool? IsController { get; set; }

    /// <summary>
    /// Whether to include only service chunks.
    /// </summary>
    public bool? IsService { get; set; }

    /// <summary>
    /// Whether to include only middleware chunks.
    /// </summary>
    public bool? IsMiddleware { get; set; }

    /// <summary>
    /// Returns true if no filters are set.
    /// </summary>
    public bool IsEmpty =>
        Namespace == null &&
        ClassName == null &&
        HttpMethod == null &&
        FilePathPattern == null &&
        ChunkType == null &&
        IsController == null &&
        IsService == null &&
        IsMiddleware == null;
}
