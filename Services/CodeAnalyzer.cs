using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using SemanticSourceCode.Models;
using System.Security.Cryptography;
using System.Text;

namespace SemanticSourceCode.Services;

public class CodeAnalyzer : ICodeAnalyzer
{
    private readonly ILogger<CodeAnalyzer> _logger;
    private readonly ChunkingOptions _chunkingOptions;

    public CodeAnalyzer(ILogger<CodeAnalyzer> logger, ChunkingOptions? chunkingOptions = null)
    {
        _logger = logger;
        _chunkingOptions = chunkingOptions ?? new ChunkingOptions();
    }
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
                    var chunk = CreateMethodChunk(filePath, namespaceName, className, method, classDocumentation, lines, isController, isService, isMiddleware);
                    
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
                    var chunk = CreatePropertyChunk(filePath, namespaceName, className, property, classDocumentation, lines, isController, isService, isMiddleware);
                    chunk.IsController = isController;
                    chunk.IsService = isService;
                    chunks.Add(chunk);
                }

                // Analyze constructors
                foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
                {
                    var chunk = CreateConstructorChunk(filePath, namespaceName, className, ctor, classDocumentation, lines, isController, isService, isMiddleware);
                    chunk.IsService = isService;
                    chunk.CallsTo = ExtractCallTargets(ctor);
                    chunks.Add(chunk);
                }

                // Analyze fields
                foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>())
                {
                    var chunk = CreateFieldChunk(filePath, namespaceName, className, field, classDocumentation, lines, isController, isService, isMiddleware);
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

        // Process pending method chunks (for split methods)
        var additionalChunks = new List<CodeChunk>();
        foreach (var (parentId, data) in _pendingMethodChunks)
        {
            var (method, bodyChunks, classDoc, signature, documentation,
                 isCtrl, isSvc, isMid, className, methodStartLine) = data;

            var chunker = new MethodChunker(_chunkingOptions);
            var parent = chunks.FirstOrDefault(c => c.Id == parentId);
            if (parent == null) continue;

            int currentLine = methodStartLine + signature.Split('\n').Length;

            for (int i = 1; i < bodyChunks.Count; i++)
            {
                var chunkBody = bodyChunks[i];
                var content = BuildContent(new[] { classDoc, documentation, signature, chunkBody });
                var boostedContent = BoostContent(content, className, method.Identifier.Text, isCtrl, isSvc, isMid);

                var chunkLines = chunkBody.Split('\n').Length;
                var subChunk = new CodeChunk
                {
                    FilePath = parent.FilePath,
                    NamespaceName = parent.NamespaceName,
                    ClassName = parent.ClassName,
                    MemberName = parent.MemberName,
                    MemberType = "Method",
                    Content = boostedContent,
                    Signature = signature,
                    Documentation = documentation,
                    StartLine = currentLine,
                    EndLine = currentLine + chunkLines,
                    ChunkIndex = i,
                    TotalChunks = bodyChunks.Count,
                    ParentChunkId = parentId
                };

                additionalChunks.Add(subChunk);
                currentLine += chunkLines;
            }
        }

        _pendingMethodChunks.Clear();
        chunks.AddRange(additionalChunks);

        // Finalize semantic IDs and content hashes for all chunks
        foreach (var chunk in chunks)
        {
            FinalizeChunkIdentity(chunk);
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
                _logger.LogError(ex, "Error analyzing {File}: {Message}", file, ex.Message);
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
        MethodDeclarationSyntax method, string classDocumentation, string[] lines, bool isController, bool isService, bool isMiddleware)
    {
        var documentation = GetDocumentation(method);
        var signature = method.ToString().Split('\n')[0];
        var startLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = method.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        // Get the body as raw source text (preserves newlines) instead of normalized ToString()
        var body = GetMethodBodyText(method, lines);

        // Check if the method body needs to be split
        var chunker = new MethodChunker(_chunkingOptions);
        if (!chunker.NeedsSplitting(body))
        {
            // Small method — single chunk as before
            var content = BuildContent(new[] { classDocumentation, documentation, signature, body });
            var boostedContent = BoostContent(content, className, method.Identifier.Text, isController, isService, isMiddleware);

            return new CodeChunk
            {
                FilePath = filePath,
                NamespaceName = namespaceName,
                ClassName = className,
                MemberName = method.Identifier.Text,
                MemberType = "Method",
                Content = boostedContent,
                Signature = signature,
                Documentation = documentation,
                StartLine = startLine,
                EndLine = endLine,
                ChunkIndex = 0,
                TotalChunks = 1
            };
        }

        // Large method — split into multiple chunks
        var bodyChunks = chunker.SplitMethodBodyByStatements(body);
        var firstChunk = bodyChunks[0];

        var firstContent = BuildContent(new[] { classDocumentation, documentation, signature, firstChunk });
        var firstBoostedContent = BoostContent(firstContent, className, method.Identifier.Text, isController, isService, isMiddleware);

        // Calculate line range for first chunk
        var firstChunkLines = firstChunk.Split('\n').Length;
        var firstEndLine = startLine + signature.Split('\n').Length + firstChunkLines;

        var mainChunk = new CodeChunk
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = filePath,
            NamespaceName = namespaceName,
            ClassName = className,
            MemberName = method.Identifier.Text,
            MemberType = "Method",
            Content = firstBoostedContent,
            Signature = signature,
            Documentation = documentation,
            StartLine = startLine,
            EndLine = firstEndLine,
            ChunkIndex = 0,
            TotalChunks = bodyChunks.Count
        };

        // Store the chunks to add after the main chunk is created
        _pendingMethodChunks[mainChunk.Id] = (method, bodyChunks, classDocumentation, signature, documentation,
            isController, isService, isMiddleware, className, startLine);

        return mainChunk;
    }

    /// <summary>
    /// Gets the method body as raw source text, preserving original line breaks and formatting.
    /// </summary>
    private string GetMethodBodyText(MethodDeclarationSyntax method, string[] lines)
    {
        if (method.Body == null)
        {
            return method.ExpressionBody?.ToString() ?? "";
        }

        var openBrace = method.Body.OpenBraceToken;
        var closeBrace = method.Body.CloseBraceToken;

        if (openBrace.IsKind(SyntaxKind.None) || closeBrace.IsKind(SyntaxKind.None))
        {
            return method.Body.ToString();
        }

        var startSpan = openBrace.GetLocation().GetLineSpan();
        var endSpan = closeBrace.GetLocation().GetLineSpan();
        var startLine = startSpan.StartLinePosition.Line;
        var endLine = endSpan.EndLinePosition.Line;

        if (startLine >= lines.Length)
        {
            return method.Body.ToString();
        }

        if (endLine >= lines.Length)
        {
            endLine = lines.Length - 1;
        }

        // Extract the body text from the source lines
        var sb = new StringBuilder();
        for (int i = startLine; i <= endLine; i++)
        {
            if (i > startLine) sb.AppendLine();
            sb.Append(lines[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pending method chunks that need to be created after the main method chunk.
    /// Key: parent chunk ID, Value: chunking data
    /// </summary>
    private Dictionary<string, (MethodDeclarationSyntax method, List<string> bodyChunks, string classDoc,
        string signature, string documentation, bool isCtrl, bool isSvc, bool isMid, string className, int startLine)>
        _pendingMethodChunks = new();

    private CodeChunk CreatePropertyChunk(string filePath, string namespaceName, string className,
        PropertyDeclarationSyntax property, string classDocumentation, string[] lines, bool isController, bool isService, bool isMiddleware)
    {
        var documentation = GetDocumentation(property);
        var signature = property.ToString();
        var startLine = property.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = property.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var content = BuildContent(new[] { classDocumentation, documentation, signature });

        // Boost für bessere Suchergebnisse
        var boostedContent = BoostContent(content, className, property.Identifier.Text, isController, isService, isMiddleware);

        return new CodeChunk
        {
            FilePath = filePath,
            NamespaceName = namespaceName,
            ClassName = className,
            MemberName = property.Identifier.Text,
            MemberType = "Property",
            Content = boostedContent,  // <-- Geboosteter Content
            Signature = signature,
            Documentation = documentation,
            StartLine = startLine,
            EndLine = endLine
        };
    }

    private CodeChunk CreateConstructorChunk(string filePath, string namespaceName, string className,
        ConstructorDeclarationSyntax ctor, string classDocumentation, string[] lines, bool isController, bool isService, bool isMiddleware)
    {
        var documentation = GetDocumentation(ctor);
        var signature = ctor.ToString().Split('\n')[0];
        var startLine = ctor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = ctor.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var content = BuildContent(new[] { classDocumentation, documentation, signature, ctor.Body?.ToString() ?? ctor.ExpressionBody?.ToString() ?? "" });

        // Boost für bessere Suchergebnisse
        var boostedContent = BoostContent(content, className, ctor.Identifier.Text, isController, isService, isMiddleware);

        return new CodeChunk
        {
            FilePath = filePath,
            NamespaceName = namespaceName,
            ClassName = className,
            MemberName = ctor.Identifier.Text,
            MemberType = "Constructor",
            Content = boostedContent,  // <-- Geboosteter Content
            Signature = signature,
            Documentation = documentation,
            StartLine = startLine,
            EndLine = endLine
        };
    }

    private CodeChunk CreateFieldChunk(string filePath, string namespaceName, string className,
        FieldDeclarationSyntax field, string classDocumentation, string[] lines, bool isController, bool isService, bool isMiddleware)
    {
        var documentation = GetDocumentation(field);
        var signature = field.ToString();
        var startLine = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = field.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var content = BuildContent(new[] { classDocumentation, documentation, signature });

        // Boost für bessere Suchergebnisse
        var variable = field.Declaration.Variables.First();
        var boostedContent = BoostContent(content, className, variable.Identifier.Text, isController, isService, isMiddleware);

        return new CodeChunk
        {
            FilePath = filePath,
            NamespaceName = namespaceName,
            ClassName = className,
            MemberName = variable.Identifier.Text,
            MemberType = "Field",
            Content = boostedContent,  // <-- Geboosteter Content
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

    /// <summary>
    /// Boostet den Content mit Schlüsselwörtern für bessere semantische Suche.
    /// </summary>
    private string BoostContent(string content, string className, string memberName, bool isController, bool isService, bool isMiddleware)
    {
        var sb = new StringBuilder(content);
        
        // Klassenname boosten (2x)
        sb.AppendLine($"[CLASS_BOOST] {className} {className}");
        
        // Membername boosten (3x)
        sb.AppendLine($"[MEMBER_BOOST] {memberName} {memberName} {memberName}");
        
        // Framework-Typen boosten
        if (isController) sb.AppendLine("[FRAMEWORK] controller api http");
        if (isService) sb.AppendLine("[FRAMEWORK] service business logic");
        if (isMiddleware) sb.AppendLine("[FRAMEWORK] middleware pipeline");
        
        return sb.ToString();
    }

    /// <summary>
    /// Computes a deterministic, semantic ID for a code chunk.
    /// The same member in the same file with the same chunk index and signature always gets the same ID.
    /// This enables stable UPSERT behaviour across re-indexing runs.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the source file.</param>
    /// <param name="namespaceName">Namespace containing the member.</param>
    /// <param name="className">Name of the declaring class.</param>
    /// <param name="memberName">Name of the member (method, property, etc.).</param>
    /// <param name="memberType">Type of the member (Method, Property, Constructor, etc.).</param>
    /// <param name="chunkIndex">Index of the chunk within a split method (0 for non-split).</param>
    /// <param name="signature">Full member signature (including parameter list) to disambiguate overloads.</param>
    /// <returns>A 64-character lowercase hex string.</returns>
    public static string ComputeSemanticId(
        string filePath, string namespaceName, string className,
        string memberName, string memberType, int chunkIndex,
        string signature)
    {
        var input = $"{filePath}|{namespaceName}|{className}|{memberName}|{memberType}|{chunkIndex}|{signature ?? string.Empty}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes a SHA256 hash of the chunk content for change detection.
    /// </summary>
    /// <param name="content">The chunk's code content.</param>
    /// <returns>A 64-character lowercase hex string.</returns>
    public static string ComputeContentHash(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Populates the semantic ID and content hash on a chunk.
    /// Idempotent — safe to call multiple times on the same chunk.
    /// </summary>
    public static void FinalizeChunkIdentity(CodeChunk chunk)
    {
        if (chunk == null) return;
        chunk.Id = ComputeSemanticId(
            chunk.FilePath, chunk.NamespaceName, chunk.ClassName,
            chunk.MemberName, chunk.MemberType, chunk.ChunkIndex,
            chunk.Signature);
        chunk.ContentHash = ComputeContentHash(chunk.Content);
    }
}
