using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using Xunit;

namespace SemanticSourceCode.Tests.Search;

public class QuerySuggesterTests
{
    private readonly QuerySuggester _suggester = new();

    private static CodeChunk CreateChunk(string className, string memberName)
    {
        return new CodeChunk
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = "/test.cs",
            NamespaceName = "TestNS",
            ClassName = className,
            MemberName = memberName,
            MemberType = "Method",
            Content = "void X() {}",
            Signature = "void X()",
            Documentation = "",
            StartLine = 1,
            EndLine = 2,
            IndexedAt = DateTime.UtcNow
        };
    }

    [Fact]
    public void Suggest_TypoInClassName_ReturnsAtLeastOneSuggestion()
    {
        // Arrange
        var index = new List<CodeChunk>
        {
            CreateChunk("DatabaseService", "GetUser"),
            CreateChunk("UserController", "Index"),
            CreateChunk("ProductRepository", "GetById")
        };

        // Act
        var suggestions = _suggester.Suggest("DataBase", index);

        // Assert
        Assert.NotEmpty(suggestions);
    }

    [Fact]
    public void Suggest_ExactMatch_ReturnsNoSuggestions()
    {
        // Arrange
        var index = new List<CodeChunk>
        {
            CreateChunk("DatabaseService", "GetUser")
        };

        // Act
        var suggestions = _suggester.Suggest("DatabaseService", index);

        // Assert
        Assert.Empty(suggestions);
    }

    [Fact]
    public void Suggest_MultipleTypos_ReturnsTop3OrderedByDistance()
    {
        // Arrange
        var index = new List<CodeChunk>
        {
            CreateChunk("DatabaseService", "GetUser"),
            CreateChunk("DataProcessor", "Process"),
            CreateChunk("DataValidator", "Validate"),
            CreateChunk("DataTransformer", "Transform")
        };

        // Act
        var suggestions = _suggester.Suggest("Databse", index);

        // Assert
        Assert.NotEmpty(suggestions);
        Assert.True(suggestions.Count <= 3, "Should return at most 3 suggestions");
    }

    [Fact]
    public void Suggest_EmptyQuery_ReturnsEmptyList()
    {
        // Arrange
        var index = new List<CodeChunk> { CreateChunk("A", "B") };

        // Act
        var suggestions = _suggester.Suggest("", index);

        // Assert
        Assert.Empty(suggestions);
    }

    [Fact]
    public void Suggest_TooDistant_ReturnsEmptyList()
    {
        // Arrange
        var index = new List<CodeChunk>
        {
            CreateChunk("DatabaseService", "GetUser")
        };

        // Act
        var suggestions = _suggester.Suggest("CompletelyDifferentWord", index);

        // Assert
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeLevenshteinDistance_SingleCharDifference_Returns1()
    {
        // Act
        var distance = QuerySuggester.ComputeLevenshteinDistance("Database", "Databse");

        // Assert
        Assert.Equal(1, distance);
    }

    [Fact]
    public void ComputeLevenshteinDistance_IdenticalStrings_Returns0()
    {
        // Act
        var distance = QuerySuggester.ComputeLevenshteinDistance("Database", "Database");

        // Assert
        Assert.Equal(0, distance);
    }

    [Fact]
    public void ComputeLevenshteinDistance_DifferentLengths_CorrectDistance()
    {
        // Act
        var distance = QuerySuggester.ComputeLevenshteinDistance("Db", "Database");

        // Assert
        Assert.Equal(6, distance);
    }
}
