using Microsoft.Data.Sqlite;

namespace SemanticSourceCode.Data;

/// <summary>
/// Centralizes opening SQLite database connections so every service goes through
/// one chokepoint. Makes it possible to swap the underlying connection logic
/// (e.g. for in-memory test DBs) and ensures the connection string format is
/// consistent across the codebase.
/// </summary>
public interface ISqliteConnectionFactory
{
    /// <summary>
    /// Opens a new <see cref="SqliteConnection"/> to the configured database
    /// file. The caller owns the returned connection and is responsible for
    /// disposing it (typically via <c>await using</c>).
    /// </summary>
    /// <param name="ct">Cancellation token forwarded to <see cref="SqliteConnection.OpenAsync(CancellationToken)"/>.</param>
    Task<SqliteConnection> OpenAsync(CancellationToken ct = default);

    /// <summary>
    /// Absolute or relative path to the SQLite database file (read-only).
    /// </summary>
    string DatabasePath { get; }
}
