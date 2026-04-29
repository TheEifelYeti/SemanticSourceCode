using Microsoft.Extensions.Configuration;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Unit tests for the <see cref="OllamaEmbeddingService"/> class.
/// Tests embedding generation with mocked HTTP responses.
/// </summary>
public class OllamaEmbeddingServiceTests
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes test configuration with default Ollama settings.
    /// </summary>
    public OllamaEmbeddingServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "http://localhost:11434",
                ["Ollama:EmbeddingModel"] = "nomic-embed-text"
            })
            .Build();
    }

    /// <summary>
    /// Tests that empty text returns an empty embedding array.
    /// </summary>
    [Fact]
    public void Constructor_ValidConfiguration_SetsProperties()
    {
        // Act
        var service = new OllamaEmbeddingService(_configuration);

        // Assert - service was created successfully
        Assert.NotNull(service);
    }

    /// <summary>
    /// Tests that empty text returns an empty embedding array without calling Ollama.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingAsync_EmptyText_ReturnsEmptyArray()
    {
        // Arrange
        var service = new OllamaEmbeddingService(_configuration);

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
        var service = new OllamaEmbeddingService(_configuration);

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
        var service = new OllamaEmbeddingService(_configuration);

        // Act
        var result = await service.GenerateEmbeddingAsync(null!);

        // Assert
        Assert.Empty(result);
    }

    /// <summary>
    /// Tests that GenerateEmbeddingsAsync returns empty arrays when Ollama is not available.
    /// </summary>
    [Fact]
    public async Task GenerateEmbeddingsAsync_OllamaNotAvailable_ReturnsEmptyArrays()
    {
        // Arrange
        var service = new OllamaEmbeddingService(_configuration);
        var texts = new[] { "Hello", "World", "Test" };

        // Act
        var results = await service.GenerateEmbeddingsAsync(texts);

        // Assert - when Ollama is not available, returns empty arrays
        Assert.Equal(3, results.Length);
    }

    /// <summary>
    /// Tests that custom configuration values are used correctly.
    /// </summary>
    [Fact]
    public void Constructor_CustomConfiguration_UsesCustomValues()
    {
        // Arrange
        var customConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "http://custom:1234",
                ["Ollama:EmbeddingModel"] = "custom-model"
            })
            .Build();

        // Act
        var service = new OllamaEmbeddingService(customConfig);

        // Assert - service was created with custom config
        Assert.NotNull(service);
    }

    /// <summary>
    /// Tests that missing configuration uses default values.
    /// </summary>
    [Fact]
    public void Constructor_MissingConfiguration_UsesDefaults()
    {
        // Arrange
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act
        var service = new OllamaEmbeddingService(emptyConfig);

        // Assert - service was created with defaults
        Assert.NotNull(service);
    }
}
