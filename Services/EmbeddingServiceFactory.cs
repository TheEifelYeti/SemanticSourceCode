using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SemanticSourceCode.Services;

/// <summary>
/// Factory for creating embedding service instances based on configuration.
/// Supports multiple embedding providers: Ollama and LM Studio.
/// </summary>
public class EmbeddingServiceFactory
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the embedding service factory.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="loggerFactory">Logger factory for creating service-specific loggers.</param>
    public EmbeddingServiceFactory(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>
    /// Creates an embedding service based on the configured provider.
    /// </summary>
    /// <returns>An instance of IEmbeddingService.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the configured provider is not supported.</exception>
    public IEmbeddingService CreateEmbeddingService()
    {
        var provider = _configuration["Embedding:Provider"]?.ToLowerInvariant() ?? "ollama";
        
        return provider switch
        {
            "ollama" => CreateOllamaService(),
            "lmstudio" => CreateLMStudioService(),
            _ => throw new InvalidOperationException($"Unsupported embedding provider: {provider}. " +
                "Supported providers: ollama, lmstudio")
        };
    }

    /// <summary>
    /// Creates an Ollama embedding service.
    /// </summary>
    /// <returns>An OllamaEmbeddingService instance.</returns>
    private IEmbeddingService CreateOllamaService()
    {
        var logger = _loggerFactory.CreateLogger<OllamaEmbeddingService>();
        return new OllamaEmbeddingService(_configuration, logger);
    }

    /// <summary>
    /// Creates an LM Studio embedding service.
    /// </summary>
    /// <returns>An LMStudioEmbeddingService instance.</returns>
    private IEmbeddingService CreateLMStudioService()
    {
        var logger = _loggerFactory.CreateLogger<LMStudioEmbeddingService>();
        return new LMStudioEmbeddingService(_configuration, logger);
    }
}
