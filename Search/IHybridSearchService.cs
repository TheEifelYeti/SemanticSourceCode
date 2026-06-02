using SemanticSourceCode.Models;

namespace SemanticSourceCode.Search;

/// <summary>
/// Combines semantic and keyword search into a unified hybrid search.
/// </summary>
public interface IHybridSearchService
{
    /// <summary>
    /// Performs hybrid search using both semantic (embedding) and keyword signals.
    /// </summary>
    Task<List<HybridResult>> SearchAsync(
        float[] queryEmbedding,
        string query,
        int topK = 20,
        SearchFilter? filter = null,
        HybridOptions? hybridOptions = null,
        RankerOptions? rankerOptions = null);
}
