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

    // ============================================================================
    // Semantic Search Tests
    // ============================================================================

    /// <summary>
    /// Tests that a semantically unrelated query ("Airplane") returns no meaningful results
    /// when searching against code-related chunks. The similarity scores should be very low.
    /// </summary>
    [Fact]
    public async Task SearchSimilarWithScoresAsync_SemanticallyUnrelatedQuery_ReturnsLowScores()
    {
        // Arrange
        await _database.InitializeAsync();
        
        // Create code-related chunks with embeddings pointing in one direction
        var codeChunks = new List<CodeChunk>
        {
            CreateTestChunk("GetUserById", new float[] { 0.9f, 0.1f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f }),
            CreateTestChunk("SaveToDatabase", new float[] { 0.8f, 0.2f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f }),
            CreateTestChunk("ProcessRequest", new float[] { 0.85f, 0.15f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f }),
            CreateTestChunk("ValidateInput", new float[] { 0.7f, 0.3f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f })
        };
        await _database.InsertChunksAsync(codeChunks);

        // Act - Search with an embedding pointing in a completely different direction (simulating "Airplane")
        var airplaneQueryEmbedding = new float[] { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.9f, 0.1f, 0.0f };
        var results = await _database.SearchSimilarWithScoresAsync(airplaneQueryEmbedding, 5);

        // Assert - Either no results, or all scores should be very low (< 0.3)
        // indicating semantic irrelevance
        Assert.True(results.Count == 0 || results.All(r => r.Similarity < 0.3f),
            $"Expected no results or very low similarity scores for unrelated query, but got: " +
            $"{string.Join(", ", results.Select(r => $"{r.Chunk.MemberName}={r.Similarity:F4}"))}");
    }

    /// <summary>
    /// Tests that a semantically related query returns high similarity scores
    /// when searching against code-related chunks with similar meaning.
    /// </summary>
    [Fact]
    public async Task SearchSimilarWithScoresAsync_SemanticallyRelatedQuery_ReturnsHighScores()
    {
        // Arrange
        await _database.InitializeAsync();
        
        // Create code-related chunks with embeddings pointing in similar directions
        var codeChunks = new List<CodeChunk>
        {
            CreateTestChunk("GetUserById", new float[] { 0.9f, 0.1f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f }),
            CreateTestChunk("SaveToDatabase", new float[] { 0.85f, 0.15f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f }),
            CreateTestChunk("ProcessRequest", new float[] { 0.8f, 0.2f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f }),
            CreateTestChunk("ValidateInput", new float[] { 0.1f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.9f, 0.0f }) // Different direction
        };
        await _database.InsertChunksAsync(codeChunks);

        // Act - Search with an embedding similar to the first three chunks
        var queryEmbedding = new float[] { 0.95f, 0.05f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarWithScoresAsync(queryEmbedding, 5);

        // Assert - Should return results with high similarity scores
        Assert.True(results.Count >= 3, "Expected at least 3 results");
        
        // Top results should have very high similarity (> 0.8)
        var topResults = results.Take(3);
        Assert.All(topResults, r => Assert.True(r.Similarity > 0.8f, 
            $"Expected similarity > 0.8 but got {r.Similarity:F4} for {r.Chunk.MemberName}"));
        
        // The dissimilar chunk should have much lower score
        var validateInputResult = results.FirstOrDefault(r => r.Chunk.MemberName == "ValidateInput");
        if (validateInputResult != default)
        {
            Assert.True(validateInputResult.Similarity < 0.5f,
                $"Expected dissimilar chunk to have score < 0.5 but got {validateInputResult.Similarity:F4}");
        }
    }

    /// <summary>
    /// Tests that results are properly ordered by similarity score (highest first).
    /// </summary>
    [Fact]
    public async Task SearchSimilarWithScoresAsync_ResultsOrderedBySimilarity()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateTestChunk("ExactMatch", new float[] { 1.0f, 0.0f, 0.0f }),
            CreateTestChunk("CloseMatch", new float[] { 0.9f, 0.1f, 0.0f }),
            CreateTestChunk("MediumMatch", new float[] { 0.7f, 0.3f, 0.0f }),
            CreateTestChunk("LowMatch", new float[] { 0.5f, 0.5f, 0.0f })
        };
        await _database.InsertChunksAsync(chunks);

        // Act
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarWithScoresAsync(queryEmbedding, 4);

        // Assert
        Assert.Equal(4, results.Count);
        
        // Verify descending order
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(results[i - 1].Similarity >= results[i].Similarity,
                $"Results not ordered by similarity. Index {i - 1}: {results[i - 1].Similarity:F4}, Index {i}: {results[i].Similarity:F4}");
        }
        
        // Verify expected order
        Assert.Equal("ExactMatch", results[0].Chunk.MemberName);
        Assert.Equal("CloseMatch", results[1].Chunk.MemberName);
    }

    /// <summary>
    /// Tests that empty query embedding throws InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task SearchSimilarWithScoresAsync_EmptyQueryEmbedding_ThrowsException()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunk = CreateTestChunk("Test", new float[] { 1.0f, 0.0f, 0.0f });
        await _database.InsertChunkAsync(chunk);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _database.SearchSimilarWithScoresAsync(Array.Empty<float>(), 5));
        Assert.Contains("embedding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tests that updating a chunk's embedding changes search results.
    /// </summary>
    [Fact]
    public async Task InsertChunkAsync_UpdateEmbedding_ChangesSearchResults()
    {
        // Arrange
        await _database.InitializeAsync();
        var id = Guid.NewGuid().ToString();
        var chunk1 = CreateTestChunk("OriginalDirection", new float[] { 1.0f, 0.0f, 0.0f }, id);
        await _database.InsertChunkAsync(chunk1);

        // Act - Update with different embedding
        var chunk2 = CreateTestChunk("NewDirection", new float[] { 0.0f, 1.0f, 0.0f }, id);
        await _database.InsertChunkAsync(chunk2);

        // Assert - Search in original direction should now find nothing or low score
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarWithScoresAsync(queryEmbedding, 5);
        
        // After update, the chunk points in different direction, so similarity should be low
        if (results.Count > 0)
        {
            Assert.True(results[0].Similarity < 0.5f,
                $"Expected low similarity after embedding update, but got {results[0].Similarity:F4}");
        }
        
        // Search in new direction should find it
        var newQueryEmbedding = new float[] { 0.0f, 1.0f, 0.0f };
        var newResults = await _database.SearchSimilarWithScoresAsync(newQueryEmbedding, 5);
        Assert.True(newResults.Count > 0, "Expected to find chunk after updating embedding to new direction");
        Assert.True(newResults[0].Similarity > 0.9f,
            $"Expected high similarity for new direction, but got {newResults[0].Similarity:F4}");
    }

    /// <summary>
    /// Tests that SearchSimilarAsync (without scores) returns same chunks as SearchSimilarWithScoresAsync.
    /// </summary>
    [Fact]
    public async Task SearchSimilarAsync_And_SearchSimilarWithScoresAsync_ReturnSameChunks()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateTestChunk("Method1", new float[] { 0.9f, 0.1f, 0.0f }),
            CreateTestChunk("Method2", new float[] { 0.1f, 0.9f, 0.0f }),
            CreateTestChunk("Method3", new float[] { 0.5f, 0.5f, 0.0f })
        };
        await _database.InsertChunksAsync(chunks);

        // Act
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var resultsWithoutScores = await _database.SearchSimilarAsync(queryEmbedding, 3);
        var resultsWithScores = await _database.SearchSimilarWithScoresAsync(queryEmbedding, 3);

        // Assert
        Assert.Equal(resultsWithoutScores.Count, resultsWithScores.Count);
        for (int i = 0; i < resultsWithoutScores.Count; i++)
        {
            Assert.Equal(resultsWithoutScores[i].MemberName, resultsWithScores[i].Chunk.MemberName);
        }
    }

    /// <summary>
    /// Tests that chunks with identical embeddings are all returned.
    /// </summary>
    [Fact]
    public async Task SearchSimilarAsync_IdenticalEmbeddings_ReturnsAll()
    {
        // Arrange
        await _database.InitializeAsync();
        var chunks = new List<CodeChunk>
        {
            CreateTestChunk("Method1", new float[] { 1.0f, 0.0f, 0.0f }),
            CreateTestChunk("Method2", new float[] { 1.0f, 0.0f, 0.0f }),
            CreateTestChunk("Method3", new float[] { 1.0f, 0.0f, 0.0f })
        };
        await _database.InsertChunksAsync(chunks);

        // Act
        var queryEmbedding = new float[] { 1.0f, 0.0f, 0.0f };
        var results = await _database.SearchSimilarAsync(queryEmbedding, 5);

        // Assert
        Assert.Equal(3, results.Count);
        // All should have perfect or near-perfect similarity
        var resultsWithScores = await _database.SearchSimilarWithScoresAsync(queryEmbedding, 5);
        Assert.All(resultsWithScores, r => Assert.True(r.Similarity > 0.99f,
            $"Expected similarity ~1.0 for identical embeddings, but got {r.Similarity:F4}"));
    }

    // ============================================================================
    // Helper Methods
    // ============================================================================

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
