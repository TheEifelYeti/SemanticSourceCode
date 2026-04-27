using SemanticSourceCode.Models;

namespace SemanticSourceCode.Services;

public interface IVectorDatabase
{
    Task InitializeAsync();
    Task InsertChunkAsync(CodeChunk chunk);
    Task InsertChunksAsync(IEnumerable<CodeChunk> chunks);
    Task<List<CodeChunk>> SearchSimilarAsync(float[] queryEmbedding, int topK = 5);
    Task ClearDatabaseAsync();
    Task<bool> IsInitializedAsync();
}
