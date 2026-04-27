using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SemanticSourceCode.Services;

public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _embeddingModel;
    private readonly ILogger<OllamaEmbeddingService>? _logger;

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
        
        _logger?.LogInformation("OllamaEmbeddingService initialized with model: {Model}", _embeddingModel);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<float>();

        var request = new
        {
            model = _embeddingModel,
            prompt = TruncateText(text, 8192) // Limit context
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request);
            response.EnsureSuccessStatusCode();
            
            var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>();
            
            if (result?.Embedding == null)
                throw new InvalidOperationException("No embedding returned from Ollama");

            return result.Embedding;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to generate embedding for text starting with: {TextStart}", 
                text.Length > 50 ? text[..50] + "..." : text);
            throw;
        }
    }

    public async Task<List<float[]>> GenerateEmbeddingsAsync(List<string> texts)
    {
        var embeddings = new List<float[]>();
        foreach (var text in texts)
        {
            var embedding = await GenerateEmbeddingAsync(text);
            embeddings.Add(embedding);
            // Small delay to not overwhelm Ollama
            await Task.Delay(100);
        }
        return embeddings;
    }

    private string TruncateText(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength];
    }

    private class OllamaEmbeddingResponse
    {
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}
