using Microsoft.Extensions.Configuration;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using Xunit;

namespace SemanticSourceCode.Tests.Search;

/// <summary>
/// Regression test for the "no such table: KeywordIndex" bug that was
/// discovered when running the IndexCommand against the SemanticSourceCode
/// project itself (08.06.2026). The KeywordIndexService opened a fresh
/// SQLite connection without ensuring the table existed.
/// </summary>
public class KeywordIndexServiceRegressionTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly KeywordIndexService _service;

    public KeywordIndexServiceRegressionTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"kwindex_{Guid.NewGuid()}.db");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _testDbPath
            })
            .Build();
        _service = new KeywordIndexService(config);
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
    }

    [Fact]
    public async Task IndexChunkAsync_FreshDatabase_CreatesTableAndDoesNotThrow()
    {
        // The bug: IndexChunkAsync threw "no such table: KeywordIndex" when
        // called on a brand new database that had never been initialised by
        // SqliteVssDatabase. After the fix, KeywordIndexService creates its
        // own table lazily via EnsureInitializedAsync.
        var chunk = new CodeChunk
        {
            Id = "test-chunk-1",
            FilePath = "/src/Test.cs",
            NamespaceName = "MyApp",
            ClassName = "TestClass",
            MemberName = "TestMethod",
            MemberType = "Method",
            Content = "public void TestMethod() { }",
            Signature = "public void TestMethod()",
            Documentation = "",
            StartLine = 1,
            EndLine = 1
        };

        // Should NOT throw
        await _service.IndexChunkAsync(chunk);

        // Table should now exist
        Assert.True(await _service.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_BeforeIndex_ReturnsFalse()
    {
        // Before any chunk is indexed, the table doesn't exist yet, so
        // IsAvailableAsync should return false (allowing callers to fall back).
        // Note: After our fix, the first IndexChunkAsync creates the table
        // via EnsureInitializedAsync. But IsAvailableAsync alone should still
        // return false on a fresh DB to preserve the fallback semantics.
        var available = await _service.IsAvailableAsync();
        Assert.False(available);
    }

    [Fact]
    public async Task ClearAsync_FreshDatabase_DoesNotThrow()
    {
        // ClearAsync should also call EnsureInitializedAsync and not throw
        // on a fresh database.
        await _service.ClearAsync();
    }

    [Fact]
    public async Task IndexChunksAsync_EmptyEnumerable_DoesNotThrow()
    {
        await _service.IndexChunksAsync(Enumerable.Empty<CodeChunk>());
    }
}
