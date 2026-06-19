using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Data;
using SemanticSourceCode.Models;
using System.Globalization;

namespace SemanticSourceCode.Search;

/// <summary>
/// SQLite-backed keyword index for code chunks.
/// Uses a regular terms table (not FTS5, since FTS5 may not be available in all SQLite builds).
/// Provides weighted keyword search on ClassName, MemberName, Signature, FilePath, Namespace.
///
/// <para>
/// Schema lifecycle and connection opening are delegated to
/// <see cref="IDatabaseInitializer"/> and <see cref="ISqliteConnectionFactory"/>
/// respectively. This service no longer tracks its own
/// <c>_isInitialized</c> flag or <c>_initLock</c> \u2014 the initializer
/// is idempotent and thread-safe.
/// </para>
/// </summary>
public class KeywordIndexService : IKeywordIndex
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly IDatabaseInitializer _initializer;
    private readonly ILogger<KeywordIndexService>? _logger;

    public KeywordIndexService(
        ISqliteConnectionFactory factory,
        IDatabaseInitializer initializer,
        ILogger<KeywordIndexService>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _logger = logger;
    }

    public async Task IndexChunkAsync(CodeChunk chunk)
    {
        await _initializer.EnsureInitializedAsync().ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        // Delete existing terms for this chunk first
        using (var deleteCmd = new SqliteCommand("DELETE FROM KeywordIndex WHERE ChunkId = @chunkId", connection))
        {
            deleteCmd.Parameters.AddWithValue("@chunkId", chunk.Id);
            await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var terms = ExtractTerms(chunk);

        foreach (var (term, weight) in terms)
        {
            var insertCmd = @"
                INSERT INTO KeywordIndex (ChunkId, Term, Weight, IndexedAt)
                VALUES (@ChunkId, @Term, @Weight, @IndexedAt)";

            using var command = new SqliteCommand(insertCmd, connection);
            command.Parameters.AddWithValue("@ChunkId", chunk.Id);
            command.Parameters.AddWithValue("@Term", term.ToLowerInvariant());
            command.Parameters.AddWithValue("@Weight", weight);
            command.Parameters.AddWithValue("@IndexedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    public async Task IndexChunksAsync(IEnumerable<CodeChunk> chunks)
    {
        await _initializer.EnsureInitializedAsync().ConfigureAwait(false);

        var chunkList = chunks as IReadOnlyCollection<CodeChunk> ?? chunks.ToList();
        if (chunkList.Count == 0) return;

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

        try
        {
            // Reused prepared statements: 1 DELETE per chunk + 1 INSERT per term.
            using var deleteCmd = new SqliteCommand("DELETE FROM KeywordIndex WHERE ChunkId = @chunkId", connection, tx);
            var deleteParam = deleteCmd.Parameters.Add("@chunkId", SqliteType.Text);

            using var insertCmd = new SqliteCommand(@"
                INSERT INTO KeywordIndex (ChunkId, Term, Weight, IndexedAt)
                VALUES (@ChunkId, @Term, @Weight, @IndexedAt)", connection, tx);
            var insertChunkParam = insertCmd.Parameters.Add("@ChunkId", SqliteType.Text);
            var insertTermParam = insertCmd.Parameters.Add("@Term", SqliteType.Text);
            var insertWeightParam = insertCmd.Parameters.Add("@Weight", SqliteType.Real);
            var insertIndexedAtParam = insertCmd.Parameters.Add("@IndexedAt", SqliteType.Text);

            var indexedAtString = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            foreach (var chunk in chunkList)
            {
                deleteParam.Value = chunk.Id;
                await deleteCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                var terms = ExtractTerms(chunk);
                foreach (var (term, weight) in terms)
                {
                    insertChunkParam.Value = chunk.Id;
                    insertTermParam.Value = term.ToLowerInvariant();
                    insertWeightParam.Value = weight;
                    insertIndexedAtParam.Value = indexedAtString;
                    await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<List<(CodeChunk Chunk, float Score)>> SearchKeywordMatchesAsync(string query, int topK = 20)
    {
        await _initializer.EnsureInitializedAsync().ConfigureAwait(false);

        // If the CodeChunks table doesn't exist (e.g. fresh DB where the user
        // is searching before indexing), the JOIN would fail. Return empty
        // list to preserve graceful degradation.
        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        using (var checkCmd = new SqliteCommand(
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='CodeChunks'", connection))
        {
            var exists = await checkCmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (exists == null)
            {
                _logger?.LogDebug("CodeChunks table does not exist; returning empty keyword results");
                return new List<(CodeChunk, float)>();
            }
        }

        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
            return new List<(CodeChunk, float)>();

        // Build a query that sums weights for matching terms per chunk
        var placeholders = string.Join(",", queryTerms.Select((_, i) => $"@term{i}"));
        var sql = $@"
            SELECT 
                c.*,
                COALESCE(SUM(k.Weight), 0) as raw_score,
                COUNT(DISTINCT k.Term) as match_count
            FROM KeywordIndex k
            JOIN CodeChunks c ON k.ChunkId = c.Id
            WHERE k.Term IN ({placeholders})
            GROUP BY k.ChunkId
            ORDER BY raw_score DESC
            LIMIT @limit";

        using var command = new SqliteCommand(sql, connection);
        for (int i = 0; i < queryTerms.Count; i++)
        {
            command.Parameters.AddWithValue($"@term{i}", queryTerms[i]);
        }
        command.Parameters.AddWithValue("@limit", topK);

        var results = new List<(CodeChunk Chunk, float Score)>();
        float maxRawScore = 0;

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var rawScore = (float)reader.GetDouble(reader.GetOrdinal("raw_score"));
            var matchCount = reader.GetInt32(reader.GetOrdinal("match_count"));

            // Bonus for matching multiple distinct terms
            var adjustedScore = rawScore * (1.0f + (matchCount - 1) * 0.2f);
            if (adjustedScore > maxRawScore) maxRawScore = adjustedScore;

            var chunk = ReadChunkFromReader(reader);
            results.Add((chunk, adjustedScore));
        }

        // Normalize scores to [0, 1]
        if (maxRawScore > 0)
        {
            results = results.Select(r => (r.Chunk, r.Score / maxRawScore)).ToList();
        }

        return results;
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

            using var cmd = new SqliteCommand(
                "SELECT name FROM sqlite_master WHERE type='table' AND name='KeywordIndex'",
                connection);
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task ClearAsync()
    {
        await _initializer.EnsureInitializedAsync().ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        using var cmd = new SqliteCommand("DELETE FROM KeywordIndex", connection);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts weighted terms from a code chunk for indexing.
    /// Weight: ClassName=1.0, MemberName=0.8, Signature=0.6, FilePath=0.4, Namespace=0.4, Content=0.3
    /// </summary>
    internal List<(string Term, float Weight)> ExtractTerms(CodeChunk chunk)
    {
        var terms = new List<(string Term, float Weight)>();

        // ClassName terms (weight 1.0)
        foreach (var term in Tokenize(chunk.ClassName))
        {
            terms.Add((term, 1.0f));
        }

        // MemberName terms (weight 0.8)
        foreach (var term in Tokenize(chunk.MemberName))
        {
            terms.Add((term, 0.8f));
        }

        // Signature terms (weight 0.6)
        foreach (var term in Tokenize(chunk.Signature))
        {
            terms.Add((term, 0.6f));
        }

        // FilePath terms (weight 0.4)
        foreach (var term in Tokenize(Path.GetFileNameWithoutExtension(chunk.FilePath)))
        {
            terms.Add((term, 0.4f));
        }

        // Namespace terms (weight 0.4)
        foreach (var term in Tokenize(chunk.NamespaceName))
        {
            terms.Add((term, 0.4f));
        }

        // Content terms (weight 0.3) \u2014 increased from 50 to 200 to catch more identifiers
        var contentTerms = Tokenize(chunk.Content).Take(200);
        foreach (var term in contentTerms)
        {
            terms.Add((term, 0.3f));
        }

        // Also add identifier parts from content (e.g., Auto.Tuer.Scheibe \u2192 auto, tuer, scheibe)
        var identifierParts = ExtractIdentifierParts(chunk.Content);
        foreach (var part in identifierParts.Take(100))
        {
            // Check if term already exists (from Tokenize) and upgrade weight if needed
            var existingIndex = terms.FindIndex(t => t.Term == part);
            if (existingIndex >= 0)
            {
                // Upgrade weight to identifier weight (0.35) if it's currently lower
                var current = terms[existingIndex];
                if (current.Weight < 0.35f)
                {
                    terms[existingIndex] = (current.Term, 0.35f);
                }
            }
            else
            {
                terms.Add((part, 0.35f)); // slightly higher weight than generic content
            }
        }

        return terms;
    }

    /// <summary>
    /// Tokenizes text into searchable terms.
    /// Splits on CamelCase, PascalCase, and non-alphanumeric characters.
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var terms = new List<string>();

        // First, split on non-alphanumeric
        var parts = text.Split(new[] { ' ', '_', '.', '/', '\\', '(', ')', '[', ']', '{', '}', ';', ',', ':', '"', '\'', '\t', '\n', '\r', '<', '>', '=', '+', '-', '*', '&', '|', '!', '?', '@', '#', '$', '%', '^', '~', '`' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            // Split CamelCase/PascalCase
            var camelSplit = SplitCamelCase(part);
            foreach (var sub in camelSplit)
            {
                if (sub.Length >= 2)
                {
                    terms.Add(sub.ToLowerInvariant());
                }
            }

            // Also add the full part if it's meaningful
            if (part.Length >= 2)
            {
                terms.Add(part.ToLowerInvariant());
            }
        }

        return terms.Distinct().ToList();
    }

    private static List<string> SplitCamelCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return new List<string>();

        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        current.Append(input[0]);

        for (int i = 1; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]) && current.Length > 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            current.Append(input[i]);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>
    /// Extracts identifier parts from content with dot notation.
    /// E.g., "Auto.Tuer.Scheibe" \u2192 ["auto", "tuer", "scheibe", "auto.tuer.scheibe"]
    /// </summary>
    internal static List<string> ExtractIdentifierParts(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var parts = new List<string>();

        // Find patterns like Identifier.Identifier.Identifier
        // Split on common delimiters first
        var tokens = text.Split(new[] { ' ', '\t', '\n', '\r', '(', ')', '{', '}', '[', ']', ';', ',', '"', '\'', '+', '-', '*', '/', '=', '!', '<', '>', '&', '|', '?', '@', '#', '$', '%', '^', '~', '`' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            // Check if token contains dots (potential member access)
            if (token.Contains('.') && token.Length >= 3)
            {
                var segments = token.Split('.');

                // Only process if at least one segment is long enough
                var hasLongSegments = segments.Any(s => s.Length >= 2);
                if (!hasLongSegments)
                {
                    continue;
                }

                // Add individual segments
                foreach (var segment in segments)
                {
                    if (segment.Length >= 2)
                    {
                        parts.Add(segment.ToLowerInvariant());
                    }
                }

                // Also add the full path
                parts.Add(token.ToLowerInvariant());
            }
        }

        return parts.Distinct().ToList();
    }

    private CodeChunk ReadChunkFromReader(SqliteDataReader reader)
    {
        return new CodeChunk
        {
            Id = reader.GetString(0),
            FilePath = reader.GetString(1),
            NamespaceName = reader.GetString(2),
            ClassName = reader.GetString(3),
            MemberName = reader.GetString(4),
            MemberType = reader.GetString(5),
            Content = reader.GetString(6),
            Signature = reader.GetString(7),
            Documentation = reader.GetString(8),
            StartLine = reader.GetInt32(9),
            EndLine = reader.GetInt32(10),
            IndexedAt = DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }
}
