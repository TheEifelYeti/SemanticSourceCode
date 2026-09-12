using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SemanticSourceCode.Services;

/// <summary>
/// Generic embedding provider for any OpenAI-compatible <c>/v1/embeddings</c>
/// endpoint: llama.cpp (<c>llama-server</c>), vLLM, LM Studio, Ollama's
/// <c>/v1</c> API, or a remote endpoint you control (issue #24).
/// </summary>
public class OpenAICompatibleEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private string _embeddingModel;
    private readonly int _batchSize;
    private readonly ILogger<OpenAICompatibleEmbeddingService>? _logger;

    /// <summary>
    /// Initializes a new instance reading <c>OpenAICompatible:*</c> settings
    /// from configuration. <see cref="VerifyModelLoadedAsync"/> runs in the
    /// constructor so misconfiguration fails fast (same contract as the
    /// other providers).
    /// </summary>
    public OpenAICompatibleEmbeddingService(IConfiguration configuration, ILogger<OpenAICompatibleEmbeddingService>? logger = null)
        : this(configuration, logger, new HttpClient())
    {
    }

    /// <summary>
    /// Initializes a new instance with an externally provided HttpClient (for testing).
    /// </summary>
    public OpenAICompatibleEmbeddingService(IConfiguration configuration, ILogger<OpenAICompatibleEmbeddingService>? logger, HttpClient httpClient)
    {
        _logger = logger;

        var baseUrl = configuration["OpenAICompatible:BaseUrl"] ?? "http://localhost:8080";
        _embeddingModel = configuration["OpenAICompatible:EmbeddingModel"] ?? "";

        _httpClient = httpClient;
        // Normalize: the caller may configure "http://host:8080" or
        // "http://host:8080/v1" — we build request paths without the /v1
        // prefix below, so strip it from the base URL if present.
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^3];
        if (baseUrl.EndsWith('/'))
            baseUrl = baseUrl[..^1];
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.Timeout = TimeSpan.FromMinutes(5);

        var apiKey = configuration["OpenAICompatible:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        // Batch size for /v1/embeddings batched requests. Default 32.
        var batchSizeStr = configuration["Embedding:BatchSize"];
        _batchSize = int.TryParse(batchSizeStr, out var parsed) && parsed > 0 ? parsed : 32;

        VerifyModelLoadedAsync().GetAwaiter().GetResult();

        _logger?.LogInformation("OpenAI-compatible embedding service initialized with model: {Model} at {BaseUrl} (batch size: {BatchSize})",
            _embeddingModel, _httpClient.BaseAddress, _batchSize);
    }

    /// <summary>
    /// Checks whether the endpoint is reachable, returns its model list and
    /// whether any model is served. Does not throw.
    /// </summary>
    public static async Task<(bool IsRunning, bool HasModel, string? SelectedModel, string? ErrorMessage)> IsAvailableAsync(
        IConfiguration configuration, HttpClient? httpClient = null)
    {
        var baseUrl = configuration["OpenAICompatible:BaseUrl"] ?? "http://localhost:8080";
        var configuredModel = configuration["OpenAICompatible:EmbeddingModel"] ?? "";

        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^3];
        if (baseUrl.EndsWith('/'))
            baseUrl = baseUrl[..^1];

        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.BaseAddress = new Uri(baseUrl);

        try
        {
            using var response = await client.GetAsync("/v1/models");
            if (!response.IsSuccessStatusCode)
                return (false, false, null, $"Endpoint not responding. Status: {response.StatusCode}");

            var responseText = await response.Content.ReadAsStringAsync();
            var servedModels = ExtractModelIds(responseText);

            if (servedModels.Count == 0)
                return (true, false, null, "Endpoint erreichbar, aber kein Modell geladen.");

            var selectedModel = servedModels.FirstOrDefault(m => m.Equals(configuredModel, StringComparison.OrdinalIgnoreCase))
                                ?? servedModels.First();

            return (true, true, selectedModel, null);
        }
        catch (Exception ex)
        {
            return (false, false, null, $"Endpoint nicht erreichbar: {ex.Message}");
        }
    }

    private static List<string> ExtractModelIds(string responseText)
    {
        var models = new List<string>();
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
                        if (!string.IsNullOrEmpty(id)) models.Add(id);
                    }
                    else if (item.TryGetProperty("model", out var modelElement))
                    {
                        var model = modelElement.GetString();
                        if (!string.IsNullOrEmpty(model)) models.Add(model);
                    }
                }
            }
        }
        catch { /* ignore parsing errors — empty list is the signal */ }
        return models;
    }

    /// <summary>
    /// Verifies the endpoint is reachable and a model is served; auto-detects
    /// the model when none is configured.
    /// </summary>
    private async Task VerifyModelLoadedAsync()
    {
        var (isRunning, hasModel, selectedModel, errorMessage) = await IsAvailableAsync(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAICompatible:BaseUrl"] = _httpClient.BaseAddress?.ToString(),
                ["OpenAICompatible:EmbeddingModel"] = _embeddingModel
            }).Build(),
            _httpClient);

        if (!isRunning)
        {
            throw new InvalidOperationException(
                $"Failed to connect to the OpenAI-compatible endpoint at {_httpClient.BaseAddress}. " +
                $"Please ensure the server is running. Error: {errorMessage}");
        }

        if (!hasModel)
        {
            throw new InvalidOperationException(errorMessage ??
                "The OpenAI-compatible endpoint has no models loaded. Please load an embedding model or set 'OpenAICompatible:EmbeddingModel'.");
        }

        _logger?.LogInformation("OpenAI-compatible endpoint serves model(s). Using: {Model}", selectedModel);
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
            _logger?.LogDebug("Sending embedding request for text: {TextPreview}", text[..Math.Min(text.Length, 100)]);

            var request = new
            {
                model = _embeddingModel,
                input = text
            };

            using var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken);
            var embedding = result?.Data?.FirstOrDefault()?.Embedding;

            if (embedding is not { Length: > 0 })
            {
                _logger?.LogError("Failed to parse embedding response. Response: {Response}", JsonSerializer.Serialize(result));
                throw new InvalidOperationException("OpenAI-compatible endpoint returned empty or invalid embedding");
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
                    "Batch embedding failed ({Message}). Falling back to sequential single-text requests.",
                    ex.Message);
                foreach (var text in batch)
                {
                    allEmbeddings.Add(await GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        _logger?.LogInformation("Successfully generated {Count} embeddings", allEmbeddings.Count);
        return allEmbeddings.ToArray();
    }

    /// <summary>
    /// Sends one batched request to the <c>/v1/embeddings</c> endpoint with
    /// <c>input</c> as an array. Returns embeddings sorted by their
    /// <c>index</c> field (matching input order).
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

        using var response = await _httpClient.PostAsJsonAsync("/v1/embeddings", request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var result = await response.Content
            .ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result?.Data == null || result.Data.Count != batch.Count)
        {
            throw new InvalidOperationException(
                $"Batch response shape mismatch: expected {batch.Count} items, got {result?.Data?.Count ?? 0}");
        }

        return result.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding ?? Array.Empty<float>())
            .ToArray();
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"OpenAI-compatible endpoint API error: {response.StatusCode} - {errorBody}");
    }

    /// <inheritdoc />
    public Task<int> GetEmbeddingDimensionsAsync()
    {
        // Determined at runtime from the first real embedding; the common
        // default for nomic-embed-text. Callers only use this for schema
        // decisions — providers like LM Studio hard-code 768 the same way.
        return Task.FromResult(768);
    }

    /// <summary>Response model for the OpenAI embeddings API.</summary>
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

    /// <summary>Individual embedding data from the response.</summary>
    private class EmbeddingData
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }

        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }

        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    /// <summary>Token usage information.</summary>
    private class UsageInfo
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}