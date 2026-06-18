using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Data;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using System.Globalization;

namespace SemanticSourceCode.Services;

public class SqliteVssDatabase : IVectorDatabase
{
    private readonly ISqliteConnectionFactory _factory;
    private readonly IDatabaseInitializer _initializer;
    private readonly ILogger<SqliteVssDatabase>? _logger;
    private bool _vecLoaded = false;
    private int? _embeddingDimensions;

    public SqliteVssDatabase(
        ISqliteConnectionFactory factory,
        IDatabaseInitializer initializer,
        ILogger<SqliteVssDatabase>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await _initializer.EnsureInitializedAsync().ConfigureAwait(false);

        // Probe the vec extension on an already-initialised database so we
        // can recreate the vec0 virtual table with the right dimensions.
        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);
        await TryLoadVecExtensionAsync(connection).ConfigureAwait(false);
        if (_vecLoaded)
        {
            await CreateVecVirtualTableAsync(connection, _embeddingDimensions).ConfigureAwait(false);
        }

        _logger?.LogInformation(
            "Database ready at {Path} (vec0: {VecStatus})",
            _factory.DatabasePath, _vecLoaded ? "enabled" : "fallback");
    }

    private async Task TryLoadVecExtensionAsync(SqliteConnection connection)
    {
        try
        {
            // Enable extension loading
            using var enableCmd = new SqliteCommand("SELECT load_extension('vec0')", connection);
            await enableCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            _logger?.LogInformation("vec0 virtual table created for ANN search with {Dim} dimensions", dim);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to create vec0 virtual table: {Message}", ex.Message);
            _vecLoaded = false;
        }
    }

    public async Task InsertChunkAsync(CodeChunk chunk)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

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

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Also insert into vec0 virtual table for ANN search
        if (_vecLoaded && chunk.Embedding != null && chunk.Embedding.Length > 0)
        {
            await InsertVecEmbeddingAsync(connection, chunk).ConfigureAwait(false);
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
                await RecreateVecTableWithDimensionsAsync(connection, _embeddingDimensions.Value).ConfigureAwait(false);
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

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var createCmd = $@"
                CREATE VIRTUAL TABLE vec_embeddings USING vec0(
                    embedding FLOAT[{dimensions}] distance_metric=cosine,
                    chunk_id TEXT
                );";

            using (var cmd = new SqliteCommand(createCmd, connection))
            {
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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
            await InsertChunkAsync(chunk).ConfigureAwait(false);
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
        await EnsureInitializedAsync().ConfigureAwait(false);

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
                return await SearchWithVecAsync(queryEmbedding, topK).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("vec0 search failed, falling back to cosine: {Message}", ex.Message);
                // Fall through to fallback
            }
        }

        // Fallback: in-memory cosine similarity
        return await SearchWithCosineFallbackAsync(queryEmbedding, topK).ConfigureAwait(false);
    }

    private async Task<List<(CodeChunk Chunk, float Similarity)>> SearchWithVecAsync(float[] queryEmbedding, int topK)
    {
        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

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

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
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

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        var selectCmd = "SELECT * FROM CodeChunks WHERE Embedding IS NOT NULL AND LENGTH(Embedding) > 0";
        using var command = new SqliteCommand(selectCmd, connection);

        var results = new List<(CodeChunk Chunk, float Similarity)>();

        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
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
        await EnsureInitializedAsync().ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        using var command = new SqliteCommand("DELETE FROM CodeChunks", connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        _logger?.LogInformation("Database cleared");
    }

    public Task<bool> IsInitializedAsync()
    {
        // Schema lifecycle is now owned by IDatabaseInitializer. The
        // IVectorDatabase contract keeps this method for backwards
        // compatibility, but the schema is always considered initialised
        // by the time a service runs an operation against it (the
        // initializer is invoked lazily on every entry point). Always
        // returning true preserves the post-refactor invariant that any
        // successful DB call implies the schema is up to date.
        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets all stored content hashes, keyed by chunk ID.
    /// Used to detect which chunks have changed since the last indexing run.
    /// </summary>
    public async Task<Dictionary<string, string>> GetAllContentHashesAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        var result = new Dictionary<string, string>();
        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        using var command = new SqliteCommand("SELECT Id, ContentHash FROM CodeChunks", connection);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
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
        await EnsureInitializedAsync().ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        // Step 1: Find all distinct file paths currently in the DB
        var filesInDb = new List<string>();
        using (var selectCmd = new SqliteCommand("SELECT DISTINCT FilePath FROM CodeChunks", connection))
        {
            using var reader = await selectCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
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
            await deleteChunksCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            _logger?.LogInformation("Removed chunks for missing file: {FilePath}", file);
        }

        // Step 4: Clean up orphaned vec_embeddings (sqlite-vec has no ON DELETE CASCADE)
        if (_vecLoaded)
        {
            try
            {
                using var cleanupVecCmd = new SqliteCommand(@"
                    DELETE FROM vec_embeddings
                    WHERE chunk_id NOT IN (SELECT Id FROM CodeChunks)", connection);
                await cleanupVecCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("vec_embeddings cleanup skipped: {Message}", ex.Message);
            }
        }

        // Step 5: Clean up orphaned call edges (only if the table exists)
        try
        {
            using var cleanupEdgesCmd = new SqliteCommand(@"
                DELETE FROM CallEdges
                WHERE SourceChunkId NOT IN (SELECT Id FROM CodeChunks)
                   OR TargetChunkId NOT IN (SELECT Id FROM CodeChunks)", connection);
            await cleanupEdgesCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // CallEdges table doesn't exist yet (e.g. fresh DB) — nothing to clean up
            _logger?.LogDebug("CallEdges cleanup skipped: table does not exist");
        }

        return missingFiles.Count;
    }

    /// <summary>
    /// Deletes all chunks, vec_embeddings and call edges for a specific file.
    /// Used by watch mode when a file is deleted or renamed.
    /// </summary>
    /// <param name="filePath">The absolute or relative path of the file whose chunks should be removed.</param>
    /// <returns>The number of chunks that were removed.</returns>
    public async Task<int> DeleteChunksByFilePathAsync(string filePath)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return 0;
        }

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        // Step 1: Count how many chunks we're about to delete (for return value + log)
        int deletedCount;
        using (var countCmd = new SqliteCommand(
            "SELECT COUNT(*) FROM CodeChunks WHERE FilePath = @FilePath", connection))
        {
            countCmd.Parameters.AddWithValue("@FilePath", filePath);
            var countResult = await countCmd.ExecuteScalarAsync().ConfigureAwait(false);
            deletedCount = countResult == null ? 0 : Convert.ToInt32(countResult);
        }

        if (deletedCount == 0)
        {
            return 0;
        }

        // Step 2: Delete the chunks for this file
        using (var deleteChunksCmd = new SqliteCommand(
            "DELETE FROM CodeChunks WHERE FilePath = @FilePath", connection))
        {
            deleteChunksCmd.Parameters.AddWithValue("@FilePath", filePath);
            await deleteChunksCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Step 3: Clean up orphaned vec_embeddings (sqlite-vec has no ON DELETE CASCADE)
        if (_vecLoaded)
        {
            try
            {
                using var cleanupVecCmd = new SqliteCommand(@"
                    DELETE FROM vec_embeddings
                    WHERE chunk_id NOT IN (SELECT Id FROM CodeChunks)", connection);
                await cleanupVecCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("vec_embeddings cleanup skipped: {Message}", ex.Message);
            }
        }

        // Step 4: Clean up orphaned call edges (only if the table exists)
        try
        {
            using var cleanupEdgesCmd = new SqliteCommand(@"
                DELETE FROM CallEdges
                WHERE SourceChunkId NOT IN (SELECT Id FROM CodeChunks)
                   OR TargetChunkId NOT IN (SELECT Id FROM CodeChunks)", connection);
            await cleanupEdgesCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // CallEdges table doesn't exist yet (e.g. fresh DB) — nothing to clean up
            _logger?.LogDebug("CallEdges cleanup skipped: table does not exist");
        }

        _logger?.LogDebug("Removed {Count} chunks for file: {FilePath}", deletedCount, filePath);
        return deletedCount;
    }

    public async Task<IReadOnlyList<CodeChunk>> GetAllChunksAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(ct).ConfigureAwait(false);

        using var command = new SqliteCommand("SELECT * FROM CodeChunks", connection);
        var results = new List<CodeChunk>();

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadChunkFromReader(reader));
        }

        return results;
    }

    private async Task EnsureInitializedAsync()
    {
        await _initializer.EnsureInitializedAsync().ConfigureAwait(false);
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
        await EnsureInitializedAsync().ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        var insertCmd = @"
            INSERT OR IGNORE INTO CallEdges (SourceChunkId, TargetChunkId, CallType, LineNumber)
            VALUES (@SourceChunkId, @TargetChunkId, @CallType, @LineNumber)";

        using var command = new SqliteCommand(insertCmd, connection);
        command.Parameters.AddWithValue("@SourceChunkId", sourceChunkId);
        command.Parameters.AddWithValue("@TargetChunkId", targetChunkId);
        command.Parameters.AddWithValue("@CallType", callType);
        command.Parameters.AddWithValue("@LineNumber", lineNumber);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Gets all chunks that call the specified chunk.
    /// </summary>
    public async Task<List<CodeChunk>> GetCallersAsync(string chunkId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        var selectCmd = @"
            SELECT c.* FROM CodeChunks c
            JOIN CallEdges e ON c.Id = e.SourceChunkId
            WHERE e.TargetChunkId = @ChunkId
            ORDER BY e.LineNumber";

        using var command = new SqliteCommand(selectCmd, connection);
        command.Parameters.AddWithValue("@ChunkId", chunkId);

        var results = new List<CodeChunk>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
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
        await EnsureInitializedAsync().ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync().ConfigureAwait(false);

        var selectCmd = @"
            SELECT c.* FROM CodeChunks c
            JOIN CallEdges e ON c.Id = e.TargetChunkId
            WHERE e.SourceChunkId = @ChunkId
            ORDER BY e.LineNumber";

        using var command = new SqliteCommand(selectCmd, connection);
        command.Parameters.AddWithValue("@ChunkId", chunkId);

        var results = new List<CodeChunk>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
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
        await EnsureInitializedAsync().ConfigureAwait(false);

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

            var callers = await GetCallersAsync(currentId).ConfigureAwait(false);
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
