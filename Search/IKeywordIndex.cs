using SemanticSourceCode.Models;

namespace SemanticSourceCode.Search;

/// <summary>
/// Service for keyword-based indexing and searching of code chunks.
/// </summary>
public interface IKeywordIndex
{
    /// <summary>
    /// Indexes a single code chunk into the keyword index.
    /// </summary>
    Task IndexChunkAsync(CodeChunk chunk);

    /// <summary>
    /// Indexes multiple code chunks into the keyword index.
    /// </summary>
    Task IndexChunksAsync(IEnumerable<CodeChunk> chunks);

    /// <summary>
    /// Searches for keyword matches in the index.
    /// Returns chunks with a normalized keyword score [0, 1].
    /// </summary>
    Task<List<(CodeChunk Chunk, float Score)>> SearchKeywordMatchesAsync(string query, int topK = 20);

    /// <summary>
    /// Checks if the keyword index table exists and is accessible.
    /// </summary>
    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Clears all data from the keyword index.
    /// </summary>
    Task ClearAsync();
}
