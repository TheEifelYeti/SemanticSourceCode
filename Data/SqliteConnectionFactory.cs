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
///
/// <para>
/// On every newly opened connection, the factory applies a small set of
/// performance-oriented PRAGMAs (Issue #21):
/// <list type="bullet">
///   <item><c>journal_mode = WAL</c> — better read/write concurrency and the
///         foundation for skipping per-commit fsyncs during bulk indexing.</item>
///   <item><c>synchronous = NORMAL</c> — drop the per-commit fsync while
///         remaining crash-safe under WAL. This is the main reason batched
///         inserts get a 5–10× write speedup.</item>
///   <item><c>temp_store = MEMORY</c> — keep temporary B-trees in RAM.</item>
///   <item><c>cache_size = -64000</c> — ~64 MB page cache.</item>
/// </list>
/// Both WAL and synchronous are sticky (persisted in the database file), so
/// applying them on every connection is essentially free.
/// </para>
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

        await ApplyPerformancePragmasAsync(connection, ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Applies performance-oriented PRAGMAs to the given connection.
    /// Made <c>internal</c> so tests can call it on a raw connection.
    /// </summary>
    internal static async Task ApplyPerformancePragmasAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        // journal_mode and synchronous are persistent in the DB file, but we
        // set them every time so the behaviour is explicit and self-healing
        // (e.g. if someone opened the file with sqlite3 CLI and reset them).
        // temp_store and cache_size are per-connection, so they must be set here.
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -64000;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}