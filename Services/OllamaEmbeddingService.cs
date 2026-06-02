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
        : this(configuration, logger, new HttpClient())
    {
    }

    /// <summary>
    /// Initializes a new instance with an externally provided HttpClient (for testing).
    /// </summary>
    public OllamaEmbeddingService(IConfiguration configuration, ILogger<OllamaEmbeddingService>? logger, HttpClient httpClient)
    {
        _logger = logger;
        var baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var configuredModel = configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.Timeout = TimeSpan.FromMinutes(5);

        // Verify model availability and auto-select fallback
        _embeddingModel = VerifyAndSelectModelAsync(configuredModel).GetAwaiter().GetResult();

        _logger?.LogInformation("Ollama embedding service initialized with model: {Model}", _embeddingModel);
    }

    /// <summary>
    /// Checks whether Ollama is reachable and has an embedding model available.
    /// Returns a result tuple — does not throw.
    /// </summary>
    public static async Task<(bool IsRunning, string? SelectedModel, string? ErrorMessage)> IsAvailableAsync(IConfiguration configuration, HttpClient? httpClient = null)
    {
        var baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var configuredModel = configuration["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.BaseAddress = new Uri(baseUrl);

        try
        {
            var response = await client.GetAsync("/api/tags");
            if (!response.IsSuccessStatusCode)
                return (false, null, $"Ollama returned status {response.StatusCode}.");

            var result = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>();
            var availableModels = result?.Models?.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).ToList() ?? new List<string>();

            if (availableModels.Count == 0)
                return (true, null, "Ollama läuft, hat aber keine Modelle geladen.");

            // Exact match
            var exactMatch = availableModels.FirstOrDefault(m => m.Equals(configuredModel, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
                return (true, exactMatch, null);

            // Base-name match
            var configuredBase = configuredModel.Contains(":") ? configuredModel.Split(":")[0] : configuredModel;
            var tagMatch = availableModels.FirstOrDefault(m =>
            {
                var modelBase = m.Contains(":") ? m.Split(":")[0] : m;
                return modelBase.Equals(configuredBase, StringComparison.OrdinalIgnoreCase);
            });
            if (tagMatch != null)
                return (true, tagMatch, null);

            // Try known embedding models
            var embeddingModels = new[] { "nomic-embed-text", "mxbai-embed-large", "all-minilm", "bge-m3", "snowflake-arctic-embed" };
            foreach (var model in embeddingModels)
            {
                var found = availableModels.FirstOrDefault(m =>
                    m.Equals(model, StringComparison.OrdinalIgnoreCase) ||
                    m.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase));
                if (found != null)
                    return (true, found, null);
            }

            return (true, null, $"Ollama läuft, aber kein Embedding-Modell gefunden. Verfügbar: {string.Join(", ", availableModels)}");
        }
        catch (Exception ex)
        {
            return (false, null, $"Ollama nicht erreichbar: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if the configured model exists. If not, tries to find a suitable embedding model.
    /// </summary>
    private async Task<string> VerifyAndSelectModelAsync(string configuredModel)
    {
        var (isRunning, selectedModel, errorMessage) = await IsAvailableAsync(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = _httpClient.BaseAddress?.ToString(),
                ["Ollama:EmbeddingModel"] = configuredModel
            }).Build(),
            _httpClient);

        if (!isRunning)
        {
            var message = $"Cannot connect to Ollama at {_httpClient.BaseAddress}. " +
                          $"Please ensure Ollama is running. Error: {errorMessage}";
            _logger?.LogError(message);
            throw new InvalidOperationException(message);
        }

        if (selectedModel == null)
        {
            _logger?.LogError(errorMessage);
            throw new InvalidOperationException(errorMessage ?? "No embedding model available in Ollama.");
        }

        _logger?.LogInformation("Using Ollama model: {Model}", selectedModel);
        return selectedModel;
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
