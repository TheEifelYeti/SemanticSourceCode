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

        logger.LogInformation("Analyzing C# files...");
        var chunks = await analyzer.AnalyzeDirectoryAsync(path);
        logger.LogInformation("Found {Count} code chunks", chunks.Count);

        if (chunks.Count == 0)
        {
            logger.LogWarning("No C# files found to index.");
            return 2;
        }

        logger.LogInformation("Generating embeddings (this may take a while)...");
        var processed = 0;
        foreach (var chunk in chunks)
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
                    logger.LogInformation("Processed {Processed}/{Total} chunks...", processed, chunks.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing {FilePath} - {MemberName}", chunk.FilePath, chunk.MemberName);
            }
        }

        logger.LogInformation("Successfully indexed {Processed} code chunks.", processed);
        return 0;
    }

    private static byte[] ConvertFloatArrayToByteArray(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
