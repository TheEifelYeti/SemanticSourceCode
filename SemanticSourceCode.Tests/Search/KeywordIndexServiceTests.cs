using SemanticSourceCode.Models;
using SemanticSourceCode.Search;
using Xunit;

namespace SemanticSourceCode.Tests.Search;

public class KeywordIndexServiceTests
{
    [Fact]
    public void Tokenize_CamelCase_ReturnsSplitTerms()
    {
        // Arrange & Act
        var terms = KeywordIndexService.Tokenize("MyDatabaseClass");

        // Assert
        Assert.Contains("my", terms);
        Assert.Contains("database", terms);
        Assert.Contains("class", terms);
        Assert.Contains("mydatabaseclass", terms);
    }

    [Fact]
    public void Tokenize_PascalCase_ReturnsSplitTerms()
    {
        // Arrange & Act
        var terms = KeywordIndexService.Tokenize("GetUserById");

        // Assert
        Assert.Contains("get", terms);
        Assert.Contains("user", terms);
        Assert.Contains("by", terms);
        Assert.Contains("id", terms);
        Assert.Contains("getuserbyid", terms);
    }

    [Fact]
    public void Tokenize_UnderscoreSeparated_ReturnsTerms()
    {
        // Arrange & Act
        var terms = KeywordIndexService.Tokenize("get_user_by_id");

        // Assert
        Assert.Contains("get", terms);
        Assert.Contains("user", terms);
        Assert.Contains("by", terms);
        Assert.Contains("id", terms);
    }

    [Fact]
    public void Tokenize_NamespacePath_ReturnsTerms()
    {
        // Arrange & Act
        var terms = KeywordIndexService.Tokenize("Api.Controllers.Home");

        // Assert
        Assert.Contains("api", terms);
        Assert.Contains("controllers", terms);
        Assert.Contains("home", terms);
    }

    [Fact]
    public void Tokenize_EmptyString_ReturnsEmptyList()
    {
        // Arrange & Act
        var terms = KeywordIndexService.Tokenize("");

        // Assert
        Assert.Empty(terms);
    }

    [Fact]
    public void ExtractTerms_ClassName_HasHighestWeight()
    {
        // Arrange
        var service = new KeywordIndexService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            null);

        var chunk = new CodeChunk
        {
            ClassName = "DatabaseService",
            MemberName = "Save",
            Signature = "public void Save()",
            FilePath = "/src/Services/DatabaseService.cs",
            NamespaceName = "MyApp.Services",
            Content = "public class DatabaseService { public void Save() { } }"
        };

        // Act
        var terms = service.ExtractTerms(chunk);

        // Assert
        var databaseTerm = terms.First(t => t.Term == "database");
        Assert.Equal(1.0f, databaseTerm.Weight); // ClassName weight

        var saveTerm = terms.First(t => t.Term == "save");
        Assert.Equal(0.8f, saveTerm.Weight); // MemberName weight
    }

    [Fact]
    public void ExtractTerms_Content_LimitedTo50Terms()
    {
        // Arrange
        var service = new KeywordIndexService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            null);

        var longContent = string.Join(" ", Enumerable.Range(1, 100).Select(i => $"word{i}"));
        var chunk = new CodeChunk
        {
            ClassName = "A",
            MemberName = "B",
            Signature = "void B()",
            FilePath = "/a.cs",
            NamespaceName = "N",
            Content = longContent
        };

        // Act
        var terms = service.ExtractTerms(chunk);

        // Assert
        var contentTerms = terms.Where(t => t.Weight == 0.3f).ToList();
        Assert.True(contentTerms.Count <= 50, "Content terms should be limited to 50");
    }
}
