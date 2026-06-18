using Microsoft.Extensions.Configuration;
using SemanticSourceCode.Models;
using SemanticSourceCode.Services;
using SemanticSourceCode.Tests.Data;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Unit tests for the changes introduced in Issue #2:
/// - Semantic chunk IDs (stable across re-indexing runs)
/// - Content-based change detection
/// - Cleanup of chunks for deleted/renamed files
/// </summary>
public class Issue2SemanticIdsTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqliteVssDatabase _database;

    public Issue2SemanticIdsTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"issue2_{Guid.NewGuid()}.db");
        _database = TestDatabaseFactory.BuildSqliteVssDatabase(_testDbPath);
    }

    public void Dispose()
    {
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }

    // ---------- CodeAnalyzer.ComputeSemanticId ----------

    [Fact]
    public void ComputeSemanticId_SameInputs_ReturnsSameId()
    {
        var id1 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar()");
        var id2 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar()");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeSemanticId_DifferentFile_ReturnsDifferentId()
    {
        var id1 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar()");
        var id2 = CodeAnalyzer.ComputeSemanticId(
            "/src/Baz.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar()");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeSemanticId_DifferentMemberName_ReturnsDifferentId()
    {
        var id1 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar()");
        var id2 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Qux", "Method", 0, "void Qux()");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeSemanticId_DifferentChunkIndex_ReturnsDifferentId()
    {
        var id1 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar()");
        var id2 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 1, "void Bar()");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeSemanticId_ReturnsLowercaseHex_64Chars()
    {
        var id = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar()");

        Assert.Equal(64, id.Length);
        Assert.Matches("^[0-9a-f]{64}$", id);
    }

    [Fact]
    public void ComputeSemanticId_DifferentMethodOverloads_ReturnsDifferentIds()
    {
        // Two method overloads with same name but different parameter lists
        // must produce different chunk IDs to avoid collisions.
        var id1 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar(int x)");
        var id2 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar(int x, int y)");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeSemanticId_DifferentConstructorOverloads_ReturnsDifferentIds()
    {
        // Two constructor overloads must produce different IDs (MemberName == ClassName).
        var id1 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Foo", "Constructor", 0, "public Foo(int x)");
        var id2 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Foo", "Constructor", 0, "public Foo(int x, int y)");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeSemanticId_OverloadVsNonOverload_ReturnsDifferentIds()
    {
        // Sanity: a method and a "phantom" overload with the same name+signature
        // are still different if the signature actually differs.
        var id1 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar()");
        var id2 = CodeAnalyzer.ComputeSemanticId(
            "/src/Foo.cs", "MyApp", "Foo", "Bar", "Method", 0, "void Bar(int x)");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void FinalizeChunkIdentity_MethodOverloads_GetDifferentIds()
    {
        // End-to-end: two chunks that differ only in signature get different IDs.
        var chunk1 = new CodeChunk
        {
            FilePath = "/src/Foo.cs",
            NamespaceName = "MyApp",
            ClassName = "Foo",
            MemberName = "Bar",
            MemberType = "Method",
            ChunkIndex = 0,
            Signature = "void Bar(int x)",
            Content = "body1"
        };
        var chunk2 = new CodeChunk
        {
            FilePath = "/src/Foo.cs",
            NamespaceName = "MyApp",
            ClassName = "Foo",
            MemberName = "Bar",
            MemberType = "Method",
            ChunkIndex = 0,
            Signature = "void Bar(int x, int y)",
            Content = "body2"
        };

        CodeAnalyzer.FinalizeChunkIdentity(chunk1);
        CodeAnalyzer.FinalizeChunkIdentity(chunk2);

        Assert.NotEqual(chunk1.Id, chunk2.Id);
    }

    [Fact]
    public void FinalizeChunkIdentity_ConstructorOverloads_GetDifferentIds()
    {
        // End-to-end: two constructor chunks with different signatures get different IDs.
        var chunk1 = new CodeChunk
        {
            FilePath = "/src/Foo.cs",
            NamespaceName = "MyApp",
            ClassName = "Foo",
            MemberName = "Foo",
            MemberType = "Constructor",
            ChunkIndex = 0,
            Signature = "public Foo(IConfiguration configuration)",
            Content = "ctor body 1"
        };
        var chunk2 = new CodeChunk
        {
            FilePath = "/src/Foo.cs",
            NamespaceName = "MyApp",
            ClassName = "Foo",
            MemberName = "Foo",
            MemberType = "Constructor",
            ChunkIndex = 0,
            Signature = "public Foo(IConfiguration configuration, ILogger<Foo>? logger)",
            Content = "ctor body 2"
        };

        CodeAnalyzer.FinalizeChunkIdentity(chunk1);
        CodeAnalyzer.FinalizeChunkIdentity(chunk2);

        Assert.NotEqual(chunk1.Id, chunk2.Id);
    }

    // ---------- CodeAnalyzer.ComputeContentHash ----------

    [Fact]
    public void ComputeContentHash_SameContent_ReturnsSameHash()
    {
        var hash1 = CodeAnalyzer.ComputeContentHash("public void Foo() { }");
        var hash2 = CodeAnalyzer.ComputeContentHash("public void Foo() { }");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeContentHash_DifferentContent_ReturnsDifferentHash()
    {
        var hash1 = CodeAnalyzer.ComputeContentHash("public void Foo() { }");
        var hash2 = CodeAnalyzer.ComputeContentHash("public void Bar() { }");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeContentHash_EmptyContent_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CodeAnalyzer.ComputeContentHash(string.Empty));
    }

    // ---------- CodeAnalyzer.FinalizeChunkIdentity ----------

    [Fact]
    public void FinalizeChunkIdentity_PopulatesIdAndHash()
    {
        var chunk = new CodeChunk
        {
            FilePath = "/src/Foo.cs",
            NamespaceName = "MyApp",
            ClassName = "Foo",
            MemberName = "Bar",
            MemberType = "Method",
            Content = "public void Bar() { }",
            ChunkIndex = 0
        };

        CodeAnalyzer.FinalizeChunkIdentity(chunk);

        Assert.False(string.IsNullOrEmpty(chunk.Id));
        Assert.Equal(64, chunk.Id.Length);
        Assert.False(string.IsNullOrEmpty(chunk.ContentHash));
        Assert.Equal(64, chunk.ContentHash.Length);
    }

    [Fact]
    public void FinalizeChunkIdentity_IsIdempotent()
    {
        var chunk = new CodeChunk
        {
            FilePath = "/src/Foo.cs",
            NamespaceName = "MyApp",
            ClassName = "Foo",
            MemberName = "Bar",
            MemberType = "Method",
            Content = "public void Bar() { }",
            ChunkIndex = 0
        };

        CodeAnalyzer.FinalizeChunkIdentity(chunk);
        var id1 = chunk.Id;
        var hash1 = chunk.ContentHash;

        CodeAnalyzer.FinalizeChunkIdentity(chunk);

        Assert.Equal(id1, chunk.Id);
        Assert.Equal(hash1, chunk.ContentHash);
    }

    // ---------- SqliteVssDatabase: ContentHash column ----------

    [Fact]
    public async Task InsertChunkAsync_PersistsContentHash()
    {
        await _database.InitializeAsync();
        var chunk = new CodeChunk
        {
            Id = "test-id-1",
            FilePath = "/src/Foo.cs",
            NamespaceName = "MyApp",
            ClassName = "Foo",
            MemberName = "Bar",
            MemberType = "Method",
            Content = "public void Bar() { }",
            Signature = "public void Bar()",
            Documentation = "",
            StartLine = 1,
            EndLine = 1,
            ContentHash = "abc123",
            Embedding = new byte[12],
            IndexedAt = DateTime.UtcNow
        };

        await _database.InsertChunkAsync(chunk);

        var hashes = await _database.GetAllContentHashesAsync();
        Assert.True(hashes.ContainsKey("test-id-1"));
        Assert.Equal("abc123", hashes["test-id-1"]);
    }

    [Fact]
    public async Task GetAllContentHashesAsync_EmptyDatabase_ReturnsEmpty()
    {
        await _database.InitializeAsync();

        var hashes = await _database.GetAllContentHashesAsync();

        Assert.Empty(hashes);
    }

    [Fact]
    public async Task GetAllContentHashesAsync_AfterMultipleInserts_ReturnsAll()
    {
        await _database.InitializeAsync();
        for (int i = 0; i < 5; i++)
        {
            await _database.InsertChunkAsync(new CodeChunk
            {
                Id = $"id-{i}",
                FilePath = $"/src/File{i}.cs",
                NamespaceName = "MyApp",
                ClassName = "C",
                MemberName = $"M{i}",
                MemberType = "Method",
                Content = $"// chunk {i}",
                Signature = $"M{i}()",
                Documentation = "",
                StartLine = 1,
                EndLine = 1,
                ContentHash = $"hash-{i}",
                Embedding = new byte[12],
                IndexedAt = DateTime.UtcNow
            });
        }

        var hashes = await _database.GetAllContentHashesAsync();

        Assert.Equal(5, hashes.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal($"hash-{i}", hashes[$"id-{i}"]);
        }
    }

    // ---------- SqliteVssDatabase: Semantic IDs enable UPSERT ----------

    [Fact]
    public async Task InsertChunkAsync_SameSemanticId_ReplacesExisting()
    {
        await _database.InitializeAsync();
        var sharedId = "semantic-id-fixed";

        var original = new CodeChunk
        {
            Id = sharedId,
            FilePath = "/src/Foo.cs",
            NamespaceName = "MyApp",
            ClassName = "Foo",
            MemberName = "Bar",
            MemberType = "Method",
            Content = "public void Bar() { }",
            Signature = "public void Bar()",
            Documentation = "old docs",
            StartLine = 1,
            EndLine = 1,
            ContentHash = "hash-old",
            Embedding = new byte[12],
            IndexedAt = DateTime.UtcNow.AddDays(-1)
        };

        var updated = new CodeChunk
        {
            Id = sharedId,
            FilePath = "/src/Foo.cs",
            NamespaceName = "MyApp",
            ClassName = "Foo",
            MemberName = "Bar",
            MemberType = "Method",
            Content = "public void Bar() { Console.WriteLine(\"hi\"); }",
            Signature = "public void Bar()",
            Documentation = "new docs",
            StartLine = 1,
            EndLine = 1,
            ContentHash = "hash-new",
            Embedding = new byte[12],
            IndexedAt = DateTime.UtcNow
        };

        await _database.InsertChunkAsync(original);
        await _database.InsertChunkAsync(updated);

        // After re-indexing with the same semantic ID, only one chunk should exist
        var hashes = await _database.GetAllContentHashesAsync();
        Assert.Single(hashes);
        Assert.Equal("hash-new", hashes[sharedId]);
    }

    [Fact]
    public async Task InsertChunksAsync_DuplicateSemanticIds_KeepsOnlyLast()
    {
        await _database.InitializeAsync();
        var sharedId = "dup-id";

        var chunks = new List<CodeChunk>
        {
            new()
            {
                Id = sharedId,
                FilePath = "/src/A.cs",
                NamespaceName = "N",
                ClassName = "C",
                MemberName = "M",
                MemberType = "Method",
                Content = "// v1",
                Signature = "M()",
                Documentation = "",
                StartLine = 1,
                EndLine = 1,
                ContentHash = "h1",
                Embedding = new byte[12],
                IndexedAt = DateTime.UtcNow
            },
            new()
            {
                Id = sharedId,
                FilePath = "/src/A.cs",
                NamespaceName = "N",
                ClassName = "C",
                MemberName = "M",
                MemberType = "Method",
                Content = "// v2",
                Signature = "M()",
                Documentation = "",
                StartLine = 1,
                EndLine = 1,
                ContentHash = "h2",
                Embedding = new byte[12],
                IndexedAt = DateTime.UtcNow
            }
        };

        await _database.InsertChunksAsync(chunks);

        var hashes = await _database.GetAllContentHashesAsync();
        Assert.Single(hashes);
        Assert.Equal("h2", hashes[sharedId]);
    }

    // ---------- SqliteVssDatabase: DeleteChunksForNonExistentFilesAsync ----------

    [Fact]
    public async Task DeleteChunksForNonExistentFilesAsync_NoFilesToDelete_ReturnsZero()
    {
        await _database.InitializeAsync();
        // Use a real temporary file that exists
        var tempFile = Path.Combine(Path.GetTempPath(), $"exists_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(tempFile, "// test");

        try
        {
            await _database.InsertChunkAsync(new CodeChunk
            {
                Id = "exists-1",
                FilePath = tempFile,
                NamespaceName = "N",
                ClassName = "C",
                MemberName = "M",
                MemberType = "Method",
                Content = "// exists",
                Signature = "M()",
                Documentation = "",
                StartLine = 1,
                EndLine = 1,
                Embedding = new byte[12],
                IndexedAt = DateTime.UtcNow
            });

            var removed = await _database.DeleteChunksForNonExistentFilesAsync();

            Assert.Equal(0, removed);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DeleteChunksForNonExistentFilesAsync_DeletedFileOnDisk_RemovesChunks()
    {
        await _database.InitializeAsync();
        var tempFile = Path.Combine(Path.GetTempPath(), $"willdisappear_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(tempFile, "// test");

        await _database.InsertChunkAsync(new CodeChunk
        {
            Id = "ghost-1",
            FilePath = tempFile,
            NamespaceName = "N",
            ClassName = "C",
            MemberName = "M",
            MemberType = "Method",
            Content = "// ghost",
            Signature = "M()",
            Documentation = "",
            StartLine = 1,
            EndLine = 1,
            Embedding = new byte[12],
            IndexedAt = DateTime.UtcNow
        });

        // Now delete the file from disk
        File.Delete(tempFile);

        var removed = await _database.DeleteChunksForNonExistentFilesAsync();

        Assert.Equal(1, removed);
        var hashes = await _database.GetAllContentHashesAsync();
        Assert.Empty(hashes);
    }

    [Fact]
    public async Task DeleteChunksForNonExistentFilesAsync_KeepsChunksForExistingFiles()
    {
        await _database.InitializeAsync();
        var tempFile = Path.Combine(Path.GetTempPath(), $"survives_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(tempFile, "// test");

        try
        {
            await _database.InsertChunkAsync(new CodeChunk
            {
                Id = "survivor-1",
                FilePath = tempFile,
                NamespaceName = "N",
                ClassName = "C",
                MemberName = "M",
                MemberType = "Method",
                Content = "// survivor",
                Signature = "M()",
                Documentation = "",
                StartLine = 1,
                EndLine = 1,
                Embedding = new byte[12],
                IndexedAt = DateTime.UtcNow
            });

            var removed = await _database.DeleteChunksForNonExistentFilesAsync();

            Assert.Equal(0, removed);
            var hashes = await _database.GetAllContentHashesAsync();
            Assert.Single(hashes);
            Assert.True(hashes.ContainsKey("survivor-1"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DeleteChunksForNonExistentFilesAsync_MultipleChunksSameMissingFile_RemovesAll()
    {
        await _database.InitializeAsync();
        var tempFile = Path.Combine(Path.GetTempPath(), $"multi_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(tempFile, "// test");

        for (int i = 0; i < 3; i++)
        {
            await _database.InsertChunkAsync(new CodeChunk
            {
                Id = $"multi-{i}",
                FilePath = tempFile,
                NamespaceName = "N",
                ClassName = "C",
                MemberName = $"M{i}",
                MemberType = "Method",
                Content = $"// chunk {i}",
                Signature = $"M{i}()",
                Documentation = "",
                StartLine = i * 10,
                EndLine = i * 10 + 5,
                Embedding = new byte[12],
                IndexedAt = DateTime.UtcNow
            });
        }

        File.Delete(tempFile);

        var removed = await _database.DeleteChunksForNonExistentFilesAsync();

        Assert.Equal(1, removed);
        var hashes = await _database.GetAllContentHashesAsync();
        Assert.Empty(hashes);
    }

    [Fact]
    public async Task DeleteChunksForNonExistentFilesAsync_EmptyDatabase_ReturnsZero()
    {
        await _database.InitializeAsync();

        var removed = await _database.DeleteChunksForNonExistentFilesAsync();

        Assert.Equal(0, removed);
    }
}
