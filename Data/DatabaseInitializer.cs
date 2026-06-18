using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SemanticSourceCode.Data;

/// <summary>
/// Versioned, idempotent database initializer.
///
/// <para>
/// The initializer owns the schema lifecycle for the SQLite database:
/// <list type="bullet">
///   <item>Applies pending migrations in version order.</item>
///   <item>Tracks the highest applied version in the <c>__SchemaVersion</c> table.</item>
///   <item>Is safe to call from multiple services concurrently
///         (guarded by a <see cref="SemaphoreSlim"/> with double-checked locking).</item>
///   <item>Detects legacy databases (no <c>__SchemaVersion</c> table, but
///         <c>CodeChunks</c> already exists) and marks them as
///         <c>Version=1</c> without destroying data.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Adding a new migration:</b> append a new <see cref="Migration"/> entry to
/// the <see cref="Migrations"/> list with the next version number and a SQL
/// payload (or a delegate). Migrations must be idempotent because they can be
/// re-run on legacy databases.
/// </para>
/// </summary>
public class DatabaseInitializer : IDatabaseInitializer
{
    /// <summary>
    /// Name of the metadata table that records the highest applied schema version.
    /// </summary>
    public const string SchemaVersionTableName = "__SchemaVersion";

    private readonly ISqliteConnectionFactory _factory;
    private readonly ILogger<DatabaseInitializer>? _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private int _currentVersion;
    private volatile bool _initializationAttempted;

    /// <inheritdoc />
    public int CurrentVersion => _currentVersion;

    /// <summary>
    /// Ordered list of migrations. Newest migrations are appended at the end.
    /// Version numbers must be unique, monotonically increasing, and start at 1.
    /// </summary>
    public static IReadOnlyList<Migration> Migrations { get; } = new Migration[]
    {
        // v1 — Fresh-install baseline. Mirrors the schema that the project
        // used before this initializer existed: CodeChunks with all current
        // columns, KeywordIndex, CallEdges, plus their indexes and a
        // back-compat ALTER for columns that may be missing on legacy DBs.
        new Migration(
            1,
            MigrationKind.SchemaBaseline,
            "Baseline schema: CodeChunks (all columns including IsController/IsService/IsMiddleware/HttpMethods/RouteTemplate/ContentHash), KeywordIndex, CallEdges + indexes. Adds legacy ALTER TABLE fallbacks for pre-Issue #2 databases.",
            BuildV1BaselineSql)
    };

    /// <summary>
    /// Highest version defined in <see cref="Migrations"/>.
    /// </summary>
    public static int LatestVersion => Migrations.Count == 0 ? 0 : Migrations[^1].Version;

    public DatabaseInitializer(ISqliteConnectionFactory factory, ILogger<DatabaseInitializer>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initializationAttempted && _currentVersion >= LatestVersion)
        {
            return;
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the lock — another caller may have completed init while we waited.
            if (_initializationAttempted && _currentVersion >= LatestVersion)
            {
                return;
            }

            await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

            await EnsureSchemaVersionTableAsync(connection, ct).ConfigureAwait(false);

            _currentVersion = await ReadCurrentVersionAsync(connection, ct).ConfigureAwait(false);
            _logger?.LogDebug("Database schema version: {Version} (latest: {Latest})", _currentVersion, LatestVersion);

            if (_currentVersion == 0)
            {
                // No version row yet. Either this is a fresh DB, or a legacy
                // DB created before this initializer existed. Detect legacy
                // state for telemetry; the v1 migration is idempotent and
                // safe to run either way, so we don't need to special-case
                // it. Legacy DBs that are missing any table or column will
                // be brought up to date by the same path as fresh DBs.
                if (await TableExistsAsync(connection, "CodeChunks", ct).ConfigureAwait(false))
                {
                    _logger?.LogInformation(
                        "Detected legacy database (CodeChunks exists, no __SchemaVersion row). Applying baseline migration to backfill any missing tables/columns.");
                }
            }

            // Apply any pending migrations in order.
            foreach (var migration in Migrations.Where(m => m.Version > _currentVersion))
            {
                _logger?.LogInformation(
                    "Applying database migration v{Version}: {Description}",
                    migration.Version, migration.Description);
                await migration.ApplyAsync(connection, _logger, ct).ConfigureAwait(false);
                _currentVersion = migration.Version;
                await RecordVersionAsync(connection, migration.Version, ct).ConfigureAwait(false);
            }

            _initializationAttempted = true;
            _logger?.LogInformation(
                "Database initialization complete (version {Version}) at {Path}",
                _currentVersion, _factory.DatabasePath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task EnsureSchemaVersionTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS __SchemaVersion (
                Version INTEGER PRIMARY KEY,
                AppliedAt TEXT NOT NULL
            );";
        await using var cmd = new SqliteCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadCurrentVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = new SqliteCommand("SELECT COALESCE(MAX(Version), 0) FROM __SchemaVersion;", connection);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result == null || result == DBNull.Value) return 0;
        return Convert.ToInt32(result);
    }

