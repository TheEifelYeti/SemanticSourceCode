namespace SemanticSourceCode.Services;

/// <summary>
/// Defines a service for generating vector embeddings from text.
/// Supports multiple embedding providers including Ollama and LM Studio.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates a vector embedding for a single text.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A float array representing the embedding vector.</returns>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Generates vector embeddings for multiple texts.
    /// </summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An array of float arrays, one for each input text.</returns>
    Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the dimensionality of the embeddings produced by this service.
    /// </summary>
    /// <returns>The number of dimensions in the embedding vectors.</returns>
    Task<int> GetEmbeddingDimensionsAsync();
}
