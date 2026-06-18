using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Data;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;

namespace SemanticSourceCode.Tests.Data;

/// <summary>
/// Helpers shared by tests that need a real <see cref="SqliteVssDatabase"/>
/// (or its factory + initializer collaborators) pointing at a temporary
/// SQLite file. Centralising the wiring here keeps the existing tests
/// readable after the constructor signature change introduced in Issue #14.
/// </summary>
public static class TestDatabaseFactory
{
    /// <summary>
    /// Builds an <see cref="IConfiguration"/> that resolves
    /// <c>Database:Path</c> to <paramref name="dbPath"/>.
    /// </summary>
    public static IConfiguration BuildConfig(string dbPath)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = dbPath
            })
            .Build();
    }

    /// <summary>
    /// Builds a wired-up <see cref="SqliteConnectionFactory"/> +
    /// <see cref="DatabaseInitializer"/> pair against the given temp path.
    /// </summary>
    public static (ISqliteConnectionFactory factory, IDatabaseInitializer initializer) BuildFactory(string dbPath)
    {
        var config = BuildConfig(dbPath);
        var factory = new SqliteConnectionFactory(config);
        var initializer = new DatabaseInitializer(factory);
        return (factory, initializer);
    }

    /// <summary>
    /// Convenience: builds a fresh <see cref="SqliteVssDatabase"/> against
    /// the given temp DB path. Replaces the old
    /// <c>new SqliteVssDatabase(config)</c> constructor call in tests.
    /// </summary>
    public static SqliteVssDatabase BuildSqliteVssDatabase(string dbPath)
    {
        var (factory, initializer) = BuildFactory(dbPath);
        return new SqliteVssDatabase(factory, initializer);
    }

    /// <summary>
    /// Builds a fresh <see cref="SqliteVssDatabase"/> with an explicit
    /// <see cref="ILogger{TCategoryName}"/>. Replaces the old
    /// <c>new SqliteVssDatabase(config, logger)</c> call in tests.
    /// </summary>
    public static SqliteVssDatabase BuildSqliteVssDatabase<T>(string dbPath, ILogger<T> logger)
    {
        var (factory, initializer) = BuildFactory(dbPath);
        return new SqliteVssDatabase(factory, initializer, logger);
    }

    /// <summary>
    /// Builds a fresh <see cref="KeywordIndexService"/> against the given
    /// temp DB path. Replaces the old
    /// <c>new KeywordIndexService(config[, logger])</c> constructor call
    /// in tests.
    /// </summary>
    public static KeywordIndexService BuildKeywordIndexService(string dbPath)
    {
        var (factory, initializer) = BuildFactory(dbPath);
        return new KeywordIndexService(factory, initializer);
    }

    /// <summary>
    /// Same as <see cref="BuildKeywordIndexService(string)"/> but with an
    /// explicit <see cref="ILogger{TCategoryName}"/>.
    /// </summary>
    public static KeywordIndexService BuildKeywordIndexService<T>(string dbPath, ILogger<T> logger)
    {
        var (factory, initializer) = BuildFactory(dbPath);
        return new KeywordIndexService(factory, initializer, logger);
    }
}
