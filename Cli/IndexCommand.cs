using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Models;
using SemanticSourceCode.Services;

namespace SemanticSourceCode.Cli;

public static class IndexCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string path, ILogger logger)
    {
        var analyzer = services.GetRequiredService<ICodeAnalyzer>();
        var embeddingService = services.GetRequiredService<IEmbeddingService>();
        var database = services.GetRequiredService<IVectorDatabase>();

        await database.InitializeAsync();

        if (!Directory.Exists(path))
        {
            logger.LogError("Directory not found: {Path}", path);
            return 1;
        }

        // Step 1: Remove chunks for deleted/renamed files
        logger.LogInformation("Cleaning up chunks for missing files...");
        var removedFiles = await database.DeleteChunksForNonExistentFilesAsync();
        if (removedFiles > 0)
        {
            logger.LogInformation("Removed chunks for {Count} missing files", removedFiles);
        }

        // Step 2: Load existing content hashes for change detection
        logger.LogInformation("Loading existing index state...");
        var existingHashes = await database.GetAllContentHashesAsync();

        logger.LogInformation("Analyzing C# files...");
        var chunks = await analyzer.AnalyzeDirectoryAsync(path);
        logger.LogInformation("Found {Count} code chunks", chunks.Count);

        if (chunks.Count == 0)
        {
            logger.LogWarning("No C# files found to index.");
            return 2;
        }

        // Step 3: Identify chunks that need re-indexing (new or changed content)
        var changedChunks = chunks.Where(c =>
            !existingHashes.TryGetValue(c.Id, out var existingHash) || existingHash != c.ContentHash
        ).ToList();

        var skippedCount = chunks.Count - changedChunks.Count;
        logger.LogInformation("{Skipped} chunks unchanged, {Changed} chunks need re-indexing",
            skippedCount, changedChunks.Count);

        if (changedChunks.Count == 0)
        {
            logger.LogInformation("No changes detected. Index is up to date.");
            return 0;
        }

        // Step 4: Generate embeddings only for changed chunks
        logger.LogInformation("Generating embeddings for {Count} changed chunks...", changedChunks.Count);
        var processed = 0;
        foreach (var chunk in changedChunks)
        {
            try
            {
                var embedding = await embeddingService.GenerateEmbeddingAsync(chunk.Content);

                if (embedding == null || embedding.Length == 0)
                {
                    logger.LogWarning("Empty embedding for {FilePath} - {MemberName}. Skipping.", chunk.FilePath, chunk.MemberName);
                    continue;
                }

                chunk.Embedding = ConvertFloatArrayToByteArray(embedding);
                await database.InsertChunkAsync(chunk);

                processed++;
                if (processed % 10 == 0)
                {
                    logger.LogInformation("Processed {Processed}/{Total} chunks...", processed, changedChunks.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing {FilePath} - {MemberName}", chunk.FilePath, chunk.MemberName);
            }
        }

        logger.LogInformation("Successfully indexed {Processed} code chunks ({Skipped} unchanged).",
            processed, skippedCount);
        return 0;
    }

    private static byte[] ConvertFloatArrayToByteArray(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
