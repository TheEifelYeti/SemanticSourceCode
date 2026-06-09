using System.Text.Json.Serialization;
using SemanticSourceCode.Models;

namespace SemanticSourceCode.Mcp;

/// <summary>
/// A serializable subset of <see cref="CodeChunk"/> used in MCP tool responses.
/// Embedding bytes are intentionally omitted (they are large, ~3 KB per chunk,
/// and useless to an AI agent that only needs the source code).
/// </summary>
public class McpCodeChunkDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("memberName")]
    public string MemberName { get; set; } = string.Empty;

    [JsonPropertyName("memberType")]
    public string MemberType { get; set; } = string.Empty;

    [JsonPropertyName("className")]
    public string ClassName { get; set; } = string.Empty;

    [JsonPropertyName("namespaceName")]
    public string NamespaceName { get; set; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("documentation")]
    public string Documentation { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public float Score { get; set; }

    [JsonPropertyName("semanticScore")]
    public float SemanticScore { get; set; }

    [JsonPropertyName("keywordScore")]
    public float KeywordScore { get; set; }

    public static McpCodeChunkDto FromHybridResult(Search.HybridResult r)
    {
        return new McpCodeChunkDto
        {
            Id = r.Chunk.Id,
            MemberName = r.Chunk.MemberName,
            MemberType = r.Chunk.MemberType,
            ClassName = r.Chunk.ClassName,
            NamespaceName = r.Chunk.NamespaceName,
            FilePath = r.Chunk.FilePath,
            StartLine = r.Chunk.StartLine,
            EndLine = r.Chunk.EndLine,
            Signature = r.Chunk.Signature,
            Content = r.Chunk.Content,
            Documentation = r.Chunk.Documentation,
            Score = r.HybridScore,
            SemanticScore = r.SemanticScore,
            KeywordScore = r.KeywordScore
        };
    }

    public static McpCodeChunkDto? FromCodeChunk(CodeChunk? c)
    {
        if (c == null) return null;
        return new McpCodeChunkDto
        {
            Id = c.Id,
            MemberName = c.MemberName,
            MemberType = c.MemberType,
            ClassName = c.ClassName,
            NamespaceName = c.NamespaceName,
            FilePath = c.FilePath,
            StartLine = c.StartLine,
            EndLine = c.EndLine,
            Signature = c.Signature,
            Content = c.Content,
            Documentation = c.Documentation
        };
    }
}
