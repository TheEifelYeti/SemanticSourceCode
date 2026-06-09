# SemanticSourceCode

A C# tool for semantic code search with local embeddings. Search your codebase by meaning, not just keywords.

![License](https://img.shields.io/badge/License-MIT-blue.svg)
![.NET Version](https://img.shields.io/badge/.NET-10.0-purple.svg)
![Tests](https://img.shields.io/badge/Tests-207%20passing-brightgreen.svg)
![Build](https://img.shields.io/badge/Build-passing-brightgreen.svg)

## Highlights

- 🔍 **Semantic Chunking** — Analyzes C# classes, methods, properties, constructors and fields separately
- 🧠 **Local Embeddings** — Uses Ollama or LM Studio locally, no cloud dependency, no data leakage
- 💾 **SQLite Vector Database** — Simple embedded database with cosine similarity search
- 🔎 **Semantic Search** — Find code based on meaning, not just keywords
- 👀 **Watch Mode** — Live incremental re-indexing on file changes (500 ms debounce, Ctrl+C to stop)
- 🔌 **MCP Server** — Expose the search as a Model Context Protocol tool.
- 📜 **Scriptable Search** — Non-interactive one-shot mode with `--query` for pipes, scripts and agentic use
- ⚡ **Multiple Providers** — Switch between Ollama and LM Studio via configuration
- 🚀 **Enhanced Search Quality** — Content boosting and query expansion for better results
- 🏷️ **Framework Detection** — Automatic detection of ASP.NET Controllers, Services and Middleware
- 📊 **Call Graph Analysis** — Track method calls and dependencies between code chunks

## Search Features

### Hybrid Search (Keyword + Semantic)

The search engine combines semantic similarity with keyword matching:

- **Semantic Score** — Cosine similarity of embeddings (weight: 0.7)
- **Keyword Score** — Matches in class names, member names, and content (weight: 0.3)
- **Combined** — `hybrid_score = 0.7 * semantic + 0.3 * keyword`

This ensures that exact keyword matches (e.g., `class DatabaseService`) are not overshadowed by semantically similar but structurally irrelevant results.

### Context Filters

Narrow down search results with structural filters:

```bash
# Only search in controllers
./SemanticSourceCode --mode search --namespace Api.Controllers --http-method GET

# Only search in specific class
./SemanticSourceCode --mode search --class DatabaseService

# File path pattern
./SemanticSourceCode --mode search --file-pattern "*/Controllers/*"
```

Available filters:
| Filter | CLI Flag | Description |
|--------|----------|-------------|
| Namespace | `--namespace` | Match namespace name (exact or partial) |
| Class | `--class` | Match class name |
| HTTP Method | `--http-method` | Match HTTP method (GET, POST, etc.) |
| File Pattern | `--file-pattern` | Match file path (glob pattern) |

### Query Suggestions ("Did you mean...?")

When no strong matches are found, the engine suggests alternative queries based on Levenshtein distance to known class and member names:

```
> DataBase
Do you mean: DatabaseService?
```

Suggestions are computed from the indexed codebase and require no external dependencies.

### Adaptive Threshold

The similarity threshold adjusts automatically based on:

- **Score Distribution** — Percentile-based analysis of result scores
- **Gap Detection** — Elbow method to find natural cutoffs
- **Query Specificity** — Shorter queries get lower thresholds (generic), longer queries get higher thresholds (specific)

Configure in `appsettings.json`:

```json
{
  "Search": {
    "AdaptiveThreshold": {
      "Enabled": true,
      "FloorThreshold": 0.30,
      "CeilingThreshold": 0.85,
      "Percentile": 70
    }
  }
}
```

### Re-Ranking with Structural Signals

Results are re-ranked using structural boosts:

| Signal | Boost | Description |
|--------|-------|-------------|
| ClassName Match | ×1.3 | Query matches class name |
| MemberName Match | ×1.0 | Query matches member name |
| Controller | ×1.1 | ASP.NET Controller detected |
| Service | ×1.1 | Service class detected |
| Middleware | ×1.1 | Middleware class detected |
| Documentation | ×1.05 | Has XML documentation |
| Small File | ×0.9 | Penalty for very small files (often helpers) |

### Configuration

All search features can be configured in `appsettings.json`:

```json
{
  "Search": {
    "MinimumSimilarity": 0.35,
    "TopK": 20,
    "DisplayCount": 5,
    "WeakMatchThreshold": 0.30,
    "Hybrid": {
      "SemanticWeight": 0.7,
      "KeywordWeight": 0.3
    },
    "AdaptiveThreshold": {
      "Enabled": true,
      "FloorThreshold": 0.30,
      "CeilingThreshold": 0.85,
      "Percentile": 70
    },
    "ReRanking": {
      "ClassNameBoost": 1.3,
      "MemberNameBoost": 1.0,
      "ControllerBoost": 1.1,
      "DocumentationBoost": 1.05
    }
  }
}
```

## Architecture

```
┌─────────────────┐      ┌──────────────────┐
│  C# Files       │ ───> │   CodeAnalyzer   │ (Roslyn)
└─────────────────┘      └────────┬─────────┘
                                 │ CodeChunks
                                 v
                        ┌──────────────────┐
                        │ EmbeddingProvider│ (Ollama/LM Studio)
                        └────────┬─────────┘
                                 │ float[]
                                 v
                        ┌──────────────────┐
                        │ SqliteVssDatabase│ (vec0)
                        └────────┬─────────┘
                                 │
                                 v
                        ┌──────────────────┐
                        │ SearchEngine     │ (Cosine Sim)
                        └──────────────────┘
```

| Components | Responsibility | File |
|------------|--------------|-------|
| CodeAnalyzer | Roslyn-based code decomposition | Services/CodeAnalyzer.cs |
| IEmbeddingService | Provider abstraction | Services/IEmbeddingService.cs |
| EmbeddingServiceFactory | Auto-detect provider | Services/EmbeddingServiceFactory.cs |
| IVectorDatabase | Vector storage with cosine similarity | Services/IVectorDatabase.cs |
| SqliteVssDatabase | SQLite + vec0 implementation | Services/SqliteVssDatabase.cs |
| HybridSearchService | Combines semantic + keyword search | Search/HybridSearchService.cs |
| ResultRanker | Re-ranking with structural signals | Search/ResultRanker.cs |
| QuerySuggester | Levenshtein-based suggestions | Search/QuerySuggester.cs |
| AdaptiveThreshold | Dynamic similarity threshold | Search/AdaptiveThreshold.cs |
| SearchFilter | Context filters (namespace, class, etc.) | Search/SearchFilter.cs |
| QueryExpander | Synonym expansion | Search/QueryExpander.cs |
| CodeChunk | Data model | Models/CodeChunk.cs |

## Getting Started

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Either [Ollama](https://ollama.com) or [LM Studio](https://lmstudio.ai) (locally installed)

### Install .NET 10

```bash
# Using the dotnet-install script
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --install-dir ~/.dotnet

# Add to PATH
export PATH="$HOME/.dotnet:$PATH"

# Verify version
dotnet --version  # Should print 10.0.x
```

For other installation methods (Windows, package managers), see the [official .NET 10 documentation](https://learn.microsoft.com/en-us/dotnet/core/install/).

### Setup Ollama (Option 1)

```bash
# Install Ollama (Linux/macOS)
curl -fsSL https://ollama.com/install.sh | sh

# Pull an embedding model
ollama pull nomic-embed-text
```

Default Ollama endpoint: `http://localhost:11434`

### Setup LM Studio (Option 2)

1. Download and install [LM Studio](https://lmstudio.ai) for your platform
2. Open LM Studio and go to the **Developer** tab
3. Start the local server (default port: 1234)
4. Load an embedding model, e.g.:
   - `nomic-ai/nomic-embed-text-v1.5`
   - `sentence-transformers/all-MiniLM-L6-v2`

Default LM Studio endpoint: `http://localhost:1234`

## Auto-Detect Mode

By default, the app uses **auto-detect** — you don't need to configure anything.

Just set:

```json
{
  "Embedding": {
    "Provider": "auto"
  }
}
```

The app will automatically:
1. **Check LM Studio first** (faster, local UI) — port 1234
2. **Fall back to Ollama** — port 11434
3. **Pick whichever is available** with a loaded embedding model

### Why Auto-Detect?

- **Zero-config out-of-the-box** — Install either LM Studio or Ollama, the app just works
- **Respects explicit choice** — Set `"ollama"` or `"lmstudio"` to pin a provider (fallback still works if that one is down)
- **Transparent logging** — The console tells you exactly which provider was chosen and why

### Fallback Behavior

| Config | First Try | Fallback |
|--------|-----------|----------|
| `auto` | LM Studio | Ollama |
| `lmstudio` | LM Studio | Ollama |
| `ollama` | Ollama | LM Studio |

If neither provider is reachable, you'll get a clear error with installation instructions for both.

### Build

```bash
dotnet restore
dotnet build
dotnet test        # All 109 tests should pass
dotnet publish -c Release
```

## Usage

### 1. Index

```bash
# Index C# files in a directory
./SemanticSourceCode --mode index --path ./src

# Example with absolute path
./SemanticSourceCode --mode index --path /home/user/projects/MyApp
```

### 2. Watch (live incremental indexing)

```bash
# Start watch mode on a directory
./SemanticSourceCode --mode watch --path ./src
```

Watch mode runs an initial full index, then keeps the process running and
re-indexes the affected file automatically whenever a `*.cs` file is
created, changed, deleted, or renamed. The index stays fresh within
~500 ms of an edit, so searches in another shell always see the latest code.

- **Debounce** — Multiple rapid saves to the same file are coalesced
  into a single re-index (default: 500 ms).
- **Excluded directories** — `bin/`, `obj/`, `.git/`, `.vs/`, `.idea/`,
  `node_modules/`, `dist/`, `build/` are ignored automatically.
- **Stop** — Press `Ctrl+C` to stop watching. The watcher exits cleanly,
  no leftover background tasks.

Example workflow:

```bash
# Terminal 1: start watching
./SemanticSourceCode --mode watch --path ./src

# Terminal 2: edit a file
vim ./src/Services/MyService.cs   # → re-indexes automatically

# Terminal 3: search while watching
./SemanticSourceCode --mode search
```

### 3. Search

**Interactive mode:**

```bash
# Start interactive search mode
./SemanticSourceCode --mode search
```

Example queries:
- "How do I find all files in a directory?"
- "Database connection handling"
- "Async HTTP client"
- "User authentication"

**Non-interactive (one-shot) mode:**

```bash
# Default (text format) — prints human-readable results, exits
./SemanticSourceCode --mode search --query "arithmetic calculation"

# JSON output — for piping into jq, scripts, or other tools
./SemanticSourceCode --mode search --query "arithmetic calculation" --format json

# Quiet output — only the top-1 result, one line
./SemanticSourceCode --mode search --query "Add" --quiet

# Short flags
./SemanticSourceCode --mode search -q "Add" -f json -l 2

# With structural filter
./SemanticSourceCode --mode search -q "Query" --namespace MyApp.Data
```

The one-shot mode is perfect for scripts and agentic use:

| Flag | Description |
|------|-------------|
| `--query, -q` | The search query (triggers non-interactive mode) |
| `--format, -f` | `text` (default), `json`, or `quiet` |
| `--limit, -l` | Max results to display |
| `--quiet` | Shorthand for `--format quiet` |
| `--namespace` | Filter to chunks in this namespace |
| `--class` | Filter to chunks in this class |
| `--http-method` | Filter to controller methods with this verb |
| `--file-pattern` | Filter to files matching this glob |

**Exit codes** (non-interactive only):
- `0` — at least one result found
- `1` — no results, validation error, or DB not initialized

### 4. MCP Server (for AI agents)

```bash
# Start the MCP server over stdio
./SemanticSourceCode --mode mcp
```

The server speaks **JSON-RPC 2.0** over **stdin/stdout** (MCP standard). It
exposes two tools that AI agents can
call directly:

| Tool | Description |
|------|-------------|
| `search_code` | Semantic search with optional `namespace`, `class`, `filePattern`, `limit` filters |
| `get_chunk_by_id` | Fetch a single indexed chunk by its semantic ID |

Status messages go to **stderr** so the JSON-RPC channel on **stdout**
stays clean for client parsing.

**Example:
project-local `.mcp.json`):**
```json
{
  "mcpServers": {
    "semantic-source-code": {
      "command": "SemanticSourceCode",
      "args": ["--mode", "mcp"]
    }
  }
}
```

After restarting the agent can call `search_code` and
`get_chunk_by_id` directly in its tool-using workflow.

## Configuration

Edit `appsettings.json` to switch providers. Use `"auto"` (default) for zero-config behavior, or explicitly pin a provider.

### Auto-Detect (default — recommended)

```json
{
  "Embedding": {
    "Provider": "auto"
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text"
  },
  "LMStudio": {
    "BaseUrl": "http://localhost:1234",
    "EmbeddingModel": "text-embedding-nomic-embed-text-v1.5"
  },
  "Database": {
    "Path": "codechunks.db"
  }
}
```

### Use Ollama (explicit)

```json
{
  "Embedding": {
    "Provider": "ollama"
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text"
  },
  "Database": {
    "Path": "codechunks.db"
  }
}
```

### Use LM Studio (explicit)

```json
{
  "Embedding": {
    "Provider": "lmstudio"
  },
  "LMStudio": {
    "BaseUrl": "http://localhost:1234",
    "EmbeddingModel": "text-embedding-nomic-embed-text-v1.5"
  },
  "Database": {
    "Path": "codechunks.db"
  }
}
```

### Configuration Options

| Section | Key | Default | Description |
|---------|-----|---------|-------------|
| `Embedding` | `Provider` | `auto` | Provider: `auto`, `ollama`, or `lmstudio` |
| `Ollama` | `BaseUrl` | `http://localhost:11434` | Ollama API endpoint |
| `Ollama` | `EmbeddingModel` | `nomic-embed-text` | Model name in Ollama |
| `LMStudio` | `BaseUrl` | `http://localhost:1234` | LM Studio API endpoint |
| `LMStudio` | `EmbeddingModel` | `text-embedding-nomic-embed-text-v1.5` | Model identifier for LM Studio |
| `Database` | `Path` | `codechunks.db` | SQLite database file path |
| `Chunking` | `MaxChunkSize` | `1000` | Maximum tokens per chunk |
| `Chunking` | `OverlapTokens` | `100` | Overlap between chunks |

### Query Expansion

Search queries are automatically expanded with synonyms and related terms. You can customize this in `appsettings.json`:

```json
{
  "QueryExpansion": {
    "db": "database,sql,entity framework",
    "http": "web,api,rest,endpoint",
    "async": "asynchronous,task,background"
  }
}
```

## Technical Details

### Chunking Strategy

Each C# class is split into separate chunks:

- **Methods** — With signature, body and XML documentation
- **Properties** — Including getter/setter logic
- **Constructors** — Separate initialization logic
- **Fields** — With type and initialization

### Search Quality Enhancements

To improve search quality, the tool implements several techniques:

#### Content Boosting

Each code chunk is enhanced with additional metadata to improve search relevance:

- **Class Name Boosting** — Class names are repeated to increase their weight
- **Member Name Boosting** — Member names are emphasized for better matching
- **Framework Metadata** — Framework-specific terms are added for ASP.NET components

#### Query Expansion

Search queries are automatically expanded with synonyms and related terms:

- `db` → `database`, `sql`, `entity framework`
- `http` → `web`, `api`, `rest`, `endpoint`
- `async` → `asynchronous`, `task`, `background`
- `sensor` → `ultrasonic`, `distance`, `color`, `gyro`
- `file` → `io`, `read`, `write`, `stream`

### Embedding Providers

#### Ollama
- Uses the Ollama HTTP API (`/api/embeddings`)
- Compatible with all Ollama embedding models
- Default: `nomic-embed-text` (768 dimensions)
- Alternatives: `mxbai-embed-large`, `all-minilm`

#### LM Studio
- Uses the OpenAI-compatible HTTP API (`/v1/embeddings`)
- Works with any model loaded in LM Studio
- Default: `text-embedding-nomic-embed-text-v1.5`
- Supports models from HuggingFace, GGUF, etc.

### Vector Search

Cosine similarity implementation:

```csharp
similarity = (A · B) / (||A|| × ||B||)
```

## Troubleshooting

### No embedding provider available

If you see:

```
No embedding provider available.
```

Make sure at least one of these is running:

**LM Studio:**
1. Open LM Studio and go to the **Developer** tab
2. Start the local server (toggle should be green)
3. Load an embedding model (e.g. `nomic-embed-text-v1.5`)
4. Verify: `curl http://localhost:1234/v1/models`

**Ollama:**
```bash
# Pull an embedding model
ollama pull nomic-embed-text

# Ensure Ollama is running
ollama serve

# Verify
curl http://localhost:11434/api/tags
```

The app is set to `auto` by default, so it will pick whichever is available.

### LM Studio has no models loaded

If you see:

```
LM Studio erreichbar, aber kein Modell geladen.
```

Go to the **Developer** tab in LM Studio, load an embedding model, and make sure the server is started.

### Ollama not reachable

```bash
# Check if Ollama is running
curl http://localhost:11434/api/tags

# Start Ollama
ollama serve
```

### LM Studio not reachable

1. Open LM Studio and go to the **Developer** tab
2. Ensure the server is started (toggle should be green)
3. Verify the port in `appsettings.json` matches the displayed port
4. Test with: `curl http://localhost:1234/v1/models`

### No search results

1. Make sure indexing completed successfully
2. Check `codechunks.db` file size (should be > 0 bytes)
3. Use more specific search terms
4. Verify your embedding provider is running and the model is loaded

### Slow indexing

- Embedding generation is CPU-intensive — expect slower performance on Raspberry Pi or low-power devices
- The tool processes chunks sequentially (batch size: 1)
- Consider using a machine with GPU support for faster embedding generation

## Contributing

We welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for details.

- Report bugs via [GitHub Issues](https://github.com/YOUR_USERNAME/SemanticSourceCode/issues)
- Request features via [GitHub Discussions](https://github.com/YOUR_USERNAME/SemanticSourceCode/discussions)
- Submit pull requests following our PR template

## License

MIT
