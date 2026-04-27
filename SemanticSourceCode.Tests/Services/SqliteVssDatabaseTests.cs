using Microsoft.Extensions.Configuration;
using SemanticSourceCode.Models;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Unit tests for the <see cref="SqliteVssDatabase"/> class.
/// Tests database initialization, CRUD operations, and vector similarity search.
/// </summary>
public class SqliteVssDatabaseTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqliteVssDatabase _database;

    /// <summary>
    /// Initializes a new test instance with a temporary database.
    /// </summary>
    public SqliteVssDatabaseTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _testDbPath
            })
            .Build();
        
        _database = new SqliteVssDatabase(config);
    }

    /// <summary>
    /// Cleans up the test database after each test.
    /// </summary>
    public void Dispose()
    {
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }

    /// <summary>
    /// Tests that the database initializes correctly.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_CreatesDatabase()
    {
        // Act
        await _database.InitializeAsync();

        // Assert
        Assert.True(File.Exists(_testDbPath));
        Assert.True(await _database.IsInitializedAsync());
    }

    /// <summary>
    /// Tests that a single chunk can be inserted and retrieved.
    /// </summary>
    [Fact]
    public async Task InsertChunkAsync_ValidChunk_StoresSuccessfully()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunk = CreateTestChunk("TestMethod");

        // Act
        await _database.InsertChunkAsync(chunk);

        // Assert - Verify by searching
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarAsync(queryEmbedding, 1);
        Assert.Single(results);
    }

    /// <summary>
    /// Tests that multiple chunks can be inserted in a batch.
    /// </summary>
    [Fact]
    public async Task InsertChunksAsync_MultipleChunks_StoresAll()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateTestChunk("Method1", new float[] { 1.0f, 0.0f, 0.0f }),
            CreateTestChunk("Method2", new float[] { 0.0f, 1.0f, 0.0f }),
            CreateTestChunk("Method3", new float[] { 0.0f, 0.0f, 1.0f })
        };

        // Act
        await _database.InsertChunksAsync(chunks);

        // Assert
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarAsync(queryEmbedding, 10);
        Assert.Equal(3, results.Count);
    }

    /// <summary>
    /// Tests that cosine similarity search returns the most similar chunks.
    /// </summary>
    [Fact]
    public async Task SearchSimilarAsync_SimilarEmbeddings_ReturnsOrderedBySimilarity()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateTestChunk("Similar", new float[] { 0.9f, 0.1f, 0.0f }),
            CreateTestChunk("Dissimilar", new float[] { 0.0f, 0.9f, 0.1f }),
            CreateTestChunk("Exact", new float[] { 1.0f, 0.0f, 0.0f })
        };
        await _database.InsertChunksAsync(chunks);

        // Act
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarAsync(queryEmbedding, 3);

        // Assert
        Assert.Equal(3, results.Count);
        Assert.Equal("Exact", results[0].MemberName); // Highest similarity
        Assert.Equal("Similar", results[1].MemberName); // Second highest
    }

    /// <summary>
    /// Tests that the topK parameter limits the number of results.
    /// </summary>
    [Fact]
    public async Task SearchSimilarAsync_TopKParameter_LimitsResults()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = Enumerable.Range(1, 10)
            .Select(i => CreateTestChunk($"Method{i}"))
            .ToList();
        await _database.InsertChunksAsync(chunks);

        // Act
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarAsync(queryEmbedding, 5);

        // Assert
        Assert.Equal(5, results.Count);
    }

    /// <summary>
    /// Tests that ClearDatabaseAsync removes all chunks.
    /// </summary>
    [Fact]
    public async Task ClearDatabaseAsync_RemovesAllData()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunk = CreateTestChunk("Test");
        await _database.InsertChunkAsync(chunk);

        // Act
        await _database.ClearDatabaseAsync();

        // Assert
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarAsync(queryEmbedding, 10);
        Assert.Empty(results);
    }

    /// <summary>
    /// Tests that inserting a chunk with the same ID updates the existing chunk.
    /// </summary>
    [Fact]
    public async Task InsertChunkAsync_DuplicateId_UpdatesExisting()
    {
        // Arrange
        await _database.InitializeAsync();
        var id = Guid.NewGuid().ToString();
        var chunk1 = CreateTestChunk("Original", id: id);
        var chunk2 = CreateTestChunk("Updated", id: id);

        // Act
        await _database.InsertChunkAsync(chunk1);
        await _database.InsertChunkAsync(chunk2);

        // Assert
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarAsync(queryEmbedding, 10);
        Assert.Single(results);
        Assert.Equal("Updated", results[0].MemberName);
    }

    /// <summary>
    /// Tests that chunks without embeddings are not returned in search results.
    /// </summary>
    [Fact]
    public async Task SearchSimilarAsync_ChunkWithoutEmbedding_NotReturned()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunkWithEmbedding = CreateTestChunk("WithEmbedding", new float[] { 1.0f, 0.0f, 0.0f });
        var chunkWithoutEmbedding = CreateTestChunk("WithoutEmbedding");
        chunkWithoutEmbedding.Embedding = null;

        // Act
        await _database.InsertChunkAsync(chunkWithEmbedding);
        await _database.InsertChunkAsync(chunkWithoutEmbedding);

        // Assert
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarAsync(queryEmbedding, 10);
        Assert.Single(results);
        Assert.Equal("WithEmbedding", results[0].MemberName);
    }

    /// <summary>
    /// Tests that empty query embeddings are handled correctly.
    /// </summary>
    [Fact]
    public async Task SearchSimilarAsync_EmptyDatabase_ReturnsEmptyList()
    {
        // Arrange
        await _database.InitializeAsync();

        // Act
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarAsync(queryEmbedding, 5);

        // Assert
        Assert.Empty(results);
    }

    /// <summary>
    /// Tests that InitializeAsync is idempotent (can be called multiple times).
    /// </summary>
    [Fact]
    public async Task InitializeAsync_MultipleCalls_DoesNotThrow()
    {
        // Act
        await _database.InitializeAsync();
        await _database.InitializeAsync(); // Second call
        await _database.InitializeAsync(); // Third call

        // Assert
        Assert.True(await _database.IsInitializedAsync());
    }

    private CodeChunk CreateTestChunk(string memberName, float[]? embedding = null, string? id = null)
    {
        var chunk = new CodeChunk
        {
            Id = id ?? Guid.NewGuid().ToString(),
            FilePath = "/test/path.cs",
            NamespaceName = "TestNamespace",
            ClassName = "TestClass",
            MemberName = memberName,
            MemberType = "Method",
            Content = $"public void {memberName}() {{ }}",
            Signature = $"public void {memberName}()",
            Documentation = "",
            StartLine = 1,
            EndLine = 3,
            IndexedAt = DateTime.UtcNow
        };

        if (embedding != null)
        {
            var bytes = new byte[embedding.Length * sizeof(float)];
            Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
            chunk.Embedding = bytes;
        }
        else
        {
            // Default embedding for tests
            var defaultEmbedding = new float[] { 0.0f, 0.0f, 1.0f };
            var bytes = new byte[defaultEmbedding.Length * sizeof(float)];
            Buffer.BlockCopy(defaultEmbedding, 0, bytes, 0, bytes.Length);
            chunk.Embedding = bytes;
        }

        return chunk;
    }
}
