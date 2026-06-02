using SemanticSourceCode.Models;
using SemanticSourceCode.Services;

namespace SemanticSourceCode.Search;

/// <summary>
/// Combines semantic (cosine similarity) and keyword search into a unified hybrid score.
/// Applies re-ranking with structural signals.
/// </summary>
public class HybridSearchService : IHybridSearchService
{
    private readonly IVectorDatabase _vectorDatabase;
    private readonly IKeywordIndex _keywordIndex;
    private readonly IResultRanker _ranker;

    public HybridSearchService(
        IVectorDatabase vectorDatabase,
        IKeywordIndex keywordIndex,
        IResultRanker ranker)
    {
        _vectorDatabase = vectorDatabase;
        _keywordIndex = keywordIndex;
        _ranker = ranker;
    }

    public async Task<List<HybridResult>> SearchAsync(
        float[] queryEmbedding,
        string query,
        int topK = 20,
        SearchFilter? filter = null,
        HybridOptions? hybridOptions = null,
        RankerOptions? rankerOptions = null)
    {
        var hybridOpts = hybridOptions ?? new HybridOptions();
        var rankerOpts = rankerOptions ?? new RankerOptions();

        // 1. Get semantic results
        var semanticResults = await _vectorDatabase.SearchSimilarWithScoresAsync(queryEmbedding, topK * 2);

        // 2. Get keyword results
        var keywordResults = await _keywordIndex.SearchKeywordMatchesAsync(query, topK * 2);

        // 3. Combine into hybrid results
        var hybridResults = CombineResults(semanticResults, keywordResults, hybridOpts);

        // 4. Apply filters
        if (filter != null && !filter.IsEmpty)
        {
            hybridResults = ApplyFilter(hybridResults, filter);
        }

        // 5. Re-rank
        var rankedResults = _ranker.Rank(hybridResults, query, rankerOpts);

        // 6. Return topK
        return rankedResults.Take(topK).ToList();
    }

    internal List<HybridResult> CombineResults(
        List<(CodeChunk Chunk, float Similarity)> semanticResults,
        List<(CodeChunk Chunk, float Score)> keywordResults,
        HybridOptions options)
    {
        var resultMap = new Dictionary<string, HybridResult>();

        // Add semantic results
        foreach (var (chunk, score) in semanticResults)
        {
            resultMap[chunk.Id] = new HybridResult
            {
                Chunk = chunk,
                SemanticScore = score,
                KeywordScore = 0f
            };
        }

        // Add or merge keyword results
        foreach (var (chunk, score) in keywordResults)
        {
            if (resultMap.TryGetValue(chunk.Id, out var existing))
            {
                existing.KeywordScore = score;
            }
            else
            {
                resultMap[chunk.Id] = new HybridResult
                {
                    Chunk = chunk,
                    SemanticScore = 0f,
                    KeywordScore = score
                };
            }
        }

        // Compute hybrid score
        foreach (var result in resultMap.Values)
        {
            result.HybridScore = options.SemanticWeight * result.SemanticScore
                               + options.KeywordWeight * result.KeywordScore;
        }

        return resultMap.Values.ToList();
    }

    internal List<HybridResult> ApplyFilter(List<HybridResult> results, SearchFilter filter)
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
}
