using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SemanticSourceCode.Services;

/// <summary>
/// Implements embedding generation using LM Studio's local HTTP API.
/// Alternative to Ollama for local embedding generation.
/// </summary>
public class LMStudioEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _embeddingModel;
    private readonly ILogger<LMStudioEmbeddingService>? _logger;

    /// <summary>
    /// Initializes a new instance of the LM Studio embedding service.
    /// </summary>
    /// <param name="configuration">Application configuration containing LM Studio settings.</param>
    /// <param name="logger">Optional logger for operation tracking.</param>
    public LMStudioEmbeddingService(IConfiguration configuration, ILogger<LMStudioEmbeddingService>? logger = null)
    {
        _logger = logger;
        
        var baseUrl = configuration["LMStudio:BaseUrl"] ?? "http://localhost:1234";
        _embeddingModel = configuration["LMStudio:EmbeddingModel"] ?? "text-embedding-nomic-embed-text-v1.5";
        
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        _logger?.LogInformation("LM Studio embedding service initialized with model: {Model}", _embeddingModel);
    }

    /// <inheritdoc />
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger?.LogWarning("Attempted to generate embedding for empty text");
            return Array.Empty<float>();
        }

        try
        {
            _logger?.LogDebug("Generating embedding for text of length {Length}", text.Length);
            
            var request = new
            {
                model = _embeddingModel,
                input = text
            };

            var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken);
            
            if (result?.Data?.FirstOrDefault()?.Embedding is not { } embedding)
            {
                _logger?.LogError("Failed to parse embedding response from LM Studio");
                return Array.Empty<float>();
            }

            _logger?.LogDebug("Successfully generated embedding with {Dimension} dimensions", embedding.Length);
            return embedding;
        }
        catch (TaskCanceledException)
        {
            _logger?.LogWarning("Embedding generation was cancelled or timed out");
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

    /// <summary>
    /// Response model for LM Studio embedding API.
    /// </summary>
    private class EmbeddingResponse
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }
        
        [JsonPropertyName("data")]
        public List<EmbeddingData>? Data { get; set; }
        
        [JsonPropertyName("model")]
        public string? Model { get; set; }
        
        [JsonPropertyName("usage")]
        public UsageInfo? Usage { get; set; }
    }

    /// <summary>
    /// Individual embedding data from the response.
    /// </summary>
    private class EmbeddingData
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }
        
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
        
        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    /// <summary>
    /// Token usage information.
    /// </summary>
    private class UsageInfo
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }
        
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
