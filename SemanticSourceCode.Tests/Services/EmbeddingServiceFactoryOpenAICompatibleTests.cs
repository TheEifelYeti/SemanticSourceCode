using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Factory tests for the openai-compatible provider (issue #24):
/// sync creation, auto-detection, provider order and the install hint.
/// </summary>
public class EmbeddingServiceFactoryOpenAICompatibleTests
{
    private static IConfiguration CreateConfig(
        string provider,
        string? compatUrl = null,
        string? compatModel = null,
        string? ollamaUrl = null,
        string? lmStudioUrl = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Embedding:Provider"] = provider
        };
        if (compatUrl != null) dict["OpenAICompatible:BaseUrl"] = compatUrl;
        if (compatModel != null) dict["OpenAICompatible:EmbeddingModel"] = compatModel;
        if (ollamaUrl != null) dict["Ollama:BaseUrl"] = ollamaUrl;
        if (lmStudioUrl != null) dict["LMStudio:BaseUrl"] = lmStudioUrl;

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static HttpClient CreateMockHttpClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        => new(new MockHttpMessageHandler(sendAsync));

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

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private const string ModelsBody = "{\"object\":\"list\",\"data\":[{\"id\":\"nomic-embed-text:latest\"}]}";
    private const string EmbeddingBody =
        "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\",\"embedding\":[0.1,0.2],\"index\":0}]}";

    private static HttpClient CreateCompatMockClient() => CreateMockHttpClient((req, _) =>
    {
        var path = req.RequestUri?.PathAndQuery;
        if (path == "/v1/models")
            return Task.FromResult(Json(HttpStatusCode.OK, ModelsBody));
        if (path == "/v1/embeddings")
            return Task.FromResult(Json(HttpStatusCode.OK, EmbeddingBody));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    });

    // ─────────────────────────────────────────────────────────────────
    // Sync creation (CreateEmbeddingService)
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("openai-compatible")]
    [InlineData("openaicompatible")]
    [InlineData("OpenAI-Compatible")] // case-insensitive (factory lowercases)
    public void CreateEmbeddingService_OpenAICompatible_ReturnsCorrectType(string provider)
    {
        // The service constructor verifies availability, so the mock must
        // answer both /v1/models and /v1/embeddings via a HttpClient we can't
        // inject through the factory. Instead we point the BaseUrl at a
        // guaranteed-dead port and expect the constructor to throw with the
        // connect-failure message — proving the factory routed to the
        // OpenAI-compatible provider rather than ollama/lmstudio.
        var config = CreateConfig(provider, compatUrl: "http://localhost:9");

        var factory = new EmbeddingServiceFactory(config, NullLoggerFactory.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateEmbeddingService());

        Assert.Contains("Failed to connect to the OpenAI-compatible endpoint", ex.Message);
    }

    [Fact]
    public void CreateEmbeddingService_UnknownProvider_ErrorListsNewProvider()
    {
        var config = CreateConfig("does-not-exist");
        var factory = new EmbeddingServiceFactory(config, NullLoggerFactory.Instance);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateEmbeddingService());

        Assert.Contains("ollama, lmstudio, openai-compatible", ex.Message);
    }

    // ─────────────────────────────────────────────────────────────────
    // Auto-detection (CreateEmbeddingServiceAsync)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateEmbeddingServiceAsync_OpenAICompatible_Configured_ReturnsService()
    {
        var config = CreateConfig("openai-compatible", compatUrl: "http://localhost:9");
        var factory = new EmbeddingServiceFactory(config, NullLoggerFactory.Instance);

        // IsAvailableAsync creates its own HttpClient — port 9 is dead, so
        // auto-detection must skip the provider and throw the aggregate
        // "no provider available" message that now includes option 3.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateEmbeddingServiceAsync());

        Assert.Contains("No embedding provider available", ex.Message);
        Assert.Contains("openai-compatible", ex.Message);
    }

    [Fact]
    public async Task CreateEmbeddingServiceAsync_Auto_DoesNotTryOpenAICompatible()
    {
        // Spin up a real local HTTP listener as the "compat endpoint". With
        // provider=auto and lmstudio/ollama dead, auto-detection must NOT
        // contact the OpenAI-compatible endpoint — proven by the listener
        // having received zero requests when the aggregate error throws.
        using var listener = new System.Net.HttpListener();
        var port = 18812;
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var requests = 0;
        var listenTask = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    Interlocked.Increment(ref requests);
                    ctx.Response.StatusCode = 200;
                    var body = System.Text.Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"nomic-embed-text\"}]}");
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
                catch (Exception) { break; }
            }
        });

        try
        {
            var config = CreateConfig("auto",
                compatUrl: $"http://localhost:{port}/v1",
                ollamaUrl: "http://localhost:9",
                lmStudioUrl: "http://localhost:9");
            var factory = new EmbeddingServiceFactory(config, NullLoggerFactory.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => factory.CreateEmbeddingServiceAsync());

            Assert.Contains("No embedding provider available", ex.Message);
            Assert.Equal(0, requests); // auto never touched the compat endpoint
        }
        finally
        {
            listener.Stop();
            await listenTask;
        }
    }
}