using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Unit tests for the <see cref="EmbeddingServiceFactory"/> class.
/// Tests auto-detection and fallback logic with mocked HTTP responses.
/// </summary>
public class EmbeddingServiceFactoryTests
{
    private static IConfiguration CreateConfig(string provider, string? ollamaUrl = null, string? lmStudioUrl = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Embedding:Provider"] = provider
        };
        if (ollamaUrl != null) dict["Ollama:BaseUrl"] = ollamaUrl;
        if (lmStudioUrl != null) dict["LMStudio:BaseUrl"] = lmStudioUrl;

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static HttpClient CreateMockHttpClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        var handler = new MockHttpMessageHandler(sendAsync);
        return new HttpClient(handler);
    }

    /// <summary>
    /// Helper: builds factory with the given config (no HTTP mocking at factory level —
    /// IsAvailableAsync uses its own short-lived HttpClient; we rely on no real server running).
    /// </summary>
    private static EmbeddingServiceFactory CreateFactory(IConfiguration config)
    {
        return new EmbeddingServiceFactory(config, NullLoggerFactory.Instance);
    }

    // ─────────────────────────────────────────────────────────────────
    // LM Studio IsAvailableAsync
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LMStudio_IsAvailableAsync_RunningWithModel_ReturnsAvailable()
    {
        var config = CreateConfig("lmstudio", lmStudioUrl: "http://localhost:1234");
        var json = "{\"object\":\"list\",\"data\":[{\"id\":\"nomic-embed-text-v1.5\"}]}";
        var client = CreateMockHttpClient((req, _) =>
        {
            if (req.RequestUri?.PathAndQuery == "/v1/models")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var result = await LMStudioEmbeddingService.IsAvailableAsync(config, client);

        Assert.True(result.IsRunning);
        Assert.True(result.HasModel);
        Assert.Equal("nomic-embed-text-v1.5", result.SelectedModel);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task LMStudio_IsAvailableAsync_RunningNoModel_ReturnsNoModel()
    {
        var config = CreateConfig("lmstudio", lmStudioUrl: "http://localhost:1234");
        var json = "{\"object\":\"list\",\"data\":[]}";
        var client = CreateMockHttpClient((req, _) =>
        {
            if (req.RequestUri?.PathAndQuery == "/v1/models")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var result = await LMStudioEmbeddingService.IsAvailableAsync(config, client);

        Assert.True(result.IsRunning);
        Assert.False(result.HasModel);
        Assert.Null(result.SelectedModel);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("kein Modell geladen", result.ErrorMessage);
    }

    [Fact]
    public async Task LMStudio_IsAvailableAsync_NotRunning_ReturnsNotRunning()
    {
        var config = CreateConfig("lmstudio", lmStudioUrl: "http://localhost:1234");
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var result = await LMStudioEmbeddingService.IsAvailableAsync(config, client);

        Assert.False(result.IsRunning);
        Assert.False(result.HasModel);
        Assert.NotNull(result.ErrorMessage);
    }

    // ─────────────────────────────────────────────────────────────────
    // Ollama IsAvailableAsync
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ollama_IsAvailableAsync_RunningWithModel_ReturnsAvailable()
    {
        var config = CreateConfig("ollama", ollamaUrl: "http://localhost:11434");
        var json = "{\"models\":[{\"name\":\"nomic-embed-text:latest\"}]}";
        var client = CreateMockHttpClient((req, _) =>
        {
            if (req.RequestUri?.PathAndQuery == "/api/tags")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var result = await OllamaEmbeddingService.IsAvailableAsync(config, client);

        Assert.True(result.IsRunning);
        Assert.Equal("nomic-embed-text:latest", result.SelectedModel);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_IsAvailableAsync_RunningNoEmbeddingModel_ReturnsNoModel()
    {
        var config = CreateConfig("ollama", ollamaUrl: "http://localhost:11434");
        var json = "{\"models\":[{\"name\":\"llama3:latest\"}]}";
        var client = CreateMockHttpClient((req, _) =>
        {
            if (req.RequestUri?.PathAndQuery == "/api/tags")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var result = await OllamaEmbeddingService.IsAvailableAsync(config, client);

        Assert.True(result.IsRunning);
        Assert.Null(result.SelectedModel);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("kein Embedding-Modell", result.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_IsAvailableAsync_NotRunning_ReturnsNotRunning()
    {
        var config = CreateConfig("ollama", ollamaUrl: "http://localhost:11434");
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var result = await OllamaEmbeddingService.IsAvailableAsync(config, client);

        Assert.False(result.IsRunning);
        Assert.Null(result.SelectedModel);
        Assert.NotNull(result.ErrorMessage);
    }

    // ─────────────────────────────────────────────────────────────────
    // Factory CreateEmbeddingServiceAsync — integration with environment
    // We use dead ports to guarantee unreachability.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateEmbeddingServiceAsync_NeitherRunning_ThrowsInvalidOperationException()
    {
        // Use non-existent ports so both providers are unreachable
        var config = CreateConfig("auto", ollamaUrl: "http://localhost:59999", lmStudioUrl: "http://localhost:59998");
        var factory = CreateFactory(config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateEmbeddingServiceAsync());
        Assert.Contains("No embedding provider available", ex.Message);
        Assert.Contains("LM Studio", ex.Message);
        Assert.Contains("Ollama", ex.Message);
    }

    [Fact]
    public async Task CreateEmbeddingServiceAsync_AutoMode_DeadPorts_ThrowsInvalidOperationException()
    {
        var config = CreateConfig("auto", ollamaUrl: "http://localhost:59999", lmStudioUrl: "http://localhost:59998");
        var factory = CreateFactory(config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateEmbeddingServiceAsync());
        Assert.Contains("No embedding provider available", ex.Message);
    }

    // ─────────────────────────────────────────────────────────────────
    // Sync backward-compatible API
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateEmbeddingService_Sync_UnsupportedProvider_ThrowsInvalidOperationException()
    {
        var config = CreateConfig("unknown-provider");
        var factory = CreateFactory(config);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateEmbeddingService());
        Assert.Contains("Unsupported embedding provider", ex.Message);
    }

    [Fact]
    public void CreateEmbeddingService_Sync_Ollama_ReturnsOllamaService()
    {
        // Will throw on constructor because Ollama isn't running, but proves type resolution
        var config = CreateConfig("ollama", ollamaUrl: "http://localhost:59999");
        var factory = CreateFactory(config);

        // The sync API instantiates OllamaEmbeddingService directly — it will fail verification.
        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateEmbeddingService());
        Assert.Contains("Ollama", ex.Message);
    }

    [Fact]
    public void CreateEmbeddingService_Sync_LMStudio_ReturnsLMStudioService()
    {
        var config = CreateConfig("lmstudio", lmStudioUrl: "http://localhost:59998");
        var factory = CreateFactory(config);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateEmbeddingService());
        Assert.Contains("LM Studio", ex.Message);
    }

    /// <summary>
    /// Simple mock HttpMessageHandler that delegates to a lambda.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        {
            _sendAsync = sendAsync;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _sendAsync(request, cancellationToken);
        }
    }
}
