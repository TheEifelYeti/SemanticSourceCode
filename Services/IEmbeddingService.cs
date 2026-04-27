namespace SemanticSourceCode.Services;

/// <summary>
/// Generates vector embeddings from text using an embedding model.
/// Typically uses Ollama with models like nomic-embed-text or mxbai-embed-large.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates a vector embedding for a single text.
    /// </summary>
    /// <param name="text">The text to embed. Must not be null or empty.</param>
    /// <returns>A float array representing the embedding vector.
    /// Returns an empty array if the input text is null or whitespace.</returns>
    /// <exception cref="HttpRequestException">Thrown when the embedding service is unavailable.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the embedding generation fails.</exception>
    Task<float[]> GenerateEmbeddingAsync(string text);

    /// <summary>
    /// Generates vector embeddings for multiple texts.
    /// Processes texts sequentially with a small delay between requests.
    /// </summary>
    /// <param name="texts">The list of texts to embed.</param>
    /// <returns>A list of float arrays, one for each input text.
    /// Empty or whitespace texts return empty arrays.</returns>
    /// <exception cref="HttpRequestException">Thrown when the embedding service is unavailable.</exception>
    Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts);
}
