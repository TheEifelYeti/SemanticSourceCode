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
    public void ExtractTerms_Content_LimitedTo200Terms()
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

        // Assert - updated from 50 to 200 terms
        var contentTerms = terms.Where(t => t.Weight == 0.3f).ToList();
        Assert.True(contentTerms.Count <= 200, "Content terms should be limited to 200");
    }

    [Fact]
    public void ExtractIdentifierParts_DotNotation_ReturnsSegments()
    {
        // Arrange
        var content = "if (Auto.Tuer.Scheibe == 1 || Auto.Tuer.Scheibe == 2) {";

        // Act
        var parts = KeywordIndexService.ExtractIdentifierParts(content);

        // Assert
        Assert.Contains("auto", parts);
        Assert.Contains("tuer", parts);
        Assert.Contains("scheibe", parts);
        Assert.Contains("auto.tuer.scheibe", parts);
    }

    [Fact]
    public void ExtractIdentifierParts_NoDots_ReturnsEmpty()
    {
        // Arrange
        var content = "var x = 42;";

        // Act
        var parts = KeywordIndexService.ExtractIdentifierParts(content);

        // Assert
        Assert.Empty(parts);
    }

    [Fact]
    public void ExtractIdentifierParts_EmptyString_ReturnsEmpty()
    {
        // Act
        var parts = KeywordIndexService.ExtractIdentifierParts("");

        // Assert
        Assert.Empty(parts);
    }

    [Fact]
    public void ExtractIdentifierParts_SingleSegmentTooShort_Ignored()
    {
        // Arrange - "A.B" has segments of length 1, should be ignored
        var content = "x.y.z";

        // Act
        var parts = KeywordIndexService.ExtractIdentifierParts(content);

        // Assert - all segments are length 1, so nothing should be extracted
        Assert.Empty(parts);
    }

    [Fact]
    public void ExtractIdentifierParts_FullPathPreserved()
    {
        // Arrange
        var content = "Api.Controllers.HomeController.GetUser";

        // Act
        var parts = KeywordIndexService.ExtractIdentifierParts(content);

        // Assert
        Assert.Contains("api", parts);
        Assert.Contains("controllers", parts);
        Assert.Contains("homecontroller", parts);
        Assert.Contains("getuser", parts);
        Assert.Contains("api.controllers.homecontroller.getuser", parts);
    }

    [Fact]
    public void ExtractTerms_IdentifierParts_HaveHigherWeight()
    {
        // Arrange
        var service = new KeywordIndexService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            null);

        var chunk = new CodeChunk
        {
            ClassName = "Auto",
            MemberName = "CheckTuer",
            Signature = "void CheckTuer()",
            FilePath = "/auto.cs",
            NamespaceName = "MyApp",
            Content = "if (Auto.Tuer.Scheibe == 1 || Auto.Tuer.Scheibe == 2) {"
        };

        // Act
        var terms = service.ExtractTerms(chunk);

        // Assert - identifier parts should have weight 0.35
        var scheibeTerm = terms.FirstOrDefault(t => t.Term == "scheibe");
        Assert.True(scheibeTerm != default, "scheibe should be found in terms");
        Assert.Equal(0.35f, scheibeTerm.Weight);
    }
}
