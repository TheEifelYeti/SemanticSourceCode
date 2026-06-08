using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;

namespace SemanticSourceCode.Cli;

/// <summary>
/// Watch mode for live incremental indexing.
///
/// Runs an initial full index of the directory, then watches the file system
/// for *.cs changes. On change, debounces (500 ms) and re-indexes only the
/// affected file. On delete, removes the file's chunks from the database.
/// On rename, treats it as a delete of the old name + a change of the new name.
///
/// Stops cleanly on Ctrl+C / SIGTERM via the supplied <see cref="CancellationToken"/>.
/// </summary>
public static class WatchCommand
{
    // Default directories that should never be watched (build output, VCS, etc.)
    private static readonly HashSet<string> _excludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".next", "dist", "build"
    };

    private const int DebounceMilliseconds = 500;

    /// <summary>
    /// Entry point for `--mode watch --path X`.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, string path, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
        {
            logger.LogError("Directory not found: {Path}", path);
            return 1;
        }

        // Resolve dependencies once
        var analyzer = services.GetRequiredService<ICodeAnalyzer>();
        var embeddingService = services.GetRequiredService<IEmbeddingService>();
        var database = services.GetRequiredService<IVectorDatabase>();
        var keywordIndex = services.GetRequiredService<IKeywordIndex>();

        await database.InitializeAsync();

        // Initial full index
        logger.LogInformation("Watch mode: initial full index of {Path}...", path);
        var initialResult = await IndexCommand.RunAsync(services, path, logger);
        if (initialResult == 1)
        {
            return 1; // directory not found etc.
        }

        // Set up the file system watcher
        using var watcher = new FileSystemWatcher(path)
        {
            Filter = "*.cs",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024 // 64 KB to absorb burst writes
        };

        // Debounce state: file path -> (CTS that will fire the actual re-index, last queued timestamp)
        var debounceLock = new object();
        var pendingTimers = new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        var pendingQueuedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        void ScheduleReindex(string filePath, string reason)
        {
            lock (debounceLock)
            {
                // Cancel any previously scheduled timer for this file
                if (pendingTimers.TryGetValue(filePath, out var oldCts))
                {
                    try { oldCts.Cancel(); } catch { /* already disposed */ }
                    oldCts.Dispose();
                }

                var cts = new CancellationTokenSource();
                pendingTimers[filePath] = cts;
                pendingQueuedAt[filePath] = DateTime.UtcNow;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(DebounceMilliseconds, cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        // A newer event came in for this file. Skip.
                        return;
                    }

                    // Re-check the file actually still exists (could be deleted in the meantime)
                    if (!File.Exists(filePath))
                    {
                        logger.LogDebug("File disappeared before debounce fired: {FilePath}", filePath);
                        lock (debounceLock) { pendingTimers.Remove(filePath); pendingQueuedAt.Remove(filePath); }
                        return;
                    }

                    // Snapshot how long we waited
                    TimeSpan waited;
                    lock (debounceLock)
                    {
                        waited = DateTime.UtcNow - pendingQueuedAt.GetValueOrDefault(filePath, DateTime.UtcNow);
                        pendingTimers.Remove(filePath);
                        pendingQueuedAt.Remove(filePath);
                    }

                    logger.LogInformation("Re-indexing {FilePath} (reason: {Reason}, debounced {Ms} ms)...",
                        filePath, reason, (int)waited.TotalMilliseconds);

                    try
                    {
                        await IndexSingleFileAsync(filePath, analyzer, embeddingService, database, keywordIndex, logger, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error re-indexing {FilePath}", filePath);
                    }
                }, cts.Token);
            }
        }

        watcher.Created += (_, e) => ScheduleReindex(e.FullPath, "created");
        watcher.Changed += (_, e) => ScheduleReindex(e.FullPath, "changed");
        watcher.Deleted += async (_, e) =>
        {
            // No debounce for delete — the file is already gone, so we must drop its chunks now
            // to prevent stale hits in search results.
            logger.LogInformation("File deleted: {FilePath}. Removing chunks...", e.FullPath);
            try
            {
                var removed = await database.DeleteChunksByFilePathAsync(e.FullPath);
                logger.LogInformation("Removed {Count} chunks for {FilePath}", removed, e.FullPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error removing chunks for deleted file {FilePath}", e.FullPath);
            }
        };
        watcher.Renamed += (_, e) =>
        {
            // Rename = delete old + create new
            logger.LogInformation("File renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);
            _ = Task.Run(async () =>
            {
                try
                {
                    var removed = await database.DeleteChunksByFilePathAsync(e.OldFullPath);
                    logger.LogInformation("Removed {Count} chunks for old path {FilePath}", removed, e.OldFullPath);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error removing chunks for renamed file {FilePath}", e.OldFullPath);
                }
            });
            ScheduleReindex(e.FullPath, "renamed");
        };
        watcher.Error += (_, e) =>
        {
            logger.LogError(e.GetException(), "FileSystemWatcher error");
        };

        // Skip events from excluded directories (bin, obj, .git, etc.)
        watcher.Created += (_, e) =>
        {
            if (IsInExcludedDirectory(e.FullPath, path))
            {
                return;
            }
        };
        watcher.Changed += (_, e) =>
        {
            if (IsInExcludedDirectory(e.FullPath, path))
            {
                return;
            }
        };

        watcher.EnableRaisingEvents = true;
        logger.LogInformation("Watching {Path} for *.cs changes. Press Ctrl+C to stop.", path);

        // Wait until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // Normal shutdown path
        }

        watcher.EnableRaisingEvents = false;
        logger.LogInformation("Watch mode stopped.");

        // Dispose any pending timers
        lock (debounceLock)
        {
            foreach (var cts in pendingTimers.Values)
            {
                try { cts.Cancel(); } catch { /* best effort */ }
                cts.Dispose();
            }
            pendingTimers.Clear();
        }

        return 0;
    }

    /// <summary>
    /// Re-indexes a single file: analyzes it, computes the delta against existing
    /// hashes and only embeds/persists changed chunks. If the file no longer exists,
    /// its chunks are removed instead.
    /// </summary>
    private static async Task IndexSingleFileAsync(
        string filePath,
        ICodeAnalyzer analyzer,
        IEmbeddingService embeddingService,
        IVectorDatabase database,
        IKeywordIndex keywordIndex,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // File might have been deleted between debounce and now
        if (!File.Exists(filePath))
        {
            logger.LogDebug("File no longer exists, removing chunks: {FilePath}", filePath);
            var removed = await database.DeleteChunksByFilePathAsync(filePath);
            logger.LogInformation("Removed {Count} chunks for {FilePath} (file vanished during watch)", removed, filePath);
            return;
        }

        // Analyze just this file
        List<CodeChunk> chunks;
        try
        {
            chunks = await analyzer.AnalyzeFileAsync(filePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Skipping {FilePath} (analysis failed)", filePath);
            return;
        }

        if (chunks.Count == 0)
        {
            // File has no extractable chunks (e.g. comments only) — make sure stale chunks are removed
            var removed = await database.DeleteChunksByFilePathAsync(filePath);
            if (removed > 0)
            {
                logger.LogInformation("Removed {Count} stale chunks for {FilePath}", removed, filePath);
            }
            return;
        }

        // Load only the hashes for the IDs in this file (cheap; full map is also fine)
        var existingHashes = await database.GetAllContentHashesAsync();

        // Reuse the shared embed+persist loop from IndexCommand
        await IndexCommand.ProcessChunksAsync(chunks, existingHashes, embeddingService, database, keywordIndex, logger);
    }

    /// <summary>
    /// Returns true if <paramref name="fullPath"/> is inside one of the well-known
    /// directories that should not be watched (bin, obj, .git, etc.).
    /// </summary>
    private static bool IsInExcludedDirectory(string fullPath, string rootPath)
    {
        if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(rootPath))
        {
            return false;
        }

        var relative = Path.GetRelativePath(rootPath, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return false; // outside the watched root
        }

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => _excludedDirectories.Contains(segment));
    }
}
