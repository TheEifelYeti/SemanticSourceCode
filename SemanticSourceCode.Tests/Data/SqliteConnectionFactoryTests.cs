using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SemanticSourceCode.Data;
using Xunit;

namespace SemanticSourceCode.Tests.Data;

/// <summary>
/// Unit tests for <see cref="SqliteConnectionFactory"/>.
/// </summary>
public class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly string _tempDir;

    public SqliteConnectionFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _testDbPath = Path.Combine(_tempDir, "factory.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private IConfiguration BuildConfig(string? dbPath = null)
    {
        var dict = new Dictionary<string, string?>();
        if (dbPath != null)
        {
            dict["Database:Path"] = dbPath;
        }
        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    [Fact]
    public void DatabasePath_UsesConfiguredPath_WhenProvided()
    {
        // Arrange
        var config = BuildConfig(_testDbPath);

        // Act
        var factory = new SqliteConnectionFactory(config);

        // Assert
        Assert.Equal(_testDbPath, factory.DatabasePath);
    }

    [Fact]
    public void DatabasePath_DefaultsToCodechunksDb_WhenConfigMissing()
    {
        // Arrange
        var config = BuildConfig(dbPath: null);

        // Act
        var factory = new SqliteConnectionFactory(config);

        // Assert
        Assert.Equal("codechunks.db", factory.DatabasePath);
    }

    [Fact]
    public async Task OpenAsync_ReturnsOpenConnection()
    {
        // Arrange
        var factory = new SqliteConnectionFactory(BuildConfig(_testDbPath));

        // Act
        await using var connection = await factory.OpenAsync();

        // Assert
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task OpenAsync_CreatesDatabaseFileOnFirstOpen()
    {
        // Arrange
        Assert.False(File.Exists(_testDbPath));
        var factory = new SqliteConnectionFactory(BuildConfig(_testDbPath));

        // Act
        await using var connection = await factory.OpenAsync();
        // Run a no-op query to make sure the file is fully materialized.
        await using var cmd = new SqliteCommand("SELECT 1;", connection);
        await cmd.ExecuteScalarAsync();

        // Assert
        Assert.True(File.Exists(_testDbPath));
    }

    [Fact]
    public async Task OpenAsync_RespectsCancellationToken_WhenCancelledBefore()
    {
        // Arrange
        var factory = new SqliteConnectionFactory(BuildConfig(_testDbPath));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.OpenAsync(cts.Token));
    }

    [Fact]
    public async Task OpenAsync_AllowsSequentialOpenClose()
    {
        // Arrange
        var factory = new SqliteConnectionFactory(BuildConfig(_testDbPath));

        // Act — open and close twice
        for (int i = 0; i < 2; i++)
        {
            await using var conn = await factory.OpenAsync();
            Assert.Equal(ConnectionState.Open, conn.State);
        }

        // Assert — file still exists and is reusable
        Assert.True(File.Exists(_testDbPath));
        await using var cmd = new SqliteCommand(
            "SELECT name FROM sqlite_master WHERE type='table';", await factory.OpenAsync());
        await cmd.ExecuteScalarAsync();
    }
}
