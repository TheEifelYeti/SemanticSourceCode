using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using System.Globalization;

namespace SemanticSourceCode.Services;

public class SqliteVssDatabase : IVectorDatabase
{
    private readonly string _dbPath;
    private readonly ILogger<SqliteVssDatabase>? _logger;
    private bool _isInitialized = false;
    private bool _vecLoaded = false;
    private int? _embeddingDimensions;

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

        // Try to load sqlite-vec extension for vector search
        await TryLoadVecExtensionAsync(connection);

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
                IndexedAt TEXT NOT NULL,
                ContentHash TEXT NOT NULL DEFAULT '',
                IsController INTEGER DEFAULT 0,
                IsService INTEGER DEFAULT 0,
                IsMiddleware INTEGER DEFAULT 0,
                HttpMethods TEXT,
                RouteTemplate TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_filepath ON CodeChunks(FilePath);
            CREATE INDEX IF NOT EXISTS idx_classname ON CodeChunks(ClassName);
            CREATE INDEX IF NOT EXISTS idx_membertype ON CodeChunks(MemberType);

            -- Backward-compatible migration: add new columns if they don't exist
            SELECT CASE WHEN EXISTS(SELECT 1 FROM pragma_table_info('CodeChunks') WHERE name = 'IsController') THEN 1 ELSE 0 END;
            ALTER TABLE CodeChunks ADD COLUMN IsController INTEGER DEFAULT 0;
            ALTER TABLE CodeChunks ADD COLUMN IsService INTEGER DEFAULT 0;
            ALTER TABLE CodeChunks ADD COLUMN IsMiddleware INTEGER DEFAULT 0;
            ALTER TABLE CodeChunks ADD COLUMN HttpMethods TEXT;
            ALTER TABLE CodeChunks ADD COLUMN RouteTemplate TEXT;

            -- Keyword index for hybrid search
            -- Note: Using regular table instead of FTS5 for backward compatibility
            -- (FTS5 may not be available in all SQLite builds)
            CREATE TABLE IF NOT EXISTS KeywordIndex (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChunkId TEXT NOT NULL,
                Term TEXT NOT NULL,
                Weight REAL NOT NULL DEFAULT 1.0,
                IndexedAt TEXT NOT NULL,
                FOREIGN KEY (ChunkId) REFERENCES CodeChunks(Id)
            );

            CREATE INDEX IF NOT EXISTS idx_keyword_term ON KeywordIndex(Term);
            CREATE INDEX IF NOT EXISTS idx_keyword_chunk ON KeywordIndex(ChunkId);

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

        // Backward-compatible migration: add new columns if they don't exist
        await TryAddColumnAsync(connection, "CodeChunks", "IsController", "INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection, "CodeChunks", "IsService", "INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection, "CodeChunks", "IsMiddleware", "INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection, "CodeChunks", "HttpMethods", "TEXT");
        await TryAddColumnAsync(connection, "CodeChunks", "RouteTemplate", "TEXT");
        await TryAddColumnAsync(connection, "CodeChunks", "ContentHash", "TEXT NOT NULL DEFAULT ''");

        // Create vec0 virtual table for ANN vector search if extension loaded
        if (_vecLoaded)
        {
            await CreateVecVirtualTableAsync(connection, _embeddingDimensions);
        }

        _isInitialized = true;
        _logger?.LogInformation("Database initialized at {Path} (vec0: {VecStatus})", _dbPath, _vecLoaded ? "enabled" : "fallback");
    }

    private async Task TryLoadVecExtensionAsync(SqliteConnection connection)
    {
        try
        {
            // Enable extension loading
            using var enableCmd = new SqliteCommand("SELECT load_extension('vec0')", connection);
            await enableCmd.ExecuteNonQueryAsync();
            _vecLoaded = true;
            _logger?.LogInformation("sqlite-vec extension loaded successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Could not load sqlite-vec extension: {Message}. Falling back to in-memory cosine similarity.", ex.Message);
            _vecLoaded = false;
        }
    }

