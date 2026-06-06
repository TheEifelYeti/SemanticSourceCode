using SemanticSourceCode.Search;
using Xunit;

namespace SemanticSourceCode.Tests.Search;

public class AdaptiveThresholdTests
{
    private readonly AdaptiveThreshold _threshold = new();
    private readonly AdaptiveThresholdOptions _options = new()
    {
        Enabled = true,
        FloorThreshold = 0.30f,
        CeilingThreshold = 0.85f,
        Percentile = 70
    };

    [Fact]
    public void Compute_GenericQuery_OneWord_ReturnsReasonableThreshold()
    {
        // Arrange
        var scores = new List<float> { 0.95f, 0.90f, 0.85f, 0.80f, 0.75f };

        // Act
        var result = _threshold.Compute(scores, _options, "class");

        // Assert - percentile(70) of [0.95,0.90,0.85,0.80,0.75] = 0.80, 
        // query-length(1) = 0.30, combined weighted = ~0.65
        Assert.True(result >= _options.FloorThreshold, "Should be at least floor");
        Assert.True(result <= _options.CeilingThreshold, "Should be at most ceiling");
    }

    [Fact]
    public void Compute_SpecificQuery_FourWords_ReturnsReasonableThreshold()
    {
        // Arrange
        var scores = new List<float> { 0.95f, 0.90f, 0.85f, 0.80f, 0.75f };

        // Act
        var result = _threshold.Compute(scores, _options, "Configure Container Service");

        // Assert - query-length(3+) = 0.60, weighted with percentile = ~0.68
        Assert.True(result >= _options.FloorThreshold, "Should be at least floor");
        Assert.True(result <= _options.CeilingThreshold, "Should be at most ceiling");
        Assert.True(result >= 0.50f, "Specific query should have higher threshold");
    }

    [Fact]
    public void Compute_FloorThreshold_Respected()
    {
        // Arrange
        var scores = new List<float> { 0.10f, 0.05f, 0.01f }; // Very low scores

        // Act
        var result = _threshold.Compute(scores, _options, "test");

        // Assert
        Assert.True(result >= _options.FloorThreshold, "Result should be clamped to floor");
    }

    [Fact]
    public void Compute_CeilingThreshold_Respected()
    {
        // Arrange
        var scores = new List<float> { 0.99f, 0.98f, 0.97f }; // Very high scores

        // Act
        var result = _threshold.Compute(scores, _options, "specific query here");

        // Assert
        Assert.True(result <= _options.CeilingThreshold, "Result should be clamped to ceiling");
    }

    [Fact]
    public void Compute_Disabled_ReturnsFloorThreshold()
    {
        // Arrange
        var disabledOptions = new AdaptiveThresholdOptions { Enabled = false };
        var scores = new List<float> { 0.95f, 0.90f };

        // Act
        var result = _threshold.Compute(scores, disabledOptions, "test query");

        // Assert
        Assert.Equal(disabledOptions.FloorThreshold, result);
    }

    [Fact]
    public void Compute_EmptyScores_ReturnsFloorThreshold()
    {
        // Arrange
        var scores = new List<float>();

        // Act
        var result = _threshold.Compute(scores, _options, "test");

        // Assert
        Assert.Equal(_options.FloorThreshold, result);
    }

    [Fact]
    public void Compute_GapBasedThreshold_FindsElbow()
    {
        // Arrange - big gap between score 2 and 3
        var scores = new List<float> { 0.95f, 0.93f, 0.50f, 0.48f, 0.45f };

        // Act
        var result = _threshold.Compute(scores, _options, "specific query here");

        // Assert - should pick up the gap threshold, clamped by floor
        Assert.True(result >= 0.50f, $"Should detect gap at ~0.50 but was {result}");
    }

    [Fact]
    public void Compute_TwoWordQuery_ReturnsReasonableThreshold()
    {
        // Arrange
        var scores = new List<float> { 0.95f, 0.90f, 0.85f };

        // Act
        var result = _threshold.Compute(scores, _options, "two words");

        // Assert - query-length(2) = 0.40, weighted with percentile = ~0.55
        Assert.True(result >= _options.FloorThreshold, "Should be at least floor");
        Assert.True(result <= _options.CeilingThreshold, "Should be at most ceiling");
    }
}
