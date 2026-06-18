using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace SemanticSourceCode.Data;

/// <summary>
/// Default <see cref="ISqliteConnectionFactory"/> that opens a file-backed
/// SQLite database at the path configured under <c>Database:Path</c>
/// (default: <c>codechunks.db</c> in the current working directory).
///
/// The connection string format is intentionally kept minimal
/// (<c>Data Source={path};</c>) so the <see cref="SqliteConnection"/> pool
/// keys on the same string and existing pooling behaviour is preserved.
/// </summary>
public class SqliteConnectionFactory : ISqliteConnectionFactory
{
    /// <summary>
    /// Configuration key that holds the database file path.
    /// </summary>
    public const string DatabasePathConfigKey = "Database:Path";

    /// <summary>
    /// Fallback database file name when no configuration is supplied.
    /// </summary>
    public const string DefaultDatabaseFileName = "codechunks.db";

    private readonly string _connectionString;

    /// <inheritdoc />
    public string DatabasePath { get; }

    public SqliteConnectionFactory(IConfiguration configuration)
    {
        var path = configuration[DatabasePathConfigKey] ?? DefaultDatabaseFileName;
        DatabasePath = path;
        _connectionString = $"Data Source={path};";
    }

    /// <inheritdoc />
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
