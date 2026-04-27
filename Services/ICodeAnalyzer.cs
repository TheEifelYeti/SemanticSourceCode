using SemanticSourceCode.Models;

namespace SemanticSourceCode.Services;

public interface ICodeAnalyzer
{
    Task<List<CodeChunk>> AnalyzeFileAsync(string filePath);
    Task<List<CodeChunk>> AnalyzeDirectoryAsync(string directoryPath, string searchPattern = "*.cs");
}
