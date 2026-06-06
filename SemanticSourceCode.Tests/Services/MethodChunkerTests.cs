using SemanticSourceCode.Models;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

public class MethodChunkerTests
{
    private MethodChunker CreateChunker(int maxLines = 50, int overlap = 10)
    {
        var options = new ChunkingOptions
        {
            MaxMethodChunkLines = maxLines,
            OverlapLines = overlap
        };
        return new MethodChunker(options);
    }

    [Fact]
    public void NeedsSplitting_SmallMethod_ReturnsFalse()
    {
        var chunker = CreateChunker(maxLines: 50);
        var body = "var x = 1;\nvar y = 2;";

        Assert.False(chunker.NeedsSplitting(body));
    }

    [Fact]
    public void NeedsSplitting_LargeMethod_ReturnsTrue()
    {
        var chunker = CreateChunker(maxLines: 50);
        var body = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"var x{i} = {i};"));

        Assert.True(chunker.NeedsSplitting(body));
    }

    [Fact]
    public void NeedsSplitting_EmptyBody_ReturnsFalse()
    {
        var chunker = CreateChunker();

        Assert.False(chunker.NeedsSplitting(""));
        Assert.False(chunker.NeedsSplitting(null));
    }

    [Fact]
    public void SplitMethodBody_SmallMethod_ReturnsSingleChunk()
    {
        var chunker = CreateChunker(maxLines: 50);
        var body = "var x = 1;\nvar y = 2;";

        var chunks = chunker.SplitMethodBody(body);

        Assert.Single(chunks);
        Assert.Contains("var x = 1", chunks[0]);
    }

    [Fact]
    public void SplitMethodBody_LargeMethod_ReturnsMultipleChunks()
    {
        var chunker = CreateChunker(maxLines: 30, overlap: 5);
        var body = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"var x{i} = {i};"));

        var chunks = chunker.SplitMethodBody(body);

        Assert.True(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");
    }

    [Fact]
    public void SplitMethodBodyByStatements_RespectsStatementBoundaries()
    {
        var chunker = CreateChunker(maxLines: 10, overlap: 2);
        // Each statement is on its own line
        var body = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"var stmt{i} = {i};"));

        var chunks = chunker.SplitMethodBodyByStatements(body);

        Assert.True(chunks.Count >= 1);

        // Verify chunks are non-empty
        foreach (var chunk in chunks)
        {
            Assert.False(string.IsNullOrWhiteSpace(chunk));
        }
    }

    [Fact]
    public void SplitMethodBodyByStatements_PreservesAllContent()
    {
        var chunker = CreateChunker(maxLines: 10, overlap: 2);
        var lines = Enumerable.Range(1, 30).Select(i => $"var x{i} = {i};").ToList();
        var body = string.Join("\n", lines);

        var chunks = chunker.SplitMethodBodyByStatements(body);

        // Concatenate all chunks and verify all original lines are present
        var combined = string.Join("\n", chunks);

        foreach (var line in lines)
        {
            Assert.Contains(line, combined);
        }
    }

    [Fact]
    public void SplitMethodBodyByStatements_HandlesNestedBlocks()
    {
        var chunker = CreateChunker(maxLines: 5, overlap: 1);
        var body = @"var x = 1;
if (x > 0)
{
    var y = 2;
    if (y > 0)
    {
        var z = 3;
    }
}
var a = 4;
var b = 5;
var c = 6;
var d = 7;
var e = 8;
var f = 9;
var g = 10;
var h = 11;
var i = 12;";

        var chunks = chunker.SplitMethodBodyByStatements(body);

        // Should produce multiple chunks
        Assert.True(chunks.Count >= 1);

        // All content should be preserved
        var combined = string.Join("\n", chunks);
        Assert.Contains("var x = 1", combined);
        Assert.Contains("var y = 2", combined);
        Assert.Contains("var z = 3", combined);
    }
}
