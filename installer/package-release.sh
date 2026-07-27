#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./installer/package-release.sh <version> [mac-runtime] [windows-runtime]

Examples:
  ./installer/package-release.sh 1.2.0
  ./installer/package-release.sh 1.2.0 osx-x64 win-arm64

Defaults:
  mac-runtime      osx-arm64
  windows-runtime  win-x64
EOF
}

if [[ $# -lt 1 || $# -gt 3 ]]; then
  usage >&2
  exit 2
fi

version="$1"
mac_runtime="${2:-osx-arm64}"
windows_runtime="${3:-win-x64}"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
  echo "Version must contain three or four numeric components, for example 1.2.0." >&2
  exit 2
fi

case "$mac_runtime" in
  osx-arm64|osx-x64) ;;
  *)
    echo "Unsupported macOS runtime '$mac_runtime'. Use osx-arm64 or osx-x64." >&2
    exit 2
    ;;
esac

case "$windows_runtime" in
  win-x64|win-arm64) ;;
  *)
    echo "Unsupported Windows runtime '$windows_runtime'. Use win-x64 or win-arm64." >&2
    exit 2
    ;;
esac

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This script must run on macOS so it can create and sign the application bundle." >&2
  exit 1
fi

for command_name in dotnet ditto codesign shasum; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command is not available: $command_name" >&2
    exit 1
  fi
done

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
project="$repo_root/src/NoteManager.Desktop/NoteManager.Desktop.csproj"
solution="$repo_root/NoteManager.sln"
plist_template="$script_dir/macos/Info.plist"
output_dir="$script_dir/Output"

mac_archive="$output_dir/NoteManager-$version-$mac_runtime.zip"
windows_archive="$output_dir/NoteManager-$version-$windows_runtime.zip"
checksum_file="$output_dir/NoteManager-$version-SHA256SUMS.txt"

for output_path in "$mac_archive" "$windows_archive" "$checksum_file"; do
  if [[ -e "$output_path" ]]; then
    echo "Output already exists: $output_path" >&2
    echo "Use a new release version or move the existing artifact before packaging." >&2
    exit 1
  fi
done

staging_dir="$(mktemp -d "${TMPDIR:-/tmp}/notemanager-release.XXXXXX")"
cleanup() {
  rm -rf "$staging_dir"
}
trap cleanup EXIT

mac_publish_dir="$staging_dir/publish-$mac_runtime"
windows_publish_dir="$staging_dir/publish-$windows_runtime"
mac_app="$staging_dir/NoteManager-$version-$mac_runtime.app"
windows_folder="$staging_dir/NoteManager-$version-$windows_runtime"

version_parts=(${version//./ })
while [[ ${#version_parts[@]} -lt 4 ]]; do
  version_parts+=("0")
done
version_info="${version_parts[0]}.${version_parts[1]}.${version_parts[2]}.${version_parts[3]}"

publish_application() {
  local runtime="$1"
  local destination="$2"

  dotnet publish "$project" \
    --nologo \
    -c Release \
    -r "$runtime" \
    --self-contained true \
    -o "$destination" \
    -p:Version="$version" \
    -p:FileVersion="$version_info" \
    -p:AssemblyVersion="$version_info" \
    -p:UseAppHost=true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -p:DebugSymbols=false \
    -p:DebugType=None \
    -p:ContinuousIntegrationBuild=true \
    -p:Deterministic=true
}

echo "Running release tests..."
dotnet test "$solution" -c Release --nologo

echo
echo "Publishing NoteManager for $mac_runtime..."
publish_application "$mac_runtime" "$mac_publish_dir"

if [[ ! -x "$mac_publish_dir/NoteManager" ]]; then
  echo "The macOS publish did not produce an executable NoteManager app host." >&2
  exit 1
fi

mkdir -p "$mac_app/Contents/MacOS" "$mac_app/Contents/Resources"
cp -R "$mac_publish_dir/." "$mac_app/Contents/MacOS/"
sed "s/__VERSION__/$version/g" \
  "$plist_template" > "$mac_app/Contents/Info.plist"

codesign_identity="${NOTEMANAGER_CODESIGN_IDENTITY:--}"
codesign --force --deep --sign "$codesign_identity" "$mac_app"

echo
echo "Publishing NoteManager for $windows_runtime..."
publish_application "$windows_runtime" "$windows_publish_dir"

if [[ ! -f "$windows_publish_dir/NoteManager.exe" ]]; then
  echo "The Windows publish did not produce NoteManager.exe." >&2
  exit 1
fi

mkdir -p "$windows_folder"
cp -R "$windows_publish_dir/." "$windows_folder/"
mkdir -p "$output_dir"

echo
echo "Creating team-sharing archives..."
ditto -c -k --sequesterRsrc --keepParent "$mac_app" "$mac_archive"
ditto -c -k --norsrc --keepParent "$windows_folder" "$windows_archive"

(
  cd "$output_dir"
  shasum -a 256 \
    "$(basename "$mac_archive")" \
    "$(basename "$windows_archive")" > "$(basename "$checksum_file")"
)

echo
echo "Release package created successfully:"
du -h "$mac_archive" "$windows_archive" "$checksum_file"
