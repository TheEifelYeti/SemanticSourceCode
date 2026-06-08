using SemanticSourceCode.Models;
using SemanticSourceCode.Services;
using Xunit;

namespace SemanticSourceCode.Tests.Services;

/// <summary>
/// Regression tests for the "5 chunks always re-indexed" bug.
/// 
/// Bug: ComputeSemanticId did not include the member signature, so method/constructor
/// overloads collided on the same ID. When two chunks share an ID, the second
/// INSERT overwrites the first in the database, and the next run reports the
/// "lost" chunk as changed even though no code changed.
/// 
/// Fix: ComputeSemanticId now takes the signature as an additional input,
/// making IDs unique across overloads.
/// 
/// These tests run against the actual project sources to catch any regression
/// in the analyzer pipeline (mock-only tests did not catch the original bug).
/// </summary>
public class CodeAnalyzerOverloadRegressionTests
{
    private static readonly string ProjectRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task AnalyzeDirectory_NoDuplicateChunkIds()
    {
        // Run the analyzer on the actual project sources — if anyone reintroduces
        // a collision in ComputeSemanticId, this test will fail loudly.
        var analyzer = new CodeAnalyzer(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeAnalyzer>.Instance);

        var chunks = await analyzer.AnalyzeDirectoryAsync(ProjectRoot);

        var duplicateGroups = chunks
            .GroupBy(c => c.Id)
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicateGroups);
    }

    [Fact]
    public async Task AnalyzeDirectory_TwoRuns_ProduceIdenticalContentHashes()
    {
        // Determinism: indexing the same code twice should yield the same
        // (Id, ContentHash) pairs. If hashes drift between runs, incremental
        // re-indexing will re-index unchanged chunks.
        var analyzer = new CodeAnalyzer(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeAnalyzer>.Instance);

        var first = await analyzer.AnalyzeDirectoryAsync(ProjectRoot);
        var second = await analyzer.AnalyzeDirectoryAsync(ProjectRoot);

        var firstById = first.ToDictionary(c => c.Id);

        Assert.Equal(first.Count, second.Count);

        foreach (var c2 in second)
        {
            Assert.True(
                firstById.ContainsKey(c2.Id),
                $"Run 2 produced a chunk Id that did not exist in Run 1: {c2.Id}");

            Assert.Equal(
                firstById[c2.Id].ContentHash,
                c2.ContentHash);
        }
    }

    [Fact]
    public void ExtractCallTargets_Overloads_AreDisambiguatedBySignature()
    {
        // Targeted regression for the ExtractCallTargets overloads that triggered
        // the bug report. Both private methods share MemberName, MemberType,
        // and ChunkIndex — they MUST be distinguished by their signature.
        var sigMethod = "private List<string> ExtractCallTargets(MethodDeclarationSyntax method)";
        var sigCtor = "private List<string> ExtractCallTargets(ConstructorDeclarationSyntax ctor)";

        var idMethod = CodeAnalyzer.ComputeSemanticId(
            ProjectRoot + "/Services/CodeAnalyzer.cs",
            "SemanticSourceCode.Services",
            "CodeAnalyzer",
            "ExtractCallTargets",
            "Method",
            0,
            sigMethod);

        var idCtor = CodeAnalyzer.ComputeSemanticId(
            ProjectRoot + "/Services/CodeAnalyzer.cs",
            "SemanticSourceCode.Services",
            "CodeAnalyzer",
            "ExtractCallTargets",
            "Method",
            0,
            sigCtor);

        Assert.NotEqual(idMethod, idCtor);
    }

    [Fact]
    public void OllamaEmbeddingService_ConstructorOverloads_AreDisambiguatedBySignature()
    {
        // Targeted regression for OllamaEmbeddingService's two constructors.
        var sig1 = "public OllamaEmbeddingService(IConfiguration configuration, ILogger<OllamaEmbeddingService>? logger = null)";
        var sig2 = "public OllamaEmbeddingService(IConfiguration configuration, ILogger<OllamaEmbeddingService>? logger, HttpClient httpClient)";

        var id1 = CodeAnalyzer.ComputeSemanticId(
            ProjectRoot + "/Services/OllamaEmbeddingService.cs",
            "SemanticSourceCode.Services",
            "OllamaEmbeddingService",
            "OllamaEmbeddingService",
            "Constructor",
            0,
            sig1);

        var id2 = CodeAnalyzer.ComputeSemanticId(
            ProjectRoot + "/Services/OllamaEmbeddingService.cs",
            "SemanticSourceCode.Services",
            "OllamaEmbeddingService",
            "OllamaEmbeddingService",
            "Constructor",
            0,
            sig2);

        Assert.NotEqual(id1, id2);
    }
}
