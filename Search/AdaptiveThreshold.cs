namespace SemanticSourceCode.Search;

/// <summary>
/// Computes an adaptive similarity threshold based on score distribution and query characteristics.
/// </summary>
public interface IAdaptiveThreshold
{
    float Compute(IReadOnlyList<float> scores, AdaptiveThresholdOptions options, string query);
}

/// <summary>
/// Options for adaptive threshold computation.
/// </summary>
public class AdaptiveThresholdOptions
{
    public bool Enabled { get; set; } = true;
    public float FloorThreshold { get; set; } = 0.30f;
    public float CeilingThreshold { get; set; } = 0.85f;
    public int Percentile { get; set; } = 70;
}

/// <summary>
/// Default implementation of adaptive threshold computation.
/// </summary>
public class AdaptiveThreshold : IAdaptiveThreshold
{
    /// <inheritdoc/>
    public float Compute(IReadOnlyList<float> scores, AdaptiveThresholdOptions options, string query)
    {
        if (!options.Enabled || scores.Count == 0)
            return options.FloorThreshold;

        var sorted = scores.OrderByDescending(s => s).ToList();

        // 1. Percentile-based threshold
        var percentileIndex = (int)((options.Percentile / 100.0) * sorted.Count);
        percentileIndex = Math.Min(percentileIndex, sorted.Count - 1);
        var percentileThreshold = sorted[percentileIndex];

        // 2. Gap-based analysis (elbow method)
        var gapThreshold = ComputeGapThreshold(sorted);

        // 3. Query-length based threshold
        var queryWords = query?.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries).Length ?? 1;
        var queryLengthThreshold = ComputeQueryLengthThreshold(queryWords, options);

        // Combine: weighted approach where query-length threshold is a strong signal
        // Percentile captures score distribution, gap captures elbow, query-length captures specificity
        var combined = (percentileThreshold * 0.4f + gapThreshold * 0.3f + queryLengthThreshold * 0.3f);

        // Clamp between floor and ceiling
        return Math.Clamp(combined, options.FloorThreshold, options.CeilingThreshold);
    }

    /// <summary>
    /// Finds the largest gap in score distribution and returns the score after the gap.
    /// </summary>
    private static float ComputeGapThreshold(List<float> sortedScores)
    {
        if (sortedScores.Count < 2)
            return sortedScores.FirstOrDefault();

        float maxGap = 0;
        int gapIndex = 0;

        for (int i = 0; i < sortedScores.Count - 1; i++)
        {
            var gap = sortedScores[i] - sortedScores[i + 1];
            if (gap > maxGap)
            {
                maxGap = gap;
                gapIndex = i;
            }
        }

        // Threshold is just after the gap
        return sortedScores[gapIndex + 1];
    }

    /// <summary>
    /// Computes a threshold based on query specificity.
    /// Short queries (1-2 words) = lower threshold (generic)
    /// Long queries (3+ words) = higher threshold (specific)
    /// </summary>
    private static float ComputeQueryLengthThreshold(int wordCount, AdaptiveThresholdOptions options)
    {
        // 1 word: 0.30, 2 words: 0.40, 3+ words: 0.60
        return wordCount switch
        {
            1 => options.FloorThreshold,
            2 => 0.40f,
            _ => 0.60f
        };
    }
}
