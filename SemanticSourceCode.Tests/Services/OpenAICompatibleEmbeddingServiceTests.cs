using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Unit tests for the <see cref="OpenAICompatibleEmbeddingService"/> (issue #24).
/// Uses a mocked HTTP handler — no real endpoint required.
/// </summary>
public class OpenAICompatibleEmbeddingServiceTests
{
    private static IConfiguration CreateConfig(
        string baseUrl = "http://localhost:8080",
        string? model = null,
        string? apiKey = null,
        string? batchSize = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["OpenAICompatible:BaseUrl"] = baseUrl,
            ["OpenAICompatible:EmbeddingModel"] = model ?? "nomic-embed-text",
        };
        if (apiKey != null) dict["OpenAICompatible:ApiKey"] = apiKey;
        if (batchSize != null) dict["Embedding:BatchSize"] = batchSize;

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static HttpClient CreateMockHttpClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        return new HttpClient(new MockHttpMessageHandler(sendAsync));
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    // ─────────────────────────────────────────────────────────────────
    // Base URL normalization
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("http://localhost:8080/v1/")]
    [InlineData("http://localhost:8080/")]
    public async Task IsAvailableAsync_NormalizesBaseUrl_AndReachesModelsEndpoint(string baseUrl)
    {
        var config = CreateConfig(baseUrl: baseUrl);
        string? requestedPath = null;
        var client = CreateMockHttpClient((req, _) =>
        {
            requestedPath = req.RequestUri?.PathAndQuery;
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"object\":\"list\",\"data\":[{\"id\":\"qwen-embed\"}]}"));
        });

        var result = await OpenAICompatibleEmbeddingService.IsAvailableAsync(config, client);

        Assert.True(result.IsRunning);
        Assert.True(result.HasModel);
        Assert.Equal("qwen-embed", result.SelectedModel);
        Assert.Equal("/v1/models", requestedPath);
    }

    // ─────────────────────────────────────────────────────────────────
    // IsAvailableAsync
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsAvailableAsync_PrefersConfiguredModel_WhenServed()
    {
        var config = CreateConfig(model: "my-model");
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(Json(HttpStatusCode.OK,
                "{\"data\":[{\"id\":\"other\"},{\"id\":\"my-model\"}]}")));

        var result = await OpenAICompatibleEmbeddingService.IsAvailableAsync(config, client);

        Assert.True(result.HasModel);
        Assert.Equal("my-model", result.SelectedModel);
    }

    [Fact]
    public async Task IsAvailableAsync_NoModelConfigured_PicksFirstServed()
    {
        var config = CreateConfig(model: "");
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(Json(HttpStatusCode.OK,
                "{\"data\":[{\"id\":\"llama-embed\"},{\"id\":\"second\"}]}")));

        var result = await OpenAICompatibleEmbeddingService.IsAvailableAsync(config, client);

        Assert.True(result.HasModel);
        Assert.Equal("llama-embed", result.SelectedModel);
    }

    [Fact]
    public async Task IsAvailableAsync_EmptyModelList_ReportsNoModel()
    {
        var config = CreateConfig();
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[]}")));

        var result = await OpenAICompatibleEmbeddingService.IsAvailableAsync(config, client);

        Assert.True(result.IsRunning);
        Assert.False(result.HasModel);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task IsAvailableAsync_ServerDown_ReportsNotRunning()
    {
        var config = CreateConfig();
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(Json(HttpStatusCode.InternalServerError, "boom")));

        var result = await OpenAICompatibleEmbeddingService.IsAvailableAsync(config, client);

        Assert.False(result.IsRunning);
        Assert.False(result.HasModel);
        Assert.NotNull(result.ErrorMessage);
    }

    // ─────────────────────────────────────────────────────────────────
    // Constructor + GenerateEmbeddingAsync
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateEmbeddingAsync_ReturnsEmbedding_FromSingleRequest()
    {
        var config = CreateConfig();
        string? authHeader = null;
        var client = CreateMockHttpClient((req, _) =>
        {
            authHeader = req.Headers.Authorization?.ToString();
            if (req.RequestUri?.PathAndQuery == "/v1/models")
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"nomic-embed-text\"}]}"));
            if (req.RequestUri?.PathAndQuery == "/v1/embeddings")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\",\"embedding\":[0.1,0.2,0.3],\"index\":0}]}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var service = new OpenAICompatibleEmbeddingService(config, NullLogger<OpenAICompatibleEmbeddingService>.Instance, client);

        var embedding = await service.GenerateEmbeddingAsync("test text");

        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, embedding);
        Assert.Null(authHeader); // no ApiKey configured → no Authorization header
    }

    [Fact]
    public async Task Constructor_SetsBearerHeader_WhenApiKeyConfigured()
    {
        var config = CreateConfig(apiKey: "secret-key");
        string? authHeader = null;
        var client = CreateMockHttpClient((req, _) =>
        {
            authHeader ??= req.Headers.Authorization?.ToString();
            if (req.RequestUri?.PathAndQuery == "/v1/models")
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"nomic-embed-text\"}]}"));
            return Task.FromResult(Json(HttpStatusCode.OK,
                "{\"data\":[{\"embedding\":[0.5],\"index\":0}]}"));
        });

        var service = new OpenAICompatibleEmbeddingService(config, null, client);

        Assert.Equal("Bearer secret-key", authHeader);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_EmptyText_ReturnsEmptyArray()
    {
        var config = CreateConfig();
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"nomic-embed-text\"}]}")));

        var service = new OpenAICompatibleEmbeddingService(config, null, client);

        var embedding = await service.GenerateEmbeddingAsync("   ");

        Assert.Empty(embedding);
    }

    [Fact]
    public void Constructor_Throws_WhenEndpointUnreachable()
    {
        var config = CreateConfig();
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new OpenAICompatibleEmbeddingService(config, null, client));

        Assert.Contains("Failed to connect", ex.Message);
    }

    // ─────────────────────────────────────────────────────────────────
    // Batch embeddings
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateEmbeddingsAsync_BatchesRequests_AndRestoresOrder()
    {
        // batchSize 2 → two batches for three inputs; second batch response
        // deliberately arrives index-reversed to prove re-sorting.
        var config = CreateConfig(batchSize: "2");
        var requestCount = 0;
        var client = CreateMockHttpClient((req, _) =>
        {
            if (req.RequestUri?.PathAndQuery == "/v1/models")
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"nomic-embed-text\"}]}"));
            if (req.RequestUri?.PathAndQuery == "/v1/embeddings")
            {
                requestCount++;
                if (requestCount == 1)
                    return Task.FromResult(Json(HttpStatusCode.OK,
                        "{\"data\":[{\"embedding\":[1],\"index\":0},{\"embedding\":[2],\"index\":1}]}"));
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"data\":[{\"embedding\":[3],\"index\":0}]}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var service = new OpenAICompatibleEmbeddingService(config, null, client);

        var embeddings = await service.GenerateEmbeddingsAsync(new[] { "a", "b", "c" });

        Assert.Equal(3, embeddings.Length);
        Assert.Equal(new float[] { 1f }, embeddings[0]);
        Assert.Equal(new float[] { 2f }, embeddings[1]);
        Assert.Equal(new float[] { 3f }, embeddings[2]);
        Assert.Equal(2, requestCount); // two batch calls, no per-item fallback
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_ReversesIndexOrder_InResponse()
    {
        var config = CreateConfig(batchSize: "3");
        var client = CreateMockHttpClient((req, _) =>
        {
            if (req.RequestUri?.PathAndQuery == "/v1/models")
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"nomic-embed-text\"}]}"));
            return Task.FromResult(Json(HttpStatusCode.OK,
                "{\"data\":[{\"embedding\":[9],\"index\":2},{\"embedding\":[8],\"index\":0},{\"embedding\":[7],\"index\":1}]}"));
        });

        var service = new OpenAICompatibleEmbeddingService(config, null, client);

        var embeddings = await service.GenerateEmbeddingsAsync(new[] { "a", "b", "c" });

        Assert.Equal(new float[] { 8f }, embeddings[0]);
        Assert.Equal(new float[] { 7f }, embeddings[1]);
        Assert.Equal(new float[] { 9f }, embeddings[2]);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_FallsBackToSingleRequests_WhenBatchFails()
    {
        var config = CreateConfig(batchSize: "2");
        var singleCalls = 0;
        var client = CreateMockHttpClient((req, _) =>
        {
            if (req.RequestUri?.PathAndQuery == "/v1/models")
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"nomic-embed-text\"}]}"));
            if (req.RequestUri?.PathAndQuery == "/v1/embeddings")
            {
                // Batch requests carry an array input; single requests a string.
                singleCalls++;
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"data\":[{\"embedding\":[1],\"index\":0}]}"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var service = new OpenAICompatibleEmbeddingService(config, null, client);

        var embeddings = await service.GenerateEmbeddingsAsync(new[] { "a", "b" });

        Assert.Equal(2, embeddings.Length);
        // 1 batch call + 2 fallback single calls
        Assert.Equal(3, singleCalls);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_SkipsEmptyTexts()
    {
        var config = CreateConfig();
        var client = CreateMockHttpClient((req, _) =>
        {
            if (req.RequestUri?.PathAndQuery == "/v1/models")
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"nomic-embed-text\"}]}"));
            return Task.FromResult(Json(HttpStatusCode.OK,
                "{\"data\":[{\"embedding\":[0.42],\"index\":0}]}"));
        });

        var service = new OpenAICompatibleEmbeddingService(config, null, client);

        var embeddings = await service.GenerateEmbeddingsAsync(new[] { "a", " ", "" });

        Assert.Single(embeddings);
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_EmptyInput_ReturnsEmptyArray()
    {
        var config = CreateConfig();
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"nomic-embed-text\"}]}")));

        var service = new OpenAICompatibleEmbeddingService(config, null, client);

        var embeddings = await service.GenerateEmbeddingsAsync(Array.Empty<string>());

        Assert.Empty(embeddings);
    }

    // ─────────────────────────────────────────────────────────────────
    // Dimensions
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEmbeddingDimensionsAsync_Returns768()
    {
        var config = CreateConfig();
        var client = CreateMockHttpClient((req, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"nomic-embed-text\"}]}")));

        var service = new OpenAICompatibleEmbeddingService(config, null, client);

        Assert.Equal(768, await service.GetEmbeddingDimensionsAsync());
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