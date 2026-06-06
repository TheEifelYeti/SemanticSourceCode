using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SemanticSourceCode.Services;

/// <summary>
/// Factory for creating embedding service instances based on configuration.
/// Supports multiple embedding providers: Ollama and LM Studio.
/// Implements auto-detection with fallback for out-of-the-box Open-Source user experience.
/// </summary>
public class EmbeddingServiceFactory
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<EmbeddingServiceFactory> _logger;

    /// <summary>
    /// Initializes a new instance of the embedding service factory.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="loggerFactory">Logger factory for creating service-specific loggers.</param>
    public EmbeddingServiceFactory(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<EmbeddingServiceFactory>();
    }

    /// <summary>
    /// Creates an embedding service based on the configured provider.
    /// Uses exactly the configured provider without fallback (backward-compatible sync API).
    /// </summary>
    /// <returns>An instance of IEmbeddingService.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the configured provider is not supported.</exception>
    public IEmbeddingService CreateEmbeddingService()
    {
        var provider = _configuration["Embedding:Provider"]?.ToLowerInvariant() ?? "ollama";
        _logger.LogInformation("Creating embedding service (sync) for provider: {Provider}", provider);

        return provider switch
        {
            "ollama" => CreateOllamaService(),
            "lmstudio" => CreateLMStudioService(),
            _ => throw new InvalidOperationException($"Unsupported embedding provider: {provider}. " +
                "Supported providers: ollama, lmstudio")
        };
    }

    /// <summary>
    /// Creates an embedding service with auto-detection and fallback.
    /// Tries the configured (or requested) provider first, falls back to the other if unavailable.
    /// </summary>
    /// <returns>An instance of IEmbeddingService.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no provider is available.</exception>
    public async Task<IEmbeddingService> CreateEmbeddingServiceAsync(CancellationToken cancellationToken = default)
    {
        var provider = _configuration["Embedding:Provider"]?.ToLowerInvariant() ?? "auto";
        _logger.LogInformation("Embedding provider config: {Provider}", provider);

        // Resolve the order in which to try providers
        var providersToTry = ResolveProviderOrder(provider);

        foreach (var candidate in providersToTry)
        {
            _logger.LogInformation("Checking provider: {Candidate}...", candidate);

            if (candidate == "lmstudio")
            {
                var (isRunning, hasModel, selectedModel, errorMessage) =
                    await LMStudioEmbeddingService.IsAvailableAsync(_configuration);

                if (isRunning && hasModel)
                {
                    _logger.LogInformation("LM Studio is available with model: {Model}", selectedModel);
                    return CreateLMStudioService();
                }

                if (isRunning && !hasModel)
                {
                    _logger.LogWarning("LM Studio erreichbar, aber kein Modell geladen. {Message}", errorMessage);
                }
                else
                {
                    _logger.LogWarning("LM Studio nicht verfügbar: {Message}", errorMessage);
                }
            }
            else if (candidate == "ollama")
            {
                var (isRunning, selectedModel, errorMessage) =
                    await OllamaEmbeddingService.IsAvailableAsync(_configuration);

                if (isRunning && selectedModel != null)
                {
                    _logger.LogInformation("Ollama is available with model: {Model}", selectedModel);
                    return CreateOllamaService();
                }

                if (isRunning && selectedModel == null)
                {
                    _logger.LogWarning("Ollama läuft, aber kein Embedding-Modell gefunden. {Message}", errorMessage);
                }
                else
                {
                    _logger.LogWarning("Ollama nicht verfügbar: {Message}", errorMessage);
                }
            }
        }

        // Neither provider available
        var configuredProvider = _configuration["Embedding:Provider"] ?? "auto";
        var installInstructions =
            "No embedding provider available.\n\n" +
            "To use this tool, please install one of the following:\n\n" +
            "1) LM Studio (recommended for speed):\n" +
            "   • Download: https://lmstudio.ai/\n" +
            "   • Start the local server (Developer tab → Start Server)\n" +
            "   • Load an embedding model (e.g. jina-embeddings, nomic-embed-text)\n\n" +
            "2) Ollama:\n" +
            "   • Install: https://ollama.com/\n" +
            "   • Run: ollama pull nomic-embed-text\n" +
            "   • Ensure Ollama is running: ollama serve\n\n" +
            $"Current config: Embedding:Provider = '{configuredProvider}'\n" +
            "Set to 'auto' (default) to let the app pick whichever is available.";

        _logger.LogError(installInstructions);
        throw new InvalidOperationException(installInstructions);
    }

    /// <summary>
    /// Resolves the provider trial order based on the configured provider.
    /// "auto" and "lmstudio" try LM Studio first (faster/local), then Ollama.
    /// "ollama" tries Ollama first, then LM Studio.
    /// </summary>
    private static List<string> ResolveProviderOrder(string provider)
    {
        return provider switch
        {
            "auto" => new List<string> { "lmstudio", "ollama" },
            "lmstudio" => new List<string> { "lmstudio", "ollama" },
            "ollama" => new List<string> { "ollama", "lmstudio" },
            _ => new List<string> { "lmstudio", "ollama" } // fallback for unknown values
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
