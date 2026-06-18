namespace SemanticSourceCode.Data;

/// <summary>
/// Owns the database schema lifecycle. Ensures the schema is up-to-date
/// before any service touches the database. Idempotent and thread-safe;
/// multiple services may call <see cref="EnsureInitializedAsync"/>
/// concurrently from the same DI container.
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// Applies any pending migrations. Safe to call multiple times.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task EnsureInitializedAsync(CancellationToken ct = default);

    /// <summary>
    /// The highest schema version that has been applied. Returns <c>0</c>
    /// when the database has not been initialized yet.
    /// </summary>
    int CurrentVersion { get; }
}
