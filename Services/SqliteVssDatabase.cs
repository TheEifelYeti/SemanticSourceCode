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

            CREATE TABLE IF NOT EXISTS CallEdges (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceChunkId TEXT NOT NULL,
                TargetChunkId TEXT NOT NULL,
                CallType TEXT NOT NULL,
                LineNumber INTEGER,
                FOREIGN KEY (SourceChunkId) REFERENCES CodeChunks(Id),
                FOREIGN KEY (TargetChunkId) REFERENCES CodeChunks(Id)
            );

            CREATE INDEX IF NOT EXISTS idx_caller ON CallEdges(SourceChunkId);
            CREATE INDEX IF NOT EXISTS idx_callee ON CallEdges(TargetChunkId);
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

    /// <summary>
    /// Adds a call edge between two code chunks.
    /// </summary>
    public async Task AddCallEdgeAsync(string sourceChunkId, string targetChunkId, string callType, int lineNumber)
    {
        await EnsureInitializedAsync();

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var insertCmd = @"
            INSERT OR IGNORE INTO CallEdges (SourceChunkId, TargetChunkId, CallType, LineNumber)
            VALUES (@SourceChunkId, @TargetChunkId, @CallType, @LineNumber)";

        using var command = new SqliteCommand(insertCmd, connection);
        command.Parameters.AddWithValue("@SourceChunkId", sourceChunkId);
        command.Parameters.AddWithValue("@TargetChunkId", targetChunkId);
        command.Parameters.AddWithValue("@CallType", callType);
        command.Parameters.AddWithValue("@LineNumber", lineNumber);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Gets all chunks that call the specified chunk.
    /// </summary>
    public async Task<List<CodeChunk>> GetCallersAsync(string chunkId)
    {
        await EnsureInitializedAsync();

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var selectCmd = @"
            SELECT c.* FROM CodeChunks c
            JOIN CallEdges e ON c.Id = e.SourceChunkId
            WHERE e.TargetChunkId = @ChunkId
            ORDER BY e.LineNumber";

        using var command = new SqliteCommand(selectCmd, connection);
        command.Parameters.AddWithValue("@ChunkId", chunkId);

        var results = new List<CodeChunk>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadChunkFromReader(reader));
        }

        return results;
    }

    /// <summary>
    /// Gets all chunks called by the specified chunk.
    /// </summary>
    public async Task<List<CodeChunk>> GetCalleesAsync(string chunkId)
    {
        await EnsureInitializedAsync();

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var selectCmd = @"
            SELECT c.* FROM CodeChunks c
            JOIN CallEdges e ON c.Id = e.TargetChunkId
            WHERE e.SourceChunkId = @ChunkId
            ORDER BY e.LineNumber";

        using var command = new SqliteCommand(selectCmd, connection);
        command.Parameters.AddWithValue("@ChunkId", chunkId);

        var results = new List<CodeChunk>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadChunkFromReader(reader));
        }

        return results;
    }

    /// <summary>
    /// Gets the impact radius of a chunk (callers up to N levels deep).
    /// </summary>
    public async Task<List<CodeChunk>> GetImpactRadiusAsync(string chunkId, int maxDepth = 3)
    {
        await EnsureInitializedAsync();

        var visited = new HashSet<string>();
        var results = new List<CodeChunk>();
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((chunkId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            
            if (depth >= maxDepth || visited.Contains(currentId))
                continue;
                
            visited.Add(currentId);

            var callers = await GetCallersAsync(currentId);
            foreach (var caller in callers)
            {
                if (!visited.Contains(caller.Id))
                {
                    results.Add(caller);
                    queue.Enqueue((caller.Id, depth + 1));
                }
            }
        }

        return results;
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