    private async Task CreateVecVirtualTableAsync(SqliteConnection connection, int? dimensions = null)
    {
        try
        {
            // Drop existing vec table if schema changed
            var dropCmd = "DROP TABLE IF EXISTS vec_embeddings;";
            using (var cmd = new SqliteCommand(dropCmd, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Create vec0 virtual table with dynamic dimensions
            int dim = dimensions ?? 768;
            var createVecCmd = $@"
                CREATE VIRTUAL TABLE IF NOT EXISTS vec_embeddings USING vec0(
                    embedding FLOAT[{dim}] distance_metric=cosine,
                    chunk_id TEXT
                );
            ";
            using (var cmd = new SqliteCommand(createVecCmd, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }
            _logger?.LogInformation("vec0 virtual table created for ANN search with {Dim} dimensions", dim);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to create vec0 virtual table: {Message}", ex.Message);
            _vecLoaded = false;
        }
    }

    private async Task TryAddColumnAsync(SqliteConnection connection, string table, string column, string type)
    {
        try
        {
            using var cmd = new SqliteCommand($"ALTER TABLE {table} ADD COLUMN {column} {type};", connection);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // duplicate column name
        {
            // Column already exists — this is expected on subsequent runs
        }
    }

    public async Task InsertChunkAsync(CodeChunk chunk)
    {
        await EnsureInitializedAsync();

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var insertCmd = @"
            INSERT OR REPLACE INTO CodeChunks 
            (Id, FilePath, NamespaceName, ClassName, MemberName, MemberType, Content, Signature, Documentation, StartLine, EndLine, Embedding, IndexedAt, ContentHash, IsController, IsService, IsMiddleware, HttpMethods, RouteTemplate)
            VALUES (@Id, @FilePath, @NamespaceName, @ClassName, @MemberName, @MemberType, @Content, @Signature, @Documentation, @StartLine, @EndLine, @Embedding, @IndexedAt, @ContentHash, @IsController, @IsService, @IsMiddleware, @HttpMethods, @RouteTemplate)";

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
        command.Parameters.AddWithValue("@ContentHash", chunk.ContentHash ?? string.Empty);
        command.Parameters.AddWithValue("@IsController", chunk.IsController ? 1 : 0);
        command.Parameters.AddWithValue("@IsService", chunk.IsService ? 1 : 0);
        command.Parameters.AddWithValue("@IsMiddleware", chunk.IsMiddleware ? 1 : 0);
        command.Parameters.AddWithValue("@HttpMethods", chunk.HttpMethods ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@RouteTemplate", chunk.RouteTemplate ?? (object)DBNull.Value);

        await command.ExecuteNonQueryAsync();

        // Also insert into vec0 virtual table for ANN search
        if (_vecLoaded && chunk.Embedding != null && chunk.Embedding.Length > 0)
        {
            await InsertVecEmbeddingAsync(connection, chunk);
        }
    }

    private async Task InsertVecEmbeddingAsync(SqliteConnection connection, CodeChunk chunk)
    {
        try
        {
            var embeddingFloats = ConvertByteArrayToFloatArray(chunk.Embedding!);
            
            // Detect embedding dimensions from first chunk
            if (_embeddingDimensions == null)
            {
                _embeddingDimensions = embeddingFloats.Length;
                _logger?.LogInformation("Detected embedding dimensions: {Dimensions}", _embeddingDimensions);
                
                // Recreate vec table with correct dimensions
                await RecreateVecTableWithDimensionsAsync(connection, _embeddingDimensions.Value);
            }

            // Convert floats to JSON array for vec0
            var embeddingJson = "[" + string.Join(",", embeddingFloats.Select(f => f.ToString("G9", CultureInfo.InvariantCulture))) + "]";
            
            var insertVecCmd = @"
                INSERT INTO vec_embeddings (embedding, chunk_id) 
                VALUES (json(?), ?)
                ON CONFLICT(chunk_id) DO UPDATE SET embedding = json(?);";

            using var command = new SqliteCommand(insertVecCmd, connection);
            command.Parameters.AddWithValue("@embedding", embeddingJson);
            command.Parameters.AddWithValue("@chunk_id", chunk.Id);
            command.Parameters.AddWithValue("@embedding_update", embeddingJson);
            
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to insert vec embedding for chunk {ChunkId}: {Message}", chunk.Id, ex.Message);
        }
    }

    private async Task RecreateVecTableWithDimensionsAsync(SqliteConnection connection, int dimensions)
    {
        try
        {
            var dropCmd = "DROP TABLE IF EXISTS vec_embeddings;";
            using (var cmd = new SqliteCommand(dropCmd, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            var createCmd = $@"
                CREATE VIRTUAL TABLE vec_embeddings USING vec0(
                    embedding FLOAT[{dimensions}] distance_metric=cosine,
                    chunk_id TEXT
                );";
            
            using (var cmd = new SqliteCommand(createCmd, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }
            
            _logger?.LogInformation("Recreated vec_embeddings table with {Dimensions} dimensions", dimensions);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to recreate vec table: {Message}. Disabling vec0.", ex.Message);
            _vecLoaded = false;
        }
    }

    public async Task InsertChunksAsync(IEnumerable<CodeChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            await InsertChunkAsync(chunk);
        }
    }

    public async Task<List<(CodeChunk Chunk, float Similarity)>> SearchSimilarWithScoresAsync(float[] queryEmbedding, SearchFilter filter, int topK = 5)
    {
        var results = await SearchSimilarWithScoresAsync(queryEmbedding, topK * 3); // Get more for filtering
        return ApplyFilter(results, filter).Take(topK).ToList();
    }

    public async Task<List<CodeChunk>> SearchSimilarAsync(float[] queryEmbedding, SearchFilter filter, int topK = 5)
    {
        var results = await SearchSimilarWithScoresAsync(queryEmbedding, filter, topK);
        return results.Select(r => r.Chunk).ToList();
    }

    private List<(CodeChunk Chunk, float Similarity)> ApplyFilter(List<(CodeChunk Chunk, float Similarity)> results, SearchFilter filter)
    {
        var filtered = results.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.Namespace))
        {
            filtered = filtered.Where(r => r.Chunk.NamespaceName.Contains(filter.Namespace, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.ClassName))
        {
            filtered = filtered.Where(r => r.Chunk.ClassName.Contains(filter.ClassName, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.HttpMethod))
        {
            filtered = filtered.Where(r =>
                r.Chunk.HttpMethods != null &&
                r.Chunk.HttpMethods.Contains(filter.HttpMethod, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.FilePathPattern))
        {
            var pattern = filter.FilePathPattern.Replace("*", "").Replace("?", "");
            filtered = filtered.Where(r => r.Chunk.FilePath.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.ChunkType))
        {
            filtered = filtered.Where(r =>
                r.Chunk.MemberType.Equals(filter.ChunkType, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.IsController.HasValue)
        {
            filtered = filtered.Where(r => r.Chunk.IsController == filter.IsController.Value);
        }

        if (filter.IsService.HasValue)
        {
            filtered = filtered.Where(r => r.Chunk.IsService == filter.IsService.Value);
        }

        if (filter.IsMiddleware.HasValue)
        {
            filtered = filtered.Where(r => r.Chunk.IsMiddleware == filter.IsMiddleware.Value);
        }

        return filtered.ToList();
    }

    public async Task<List<(CodeChunk Chunk, float Similarity)>> SearchSimilarWithScoresAsync(float[] queryEmbedding, int topK = 5)
    {
        await EnsureInitializedAsync();

        if (queryEmbedding == null || queryEmbedding.Length == 0)
        {
            _logger?.LogError("Search called with empty query embedding. Is the embedding service running?");
            throw new InvalidOperationException("Query embedding is empty. Check if Ollama/LM Studio is running and the model is loaded.");
        }

        // Use sqlite-vec ANN search if available
        if (_vecLoaded && _embeddingDimensions.HasValue && queryEmbedding.Length == _embeddingDimensions.Value)
        {
            try
            {
                return await SearchWithVecAsync(queryEmbedding, topK);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("vec0 search failed, falling back to cosine: {Message}", ex.Message);
                // Fall through to fallback
            }
        }

        // Fallback: in-memory cosine similarity
        return await SearchWithCosineFallbackAsync(queryEmbedding, topK);
    }

    private async Task<List<(CodeChunk Chunk, float Similarity)>> SearchWithVecAsync(float[] queryEmbedding, int topK)
    {
        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var embeddingJson = "[" + string.Join(",", queryEmbedding.Select(f => f.ToString("G9", CultureInfo.InvariantCulture))) + "]";
        
        // vec0 uses MATCH operator with KNN
        var selectCmd = $@"
            SELECT 
                vec_distance_cosine(v.embedding, json(?)) as distance,
                c.*
            FROM vec_embeddings v
            JOIN CodeChunks c ON v.chunk_id = c.Id
            WHERE v.embedding MATCH json(?)
            ORDER BY distance
            LIMIT {topK};";

        using var command = new SqliteCommand(selectCmd, connection);
        command.Parameters.AddWithValue("@query", embeddingJson);
        command.Parameters.AddWithValue("@match", embeddingJson);

        var results = new List<(CodeChunk Chunk, float Similarity)>();
        
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var distance = reader.GetDouble(0); // distance is first column
            var similarity = (float)(1.0 - distance); // Convert cosine distance to similarity
            
            var chunk = ReadChunkFromReader(reader, 1); // offset by 1 for distance column
            results.Add((chunk, similarity));
        }

        _logger?.LogDebug("vec0 search returned {Count} results", results.Count);
        return results;
    }

    private async Task<List<(CodeChunk Chunk, float Similarity)>> SearchWithCosineFallbackAsync(float[] queryEmbedding, int topK)
    {
        _logger?.LogDebug("Using in-memory cosine similarity fallback");
        
        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

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
            
            var chunk = ReadChunkFromReader(reader);
            
            results.Add((chunk, similarity));
        }

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

    /// <summary>
    /// Gets all stored content hashes, keyed by chunk ID.
    /// Used to detect which chunks have changed since the last indexing run.
    /// </summary>
    public async Task<Dictionary<string, string>> GetAllContentHashesAsync()
    {
        await EnsureInitializedAsync();

        var result = new Dictionary<string, string>();
        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = new SqliteCommand("SELECT Id, ContentHash FROM CodeChunks", connection);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        }
        return result;
    }

    /// <summary>
    /// Deletes all chunks and call edges for files that no longer exist on disk.
    /// Also cleans up orphaned vec_embeddings entries.
    /// </summary>
    /// <returns>The number of files whose chunks were removed.</returns>
    public async Task<int> DeleteChunksForNonExistentFilesAsync()
    {
        await EnsureInitializedAsync();

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // Step 1: Find all distinct file paths currently in the DB
        var filesInDb = new List<string>();
        using (var selectCmd = new SqliteCommand("SELECT DISTINCT FilePath FROM CodeChunks", connection))
        {
            using var reader = await selectCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                filesInDb.Add(reader.GetString(0));
            }
        }

        // Step 2: Filter to those that no longer exist
        var missingFiles = filesInDb.Where(f => !File.Exists(f)).ToList();
        if (missingFiles.Count == 0)
        {
            return 0;
        }

        // Step 3: Delete chunks for missing files (one DELETE per file to keep the query simple)
        foreach (var file in missingFiles)
        {
            using var deleteChunksCmd = new SqliteCommand(
                "DELETE FROM CodeChunks WHERE FilePath = @FilePath", connection);
            deleteChunksCmd.Parameters.AddWithValue("@FilePath", file);
            await deleteChunksCmd.ExecuteNonQueryAsync();
            _logger?.LogInformation("Removed chunks for missing file: {FilePath}", file);
        }

        // Step 4: Clean up orphaned vec_embeddings (sqlite-vec has no ON DELETE CASCADE)
        if (_vecLoaded)
        {
            using var cleanupVecCmd = new SqliteCommand(@"
                DELETE FROM vec_embeddings
                WHERE chunk_id NOT IN (SELECT Id FROM CodeChunks)", connection);
            await cleanupVecCmd.ExecuteNonQueryAsync();
        }

        // Step 5: Clean up orphaned call edges
        using (var cleanupEdgesCmd = new SqliteCommand(@"
            DELETE FROM CallEdges
            WHERE SourceChunkId NOT IN (SELECT Id FROM CodeChunks)
               OR TargetChunkId NOT IN (SELECT Id FROM CodeChunks)", connection))
        {
            await cleanupEdgesCmd.ExecuteNonQueryAsync();
        }

        return missingFiles.Count;
    }

    public async Task<IReadOnlyList<CodeChunk>> GetAllChunksAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        var connectionString = $"Data Source={_dbPath};";
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        using var command = new SqliteCommand("SELECT * FROM CodeChunks", connection);
        var results = new List<CodeChunk>();

        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadChunkFromReader(reader));
        }

        return results;
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

    private CodeChunk ReadChunkFromReader(SqliteDataReader reader, int offset = 0)
    {
        var chunk = new CodeChunk
        {
            Id = reader.GetString(0 + offset),
            FilePath = reader.GetString(1 + offset),
            NamespaceName = reader.GetString(2 + offset),
            ClassName = reader.GetString(3 + offset),
            MemberName = reader.GetString(4 + offset),
            MemberType = reader.GetString(5 + offset),
            Content = reader.GetString(6 + offset),
            Signature = reader.GetString(7 + offset),
            Documentation = reader.GetString(8 + offset),
            StartLine = reader.GetInt32(9 + offset),
            EndLine = reader.GetInt32(10 + offset),
            IndexedAt = DateTime.Parse(reader.GetString(12 + offset), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };

        // Read optional columns that may not exist in older databases
        // Use field count to determine available columns
        var fieldCount = reader.FieldCount;
        var expectedColumns = 13; // Original 13 columns (0-12)

        // ContentHash is at index 13 (added in Issue #2)
        if (fieldCount > 13)
        {
            var contentHashValue = reader.GetValue(13 + offset);
            chunk.ContentHash = contentHashValue == DBNull.Value ? string.Empty : (string)contentHashValue;
        }

        if (fieldCount > expectedColumns)
        {
            chunk.IsController = reader.GetInt32(14 + offset) != 0;
            chunk.IsService = reader.GetInt32(15 + offset) != 0;
            chunk.IsMiddleware = reader.GetInt32(16 + offset) != 0;

            var httpMethodsValue = reader.GetValue(17 + offset);
            chunk.HttpMethods = httpMethodsValue == DBNull.Value ? null : (string?)httpMethodsValue;

            var routeTemplateValue = reader.GetValue(18 + offset);
            chunk.RouteTemplate = routeTemplateValue == DBNull.Value ? null : (string?)routeTemplateValue;
        }

        return chunk;
    }
}
