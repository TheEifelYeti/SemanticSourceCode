#!/usr/bin/env bash
# Measures `dotnet run --mode index --path X` wall-clock time.
# Logs go to /tmp/benchmark-<label>.log, summary printed to stdout.
#
# Usage:
#   ./scripts/benchmark-index.sh                          # default: ./Services, label=run
#   ./scripts/benchmark-index.sh ./Services before        # explicit path + label
#   ./scripts/benchmark-index.sh ./SomeOtherPath test1
#
# Pre-requisites:
#   - .NET 10 SDK on PATH (export PATH="$HOME/.dotnet:$PATH")
#   - DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 set in env or auto-exported here
#   - Ollama (or LMStudio) reachable for embeddings; or set Embedding:Provider=fake in appsettings
#
# What it does:
#   1. Deletes codechunks.db* files in the project root.
#   2. Runs `dotnet run --mode index --path <path>` and times the wall clock.
#   3. Greps the log for chunk count + total time info.
#   4. Prints "[label] X.XX seconds (N chunks)".

set -euo pipefail

# Default dotnet env (matches workspace conventions).
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-1}"

# Resolve repo root (the directory that contains SemanticSourceCode.csproj).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

PATH_ARG="${1:-./Services}"
LABEL="${2:-run}"
LOG_FILE="/tmp/benchmark-${LABEL}.log"

echo "=== benchmark-index: label=$LABEL path=$PATH_ARG ==="

# Clean DB so we measure cold indexing, not incremental.
rm -f codechunks.db codechunks.db-wal codechunks.db-shm

START=$(date +%s.%N)
dotnet run --project SemanticSourceCode.csproj -c Release \
    --mode index --path "$PATH_ARG" \
    > "$LOG_FILE" 2>&1
RC=$?
END=$(date +%s.%N)

if [ $RC -ne 0 ]; then
    echo "❌ Indexing failed (exit $RC). Last 30 lines of log:"
    tail -30 "$LOG_FILE"
    exit $RC
fi

ELAPSED=$(awk -v s="$START" -v e="$END" 'BEGIN{printf "%.2f", e-s}')
CHUNKS=$(grep -oP 'Found \K[0-9]+ code chunks' "$LOG_FILE" | head -1 | grep -oP '^[0-9]+' || echo "?")
INDEXED=$(grep -oP 'Successfully indexed \K[0-9]+' "$LOG_FILE" | head -1 || echo "?")
EMBED_INFO=$(grep -oP 'Generating embeddings for \K[0-9]+' "$LOG_FILE" | head -1 || echo "?")

printf "[%-12s] %7.2fs  (chunks=%s indexed=%s embedding-batch-input=%s)\n" \
    "$LABEL" "$ELAPSED" "$CHUNKS" "$INDEXED" "$EMBED_INFO"
echo "  log: $LOG_FILE"