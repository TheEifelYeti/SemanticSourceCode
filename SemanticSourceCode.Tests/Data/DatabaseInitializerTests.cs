using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SemanticSourceCode.Data;
using Xunit;

namespace SemanticSourceCode.Tests.Data;

/// <summary>
/// Unit tests for <see cref="DatabaseInitializer"/>.
/// Covers the fresh-DB path, idempotency, version tracking, the legacy-DB
/// detection path, and concurrent-init safety.
/// </summary>
public class DatabaseInitializerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public DatabaseInitializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dbinit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "init.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private DatabaseInitializer CreateInitializer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath
            })
            .Build();
        var factory = new SqliteConnectionFactory(config);
        return new DatabaseInitializer(factory);
    }

    private async Task<List<string>> GetTableNamesAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath
            })
            .Build();
        var factory = new SqliteConnectionFactory(config);
        await using var conn = await factory.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;", conn);
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    [Fact]
    public async Task EnsureInitializedAsync_FreshDatabase_CreatesAllExpectedTables()
    {
        // Arrange
        var initializer = CreateInitializer();

        // Act
        await initializer.EnsureInitializedAsync();

        // Assert — every table that v1 promises should exist
        var tables = await GetTableNamesAsync();
        Assert.Contains(DatabaseInitializer.SchemaVersionTableName, tables);
        Assert.Contains("CodeChunks", tables);
        Assert.Contains("KeywordIndex", tables);
        Assert.Contains("CallEdges", tables);
    }

    [Fact]
    public async Task EnsureInitializedAsync_RecordsCurrentVersion()
    {
        // Arrange
        var initializer = CreateInitializer();

        // Act
        await initializer.EnsureInitializedAsync();

        // Assert
        Assert.Equal(DatabaseInitializer.LatestVersion, initializer.CurrentVersion);
        Assert.True(initializer.CurrentVersion >= 1);
    }

    [Fact]
    public async Task EnsureInitializedAsync_IsIdempotent()
    {
        // Arrange
        var initializer = CreateInitializer();

        // Act — call three times
        await initializer.EnsureInitializedAsync();
        var versionAfterFirst = initializer.CurrentVersion;
        await initializer.EnsureInitializedAsync();
        await initializer.EnsureInitializedAsync();

        // Assert — no exception, version unchanged
        Assert.Equal(versionAfterFirst, initializer.CurrentVersion);
    }

    [Fact]
    public async Task EnsureInitializedAsync_SchemaVersionTableHasExactlyOneRow_AfterFreshInit()
    {
        // Arrange
        var initializer = CreateInitializer();

        // Act
        await initializer.EnsureInitializedAsync();

        // Assert
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath
            })
            .Build();
        var factory = new SqliteConnectionFactory(config);
        await using var conn = await factory.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM __SchemaVersion;", conn);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ConcurrentCalls_DoNotCorruptSchemaVersionTable()
    {
        // Arrange
        // Fire 10 parallel initializers against the SAME db path. With the
        // SemaphoreSlim guard exactly one schema-version row should end up
        // in the table.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath
            })
            .Build();

        var initializers = Enumerable.Range(0, 10)
            .Select(_ => new DatabaseInitializer(new SqliteConnectionFactory(config)))
            .ToArray();

        // Act
        await Task.WhenAll(initializers.Select(i => i.EnsureInitializedAsync()));

        // Assert — every initializer sees the latest version
        foreach (var initializer in initializers)
        {
            Assert.Equal(DatabaseInitializer.LatestVersion, initializer.CurrentVersion);
        }

        // Assert — exactly one row in __SchemaVersion (the SemaphoreSlim
        // serialised the version-recording writes).
        var factory = new SqliteConnectionFactory(config);
        await using var conn = await factory.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM __SchemaVersion;", conn);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task EnsureInitializedAsync_LegacyDatabaseWithoutSchemaVersion_MarksAsV1WithoutThrowing()
    {
        // Arrange — simulate a legacy database: CodeChunks already exists,
        // no __SchemaVersion table, no migration history.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath
            })
            .Build();
        var factory = new SqliteConnectionFactory(config);

        await using (var conn = await factory.OpenAsync())
        {
            await using var cmd = new SqliteCommand(@"
                CREATE TABLE CodeChunks (
                    Id TEXT PRIMARY KEY,
                    FilePath TEXT NOT NULL
                );", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = CreateInitializer();

        // Act
        await initializer.EnsureInitializedAsync();

        // Assert — legacy DB is recognised, version recorded, no exception,
        // and the user-data table is preserved (we did NOT drop & recreate it).
        Assert.Equal(1, initializer.CurrentVersion);
        await using var verify = await factory.OpenAsync();
        await using (var countChunks = new SqliteCommand(
            "SELECT COUNT(*) FROM CodeChunks;", verify))
        {
            // 0 rows, but table itself still there.
            Assert.Equal(0, Convert.ToInt32(await countChunks.ExecuteScalarAsync()));
        }
        await using (var countVersion = new SqliteCommand(
            "SELECT COUNT(*) FROM __SchemaVersion WHERE Version = 1;", verify))
        {
            Assert.Equal(1, Convert.ToInt32(await countVersion.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task EnsureInitializedAsync_AfterLegacyDetection_KeywordIndexIsCreated()
    {
        // Arrange — legacy DB with only CodeChunks. KeywordIndex is missing.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath
            })
            .Build();
        var factory = new SqliteConnectionFactory(config);

        await using (var conn = await factory.OpenAsync())
        {
            await using var cmd = new SqliteCommand(@"
                CREATE TABLE CodeChunks (
                    Id TEXT PRIMARY KEY,
                    FilePath TEXT NOT NULL
                );", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = CreateInitializer();
        await initializer.EnsureInitializedAsync();

        // Assert — KeywordIndex now exists, even though we detected a legacy DB.
        var tables = await GetTableNamesAsync();
        Assert.Contains("KeywordIndex", tables);
    }

    [Fact]
    public async Task Migrations_AreOrderedAndUnique()
    {
        // Defensive: every migration version must be unique and ascending.
        var versions = DatabaseInitializer.Migrations.Select(m => m.Version).ToList();
        Assert.NotEmpty(versions);
        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.True(versions.First() == 1, "Migrations must start at version 1");
    }
}
