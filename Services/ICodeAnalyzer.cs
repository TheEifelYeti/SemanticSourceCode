using SemanticSourceCode.Models;

namespace SemanticSourceCode.Services;

/// <summary>
/// Analyzes C# source code files and extracts semantic chunks.
/// Uses Roslyn to parse C# syntax and create structured representations
/// of classes, methods, properties, and other members.
/// </summary>
public interface ICodeAnalyzer
{
    /// <summary>
    /// Analyzes a single C# file and extracts code chunks.
    /// </summary>
    /// <param name="filePath">The full path to the C# file to analyze.</param>
    /// <returns>A list of <see cref="CodeChunk"/> objects representing
    /// the extracted code members (methods, properties, constructors, fields).</returns>
    /// <exception cref="FileNotFoundException">Thrown when the specified file does not exist.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs while reading the file.</exception>
    Task<List<CodeChunk>> AnalyzeFileAsync(string filePath);

    /// <summary>
    /// Analyzes all C# files in a directory and extracts code chunks.
    /// </summary>
    /// <param name="directoryPath">The root directory to search for C# files.</param>
    /// <param name="searchPattern">The search pattern to match against file names. Default is "*.cs".</param>
    /// <returns>A list of <see cref="CodeChunk"/> objects from all analyzed files.
    /// Errors in individual files are logged and skipped.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when the specified directory does not exist.</exception>
    Task<List<CodeChunk>> AnalyzeDirectoryAsync(string directoryPath, string searchPattern = "*.cs");
}
