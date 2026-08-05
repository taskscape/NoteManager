#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/src/NoteManager.Desktop/NoteManager.Desktop.csproj"
CONFIGURATION="${NOTEMANAGER_CONFIGURATION:-Debug}"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Error: the .NET SDK is not installed or 'dotnet' is not on PATH." >&2
  exit 1
fi

echo "Building NoteManager ($CONFIGURATION)..."
dotnet build "$PROJECT" --configuration "$CONFIGURATION"

echo "Starting NoteManager..."
exec dotnet run \
  --project "$PROJECT" \
  --configuration "$CONFIGURATION" \
  --no-build \
  -- "$@"
