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
    private string _embeddingModel;
    private readonly ILogger<LMStudioEmbeddingService>? _logger;

    /// <summary>
    /// Initializes a new instance of the LM Studio embedding service.
    /// Verifies that LM Studio is running and a model is loaded.
    /// </summary>
    /// <param name="configuration">Application configuration containing LM Studio settings.</param>
    /// <param name="logger">Optional logger for operation tracking.</param>
    public LMStudioEmbeddingService(IConfiguration configuration, ILogger<LMStudioEmbeddingService>? logger = null)
    {
        _logger = logger;
        
        var baseUrl = configuration["LMStudio:BaseUrl"] ?? "http://localhost:1234";
        _embeddingModel = configuration["LMStudio:EmbeddingModel"] ?? "";
        
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        // Verify LM Studio is running and has a model loaded
        VerifyModelLoadedAsync().GetAwaiter().GetResult();
        
        _logger?.LogInformation("LM Studio embedding service initialized with model: {Model}", _embeddingModel);
    }

    /// <summary>
    /// Verifies that LM Studio is running and has a model loaded.
    /// Tries to auto-detect the loaded model if none is configured.
    /// </summary>
    private async Task VerifyModelLoadedAsync()
    {
        try
        {
            // Check if LM Studio is running
            var response = await _httpClient.GetAsync("/v1/models");
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"LM Studio is not responding. Status: {response.StatusCode}");
            }
            
            var responseText = await response.Content.ReadAsStringAsync();
            _logger?.LogDebug("LM Studio /v1/models response: {Response}", responseText);
            
            var modelsResponse = JsonSerializer.Deserialize<ModelsResponse>(responseText);
            var loadedModels = modelsResponse?.Data?.Select(m => m.Id).ToList() ?? new List<string>();
            
            if (loadedModels.Count == 0)
            {
                throw new InvalidOperationException(
                    "LM Studio has no models loaded. Please load a model in the developer page or use the 'lms load' command.");
            }
            
            _logger?.LogInformation("LM Studio has {Count} model(s) loaded: {Models}", loadedModels.Count, string.Join(", ", loadedModels));
            
            // Always use the first loaded model - LM Studio only supports one model at a time for embeddings
            var selectedModel = loadedModels.First();
            
            if (!string.IsNullOrEmpty(_embeddingModel) && !loadedModels.Any(m => m.Equals(_embeddingModel, StringComparison.OrdinalIgnoreCase)))
            {
                _logger?.LogWarning("Configured model '{Configured}' not found. Loaded models: {Loaded}. Using: {Selected}",
                    _embeddingModel, string.Join(", ", loadedModels), selectedModel);
            }
            else
            {
                _logger?.LogInformation("Using loaded model: {Model}", selectedModel);
            }
            
            _embeddingModel = selectedModel;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Failed to connect to LM Studio. Please ensure LM Studio is running and the server is started.", ex);
        }
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
            _logger?.LogDebug("Sending embedding request to LM Studio for text: {TextPreview}", text[..Math.Min(text.Length, 100)]);
            
            var request = new
            {
                model = _embeddingModel,
                input = text
            };

            var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger?.LogError("LM Studio returned HTTP {StatusCode}: {ErrorBody}", response.StatusCode, errorBody);
                throw new HttpRequestException($"LM Studio API error: {response.StatusCode} - {errorBody}");
            }

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken);
            
            if (result?.Data?.FirstOrDefault()?.Embedding is not { } embedding)
            {
                _logger?.LogError("Failed to parse embedding response from LM Studio. Response: {Response}", JsonSerializer.Serialize(result));
                throw new InvalidOperationException("LM Studio returned empty or invalid embedding");
            }

            _logger?.LogDebug("Successfully generated embedding with {Dimension} dimensions", embedding.Length);
            return embedding;
        }
        catch (TaskCanceledException)
        {
            _logger?.LogWarning("Embedding generation was cancelled or timed out");
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
    /// Response model for LM Studio models listing API.
    /// </summary>
    private class ModelsResponse
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }
        
        [JsonPropertyName("data")]
        public List<ModelInfo>? Data { get; set; }
    }

    /// <summary>
    /// Model information from LM Studio.
    /// </summary>
    private class ModelInfo
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        
        [JsonPropertyName("object")]
        public string? Object { get; set; }
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
