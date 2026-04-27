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
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
}
