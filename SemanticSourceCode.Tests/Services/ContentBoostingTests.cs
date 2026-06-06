using Xunit;
using System.IO;
using System.Reflection;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Moq;
using SemanticSourceCode.Models;
using SemanticSourceCode.Services;

namespace SemanticSourceCode.Tests.Services;

public class ContentBoostingTests
{
    private CodeAnalyzer _analyzer = null!;
    private MethodInfo? _boostContentMethod;

    public ContentBoostingTests()
    {
        var loggerMock = new Mock<ILogger<CodeAnalyzer>>();
        _analyzer = new CodeAnalyzer(loggerMock.Object);
        
        // Reflection verwenden, um auf die private BoostContent-Methode zuzugreifen
        var analyzerType = typeof(CodeAnalyzer);
        _boostContentMethod = analyzerType.GetMethod("BoostContent", 
            BindingFlags.NonPublic | BindingFlags.Instance);
    }
    
    [Fact]
    public void BoostContent_Should_Add_Class_Boost()
    {
        // Arrange
        var content = "public void TestMethod() { }";
        var className = "TestClass";
        var memberName = "TestMethod";
        
        // Act
        var result = InvokeBoostContent(content, className, memberName, false, false, false);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("[CLASS_BOOST]", result);
        Assert.Contains(className, result);
    }
    
    [Fact]
    public void BoostContent_Should_Add_Member_Boost()
    {
        // Arrange
        var content = "public void TestMethod() { }";
        var className = "TestClass";
        var memberName = "TestMethod";
        
        // Act
        var result = InvokeBoostContent(content, className, memberName, false, false, false);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("[MEMBER_BOOST]", result);
        Assert.Contains(memberName, result);
    }
    
    [Fact]
    public void BoostContent_Controller_Should_Add_Framework_Terms()
    {
        // Arrange
        var content = "public IActionResult Index() { }";
        var className = "HomeController";
        var memberName = "Index";
        
        // Act
        var result = InvokeBoostContent(content, className, memberName, true, false, false);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("[FRAMEWORK] controller api http", result);
    }
    
    [Fact]
    public void BoostContent_Service_Should_Add_Framework_Terms()
    {
        // Arrange
        var content = "public void ProcessData() { }";
        var className = "DataService";
        var memberName = "ProcessData";
        
        // Act
        var result = InvokeBoostContent(content, className, memberName, false, true, false);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("[FRAMEWORK] service business logic", result);
    }
    
    [Fact]
    public void BoostContent_Middleware_Should_Add_Framework_Terms()
    {
        // Arrange
        var content = "public async Task InvokeAsync(HttpContext context) { }";
        var className = "LoggingMiddleware";
        var memberName = "InvokeAsync";
        
        // Act
        var result = InvokeBoostContent(content, className, memberName, false, false, true);
        
        // Assert
        Assert.NotNull(result);
        Assert.Contains("[FRAMEWORK] middleware pipeline", result);
    }
    
    [Fact]
    public void CreateMethodChunk_Should_Return_Boosted_Content()
    {
        // Arrange
        var syntaxTree = CSharpSyntaxTree.ParseText(@"
            namespace TestNamespace 
            { 
                class TestClass 
                { 
                    /// <summary>
                    /// Test method documentation
                    /// </summary>
                    public void TestMethod() 
                    { 
                        // Test method body
                    } 
                } 
            }");
        
        var root = syntaxTree.GetRoot();
        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var method = classDecl.Members.OfType<MethodDeclarationSyntax>().First();
        var lines = new string[0];
        
        // Reflection verwenden, um auf die private CreateMethodChunk-Methode zuzugreifen
        var analyzerType = typeof(CodeAnalyzer);
        var createMethod = analyzerType.GetMethod("CreateMethodChunk", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        // Act
        var result = createMethod?.Invoke(_analyzer, new object[] { 
            "test.cs", 
            "TestNamespace", 
            "TestClass", 
            method, 
            "Class documentation",
            lines,
            false, // isController
            false, // isService
            false  // isMiddleware
        }) as CodeChunk;
        
        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Content);
        Assert.Contains("[CLASS_BOOST]", result.Content);
        Assert.Contains("[MEMBER_BOOST]", result.Content);
    }
    
    private string InvokeBoostContent(string content, string className, string memberName, 
        bool isController, bool isService, bool isMiddleware)
    {
        if (_boostContentMethod == null)
        {
            Assert.Fail("BoostContent method not found");
            return string.Empty;
        }
        
        var result = _boostContentMethod.Invoke(_analyzer, new object[] { 
            content, className, memberName, isController, isService, isMiddleware 
        });
        return result?.ToString() ?? string.Empty;
    }
}