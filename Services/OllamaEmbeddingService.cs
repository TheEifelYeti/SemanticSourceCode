using System.Net.Http.Json;
using System.Text.Json;
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
        var configuredModel = configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
        
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
        
        // Verify model availability and auto-select fallback
        _embeddingModel = VerifyAndSelectModelAsync(configuredModel).GetAwaiter().GetResult();
        
        _logger?.LogInformation("Ollama embedding service initialized with model: {Model}", _embeddingModel);
    }

    /// <summary>
    /// Checks if the configured model exists. If not, tries to find a suitable embedding model.
    /// </summary>
    private async Task<string> VerifyAndSelectModelAsync(string configuredModel)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags");
            response.EnsureSuccessStatusCode();
            
            var result = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>();
            var availableModels = result?.Models?.Select(m => m.Name).ToList() ?? new List<string>();
            
            _logger?.LogDebug("Available Ollama models: {Models}", string.Join(", ", availableModels));
            
            // Check if configured model exists (exact match OR starts with configured model + ":")
            var exactMatch = availableModels.FirstOrDefault(m => m.Equals(configuredModel, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                _logger?.LogInformation("Using configured model: {Model}", exactMatch);
                return exactMatch;
            }
            
            // Check if configured model WITHOUT tag exists (e.g. "nomic-embed-text" matches "nomic-embed-text:latest")
            var configuredBase = configuredModel.Contains(":") ? configuredModel.Split(":")[0] : configuredModel;
            var tagMatch = availableModels.FirstOrDefault(m => {
                var modelBase = m.Contains(":") ? m.Split(":")[0] : m;
                return modelBase.Equals(configuredBase, StringComparison.OrdinalIgnoreCase);
            });
            if (tagMatch != null)
            {
                _logger?.LogInformation("Using model with matching base name: {Model} (configured: {Configured})", tagMatch, configuredModel);
                return tagMatch;
            }
            
            _logger?.LogWarning("Configured model '{ConfiguredModel}' not found. Looking for alternative...", configuredModel);
            
            // Try known embedding models
            var embeddingModels = new[] { "nomic-embed-text", "mxbai-embed-large", "all-minilm", "bge-m3", "snowflake-arctic-embed" };
            foreach (var model in embeddingModels)
            {
                if (availableModels.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase) || 
                                              m.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase)))
                {
                    var foundModel = availableModels.First(m => m.Equals(model, StringComparison.OrdinalIgnoreCase) || 
                                                                 m.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase));
                    _logger?.LogInformation("Auto-selected alternative embedding model: {Model}", foundModel);
                    return foundModel;
                }
            }
            
            // If no embedding model found, throw with helpful message
            var message = $"Ollama embedding model not found. Configured: '{configuredModel}'. " +
                          $"Available models: {string.Join(", ", availableModels)}. " +
                          $"Please install an embedding model with: ollama pull nomic-embed-text";
            _logger?.LogError(message);
            throw new InvalidOperationException(message);
        }
        catch (HttpRequestException ex)
        {
            var message = $"Cannot connect to Ollama at {_httpClient.BaseAddress}. " +
                          $"Please ensure Ollama is running. Error: {ex.Message}";
            _logger?.LogError(message);
            throw new InvalidOperationException(message, ex);
        }
    }

    private class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModel>? Models { get; set; }
    }

    private class OllamaModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
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
            _logger?.LogDebug("Sending embedding request to Ollama for text: {TextPreview}", text[..Math.Min(text.Length, 100)]);
            
            var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogError("Ollama returned HTTP {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
                throw new HttpRequestException($"Ollama API error: {response.StatusCode} - {errorBody}");
            }
            
            var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken);
            
            if (result?.Embedding == null)
            {
                _logger?.LogError("No embedding returned from Ollama. Response: {Response}", JsonSerializer.Serialize(result));
                throw new InvalidOperationException("Ollama returned empty embedding");
            }

            _logger?.LogDebug("Generated embedding with {Dimensions} dimensions", result.Embedding.Length);
            return result.Embedding;
        }
        catch (TaskCanceledException)
        {
            _logger?.LogWarning("Embedding generation was cancelled");
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not HttpRequestException)
        {
            _logger?.LogError(ex, "Failed to generate embedding for text");
            throw;
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
