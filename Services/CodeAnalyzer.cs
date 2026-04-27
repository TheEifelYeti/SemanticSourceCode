using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SemanticSourceCode.Models;
using System.Text;

namespace SemanticSourceCode.Services;

public class CodeAnalyzer : ICodeAnalyzer
{
    public async Task<List<CodeChunk>> AnalyzeFileAsync(string filePath)
    {
        var chunks = new List<CodeChunk>();
        var code = await File.ReadAllTextAsync(filePath);
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = await tree.GetRootAsync();
        var lines = code.Split('\n');

        var namespaceDeclarations = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>()
            .Concat(root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Cast<BaseNamespaceDeclarationSyntax>());

        foreach (var ns in namespaceDeclarations)
        {
            var namespaceName = ns.Name.ToString();
            
            var classDeclarations = ns.DescendantNodes().OfType<ClassDeclarationSyntax>();
            
            foreach (var classDecl in classDeclarations)
            {
                var className = classDecl.Identifier.Text;
                var classDocumentation = GetDocumentation(classDecl);

                // Analyze methods
                foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
                {
                    var chunk = CreateMethodChunk(filePath, namespaceName, className, method, classDocumentation, lines);
                    chunks.Add(chunk);
                }

                // Analyze properties
                foreach (var property in classDecl.Members.OfType<PropertyDeclarationSyntax>())
                {
                    var chunk = CreatePropertyChunk(filePath, namespaceName, className, property, classDocumentation, lines);
                    chunks.Add(chunk);
                }

                // Analyze constructors
                foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
                {
                    var chunk = CreateConstructorChunk(filePath, namespaceName, className, ctor, classDocumentation, lines);
                    chunks.Add(chunk);
                }

                // Analyze fields
                foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>())
                {
                    var chunk = CreateFieldChunk(filePath, namespaceName, className, field, classDocumentation, lines);
                    chunks.Add(chunk);
                }
            }
        }

        return chunks;
    }

    public async Task<List<CodeChunk>> AnalyzeDirectoryAsync(string directoryPath, string searchPattern = "*.cs")
    {
        var allChunks = new List<CodeChunk>();
        var files = Directory.EnumerateFiles(directoryPath, searchPattern, SearchOption.AllDirectories);

        foreach (var file in files)
        {
            try
            {
                var chunks = await AnalyzeFileAsync(file);
                allChunks.AddRange(chunks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing {file}: {ex.Message}");
            }
        }

        return allChunks;
    }

    private CodeChunk CreateMethodChunk(string filePath, string namespaceName, string className, 
        MethodDeclarationSyntax method, string classDocumentation, string[] lines)
    {
        var documentation = GetDocumentation(method);
        var signature = method.ToString().Split('\n')[0];
        var startLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = method.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var content = BuildContent(new[] { classDocumentation, documentation, signature, method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? "" });

        return new CodeChunk
        {
            FilePath = filePath,
            NamespaceName = namespaceName,
            ClassName = className,
            MemberName = method.Identifier.Text,
            MemberType = "Method",
            Content = content,
            Signature = signature,
            Documentation = documentation,
            StartLine = startLine,
            EndLine = endLine
        };
    }

    private CodeChunk CreatePropertyChunk(string filePath, string namespaceName, string className,
        PropertyDeclarationSyntax property, string classDocumentation, string[] lines)
    {
        var documentation = GetDocumentation(property);
        var signature = property.ToString();
        var startLine = property.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = property.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var content = BuildContent(new[] { classDocumentation, documentation, signature });

        return new CodeChunk
        {
            FilePath = filePath,
            NamespaceName = namespaceName,
            ClassName = className,
            MemberName = property.Identifier.Text,
            MemberType = "Property",
            Content = content,
            Signature = signature,
            Documentation = documentation,
            StartLine = startLine,
            EndLine = endLine
        };
    }

    private CodeChunk CreateConstructorChunk(string filePath, string namespaceName, string className,
        ConstructorDeclarationSyntax ctor, string classDocumentation, string[] lines)
    {
        var documentation = GetDocumentation(ctor);
        var signature = ctor.ToString().Split('\n')[0];
        var startLine = ctor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = ctor.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var content = BuildContent(new[] { classDocumentation, documentation, signature, ctor.Body?.ToString() ?? ctor.ExpressionBody?.ToString() ?? "" });

        return new CodeChunk
        {
            FilePath = filePath,
            NamespaceName = namespaceName,
            ClassName = className,
            MemberName = ctor.Identifier.Text,
            MemberType = "Constructor",
            Content = content,
            Signature = signature,
            Documentation = documentation,
            StartLine = startLine,
            EndLine = endLine
        };
    }

    private CodeChunk CreateFieldChunk(string filePath, string namespaceName, string className,
        FieldDeclarationSyntax field, string classDocumentation, string[] lines)
    {
        var documentation = GetDocumentation(field);
        var signature = field.ToString();
        var startLine = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = field.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var content = BuildContent(new[] { classDocumentation, documentation, signature });

        var variable = field.Declaration.Variables.First();
        return new CodeChunk
        {
            FilePath = filePath,
            NamespaceName = namespaceName,
            ClassName = className,
            MemberName = variable.Identifier.Text,
            MemberType = "Field",
            Content = content,
            Signature = signature,
            Documentation = documentation,
            StartLine = startLine,
            EndLine = endLine
        };
    }

    private string GetDocumentation(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia();
        var xmlComment = trivia
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                       t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(t => t.ToString())
            .FirstOrDefault();

        return xmlComment ?? string.Empty;
    }

    private string BuildContent(IEnumerable<string?> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            sb.AppendLine(part);
        }
        return sb.ToString().Trim();
    }
}
