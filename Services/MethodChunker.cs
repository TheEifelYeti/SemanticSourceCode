using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SemanticSourceCode.Models;

namespace SemanticSourceCode.Services;

/// <summary>
/// Splits long method bodies into multiple chunks at statement boundaries.
/// This enables semantic search within method bodies, not just for the method as a whole.
/// </summary>
public class MethodChunker
{
    private readonly ChunkingOptions _options;

    public MethodChunker(ChunkingOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Determines if a method body needs to be split into multiple chunks.
    /// </summary>
    public bool NeedsSplitting(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var lineCount = body.Split('\n').Length;
        return lineCount > _options.MaxMethodChunkLines;
    }

    /// <summary>
    /// Splits a method body into multiple chunks at statement boundaries.
    /// Each chunk is a list of statements.
    /// </summary>
    public List<string> SplitMethodBody(string body, int startLineOffset = 0)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new List<string> { body };

        var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // If the body is small enough, return it as a single chunk
        if (lines.Count <= _options.MaxMethodChunkLines)
        {
            return new List<string> { string.Join("\n", lines) };
        }

        var chunks = new List<string>();
        var stepSize = _options.MaxMethodChunkLines - _options.OverlapLines;

        for (int i = 0; i < lines.Count; i += stepSize)
        {
            var endIndex = Math.Min(i + _options.MaxMethodChunkLines, lines.Count);
            var chunkLines = lines.GetRange(i, endIndex - i);
            chunks.Add(string.Join("\n", chunkLines));

            // Stop if we've covered all lines
            if (endIndex >= lines.Count)
                break;
        }

        return chunks;
    }

    /// <summary>
    /// Splits a method body by extracting top-level statements and groups them into chunks.
    /// This preserves statement boundaries (each chunk starts/ends on statement boundaries).
    /// </summary>
    public List<string> SplitMethodBodyByStatements(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new List<string> { body ?? string.Empty };

        var lines = body.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // If the body is small enough, return it as a single chunk
        if (lines.Count <= _options.MaxMethodChunkLines)
        {
            return new List<string> { string.Join("\n", lines) };
        }

        // Parse the body to find statement boundaries
        var statementInfo = FindStatementInfo(body);

        if (statementInfo.Count == 0)
        {
            // Fall back to line-based splitting
            return SplitMethodBody(body);
        }

        // Group statements into chunks
        // Each entry in statementInfo: (startLine, endLine) — both 1-based, inclusive
        var chunks = new List<string>();
        var currentChunkStart = 0; // 0-based line index
        var currentChunkLines = 0;
        var prevStatementEnd = 0;

        foreach (var (stmtStart, stmtEnd) in statementInfo)
        {
            // Calculate the gap between previous statement end and current statement start
            var gapLines = stmtStart - prevStatementEnd - 1;
            var stmtLength = (stmtEnd - stmtStart + 1) + Math.Max(0, gapLines);

            // If adding this statement would exceed the chunk size, finalize the current chunk
            if (currentChunkLines + stmtLength > _options.MaxMethodChunkLines && currentChunkLines > 0)
            {
                // Finalize current chunk
                var chunkEnd = currentChunkStart + currentChunkLines;
                if (chunkEnd > lines.Count) chunkEnd = lines.Count;
                if (currentChunkStart >= 0 && currentChunkStart < lines.Count && chunkEnd > currentChunkStart)
                {
                    var chunkLines = lines.GetRange(currentChunkStart, chunkEnd - currentChunkStart);
                    chunks.Add(string.Join("\n", chunkLines));
                }

                // Start new chunk with overlap (include previous lines for context)
                var overlapStart = Math.Max(currentChunkStart, chunkEnd - _options.OverlapLines);
                if (overlapStart < 0) overlapStart = 0;
                if (overlapStart > lines.Count) overlapStart = lines.Count;
                currentChunkStart = overlapStart;
                currentChunkLines = chunkEnd - overlapStart;
            }

            // Add the gap and statement to current chunk
            if (currentChunkLines == 0)
            {
                currentChunkStart = prevStatementEnd; // Include previous end line (e.g., '{')
                currentChunkLines = stmtStart - prevStatementEnd;
            }
            else
            {
                currentChunkLines += gapLines + (stmtEnd - stmtStart + 1);
            }

            prevStatementEnd = stmtEnd;
        }

        // Add the last chunk
        if (currentChunkLines > 0)
        {
            var chunkEnd = currentChunkStart + currentChunkLines;
            if (chunkEnd > lines.Count) chunkEnd = lines.Count;
            if (currentChunkStart < 0) currentChunkStart = 0;
            if (currentChunkStart < lines.Count && chunkEnd > currentChunkStart)
            {
                var chunkLines = lines.GetRange(currentChunkStart, chunkEnd - currentChunkStart);
                chunks.Add(string.Join("\n", chunkLines));
            }
        }

        // If chunking produced no results (shouldn't happen, but be defensive)
        if (chunks.Count == 0)
        {
            chunks.Add(string.Join("\n", lines));
        }

        return chunks;
    }

    /// <summary>
    /// Finds the line ranges (start, end) for each top-level statement in the body.
    /// Returns a list of (startLine, endLine) tuples — both 1-based, inclusive.
    /// </summary>
    private List<(int Start, int End)> FindStatementInfo(string body)
    {
        var info = new List<(int, int)>();

        try
        {
            // Strip outer braces if present (sometimes body includes them)
            var cleanBody = body.Trim();
            if (cleanBody.StartsWith("{") && cleanBody.EndsWith("}"))
            {
                // Remove first { and last }
                cleanBody = cleanBody.Substring(1, cleanBody.Length - 2).Trim();
            }

            // Parse the body as a block of statements (no outer braces needed)
            var wrappedCode = $"class T {{ void M() {{ {cleanBody} }} }}";
            var tree = CSharpSyntaxTree.ParseText(wrappedCode);
            var root = tree.GetRoot();

            var methodDecl = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (methodDecl?.Body == null)
                return info;

            foreach (var statement in methodDecl.Body.Statements)
            {
                var span = statement.GetLocation().GetLineSpan();
                // StartLinePosition.Line and EndLinePosition.Line are 0-based
                info.Add((span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
            }
        }
        catch
        {
            // If parsing fails, return empty list (caller will use line-based fallback)
        }

        return info;
    }
}
