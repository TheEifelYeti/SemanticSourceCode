using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace SemanticSourceCode.Tests.Search;

public class SearchFilterTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqliteVssDatabase _database;

    public SearchFilterTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_filter_{Guid.NewGuid()}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _testDbPath
            })
            .Build();

        _database = new SqliteVssDatabase(config);
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }

    [Fact]
    public async Task SearchSimilarWithScoresAsync_FilterByNamespace_ReturnsMatchingResults()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateChunk("1", "Api.Controllers", "HomeController", "Index", "GET", new float[] { 1.0f, 0.0f, 0.0f }),
            CreateChunk("2", "Services", "UserService", "GetUser", null, new float[] { 0.9f, 0.1f, 0.0f }),
            CreateChunk("3", "Api.Controllers.Internal", "AdminController", "Delete", "DELETE", new float[] { 0.8f, 0.2f, 0.0f })
        };
        await _database.InsertChunksAsync(chunks);

        var filter = new SearchFilter { Namespace = "Api.Controllers" };
        var query = new float[] { 1.0f, 0.0f, 0.0f };

        // Act
        var results = await _database.SearchSimilarWithScoresAsync(query, filter, 10);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Chunk.Id == "1");
        Assert.Contains(results, r => r.Chunk.Id == "3");
        Assert.DoesNotContain(results, r => r.Chunk.Id == "2");
    }

    [Fact]
    public async Task SearchSimilarWithScoresAsync_FilterByHttpMethod_ReturnsMatchingResults()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateChunk("1", "Api.Controllers", "HomeController", "GetUser", "GET", new float[] { 1.0f, 0.0f, 0.0f }),
            CreateChunk("2", "Api.Controllers", "HomeController", "CreateUser", "POST", new float[] { 0.5f, 0.5f, 0.0f }),
            CreateChunk("3", "Api.Controllers", "HomeController", "DeleteUser", "DELETE", new float[] { 0.0f, 1.0f, 0.0f })
        };
        await _database.InsertChunksAsync(chunks);

        var filter = new SearchFilter { HttpMethod = "GET" };
        var query = new float[] { 1.0f, 0.0f, 0.0f };

        // Act
        var results = await _database.SearchSimilarWithScoresAsync(query, filter, 10);

        // Assert
        Assert.Single(results);
        Assert.Equal("GetUser", results[0].Chunk.MemberName);
    }

    [Fact]
    public async Task SearchSimilarWithScoresAsync_FilterByFilePathPattern_ReturnsMatchingResults()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateChunk("1", "N", "A", "X", null, new float[] { 1.0f, 0.0f, 0.0f }, "/src/Controllers/HomeController.cs"),
            CreateChunk("2", "N", "B", "Y", null, new float[] { 0.9f, 0.1f, 0.0f }, "/src/Services/UserService.cs"),
            CreateChunk("3", "N", "C", "Z", null, new float[] { 0.8f, 0.2f, 0.0f }, "/src/Models/User.cs")
        };
        await _database.InsertChunksAsync(chunks);

        var filter = new SearchFilter { FilePathPattern = "*/Controllers/*" };
        var query = new float[] { 1.0f, 0.0f, 0.0f };

        // Act
        var results = await _database.SearchSimilarWithScoresAsync(query, filter, 10);

        // Assert
        Assert.Single(results);
        Assert.Equal("1", results[0].Chunk.Id);
    }

    [Fact]
    public async Task SearchSimilarWithScoresAsync_CombinedFilter_ReturnsIntersection()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateChunk("1", "Api.Controllers", "A", "GetUser", "GET", new float[] { 1.0f, 0.0f, 0.0f }),
            CreateChunk("2", "Api.Controllers", "B", "PostUser", "POST", new float[] { 0.5f, 0.5f, 0.0f }),
            CreateChunk("3", "Services", "C", "GetData", "GET", new float[] { 0.0f, 1.0f, 0.0f })
        };
        await _database.InsertChunksAsync(chunks);

        var filter = new SearchFilter { Namespace = "Api.Controllers", HttpMethod = "GET" };
        var query = new float[] { 1.0f, 0.0f, 0.0f };

        // Act
        var results = await _database.SearchSimilarWithScoresAsync(query, filter, 10);

        // Assert
        Assert.Single(results);
        Assert.Equal("1", results[0].Chunk.Id);
    }

    [Fact]
    public async Task SearchSimilarWithScoresAsync_EmptyFilter_ReturnsAllResults()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateChunk("1", "A", "C1", "M1", null, new float[] { 1.0f, 0.0f, 0.0f }),
            CreateChunk("2", "B", "C2", "M2", null, new float[] { 0.9f, 0.1f, 0.0f })
        };
        await _database.InsertChunksAsync(chunks);

        var filter = new SearchFilter(); // empty
        var query = new float[] { 1.0f, 0.0f, 0.0f };

        // Act
        var results = await _database.SearchSimilarWithScoresAsync(query, filter, 10);

        // Assert
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchSimilarWithScoresAsync_FilterByClassName_ReturnsMatchingResults()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateChunk("1", "N", "UserController", "A", null, new float[] { 1.0f, 0.0f, 0.0f }),
            CreateChunk("2", "N", "ProductService", "B", null, new float[] { 0.9f, 0.1f, 0.0f }),
            CreateChunk("3", "N", "UserService", "C", null, new float[] { 0.8f, 0.2f, 0.0f })
        };
        await _database.InsertChunksAsync(chunks);

        var filter = new SearchFilter { ClassName = "User" };
        var query = new float[] { 1.0f, 0.0f, 0.0f };

        // Act
        var results = await _database.SearchSimilarWithScoresAsync(query, filter, 10);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Chunk.ClassName == "UserController");
        Assert.Contains(results, r => r.Chunk.ClassName == "UserService");
    }

    [Fact]
    public async Task SearchSimilarWithScoresAsync_FilterByIsController_ReturnsOnlyControllers()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateChunk("1", "N", "A", "X", null, new float[] { 1.0f, 0.0f, 0.0f }, isController: true),
            CreateChunk("2", "N", "B", "Y", null, new float[] { 0.9f, 0.1f, 0.0f }, isController: false),
            CreateChunk("3", "N", "C", "Z", null, new float[] { 0.8f, 0.2f, 0.0f }, isController: true)
        };
        await _database.InsertChunksAsync(chunks);

        var filter = new SearchFilter { IsController = true };
        var query = new float[] { 1.0f, 0.0f, 0.0f };

        // Act
        var results = await _database.SearchSimilarWithScoresAsync(query, filter, 10);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Chunk.IsController));
    }

    private CodeChunk CreateChunk(string id, string ns, string className, string memberName, string? httpMethod, float[] embedding, string filePath = "/test/file.cs", bool isController = false)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);

        return new CodeChunk
        {
            Id = id,
            FilePath = filePath,
            NamespaceName = ns,
            ClassName = className,
            MemberName = memberName,
            MemberType = "Method",
            Content = $"public void {memberName}() {{ }}",
            Signature = $"public void {memberName}()",
            Documentation = "",
            StartLine = 1,
            EndLine = 3,
            HttpMethods = httpMethod,
            IsController = isController,
            Embedding = bytes,
            IndexedAt = DateTime.UtcNow
        };
    }
}
