using SemanticSourceCode.Models;

namespace SemanticSourceCode.Search;

/// <summary>
/// Provides access to indexed chunks for suggestion and analysis purposes.
/// </summary>
public interface IChunkIndexAccessor
{
    /// <summary>
    /// Retrieves all indexed chunks for analysis (e.g., query suggestions).
    /// </summary>
    Task<IReadOnlyList<CodeChunk>> GetAllChunksAsync(CancellationToken ct = default);
}
