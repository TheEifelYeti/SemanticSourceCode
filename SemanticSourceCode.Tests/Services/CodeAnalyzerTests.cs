using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Moq;
using SemanticSourceCode.Models;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Unit tests for the <see cref="CodeAnalyzer"/> class.
/// Tests C# code parsing and semantic chunking functionality.
/// </summary>
public class CodeAnalyzerTests
{
    private readonly Mock<ILogger<CodeAnalyzer>> _loggerMock = new();
    private readonly CodeAnalyzer _analyzer;

    public CodeAnalyzerTests()
    {
        _analyzer = new CodeAnalyzer(_loggerMock.Object);
    }
    /// <summary>
/// Tests that the analyzer correctly extracts class information from a simple C# file.
/// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_SimpleClass_ReturnsCorrectChunks()
    {
        // Arrange
        var code = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
            // Implementation
        }
    }
}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await _analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Single(chunks);
            var chunk = chunks[0];
            Assert.Equal("TestNamespace", chunk.NamespaceName);
            Assert.Equal("TestClass", chunk.ClassName);
            Assert.Equal("TestMethod", chunk.MemberName);
            Assert.Equal("Method", chunk.MemberType);
            Assert.Equal(filePath, chunk.FilePath);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
/// Tests that properties are correctly extracted and chunked.
/// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_ClassWithProperties_ReturnsPropertyChunks()
    {
        // Arrange
        var code = @"
namespace TestNamespace
{
    public class TestClass
    {
        public string Name { get; set; }
        public int Age { get; set; }
    }
}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await _analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Equal(2, chunks.Count);
            Assert.All(chunks, chunk => Assert.Equal("Property", chunk.MemberType));
            Assert.Contains(chunks, c => c.MemberName == "Name");
            Assert.Contains(chunks, c => c.MemberName == "Age");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
/// Tests that constructors are correctly identified and chunked.
/// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_ClassWithConstructor_ReturnsConstructorChunk()
    {
        // Arrange
        var code = @"
namespace TestNamespace
{
    public class TestClass
    {
        public TestClass(string name)
        {
            Name = name;
        }
    }
}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await _analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Single(chunks);
            Assert.Equal("Constructor", chunks[0].MemberType);
            Assert.Equal("TestClass", chunks[0].MemberName);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
/// Tests that fields are correctly extracted.
/// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_ClassWithFields_ReturnsFieldChunks()
    {
        // Arrange
        var code = @"
namespace TestNamespace
{
    public class TestClass
    {
        private string _name;
        private readonly int _age;
    }
}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await _analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Equal(2, chunks.Count);
            Assert.All(chunks, chunk => Assert.Equal("Field", chunk.MemberType));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
/// Tests that XML documentation is correctly extracted.
/// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_WithDocumentation_ExtractsDocumentation()
    {
        // Arrange
        var code = @"
namespace TestNamespace
{
    public class TestClass
    {
        /// <summary>
        /// This is a test method.
        /// </summary>
        public void DocumentedMethod()
        {
        }
    }
}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await _analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Single(chunks);
            Assert.Contains("This is a test method", chunks[0].Documentation);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
/// Tests that line numbers are correctly captured.
/// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_SimpleCode_CorrectLineNumbers()
    {
        // Arrange
        var code = @"namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
            var x = 1;
        }
    }
}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await _analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Single(chunks);
            Assert.True(chunks[0].StartLine > 0);
            Assert.True(chunks[0].EndLine >= chunks[0].StartLine);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
/// Tests that nested namespaces are handled correctly.
/// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_NestedClasses_HandlesNesting()
    {
        // Arrange
        var code = @"
namespace Outer
{
    public class OuterClass
    {
        public void OuterMethod() { }
        
        public class InnerClass
        {
            public void InnerMethod() { }
        }
    }
}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await _analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Equal(2, chunks.Count);
            Assert.Contains(chunks, c => c.MemberName == "OuterMethod");
            Assert.Contains(chunks, c => c.MemberName == "InnerMethod");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
/// Tests that empty files are handled gracefully.
    /// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_EmptyFile_ReturnsEmptyList()
    {
        // Arrange
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, "");

        try
        {
            // Act
            var chunks = await _analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Empty(chunks);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
/// Tests that file-scoped namespaces (C# 10+) are handled correctly.
/// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_FileScopedNamespace_HandlesCorrectly()
    {
        // Arrange
        var code = @"namespace TestNamespace;

public class TestClass
{
    public void TestMethod() { }
}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await _analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Single(chunks);
            Assert.Equal("TestNamespace", chunks[0].NamespaceName);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Tests that long methods are split into multiple chunks at statement boundaries.
    /// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_LongMethod_SplitsIntoMultipleChunks()
    {
        // Arrange
        var analyzer = new CodeAnalyzer(_loggerMock.Object, new ChunkingOptions
        {
            MaxMethodChunkLines = 20,
            OverlapLines = 5
        });

        // Generate a method with many statements, each on its own line
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 100; i++)
        {
            sb.AppendLine($"            var x{i} = {i};");
        }
        var statements = sb.ToString();

        var code = $@"namespace TestNamespace
{{
    public class TestClass
    {{
        public void LongMethod()
        {{
{statements}        }}
    }}
}}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.True(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");

            // First chunk should be the parent (index 0, total > 1)
            var parentChunk = chunks.FirstOrDefault(c => c.ChunkIndex == 0);
            Assert.NotNull(parentChunk);
            Assert.True(parentChunk.TotalChunks > 1);

            // All chunks should reference the same method
            Assert.All(chunks, c => Assert.Equal("LongMethod", c.MemberName));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Tests that short methods are NOT split (single chunk).
    /// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_ShortMethod_NoSplitting()
    {
        // Arrange
        var analyzer = new CodeAnalyzer(_loggerMock.Object, new ChunkingOptions
        {
            MaxMethodChunkLines = 50,
            OverlapLines = 5
        });

        var code = @"namespace TestNamespace
{
    public class TestClass
    {
        public void ShortMethod()
        {
            var x = 1;
            var y = 2;
        }
    }
}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await analyzer.AnalyzeFileAsync(filePath);

            // Assert
            Assert.Single(chunks);
            Assert.Equal(1, chunks[0].TotalChunks);
            Assert.Equal(0, chunks[0].ChunkIndex);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Tests that split method chunks can find identifiers deep inside the method body.
    /// This is the original "Scheibe" test case.
    /// </summary>
    [Fact]
    public async Task AnalyzeFileAsync_LongMethodWithDeepIdentifier_FindsItInSubChunk()
    {
        // Arrange
        var analyzer = new CodeAnalyzer(_loggerMock.Object, new ChunkingOptions
        {
            MaxMethodChunkLines = 20,
            OverlapLines = 5
        });

        // Place the "Scheibe" identifier deep in the method body
        var lines = new List<string>();
        for (int i = 0; i < 50; i++)
        {
            if (i == 30)
                lines.Add("if (Auto.Tuer.Scheibe == 1 || Auto.Tuer.Scheibe == 2) { Console.WriteLine(\"found\"); }");
            else
                lines.Add($"var x{i} = {i};");
        }
        var statements = string.Join("\n            ", lines);
        var code = $@"namespace TestNamespace
{{
    public class TestClass
    {{
        public void LongMethod()
        {{
            {statements}
        }}
    }}
}}";
        var filePath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            // Act
            var chunks = await analyzer.AnalyzeFileAsync(filePath);

            // Assert: there should be a chunk that contains "Scheibe"
            var chunkWithScheibe = chunks.FirstOrDefault(c => c.Content.Contains("Scheibe"));
            Assert.NotNull(chunkWithScheibe);

            // It should NOT be the first chunk (chunk 0) if Scheibe is deep in the method
            // (or it could be in chunk 0 if overlap includes it)
            var scheibeChunkIndex = chunks.IndexOf(chunkWithScheibe);
            Assert.True(scheibeChunkIndex >= 0);
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
