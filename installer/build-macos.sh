#!/usr/bin/env bash
set -euo pipefail

version="${1:-1.0.0}"
runtime="${2:-osx-arm64}"

case "$runtime" in
  osx-arm64|osx-x64) ;;
  *)
    echo "Unsupported runtime '$runtime'. Use osx-arm64 or osx-x64." >&2
    exit 2
    ;;
esac

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
project="$repo_root/src/NoteManager.Desktop/NoteManager.Desktop.csproj"
publish_dir="$script_dir/publish-macos/$runtime"
app_dir="$script_dir/Output/NoteManager-$version-$runtime.app"
contents_dir="$app_dir/Contents"

mkdir -p "$publish_dir" "$script_dir/Output"

dotnet publish "$project" \
  --nologo \
  -c Release \
  -r "$runtime" \
  --self-contained true \
  -o "$publish_dir" \
  -p:Version="$version" \
  -p:UseAppHost=true \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -p:DebugSymbols=false \
  -p:DebugType=None

if [[ ! -x "$publish_dir/NoteManager" ]]; then
  echo "Publish did not produce the NoteManager executable." >&2
  exit 1
fi

if [[ -e "$app_dir" ]]; then
  echo "Output already exists: $app_dir" >&2
  echo "Move or remove it before rebuilding." >&2
  exit 1
fi

mkdir -p "$contents_dir/MacOS" "$contents_dir/Resources"
cp -R "$publish_dir/." "$contents_dir/MacOS/"
sed "s/__VERSION__/$version/g" \
  "$script_dir/macos/Info.plist" > "$contents_dir/Info.plist"

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign \
    "${NOTEMANAGER_CODESIGN_IDENTITY:--}" "$app_dir"
fi

echo "Application bundle created: $app_dir"
