# SemanticSourceCode

A C# tool for semantic code search with local embeddings. Search your codebase by meaning, not just keywords.

## Features

- 🔍 **Semantic Chunking** — Analyzes C# classes, methods, properties, constructors and fields separately
- 🧠 **Local Embeddings** — Uses Ollama or LM Studio locally, no cloud dependency, no data leakage
- 💾 **SQLite Vector Database** — Simple embedded database with cosine similarity search
- 🔎 **Semantic Search** — Find code based on meaning, not just keywords
- ⚡ **Multiple Providers** — Switch between Ollama and LM Studio via configuration

## Installation

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

### Build

```bash
dotnet restore
dotnet build
dotnet test        # All 26 tests should pass
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

Edit `appsettings.json` to switch providers:

### Use Ollama (default)

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

### Use LM Studio

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
| `Embedding` | `Provider` | `ollama` | Provider: `ollama` or `lmstudio` |
| `Ollama` | `BaseUrl` | `http://localhost:11434` | Ollama API endpoint |
| `Ollama` | `EmbeddingModel` | `nomic-embed-text` | Model name in Ollama |
| `LMStudio` | `BaseUrl` | `http://localhost:1234` | LM Studio API endpoint |
| `LMStudio` | `EmbeddingModel` | `text-embedding-nomic-embed-text-v1.5` | Model identifier for LM Studio |
| `Database` | `Path` | `codechunks.db` | SQLite database file path |
| `Chunking` | `MaxChunkSize` | `1000` | Maximum tokens per chunk |
| `Chunking` | `OverlapTokens` | `100` | Overlap between chunks |

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                       Program.cs                             │
│                     (CLI Interface)                          │
└─────────────────────────────────────────────────────────────┘
                              │
           ┌──────────────────┼──────────────────┐
           ▼                  ▼                  ▼
┌──────────────────┐ ┌──────────────────┐ ┌──────────────────┐
│   CodeAnalyzer   │ │ EmbeddingService │ │SqliteVssDatabase │
│   (Roslyn)       │ │   Factory        │ │   (SQLite)       │
│                  │ │                  │ │                  │
│ - Parse C#       │ │ - Ollama         │ │ - Store Chunks   │
│ - Extract methods│ │ - LM Studio      │ │ - Cosine Search  │
│ - Extract props  │ │ - OpenAI-compat. │ │ - Top-K Results  │
└──────────────────┘ └──────────────────┘ └──────────────────┘
```

## Technical Details

### Chunking Strategy

Each C# class is split into separate chunks:

- **Methods** — With signature, body and XML documentation
- **Properties** — Including getter/setter logic
- **Constructors** — Separate initialization logic
- **Fields** — With type and initialization

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

## License

MIT
