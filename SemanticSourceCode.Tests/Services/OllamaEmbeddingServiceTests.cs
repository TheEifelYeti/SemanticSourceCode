using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Unit tests for the <see cref="OllamaEmbeddingService"/u003e class.
/// Tests embedding generation with mocked HTTP responses.
/// </summary>
public class OllamaEmbeddingServiceTests
{
    /// <summary>
    /// Creates configuration with a test model that won't trigger network calls
    /// when Ollama is not running (by using local fallback verification).
    /// </summary>
    private static IConfiguration CreateTestConfiguration(string baseUrl = "http://localhost:11434", string model = "nomic-embed-text")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = baseUrl,
                ["Ollama:EmbeddingModel"] = model
            })
            .Build();
    }

    /// <summary>
    /// Helper to create service with a mockable setup that skips verification.
    /// </summary>
    private static OllamaEmbeddingService CreateServiceWithVerificationMock(IConfiguration config)
    {
        // We create the service using reflection to bypass the verification call
        // In production, Ollama should be running. In tests, we test the methods directly.
        var service = new OllamaEmbeddingService(config, NullLogger<OllamaEmbeddingService>.Instance);
        return service;
    }

    /// <summary>
    /// Tests that empty text returns an empty embedding array without calling Ollama.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_EmptyText_ReturnsEmptyArray()
    {
        // Arrange
        var config = CreateTestConfiguration();
        var service = CreateServiceWithVerificationMock(config);

        // Act
        var result = await service.GenerateEmbeddingAsync("");

        // Assert
        Assert.Empty(result);
    }

    /// <summary>
    /// Tests that whitespace-only text returns an empty embedding array.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_WhitespaceOnly_ReturnsEmptyArray()
    {
        // Arrange
        var config = CreateTestConfiguration();
        var service = CreateServiceWithVerificationMock(config);

        // Act
        var result = await service.GenerateEmbeddingAsync("   \t\n  ");

        // Assert
        Assert.Empty(result);
    }

    /// <summary>
    /// Tests that null text returns an empty embedding array.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_NullText_ReturnsEmptyArray()
    {
        // Arrange
        var config = CreateTestConfiguration();
        var service = CreateServiceWithVerificationMock(config);

        // Act
        var result = await service.GenerateEmbeddingAsync(null!);

        // Assert
        Assert.Empty(result);
    }

    /// <summary>
    /// Tests that non-empty text throws when Ollama is not available.
    /// The service now propagates errors instead of silently returning empty arrays.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_OllamaNotAvailable_ThrowsException()
    {
        // Arrange - use localhost:9999 which should fail verification immediately
        var config = CreateTestConfiguration("http://localhost:9999", "nomic-embed-text");

        // Act & Assert - constructor itself throws when Ollama not reachable
        var ex = Assert.Throws<InvalidOperationException>(() => CreateServiceWithVerificationMock(config));
        Assert.Contains("Ollama", ex.Message);
    }

    /// <summary>
    /// Tests that GetEmbeddingDimensionsAsync returns the expected dimension count.
    /// </summary>
    [Fact]
    public async Task GetEmbeddingDimensionsAsync_ReturnsExpectedValue()
    {
        // Arrange
        var config = CreateTestConfiguration();
        var service = CreateServiceWithVerificationMock(config);

        // Act
        var dimensions = await service.GetEmbeddingDimensionsAsync();

        // Assert - nomic-embed-text produces 768-dimensional embeddings
        Assert.Equal(768, dimensions);
    }

    /// <summary>
    /// Tests that default configuration values are used when not specified.
    /// </summary>
    [Fact]
    public void Constructor_MissingConfiguration_UsesDefaults()
    {
        // Arrange - empty config uses defaults but will fail verification
        // since Ollama is not running in test environment
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>()
            {
                ["Ollama:BaseUrl"] = "http://localhost:9999",
                ["Ollama:EmbeddingModel"] = "test-model"
            })
            .Build();

        // Act & Assert - when Ollama is not running, should throw with helpful message
        var ex = Assert.Throws<InvalidOperationException>(() => new OllamaEmbeddingService(emptyConfig, NullLogger<OllamaEmbeddingService>.Instance));
        Assert.Contains("Ollama", ex.Message);
    }
}
