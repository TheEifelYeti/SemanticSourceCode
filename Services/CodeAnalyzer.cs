using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SemanticSourceCode.Models;
using System.Text;

namespace SemanticSourceCode.Services;

public class CodeAnalyzer : ICodeAnalyzer
{
    /// <summary>
    /// Detected ASP.NET patterns in the current project.
    /// </summary>
    public bool IsAspNetProject { get; private set; }

    /// <summary>
    /// Cache for quick lookup: qualified name → chunk ID.
    /// </summary>
    private Dictionary<string, string> _qualifiedNameToChunkId = new();

    public async Task<List<CodeChunk>> AnalyzeFileAsync(string filePath)
    {
        var chunks = new List<CodeChunk>();
        var code = await File.ReadAllTextAsync(filePath);
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = await tree.GetRootAsync();
        var lines = code.Split('\n');

        // Detect ASP.NET patterns
        var isController = IsControllerFile(root);
        var isService = IsServiceFile(root);
        var isMiddleware = IsMiddlewareFile(root);
        
        if (isController || isService || isMiddleware)
        {
            IsAspNetProject = true;
        }

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

                // Detect class-level framework attributes
                var routePrefix = GetRoutePrefix(classDecl);

                // Analyze methods
                foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
                {
                    var chunk = CreateMethodChunk(filePath, namespaceName, className, method, classDocumentation, lines);
                    
                    // Apply framework metadata
                    chunk.IsController = isController;
                    chunk.IsService = isService;
                    chunk.IsMiddleware = isMiddleware;
                    chunk.RouteTemplate = GetRouteTemplate(method, routePrefix);
                    chunk.HttpMethods = GetHttpMethods(method);
                    
                    // Extract call targets (for call graph)
                    chunk.CallsTo = ExtractCallTargets(method);
                    
                    chunks.Add(chunk);
                }

                // Analyze properties
                foreach (var property in classDecl.Members.OfType<PropertyDeclarationSyntax>())
                {
                    var chunk = CreatePropertyChunk(filePath, namespaceName, className, property, classDocumentation, lines);
                    chunk.IsController = isController;
                    chunk.IsService = isService;
                    chunks.Add(chunk);
                }

                // Analyze constructors
                foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
                {
                    var chunk = CreateConstructorChunk(filePath, namespaceName, className, ctor, classDocumentation, lines);
                    chunk.IsService = isService;
                    chunk.CallsTo = ExtractCallTargets(ctor);
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

        // Build qualified name mapping for call graph resolution
        foreach (var chunk in chunks)
        {
            var qualifiedName = $"{chunk.NamespaceName}.{chunk.ClassName}.{chunk.MemberName}";
            _qualifiedNameToChunkId[qualifiedName] = chunk.Id;
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

        // Second pass: resolve call graph edges
        ResolveCallGraphEdges(allChunks);

        return allChunks;
    }

    /// <summary>
    /// Resolves call targets to actual chunk IDs using qualified names.
    /// </summary>
    private void ResolveCallGraphEdges(List<CodeChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            var resolvedCalls = new List<string>();
            foreach (var call in chunk.CallsTo)
            {
                // Try exact match first
                if (_qualifiedNameToChunkId.TryGetValue(call, out var targetId))
                {
                    resolvedCalls.Add(targetId);
                    continue;
                }

                // Try partial match (class name only)
                var classOnly = call.Split('.').LastOrDefault();
                if (!string.IsNullOrEmpty(classOnly))
                {
                    var matching = _qualifiedNameToChunkId.Keys
                        .Where(k => k.EndsWith($".{classOnly}") || k.Contains($".{classOnly}."))
                        .Select(k => _qualifiedNameToChunkId[k])
                        .FirstOrDefault();
                    
                    if (matching != null)
                    {
                        resolvedCalls.Add(matching);
                    }
                }
            }
            chunk.CallsTo = resolvedCalls;
        }
    }

    #region Framework Detection

    private bool IsControllerFile(SyntaxNode root)
    {
        var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        return classDecls.Any(c => 
            c.BaseList?.Types.Any(b => 
                b.ToString().Contains("Controller") || 
                b.ToString().Contains("ControllerBase")
            ) == true ||
            c.AttributeLists.SelectMany(a => a.Attributes).Any(a => 
                a.Name.ToString().Contains("ApiController") ||
                a.Name.ToString().Contains("Route")
            )
        );
    }

    private bool IsServiceFile(SyntaxNode root)
    {
        var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        return classDecls.Any(c => 
            c.Identifier.Text.EndsWith("Service") ||
            c.Identifier.Text.EndsWith("Repository") ||
            c.Identifier.Text.StartsWith("I") && c.Identifier.Text.Length > 1
        );
    }

    private bool IsMiddlewareFile(SyntaxNode root)
    {
        var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
        return classDecls.Any(c => 
            c.Identifier.Text.Contains("Middleware") ||
            c.BaseList?.Types.Any(b => b.ToString().Contains("IMiddleware")) == true
        );
    }

    private string? GetRoutePrefix(ClassDeclarationSyntax classDecl)
    {
        var routeAttr = classDecl.AttributeLists
            .SelectMany(a => a.Attributes)
            .FirstOrDefault(a => a.Name.ToString() == "Route");
        
        if (routeAttr?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal)
        {
            return literal.Token.ValueText;
        }
        return null;
    }

    private string? GetRouteTemplate(MethodDeclarationSyntax method, string? routePrefix)
    {
        var httpAttrs = method.AttributeLists
            .SelectMany(a => a.Attributes)
            .Where(a => a.Name.ToString().StartsWith("Http") || a.Name.ToString() == "Route");
        
        var templates = new List<string>();
        foreach (var attr in httpAttrs)
        {
            if (attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal)
            {
                var template = literal.Token.ValueText;
                if (!string.IsNullOrEmpty(routePrefix))
                {
                    template = routePrefix.TrimEnd('/') + "/" + template.TrimStart('/');
                }
                templates.Add(template);
            }
        }
        
        return templates.Count > 0 ? string.Join(", ", templates) : null;
    }

    private string? GetHttpMethods(MethodDeclarationSyntax method)
    {
        var httpAttrs = method.AttributeLists
            .SelectMany(a => a.Attributes)
            .Where(a => a.Name.ToString().StartsWith("Http"));
        
        var methods = httpAttrs.Select(a => a.Name.ToString().Replace("Http", "")).ToList();
        return methods.Count > 0 ? string.Join(", ", methods) : null;
    }

    #endregion

    #region Call Graph Extraction

    private List<string> ExtractCallTargets(MethodDeclarationSyntax method)
    {
        var targets = new List<string>();
        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
        
        foreach (var invocation in invocations)
        {
            var target = GetInvocationTarget(invocation);
            if (!string.IsNullOrEmpty(target))
            {
                targets.Add(target);
            }
        }
        
        // Also extract object creations
        var objectCreations = method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();
        foreach (var creation in objectCreations)
        {
            var typeName = creation.Type.ToString();
            if (!string.IsNullOrEmpty(typeName))
            {
                targets.Add(typeName);
            }
        }
        
        return targets.Distinct().ToList();
    }

    private List<string> ExtractCallTargets(ConstructorDeclarationSyntax ctor)
    {
        var targets = new List<string>();
        var invocations = ctor.DescendantNodes().OfType<InvocationExpressionSyntax>();
        
        foreach (var invocation in invocations)
        {
            var target = GetInvocationTarget(invocation);
            if (!string.IsNullOrEmpty(target))
            {
                targets.Add(target);
            }
        }
        
        return targets.Distinct().ToList();
    }

    private string GetInvocationTarget(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            // instance.Method() or Type.Method()
            return $"{memberAccess.Expression}.{memberAccess.Name}";
        }
        else if (invocation.Expression is IdentifierNameSyntax identifier)
        {
            // Method() - likely within same class
            return identifier.Identifier.Text;
        }
        
        return invocation.Expression.ToString();
    }

    #endregion

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
        var xmlTrivia = trivia
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || 
                         t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .FirstOrDefault();
        
        if (xmlTrivia.Token.Parent == null)
            return "";

        return xmlTrivia.ToString().Trim();
    }

    private string BuildContent(string[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(part.Trim());
            }
        }
        return sb.ToString().Trim();
    }
}
