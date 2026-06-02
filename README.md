# SemanticSourceCode

A C# tool for semantic code search with local embeddings. Search your codebase by meaning, not just keywords.

![License](https://img.shields.io/badge/License-MIT-blue.svg)
![.NET Version](https://img.shields.io/badge/.NET-10.0-purple.svg)
![Tests](https://img.shields.io/badge/Tests-63%20passing-brightgreen.svg)
![Build](https://img.shields.io/badge/Build-passing-brightgreen.svg)

## Highlights

- 🔍 **Semantic Chunking** — Analyzes C# classes, methods, properties, constructors and fields separately
- 🧠 **Local Embeddings** — Uses Ollama or LM Studio locally, no cloud dependency, no data leakage
- 💾 **SQLite Vector Database** — Simple embedded database with cosine similarity search
- 🔎 **Semantic Search** — Find code based on meaning, not just keywords
- ⚡ **Multiple Providers** — Switch between Ollama and LM Studio via configuration
- 🚀 **Enhanced Search Quality** — Content boosting and query expansion for better results
- 🏷️ **Framework Detection** — Automatic detection of ASP.NET Controllers, Services and Middleware
- 📊 **Call Graph Analysis** — Track method calls and dependencies between code chunks

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

| Komponente | Verantwortung | Datei |
|------------|--------------|-------|
| CodeAnalyzer | Roslyn-basierte Code-Zerlegung | Services/CodeAnalyzer.cs |
| IEmbeddingService | Provider-Abstraktion | Services/IEmbeddingService.cs |
| EmbeddingServiceFactory | Auto-Detect Provider | Services/EmbeddingServiceFactory.cs |
| IVectorDatabase | Vektor-Storage mit Cosine Sim | Services/IVectorDatabase.cs |
| SqliteVssDatabase | SQLite + vec0 Implementation | Services/SqliteVssDatabase.cs |
| QueryExpander | Synonym-Erweiterung | Search/QueryExpander.cs |
| CodeChunk | Datenmodell | Models/CodeChunk.cs |

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
dotnet test        # All 63 tests should pass
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

### 2. Search

```bash
# Start interactive search mode
./SemanticSourceCode --mode search
```

Example queries:
- "How do I find all files in a directory?"
- "Database connection handling"
- "Async HTTP client"
- "User authentication"

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