    private static async Task RecordVersionAsync(SqliteConnection connection, int version, CancellationToken ct)
    {
        const string sql = "INSERT OR IGNORE INTO __SchemaVersion (Version, AppliedAt) VALUES (@v, @at);";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@v", version);
        cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        const string sql = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name LIMIT 1;";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", tableName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result != null && result != DBNull.Value;
    }

    /// <summary>
    /// Builds the v1 baseline SQL: full schema for a fresh database.
    ///
    /// <para>
    /// The statements are deliberately written so they are safe on legacy
    /// databases too: <c>CREATE TABLE IF NOT EXISTS</c>, <c>CREATE INDEX IF
    /// NOT EXISTS</c>, and <c>TryAddColumnAsync</c> for any column that may
    /// be missing on databases created before that column was added.
    /// </para>
    /// </summary>
    private static async Task BuildV1BaselineAsync(SqliteConnection connection, ILogger? logger, CancellationToken ct)
    {
        const string createCodeChunks = @"
            CREATE TABLE IF NOT EXISTS CodeChunks (
                Id TEXT PRIMARY KEY,
                FilePath TEXT NOT NULL,
                NamespaceName TEXT NOT NULL,
                ClassName TEXT NOT NULL,
                MemberName TEXT NOT NULL,
                MemberType TEXT NOT NULL,
                Content TEXT NOT NULL,
                Signature TEXT NOT NULL,
                Documentation TEXT NOT NULL,
                StartLine INTEGER NOT NULL,
                EndLine INTEGER NOT NULL,
                Embedding BLOB,
                IndexedAt TEXT NOT NULL,
                ContentHash TEXT NOT NULL DEFAULT '',
                IsController INTEGER DEFAULT 0,
                IsService INTEGER DEFAULT 0,
                IsMiddleware INTEGER DEFAULT 0,
                HttpMethods TEXT,
                RouteTemplate TEXT
            );";

        const string createKeywordIndex = @"
            CREATE TABLE IF NOT EXISTS KeywordIndex (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChunkId TEXT NOT NULL,
                Term TEXT NOT NULL,
                Weight REAL NOT NULL DEFAULT 1.0,
                IndexedAt TEXT NOT NULL
            );";

        const string createCallEdges = @"
            CREATE TABLE IF NOT EXISTS CallEdges (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceChunkId TEXT NOT NULL,
                TargetChunkId TEXT NOT NULL,
                CallType TEXT NOT NULL,
                LineNumber INTEGER
            );";

        // 1. Create the three core tables.
        await ExecuteBatchAsync(connection, createCodeChunks, ct).ConfigureAwait(false);
        await ExecuteBatchAsync(connection, createKeywordIndex, ct).ConfigureAwait(false);
        await ExecuteBatchAsync(connection, createCallEdges, ct).ConfigureAwait(false);

        // 2. Backward-compatible ALTER TABLE: add columns that may be missing
        // on legacy databases created before the column existed. Each call
        // swallows "duplicate column" errors so it's a no-op on fresh DBs
        // where the column was created above.
        await TryAddColumnAsync(connection, "CodeChunks", "IsController", "INTEGER DEFAULT 0", logger, ct).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "CodeChunks", "IsService", "INTEGER DEFAULT 0", logger, ct).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "CodeChunks", "IsMiddleware", "INTEGER DEFAULT 0", logger, ct).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "CodeChunks", "HttpMethods", "TEXT", logger, ct).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "CodeChunks", "RouteTemplate", "TEXT", logger, ct).ConfigureAwait(false);
        await TryAddColumnAsync(connection, "CodeChunks", "ContentHash", "TEXT NOT NULL DEFAULT ''", logger, ct).ConfigureAwait(false);

        // 3. Indexes can only be created when the underlying columns exist. On
        // very old legacy DBs the CodeChunks table may be missing columns
        // (e.g. only Id + FilePath). CREATE INDEX on a missing column would
        // fail, so we gate each index on a column-existence check.
        await CreateIndexIfColumnExistsAsync(connection, "CodeChunks", "FilePath", "idx_filepath", logger, ct).ConfigureAwait(false);
        await CreateIndexIfColumnExistsAsync(connection, "CodeChunks", "ClassName", "idx_classname", logger, ct).ConfigureAwait(false);
        await CreateIndexIfColumnExistsAsync(connection, "CodeChunks", "MemberType", "idx_membertype", logger, ct).ConfigureAwait(false);
        await CreateIndexIfColumnExistsAsync(connection, "KeywordIndex", "Term", "idx_keyword_term", logger, ct).ConfigureAwait(false);
        await CreateIndexIfColumnExistsAsync(connection, "KeywordIndex", "ChunkId", "idx_keyword_chunk", logger, ct).ConfigureAwait(false);
        await CreateIndexIfColumnExistsAsync(connection, "CallEdges", "SourceChunkId", "idx_caller", logger, ct).ConfigureAwait(false);
        await CreateIndexIfColumnExistsAsync(connection, "CallEdges", "TargetChunkId", "idx_callee", logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Convenience adapter for the v1 baseline (binds the static SQL-builder
    /// to the <see cref="Migration.ApplyAsync"/> signature).
    /// </summary>
    private static Task BuildV1BaselineSql(SqliteConnection connection, ILogger? logger, CancellationToken ct)
        => BuildV1BaselineAsync(connection, logger, ct);

    private static async Task ExecuteBatchAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new SqliteCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a column to a table, swallowing the "duplicate column" error
    /// (SqliteErrorCode 1) that occurs on subsequent runs or when the column
    /// was already created by the <c>CREATE TABLE</c> statement.
    /// </summary>
    private static async Task TryAddColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string type,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqliteCommand(
                $"ALTER TABLE {table} ADD COLUMN {column} {type};", connection);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Column already exists — expected on legacy DBs that pre-date the column
            // or on fresh DBs where the column was created in the same migration.
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "Failed to add column {Column} to {Table}: {Message}", column, table, ex.Message);
        }
    }

    /// <summary>
    /// Creates an index only if the underlying column exists. This makes the
    /// v1 baseline migration safe to run against very old legacy databases
    /// whose tables may be missing one or more columns (in which case
    /// <c>TryAddColumnAsync</c> will add the column first, and on a re-run
    /// the index can be created safely).
    /// </summary>
    private static async Task CreateIndexIfColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        string indexName,
        ILogger? logger,
        CancellationToken ct)
    {
        const string checkSql = "SELECT 1 FROM pragma_table_info(@table) WHERE name = @column;";
        await using (var checkCmd = new SqliteCommand(checkSql, connection))
        {
            checkCmd.Parameters.AddWithValue("@table", table);
            checkCmd.Parameters.AddWithValue("@column", column);
            var exists = await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (exists == null || exists == DBNull.Value)
            {
                logger?.LogDebug(
                    "Skipping index {Index} on {Table}.{Column}: column does not exist",
                    indexName, table, column);
                return;
            }
        }

        const string createSql = "CREATE INDEX IF NOT EXISTS {0} ON {1}({2});";
        await using var createCmd = new SqliteCommand(
            string.Format(createSql, indexName, table, column), connection);
        await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// One versioned migration step. Migrations are applied in <see cref="Version"/>
/// order, each only once per database.
/// </summary>
public sealed record Migration(
    int Version,
    MigrationKind Kind,
    string Description,
    Func<SqliteConnection, ILogger?, CancellationToken, Task> ApplyAsync);

/// <summary>
/// Loose classification of migration payloads. Future migration kinds
/// (data backfill, index-only changes) can extend this enum without
/// breaking callers.
/// </summary>
public enum MigrationKind
{
    /// <summary>
    /// Pure schema change (CREATE TABLE / CREATE INDEX / ALTER TABLE).
    /// </summary>
    SchemaBaseline = 0
}
