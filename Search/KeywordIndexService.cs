using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Models;
using System.Globalization;

namespace SemanticSourceCode.Search;

/// <summary>
/// SQLite-backed keyword index for code chunks.
/// Uses a regular terms table (not FTS5, since FTS5 may not be available in all SQLite builds).
/// Provides weighted keyword search on ClassName, MemberName, Signature, FilePath, Namespace.
/// </summary>
public class KeywordIndexService : IKeywordIndex
{
    private readonly string _dbPath;
    private readonly ILogger<KeywordIndexService>? _logger;

    public KeywordIndexService(IConfiguration configuration, ILogger<KeywordIndexService>? logger = null)
    {
        _logger = logger;
        _dbPath = configuration["Database:Path"] ?? "codechunks.db";
    }

    public async Task IndexChunkAsync(CodeChunk chunk)
    {
        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // Delete existing terms for this chunk first
        using (var deleteCmd = new SqliteCommand("DELETE FROM KeywordIndex WHERE ChunkId = @chunkId", connection))
        {
            deleteCmd.Parameters.AddWithValue("@chunkId", chunk.Id);
            await deleteCmd.ExecuteNonQueryAsync();
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
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task IndexChunksAsync(IEnumerable<CodeChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            await IndexChunkAsync(chunk);
        }
    }

    public async Task<List<(CodeChunk Chunk, float Score)>> SearchKeywordMatchesAsync(string query, int topK = 20)
    {
        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

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

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
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
            var connectionString = $"Data Source={_dbPath};";
            using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            using var cmd = new SqliteCommand(
                "SELECT name FROM sqlite_master WHERE type='table' AND name='KeywordIndex'",
                connection);
            var result = await cmd.ExecuteScalarAsync();
            return result != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task ClearAsync()
    {
        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var cmd = new SqliteCommand("DELETE FROM KeywordIndex", connection);
        await cmd.ExecuteNonQueryAsync();
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

        // Content terms (weight 0.3) — limit to avoid index bloat
        var contentTerms = Tokenize(chunk.Content).Take(50);
        foreach (var term in contentTerms)
        {
            terms.Add((term, 0.3f));
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
