using Moq;
using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Search;

public class HybridSearchServiceTests
{
    [Fact]
    public void CombineResults_BothSignalsPresent_ComputesHybridScore()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var semanticResults = new List<(CodeChunk, float)>
        {
            (new CodeChunk { Id = "1", ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 10 }, 0.9f)
        };

        var keywordResults = new List<(CodeChunk, float)>
        {
            (new CodeChunk { Id = "1", ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 10 }, 0.5f)
        };

        var options = new HybridOptions { SemanticWeight = 0.7f };

        // Act
        var combined = service.CombineResults(semanticResults, keywordResults, options);

        // Assert
        Assert.Single(combined);
        Assert.Equal(0.9f, combined[0].SemanticScore);
        Assert.Equal(0.5f, combined[0].KeywordScore);
        // hybrid_score = 0.7 * 0.9 + 0.3 * 0.5 = 0.63 + 0.15 = 0.78
        Assert.Equal(0.78f, combined[0].HybridScore, 4);
    }

    [Fact]
    public void CombineResults_OnlySemantic_KeywordScoreIsZero()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var semanticResults = new List<(CodeChunk, float)>
        {
            (new CodeChunk { Id = "1", ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 10 }, 0.8f)
        };

        var options = new HybridOptions { SemanticWeight = 0.7f };

        // Act
        var combined = service.CombineResults(semanticResults, new List<(CodeChunk, float)>(), options);

        // Assert
        Assert.Single(combined);
        Assert.Equal(0.8f, combined[0].SemanticScore);
        Assert.Equal(0f, combined[0].KeywordScore);
        // hybrid_score = 0.7 * 0.8 + 0.3 * 0 = 0.56
        Assert.Equal(0.56f, combined[0].HybridScore, 4);
    }

    [Fact]
    public void CombineResults_OnlyKeyword_SemanticScoreIsZero()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var keywordResults = new List<(CodeChunk, float)>
        {
            (new CodeChunk { Id = "1", ClassName = "DatabaseService", MemberName = "Save", Content = "...", StartLine = 1, EndLine = 10 }, 0.6f)
        };

        var options = new HybridOptions { SemanticWeight = 0.7f };

        // Act
        var combined = service.CombineResults(new List<(CodeChunk, float)>(), keywordResults, options);

        // Assert
        Assert.Single(combined);
        Assert.Equal(0f, combined[0].SemanticScore);
        Assert.Equal(0.6f, combined[0].KeywordScore);
        // hybrid_score = 0.7 * 0 + 0.3 * 0.6 = 0.18
        Assert.Equal(0.18f, combined[0].HybridScore, 4);
    }

    [Fact]
    public void CombineResults_MultipleChunks_MergesCorrectly()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var semanticResults = new List<(CodeChunk, float)>
        {
            (new CodeChunk { Id = "1", ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 10 }, 0.9f),
            (new CodeChunk { Id = "2", ClassName = "B", MemberName = "Y", Content = "...", StartLine = 1, EndLine = 10 }, 0.7f)
        };

        var keywordResults = new List<(CodeChunk, float)>
        {
            (new CodeChunk { Id = "2", ClassName = "B", MemberName = "Y", Content = "...", StartLine = 1, EndLine = 10 }, 0.8f),
            (new CodeChunk { Id = "3", ClassName = "C", MemberName = "Z", Content = "...", StartLine = 1, EndLine = 10 }, 0.5f)
        };

        var options = new HybridOptions { SemanticWeight = 0.7f };

        // Act
        var combined = service.CombineResults(semanticResults, keywordResults, options);

        // Assert
        Assert.Equal(3, combined.Count);

        var chunk1 = combined.First(c => c.Chunk.Id == "1");
        Assert.Equal(0.9f, chunk1.SemanticScore);
        Assert.Equal(0f, chunk1.KeywordScore);

        var chunk2 = combined.First(c => c.Chunk.Id == "2");
        Assert.Equal(0.7f, chunk2.SemanticScore);
        Assert.Equal(0.8f, chunk2.KeywordScore);
        // hybrid = 0.7 * 0.7 + 0.3 * 0.8 = 0.49 + 0.24 = 0.73
        Assert.Equal(0.73f, chunk2.HybridScore, 4);

        var chunk3 = combined.First(c => c.Chunk.Id == "3");
        Assert.Equal(0f, chunk3.SemanticScore);
        Assert.Equal(0.5f, chunk3.KeywordScore);
    }

    [Fact]
    public void ApplyFilter_ByNamespace_ReturnsMatchingResults()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var results = new List<HybridResult>
        {
            new() { Chunk = new CodeChunk { Id = "1", NamespaceName = "Api.Controllers", ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "2", NamespaceName = "Services", ClassName = "B", MemberName = "Y", Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "3", NamespaceName = "Api.Controllers.Internal", ClassName = "C", MemberName = "Z", Content = "...", StartLine = 1, EndLine = 10 } }
        };

        var filter = new SearchFilter { Namespace = "Api.Controllers" };

        // Act
        var filtered = service.ApplyFilter(results, filter);

        // Assert
        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, r => r.Chunk.Id == "1");
        Assert.Contains(filtered, r => r.Chunk.Id == "3");
        Assert.DoesNotContain(filtered, r => r.Chunk.Id == "2");
    }

    [Fact]
    public void ApplyFilter_ByHttpMethod_ReturnsMatchingResults()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var results = new List<HybridResult>
        {
            new() { Chunk = new CodeChunk { Id = "1", ClassName = "A", MemberName = "GetUser", HttpMethods = "GET", Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "2", ClassName = "B", MemberName = "CreateUser", HttpMethods = "POST", Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "3", ClassName = "C", MemberName = "DeleteUser", HttpMethods = "DELETE", Content = "...", StartLine = 1, EndLine = 10 } }
        };

        var filter = new SearchFilter { HttpMethod = "GET" };

        // Act
        var filtered = service.ApplyFilter(results, filter);

        // Assert
        Assert.Single(filtered);
        Assert.Equal("1", filtered[0].Chunk.Id);
    }

    [Fact]
    public void ApplyFilter_ByFilePathPattern_ReturnsMatchingResults()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var results = new List<HybridResult>
        {
            new() { Chunk = new CodeChunk { Id = "1", FilePath = "/src/Controllers/HomeController.cs", ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "2", FilePath = "/src/Services/UserService.cs", ClassName = "B", MemberName = "Y", Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "3", FilePath = "/src/Models/User.cs", ClassName = "C", MemberName = "Z", Content = "...", StartLine = 1, EndLine = 10 } }
        };

        var filter = new SearchFilter { FilePathPattern = "*/Controllers/*" };

        // Act
        var filtered = service.ApplyFilter(results, filter);

        // Assert
        Assert.Single(filtered);
        Assert.Equal("1", filtered[0].Chunk.Id);
    }

    [Fact]
    public void ApplyFilter_ByIsController_ReturnsOnlyControllers()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var results = new List<HybridResult>
        {
            new() { Chunk = new CodeChunk { Id = "1", ClassName = "A", MemberName = "X", IsController = true, Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "2", ClassName = "B", MemberName = "Y", IsController = false, Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "3", ClassName = "C", MemberName = "Z", IsController = true, Content = "...", StartLine = 1, EndLine = 10 } }
        };

        var filter = new SearchFilter { IsController = true };

        // Act
        var filtered = service.ApplyFilter(results, filter);

        // Assert
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, r => Assert.True(r.Chunk.IsController));
    }

    [Fact]
    public void ApplyFilter_EmptyFilter_ReturnsAllResults()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var results = new List<HybridResult>
        {
            new() { Chunk = new CodeChunk { Id = "1", ClassName = "A", MemberName = "X", Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "2", ClassName = "B", MemberName = "Y", Content = "...", StartLine = 1, EndLine = 10 } }
        };

        var filter = new SearchFilter(); // empty

        // Act
        var filtered = service.ApplyFilter(results, filter);

        // Assert
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void ApplyFilter_CombinedNamespaceAndHttpMethod_ReturnsIntersection()
    {
        // Arrange
        var service = new HybridSearchService(
            new Mock<IVectorDatabase>().Object,
            new Mock<IKeywordIndex>().Object,
            new ResultRanker());

        var results = new List<HybridResult>
        {
            new() { Chunk = new CodeChunk { Id = "1", NamespaceName = "Api.Controllers", ClassName = "A", MemberName = "GetUser", HttpMethods = "GET", Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "2", NamespaceName = "Api.Controllers", ClassName = "B", MemberName = "PostUser", HttpMethods = "POST", Content = "...", StartLine = 1, EndLine = 10 } },
            new() { Chunk = new CodeChunk { Id = "3", NamespaceName = "Services", ClassName = "C", MemberName = "GetData", HttpMethods = "GET", Content = "...", StartLine = 1, EndLine = 10 } }
        };

        var filter = new SearchFilter { Namespace = "Api.Controllers", HttpMethod = "GET" };

        // Act
        var filtered = service.ApplyFilter(results, filter);

        // Assert
        Assert.Single(filtered);
        Assert.Equal("1", filtered[0].Chunk.Id);
    }
}
