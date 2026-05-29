# SemanticSourceCode

Ein C# Tool für semantische Code-Suche mit lokalem Ollama Embedding.

## Features

- 🔍 **Semantisches Chunking** - Analysiert C# Klassen, Methoden, Properties, Konstruktoren und Felder separat
- 🧠 **Lokale Embeddings** - Nutzt Ollama (z.B. nomic-embed-text) ohne Cloud-Abhängigkeit
- 💾 **SQLite Vektor-Datenbank** - Einfache, eingebettete Datenbank
- 🔎 **Semantische Suche** - Finde Code basierend auf Bedeutung, nicht nur Keywords

## Installation

### Voraussetzungen

- .NET 10.0 SDK
- Ollama (lokal installiert)

### .NET 10 installieren

```bash
# Installation via dotnet-install Script
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --install-dir ~/.dotnet

# PATH setzen
export PATH="$HOME/.dotnet:$PATH"

# Version prüfen
dotnet --version  # 10.0.x
```

### Ollama Einrichtung

```bash
# Installation (Linux/macOS)
curl -fsSL https://ollama.com/install.sh | sh

# Embedding Modell herunterladen
ollama pull nomic-embed-text
```

### Build

```bash
dotnet restore
dotnet build
dotnet test        # 26 Tests sollten durchlaufen
dotnet publish -c Release
```

## Verwendung

### 1. Indexieren

```bash
# Indexiere C# Dateien in einem Verzeichnis
./SemanticSourceCode --mode index --path ./src

# Beispiel mit absolutem Pfad
./SemanticSourceCode --mode index --path /home/user/projects/MyApp
```

### 2. Semantische Suche

```bash
# Starte interaktiven Suchmodus
./SemanticSourceCode --mode search
```

Beispiel-Suchanfragen:
- "Wie finde ich alle Dateien in einem Verzeichnis?"
- "Database connection handling"
- "Async HTTP client"
- "User authentication"

## Konfiguration

`appsettings.json` anpassen:

```json
{
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "nomic-embed-text"
  },
  "Database": {
    "Path": "codechunks.db"
  }
}
```

## Architektur

```
┌─────────────────────────────────────────────────────────────┐
│                       Program.cs                             │
│                     (CLI Interface)                          │
└─────────────────────────────────────────────────────────────┘
                              │
           ┌──────────────────┼──────────────────┐
           ▼                  ▼                  ▼
┌──────────────────┐ ┌──────────────────┐ ┌──────────────────┐
│   CodeAnalyzer   │ │OllamaEmbedding   │ │SqliteVssDatabase │
│   (Roslyn)       │ │   Service        │ │   (SQLite)       │
│                  │ │                  │ │                  │
│ - Parse C#       │ │ - HTTP Client    │ │ - Store Chunks   │
│ - Extract methods│ │ - Generate       │ │ - Cosine Search  │
│ - Extract props  │ │   Embeddings     │ │ - Top-K Results  │
└──────────────────┘ └──────────────────┘ └──────────────────┘
```

## Technische Details

### Chunking Strategie

Jede C# Klasse wird in separate Chunks aufgeteilt:

- **Methods** - Mit Signature, Body und XML-Dokumentation
- **Properties** - Inklusive Getter/Setter Logik
- **Constructors** - Separate Initialisierungslogik
- **Fields** - Mit Typ und Initialisierung

### Embedding

- Modell: `nomic-embed-text` (768 Dimensionen)
- Alternative: `mxbai-embed-large`, `all-minilm`
- Kontextlimit: 8192 Token pro Chunk

### Vektor-Suche

Implementierte Cosine Similarity:

```csharp
similarity = (A · B) / (||A|| × ||B||)
```

## Troubleshooting

### Ollama nicht erreichbar

```bash
# Prüfe ob Ollama läuft
curl http://localhost:11434/api/tags

# Starte Ollama
ollama serve
```

### Keine Ergebnisse bei Suche

1. Stelle sicher, dass Indexierung erfolgreich war
2. Prüfe `codechunks.db` Dateigröße
3. Verwende präzisere Suchbegriffe

### Langsame Indexierung

- Ollama ist CPU-basiert → langsam auf Raspberry Pi
- Nächster Chunk erst nach Embedding des vorherigen
- Batch-Größe: 1 (sequentiell)

## Lizenz

MIT
