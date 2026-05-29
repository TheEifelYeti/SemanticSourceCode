namespace SemanticSourceCode.Models;

public class CodeChunk
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FilePath { get; set; } = string.Empty;
    public string NamespaceName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MemberType { get; set; } = string.Empty; // Method, Property, Constructor, etc.
    public string Content { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string Documentation { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public byte[]? Embedding { get; set; }
    /// <summary>
    /// ASP.NET route information (if applicable).
    /// </summary>
    public string? RouteTemplate { get; set; }

    /// <summary>
    /// HTTP methods (GET, POST, etc.) for controller actions.
    /// </summary>
    public string? HttpMethods { get; set; }

    /// <summary>
    /// Whether this is an ASP.NET controller.
    /// </summary>
    public bool IsController { get; set; }

    /// <summary>
    /// Whether this is a DI service.
    /// </summary>
    public bool IsService { get; set; }

    /// <summary>
    /// Whether this is middleware.
    /// </summary>
    public bool IsMiddleware { get; set; }

    /// <summary>
    /// IDs of chunks that this chunk calls (call graph).
    /// </summary>
    public List<string> CallsTo { get; set; } = new();

    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
}
