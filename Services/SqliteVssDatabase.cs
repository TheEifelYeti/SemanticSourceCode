using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Models;
using System.Globalization;

namespace SemanticSourceCode.Services;

public class SqliteVssDatabase : IVectorDatabase
{
    private readonly string _dbPath;
    private readonly ILogger<SqliteVssDatabase>? _logger;
    private bool _isInitialized = false;

    public SqliteVssDatabase(IConfiguration configuration, ILogger<SqliteVssDatabase>? logger = null)
    {
        _logger = logger;
        _dbPath = configuration["Database:Path"] ?? "codechunks.db";
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // Create table for code chunks
        var createTableCmd = @"
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
                IndexedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_filepath ON CodeChunks(FilePath);
            CREATE INDEX IF NOT EXISTS idx_classname ON CodeChunks(ClassName);
            CREATE INDEX IF NOT EXISTS idx_membertype ON CodeChunks(MemberType);
        ";

        using var command = new SqliteCommand(createTableCmd, connection);
        await command.ExecuteNonQueryAsync();

        _isInitialized = true;
        _logger?.LogInformation("Database initialized at {Path}", _dbPath);
    }

    public async Task InsertChunkAsync(CodeChunk chunk)
    {
        await EnsureInitializedAsync();

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var insertCmd = @"
            INSERT OR REPLACE INTO CodeChunks 
            (Id, FilePath, NamespaceName, ClassName, MemberName, MemberType, Content, Signature, Documentation, StartLine, EndLine, Embedding, IndexedAt)
            VALUES (@Id, @FilePath, @NamespaceName, @ClassName, @MemberName, @MemberType, @Content, @Signature, @Documentation, @StartLine, @EndLine, @Embedding, @IndexedAt)";

        using var command = new SqliteCommand(insertCmd, connection);
        command.Parameters.AddWithValue("@Id", chunk.Id);
        command.Parameters.AddWithValue("@FilePath", chunk.FilePath);
        command.Parameters.AddWithValue("@NamespaceName", chunk.NamespaceName);
        command.Parameters.AddWithValue("@ClassName", chunk.ClassName);
        command.Parameters.AddWithValue("@MemberName", chunk.MemberName);
        command.Parameters.AddWithValue("@MemberType", chunk.MemberType);
        command.Parameters.AddWithValue("@Content", chunk.Content);
        command.Parameters.AddWithValue("@Signature", chunk.Signature);
        command.Parameters.AddWithValue("@Documentation", chunk.Documentation);
        command.Parameters.AddWithValue("@StartLine", chunk.StartLine);
        command.Parameters.AddWithValue("@EndLine", chunk.EndLine);
        command.Parameters.AddWithValue("@Embedding", chunk.Embedding ?? Array.Empty<byte>());
        command.Parameters.AddWithValue("@IndexedAt", chunk.IndexedAt.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertChunksAsync(IEnumerable<CodeChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            await InsertChunkAsync(chunk);
        }
    }

    public async Task<List<(CodeChunk Chunk, float Similarity)>> SearchSimilarWithScoresAsync(float[] queryEmbedding, int topK = 5)
    {
        await EnsureInitializedAsync();

        if (queryEmbedding == null || queryEmbedding.Length == 0)
        {
            _logger?.LogError("Search called with empty query embedding. Is the embedding service running?");
            throw new InvalidOperationException("Query embedding is empty. Check if Ollama/LM Studio is running and the model is loaded.");
        }

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // Fetch all chunks and calculate cosine similarity in memory
        var selectCmd = "SELECT * FROM CodeChunks WHERE Embedding IS NOT NULL AND LENGTH(Embedding) > 0";
        using var command = new SqliteCommand(selectCmd, connection);
        
        var results = new List<(CodeChunk Chunk, float Similarity)>();
        
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var embeddingBytes = reader.GetFieldValue<byte[]>(11);
            if (embeddingBytes == null || embeddingBytes.Length == 0) continue;

            var chunkEmbedding = ConvertByteArrayToFloatArray(embeddingBytes);
            var similarity = CosineSimilarity(queryEmbedding, chunkEmbedding);
            
            var chunk = new CodeChunk
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
            
            results.Add((chunk, similarity));
        }

        // Return top K results ordered by similarity
        return results
            .OrderByDescending(r => r.Similarity)
            .Take(topK)
            .Select(r => (r.Chunk, r.Similarity))
            .ToList();
    }

    public async Task<List<CodeChunk>> SearchSimilarAsync(float[] queryEmbedding, int topK = 5)
    {
        var results = await SearchSimilarWithScoresAsync(queryEmbedding, topK);
        return results.Select(r => r.Chunk).ToList();
    }

    public async Task ClearDatabaseAsync()
    {
        await EnsureInitializedAsync();

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = new SqliteCommand("DELETE FROM CodeChunks", connection);
        await command.ExecuteNonQueryAsync();
        
        _logger?.LogInformation("Database cleared");
    }

    public Task<bool> IsInitializedAsync()
    {
        return Task.FromResult(_isInitialized);
    }

    private async Task EnsureInitializedAsync()
    {
        if (!_isInitialized)
            await InitializeAsync();
    }

    private float[] ConvertByteArrayToFloatArray(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            _logger?.LogWarning("Embedding dimension mismatch: query={QueryDim}, chunk={ChunkDim}. Truncating to minimum.", a.Length, b.Length);
            
            // Truncate to minimum length
            var minLength = Math.Min(a.Length, b.Length);
            var aTruncated = new float[minLength];
            var bTruncated = new float[minLength];
            Array.Copy(a, aTruncated, minLength);
            Array.Copy(b, bTruncated, minLength);
            
            a = aTruncated;
            b = bTruncated;
        }

        float dotProduct = 0;
        float normA = 0;
        float normB = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
            return 0;

        return dotProduct / (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
