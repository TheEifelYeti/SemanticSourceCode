# CodeGraph-Inspirierte Features für SemanticSourceCode

## 1. Framework-Awareness (ASP.NET / C#)

### Erkennung
- `.csproj` mit `Microsoft.AspNetCore` oder `Microsoft.NET.Sdk.Web`
- Dateien mit `[ApiController]`, `[Route]`, `[HttpGet]` etc.
- `ControllerBase` oder `: Controller`
- `Program.cs` mit `WebApplication`

### Erweiterte Chunk-Daten
- **Controller**: Route-Prefix, HTTP-Methoden
- **Actions**: Route-Templates, Parameter-Binding
- **Services**: DI-Registrierung
- **Middleware**: Pipeline-Position

## 2. Call-Graph

### Datenbank-Erweiterung
```sql
CREATE TABLE IF NOT EXISTS CallEdges (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SourceChunkId TEXT NOT NULL,
    TargetChunkId TEXT NOT NULL,
    CallType TEXT NOT NULL, -- 'direct', 'interface', 'event', 'delegate'
    LineNumber INTEGER,
    FOREIGN KEY (SourceChunkId) REFERENCES CodeChunks(Id),
    FOREIGN KEY (TargetChunkId) REFERENCES CodeChunks(Id)
);

CREATE INDEX IF NOT EXISTS idx_caller ON CallEdges(SourceChunkId);
CREATE INDEX IF NOT EXISTS idx_callee ON CallEdges(TargetChunkId);
```

### Erkennung via Roslyn
- `InvocationExpression` → Methode aufrufen
- `ObjectCreationExpression` → Konstruktor aufrufen
- `MethodDeclaration` → Wer ruft mich auf?

## 3. AST-basiertes Chunking

### Verbesserungen
- **Granularität**: Nicht nur Klassen/Methoden, sondern auch:
  - Property-Gruppen (getter/setter)
  - Event-Handler-Gruppen
  - Interface-Implementierungen
  - Nested Types
- **Kontext-Erhaltung**: Parent-Klasse/Namespace immer im Chunk
- **Größen-Limit**: Max 1000 Zeichen, aber nicht mitten im Statement splitten

### Implementierung
Roslyn `CSharpSyntaxWalker` für:
- Aufrufe erkennen
- Größere Methoden in logische Blöcke teilen
- Wichtige Statements (return, throw, await) nicht trennen
