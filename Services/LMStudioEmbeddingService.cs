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
    private readonly int _batchSize;
    private readonly ILogger<LMStudioEmbeddingService>? _logger;

    /// <summary>
    /// Initializes a new instance of the LM Studio embedding service.
    /// Verifies that LM Studio is running and a model is loaded.
    /// </summary>
    /// <param name="configuration">Application configuration containing LM Studio settings.</param>
    /// <param name="logger">Optional logger for operation tracking.</param>
    public LMStudioEmbeddingService(IConfiguration configuration, ILogger<LMStudioEmbeddingService>? logger = null)
        : this(configuration, logger, new HttpClient())
    {
    }

    /// <summary>
    /// Initializes a new instance with an externally provided HttpClient (for testing).
    /// </summary>
    public LMStudioEmbeddingService(IConfiguration configuration, ILogger<LMStudioEmbeddingService>? logger, HttpClient httpClient)
    {
        _logger = logger;

        var baseUrl = configuration["LMStudio:BaseUrl"] ?? "http://localhost:1234";
        _embeddingModel = configuration["LMStudio:EmbeddingModel"] ?? "";

        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // Batch size for /v1/embeddings batched requests. Default 32.
        var batchSizeStr = configuration["Embedding:BatchSize"];
        _batchSize = int.TryParse(batchSizeStr, out var parsed) && parsed > 0 ? parsed : 32;

        // Verify LM Studio is running and has a model loaded
        VerifyModelLoadedAsync().GetAwaiter().GetResult();

        _logger?.LogInformation("LM Studio embedding service initialized with model: {Model} (batch size: {BatchSize})",
            _embeddingModel, _batchSize);
    }

    /// <summary>
    /// Checks whether LM Studio is reachable and has a model loaded.
    /// Returns a result tuple — does not throw.
    /// </summary>
    public static async Task<(bool IsRunning, bool HasModel, string? SelectedModel, string? ErrorMessage)> IsAvailableAsync(IConfiguration configuration, HttpClient? httpClient = null)
    {
        var baseUrl = configuration["LMStudio:BaseUrl"] ?? "http://localhost:1234";
        var configuredModel = configuration["LMStudio:EmbeddingModel"] ?? "";

        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.BaseAddress = new Uri(baseUrl);

        try
        {
            var response = await client.GetAsync("/v1/models");
            if (!response.IsSuccessStatusCode)
                return (false, false, null, $"LM Studio not responding. Status: {response.StatusCode}");

            var responseText = await response.Content.ReadAsStringAsync();
            var loadedModels = ExtractModelIds(responseText);

            if (loadedModels.Count == 0)
                return (true, false, null, "LM Studio erreichbar, aber kein Modell geladen. Bitte lade ein Embedding-Modell im Developer-Tab.");

            var selectedModel = loadedModels.FirstOrDefault(m => m.Equals(configuredModel, StringComparison.OrdinalIgnoreCase))
                                ?? loadedModels.First();

            return (true, true, selectedModel, null);
        }
        catch (Exception ex)
        {
            return (false, false, null, $"LM Studio nicht erreichbar: {ex.Message}");
        }
    }

    private static List<string> ExtractModelIds(string responseText)
    {
        var loadedModels = new List<string>();
        try
        {
            var modelsResponse = JsonSerializer.Deserialize<ModelsResponse>(responseText);
            loadedModels = modelsResponse?.Data?.Select(m => m.Id).Where(id => !string.IsNullOrEmpty(id)).ToList() ?? new List<string>();
        }
        catch { /* ignore parsing error, try fallback */ }

        if (loadedModels.Count == 0)
        {
            try
            {
                var doc = JsonDocument.Parse(responseText);
                if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idElement))
                        {
                            var id = idElement.GetString();
                            if (!string.IsNullOrEmpty(id)) loadedModels.Add(id);
                        }
                        else if (item.TryGetProperty("model", out var modelElement))
                        {
                            var model = modelElement.GetString();
                            if (!string.IsNullOrEmpty(model)) loadedModels.Add(model);
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }
        return loadedModels;
    }

    /// <summary>
    /// Verifies that LM Studio is running and has a model loaded.
    /// Tries to auto-detect the loaded model if none is configured.
    /// </summary>
    private async Task VerifyModelLoadedAsync()
    {
        var (isRunning, hasModel, selectedModel, errorMessage) = await IsAvailableAsync(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LMStudio:BaseUrl"] = _httpClient.BaseAddress?.ToString(),
                ["LMStudio:EmbeddingModel"] = _embeddingModel
            }).Build(),
            _httpClient);

        if (!isRunning)
        {
            throw new InvalidOperationException("Failed to connect to LM Studio. Please ensure LM Studio is running and the server is started.");
        }

        if (!hasModel)
        {
            throw new InvalidOperationException(errorMessage ?? "LM Studio has no models loaded. Please load a model in the developer page or use the 'lms load' command.");
        }

        _logger?.LogInformation("LM Studio has model(s) loaded. Using: {Model}", selectedModel);
        _embeddingModel = selectedModel!;
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
            _logger?.LogDebug("Using model: {Model}", _embeddingModel);
            
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
                
                if (errorBody.Contains("No models loaded"))
                {
                    throw new HttpRequestException(
                        $"LM Studio API error: {response.StatusCode} - {errorBody}\n\n" +
                        "The model is detected but LM Studio cannot use it for embeddings.\n" +
                        "Possible solutions:\n" +
                        "1. Ensure the loaded model supports embeddings (e.g., jina-embeddings, nomic-embed-text)\n" +
                        "2. Try reloading the model in LM Studio Developer page\n" +
                        "3. Update LM Studio to the latest version\n" +
                        "4. Use Ollama instead for embeddings: set 'Embedding:Provider' to 'ollama'");
                }
                
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
        var textList = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (textList.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        _logger?.LogInformation("Generating embeddings for {Count} texts (batch size: {BatchSize})",
            textList.Count, _batchSize);

        var allEmbeddings = new List<float[]>(textList.Count);

        for (int offset = 0; offset < textList.Count; offset += _batchSize)
        {
            var batch = textList.Skip(offset).Take(_batchSize).ToList();

            try
            {
                var batchEmbeddings = await SendBatchEmbeddingRequestAsync(batch, cancellationToken).ConfigureAwait(false);
                allEmbeddings.AddRange(batchEmbeddings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(
                    "LM Studio batch embedding failed ({Message}). Falling back to sequential single-text requests.",
                    ex.Message);
                var fallback = new List<float[]>(batch.Count);
                foreach (var text in batch)
                {
                    fallback.Add(await GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false));
                }
                allEmbeddings.AddRange(fallback);
            }
        }

        _logger?.LogInformation("Successfully generated {Count} embeddings", allEmbeddings.Count);
        return allEmbeddings.ToArray();
    }

    /// <summary>
    /// Sends one batched request to LM Studio's <c>/v1/embeddings</c> endpoint
    /// with <c>input</c> as a string array. Returns embeddings sorted by
    /// their <c>index</c> field (matching the input order).
    /// </summary>
    private async Task<float[][]> SendBatchEmbeddingRequestAsync(
        IReadOnlyList<string> batch,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            model = _embeddingModel,
            input = batch.ToArray()
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"LM Studio API error: {response.StatusCode} - {errorBody}");
        }

        var result = await response.Content
            .ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result?.Data == null || result.Data.Count != batch.Count)
        {
            throw new InvalidOperationException(
                $"LM Studio batch response shape mismatch: expected {batch.Count} items, got {result?.Data?.Count ?? 0}");
        }

        // LM Studio returns items ordered by their `index` field. Re-sort to
        // match input order, matching the contract of IEmbeddingService.
        return result.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding ?? Array.Empty<float>())
            .ToArray();
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
