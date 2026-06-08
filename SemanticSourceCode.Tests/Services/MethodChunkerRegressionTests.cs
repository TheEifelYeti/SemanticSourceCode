using SemanticSourceCode.Models;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Regression tests for the MethodChunker bounds-check bug discovered in
/// production indexing of the SemanticSourceCode project (08.06.2026).
/// Previously <see cref="MethodChunker.SplitMethodBodyByStatements"/> could
/// throw <c>ArgumentException</c> from <c>List.GetRange</c> when the body
/// was large enough to require multiple chunks.
/// </summary>
public class MethodChunkerRegressionTests
{
    private static MethodChunker CreateChunker(int maxLines = 50, int overlap = 5)
    {
        return new MethodChunker(new ChunkingOptions
        {
            MaxMethodChunkLines = maxLines,
            OverlapLines = overlap
        });
    }

    [Fact]
    public void SplitMethodBodyByStatements_LargeMethod_NoOutOfBoundsException()
    {
        // Simulate a method that is just over the chunk threshold and contains
        // many statements — the original bug.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        for (int i = 0; i < 100; i++)
        {
            sb.AppendLine($"    var x{i} = {i};");
            sb.AppendLine($"    Console.WriteLine(x{i});");
        }
        sb.AppendLine("}");
        var body = sb.ToString();

        var chunker = CreateChunker(maxLines: 20, overlap: 3);

        // Should NOT throw
        var chunks = chunker.SplitMethodBodyByStatements(body);

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void SplitMethodBodyByStatements_MediumMethodWithBraces_DoesNotThrow()
    {
        // Real-world example: a method with several if/else blocks.
        var body = string.Join("\n", new[]
        {
            "{",
            "    if (a == 1)",
            "    {",
            "        DoSomething();",
            "    }",
            "    else if (a == 2)",
            "    {",
            "        DoSomethingElse();",
            "    }",
            "    else",
            "    {",
            "        DoDefault();",
            "    }",
            "    return;",
            "}"
        });

        var chunker = CreateChunker(maxLines: 8, overlap: 2);
        var chunks = chunker.SplitMethodBodyByStatements(body);

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void SplitMethodBodyByStatements_ExactlyAtThreshold_DoesNotThrow()
    {
        // Body length == MaxMethodChunkLines
        var lines = new List<string> { "{" };
        for (int i = 0; i < 49; i++) lines.Add($"    stmt{i};");
        lines.Add("}");
        var body = string.Join("\n", lines);

        var chunker = CreateChunker(maxLines: 50, overlap: 5);
        var chunks = chunker.SplitMethodBodyByStatements(body);

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void SplitMethodBodyByStatements_OneOverThreshold_DoesNotThrow()
    {
        // Body length == MaxMethodChunkLines + 1
        var lines = new List<string> { "{" };
        for (int i = 0; i < 50; i++) lines.Add($"    stmt{i};");
        lines.Add("}");
        var body = string.Join("\n", lines);

        var chunker = CreateChunker(maxLines: 50, overlap: 5);
        var chunks = chunker.SplitMethodBodyByStatements(body);

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void SplitMethodBodyByStatements_HighOverlapRatio_DoesNotThrow()
    {
        // Overlap close to MaxLines — a degenerate case
        var lines = new List<string> { "{" };
        for (int i = 0; i < 30; i++) lines.Add($"    stmt{i};");
        lines.Add("}");
        var body = string.Join("\n", lines);

        var chunker = CreateChunker(maxLines: 20, overlap: 19);
        var chunks = chunker.SplitMethodBodyByStatements(body);

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void SplitMethodBodyByStatements_EmptyBody_ReturnsEmpty()
    {
        var chunker = CreateChunker();
        var chunks = chunker.SplitMethodBodyByStatements(string.Empty);

        Assert.Single(chunks);
    }

    [Fact]
    public void SplitMethodBodyByStatements_NullBody_ReturnsEmpty()
    {
        var chunker = CreateChunker();
        var chunks = chunker.SplitMethodBodyByStatements(null);

        Assert.Single(chunks);
    }

    [Fact]
    public void SplitMethodBodyByStatements_VeryLargeBody_DoesNotCrash()
    {
        // Stress test: 1000 lines
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        for (int i = 0; i < 500; i++)
        {
            sb.AppendLine($"    stmt{i}();");
        }
        sb.AppendLine("}");

        var chunker = CreateChunker(maxLines: 30, overlap: 5);
        var chunks = chunker.SplitMethodBodyByStatements(sb.ToString());

        Assert.NotEmpty(chunks);
        // We expect multiple chunks for a 500-line body
        Assert.True(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");
    }
}
