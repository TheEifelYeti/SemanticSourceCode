using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using Xunit;

namespace SemanticSourceCode.Tests.Search;

public class ResultRankerTests
{
    private readonly ResultRanker _ranker = new();
    private readonly RankerOptions _defaultOptions = new();

    [Fact]
    public void Rank_ClassNameMatch_AppliesBoost()
    {
        // Arrange
        var results = new List<HybridResult>
        {
            new()
            {
                Chunk = new CodeChunk { ClassName = "DatabaseService", MemberName = "Save", Content = "...", StartLine = 1, EndLine = 10 },
                HybridScore = 0.8f
            },
            new()
            {
                Chunk = new CodeChunk { ClassName = "UserController", MemberName = "Get", Content = "...", StartLine = 1, EndLine = 10 },
                HybridScore = 0.8f
            }
        };

        // Act
        var ranked = _ranker.Rank(results, "Database", _defaultOptions);

        // Assert
        Assert.True(ranked[0].FinalScore > ranked[1].FinalScore,
            "DatabaseService should rank higher due to ClassName boost");
        Assert.Equal("DatabaseService", ranked[0].Chunk.ClassName);
    }

    [Fact]
    public void Rank_ControllerBoost_AppliesHigherScore()
    {
        // Arrange
        var results = new List<HybridResult>
        {
            new()
            {
                Chunk = new CodeChunk { ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 10, IsController = false },
                HybridScore = 0.8f
            },
            new()
            {
                Chunk = new CodeChunk { ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 10, IsController = true },
                HybridScore = 0.8f
            }
        };

        // Act
        var ranked = _ranker.Rank(results, "test", _defaultOptions);

        // Assert
        var controllerResult = ranked.First(r => r.Chunk.IsController);
        var nonControllerResult = ranked.First(r => !r.Chunk.IsController);
        Assert.True(controllerResult.FinalScore > nonControllerResult.FinalScore,
            "Controller should receive boost");
    }

    [Fact]
    public void Rank_DocumentationBoost_AppliesBoost()
    {
        // Arrange
        var results = new List<HybridResult>
        {
            new()
            {
                Chunk = new CodeChunk { ClassName = "A", MemberName = "X", Content = "...", Documentation = "", StartLine = 1, EndLine = 10 },
                HybridScore = 0.8f
            },
            new()
            {
                Chunk = new CodeChunk { ClassName = "A", MemberName = "X", Content = "...", Documentation = "/// <summary>Docs</summary>", StartLine = 1, EndLine = 10 },
                HybridScore = 0.8f
            }
        };

        // Act
        var ranked = _ranker.Rank(results, "test", _defaultOptions);

        // Assert
        var docResult = ranked.First(r => !string.IsNullOrEmpty(r.Chunk.Documentation));
        var noDocResult = ranked.First(r => string.IsNullOrEmpty(r.Chunk.Documentation));
        Assert.True(docResult.FinalScore > noDocResult.FinalScore,
            "Documented chunk should receive boost");
    }

    [Fact]
    public void Rank_SmallFilePenalty_ReducesScore()
    {
        // Arrange
        var results = new List<HybridResult>
        {
            new()
            {
                Chunk = new CodeChunk { ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 3 },
                HybridScore = 0.8f
            },
            new()
            {
                Chunk = new CodeChunk { ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 20 },
                HybridScore = 0.8f
            }
        };

        // Act
        var ranked = _ranker.Rank(results, "test", _defaultOptions);

        // Assert
        var smallResult = ranked.First(r => r.Chunk.EndLine - r.Chunk.StartLine <= 5);
        var largeResult = ranked.First(r => r.Chunk.EndLine - r.Chunk.StartLine > 5);
        Assert.True(smallResult.FinalScore < largeResult.FinalScore,
            "Small file should receive penalty");
    }

    [Fact]
    public void Rank_EmptyResults_ReturnsEmpty()
    {
        // Act
        var ranked = _ranker.Rank(new List<HybridResult>(), "test", _defaultOptions);

        // Assert
        Assert.Empty(ranked);
    }

    [Fact]
    public void Rank_MemberNameMatch_AppliesBoost()
    {
        // Arrange
        var results = new List<HybridResult>
        {
            new()
            {
                Chunk = new CodeChunk { ClassName = "A", MemberName = "CreateDatabase", Content = "...", StartLine = 1, EndLine = 10 },
                HybridScore = 0.7f
            },
            new()
            {
                Chunk = new CodeChunk { ClassName = "A", MemberName = "DeleteUser", Content = "...", StartLine = 1, EndLine = 10 },
                HybridScore = 0.7f
            }
        };

        // Act
        var ranked = _ranker.Rank(results, "Database", _defaultOptions);

        // Assert
        Assert.Equal("CreateDatabase", ranked[0].Chunk.MemberName);
        Assert.True(ranked[0].FinalScore > ranked[1].FinalScore);
    }
}
