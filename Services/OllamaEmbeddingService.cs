using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SemanticSourceCode.Services;

/// <summary>
/// Implements embedding generation using Ollama's local HTTP API.
/// Uses models like nomic-embed-text or mxbai-embed-large.
/// </summary>
public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _embeddingModel;
    private readonly ILogger<OllamaEmbeddingService>? _logger;

    /// <summary>
    /// Initializes a new instance of the Ollama embedding service.
    /// </summary>
    /// <param name="configuration">Application configuration containing Ollama settings.</param>
    /// <param name="logger">Optional logger for operation tracking.</param>
    public OllamaEmbeddingService(IConfiguration configuration, ILogger<OllamaEmbeddingService>? logger = null)
    {
        _logger = logger;
        var baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _embeddingModel = configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
        
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
        
        _logger?.LogInformation("Ollama embedding service initialized with model: {Model}", _embeddingModel);
    }

    /// <inheritdoc />
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger?.LogWarning("Attempted to generate embedding for empty text");
            return Array.Empty<float>();
        }

        var request = new
        {
            model = _embeddingModel,
            prompt = TruncateText(text, 8192)
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken);
            
            if (result?.Embedding == null)
            {
                _logger?.LogError("No embedding returned from Ollama");
                return Array.Empty<float>();
            }

            return result.Embedding;
        }
        catch (TaskCanceledException)
        {
            _logger?.LogWarning("Embedding generation was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate embedding for text");
            return Array.Empty<float>();
        }
    }

    /// <inheritdoc />
    public async Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        _logger?.LogInformation("Generating embeddings for {Count} texts", textList.Count);
        
        var embeddings = new List<float[]>();
        foreach (var text in textList)
        {
            var embedding = await GenerateEmbeddingAsync(text, cancellationToken);
            embeddings.Add(embedding);
            await Task.Delay(100, cancellationToken);
        }
        
        _logger?.LogInformation("Successfully generated {Count} embeddings", embeddings.Count);
        return embeddings.ToArray();
    }

    /// <inheritdoc />
    public Task<int> GetEmbeddingDimensionsAsync()
    {
        // Nomic embed text produces 768-dimensional embeddings
        return Task.FromResult(768);
    }

    private string TruncateText(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        _logger?.LogDebug("Truncating text from {OriginalLength} to {MaxLength} characters", text.Length, maxLength);
        return text[..maxLength];
    }

    private class OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }
}
