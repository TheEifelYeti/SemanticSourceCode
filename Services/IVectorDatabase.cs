using SemanticSourceCode.Models;

namespace SemanticSourceCode.Services;

/// <summary>
/// Stores and queries code chunks with their vector embeddings.
/// Provides semantic similarity search using cosine similarity.
/// </summary>
public interface IVectorDatabase
{
    /// <summary>
    /// Initializes the database, creating tables and indexes if they don't exist.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task InitializeAsync();

    /// <summary>
    /// Inserts a single code chunk into the database.
    /// If a chunk with the same ID already exists, it is replaced.
    /// </summary>
    /// <param name="chunk">The code chunk to insert.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the database has not been initialized.</exception>
    Task InsertChunkAsync(CodeChunk chunk);

    /// <summary>
    /// Inserts multiple code chunks into the database.
    /// </summary>
    /// <param name="chunks">The collection of code chunks to insert.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the database has not been initialized.</exception>
    Task InsertChunksAsync(IEnumerable<CodeChunk> chunks);

    /// <summary>
    /// Searches for code chunks similar to the query embedding.
    /// Uses cosine similarity to rank results.
    /// Only chunks with embeddings are returned.
    /// </summary>
    /// <param name="queryEmbedding">The embedding vector to search for.</param>
    /// <param name="topK">The maximum number of results to return. Default is 5.</param>
    /// <returns>A list of the most similar code chunks, ordered by similarity (highest first).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the database has not been initialized.</exception>
    Task<List<CodeChunk>> SearchSimilarAsync(float[] queryEmbedding, int topK = 5);

    /// <summary>
    /// Clears all data from the database.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the database has not been initialized.</exception>
    Task ClearDatabaseAsync();

    /// <summary>
    /// Checks if the database has been initialized.
    /// </summary>
    /// <returns>True if the database is initialized; otherwise, false.</returns>
    Task<bool> IsInitializedAsync();
}
