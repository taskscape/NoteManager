#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./installer/build-macos.sh <version> [runtime]

Examples:
  ./installer/build-macos.sh 0.4.0
  ./installer/build-macos.sh 0.4.0 osx-x64

Defaults:
  runtime  osx-arm64 on Apple Silicon, osx-x64 on Intel

Environment:
  NOTEMANAGER_CODESIGN_IDENTITY
      Developer ID Application identity. If unset, the app is signed ad hoc
      and the resulting DMG is suitable only for internal testing.

  NOTEMANAGER_NOTARY_PROFILE
      Keychain profile created by `xcrun notarytool store-credentials`.
      Required whenever NOTEMANAGER_CODESIGN_IDENTITY is set.
EOF
}

if [[ $# -lt 1 || $# -gt 2 ]]; then
  usage >&2
  exit 2
fi

version="$1"
runtime="${2:-}"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
  echo "Version must contain three or four numeric components, for example 1.2.0." >&2
  exit 2
fi

if [[ -z "$runtime" ]]; then
  case "$(uname -m)" in
    arm64) runtime="osx-arm64" ;;
    x86_64) runtime="osx-x64" ;;
    *)
      echo "Cannot choose a macOS runtime for architecture '$(uname -m)'." >&2
      exit 2
      ;;
  esac
fi

case "$runtime" in
  osx-arm64|osx-x64) ;;
  *)
    echo "Unsupported runtime '$runtime'. Use osx-arm64 or osx-x64." >&2
    exit 2
    ;;
esac

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This script must run on macOS." >&2
  exit 1
fi

for command_name in dotnet codesign ditto hdiutil shasum; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "Required command is not available: $command_name" >&2
    exit 1
  fi
done

codesign_identity="${NOTEMANAGER_CODESIGN_IDENTITY:--}"
notary_profile="${NOTEMANAGER_NOTARY_PROFILE:-}"
public_distribution=false

if [[ "$codesign_identity" != "-" ]]; then
  public_distribution=true
  if [[ -z "$notary_profile" ]]; then
    echo "NOTEMANAGER_NOTARY_PROFILE is required for Developer ID builds." >&2
    echo "This prevents producing a public release that has not been notarized." >&2
    exit 2
  fi
  for command_name in xcrun spctl; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
      echo "Required command is not available: $command_name" >&2
      exit 1
    fi
  done
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
project="$repo_root/src/NoteManager.Desktop/NoteManager.Desktop.csproj"
plist_template="$script_dir/macos/Info.plist"
entitlements_file="$script_dir/macos/entitlements.plist"
output_dir="$script_dir/Output"
artifact_name="NoteManager-$version-$runtime"
final_dmg="$output_dir/$artifact_name.dmg"
final_checksum="$output_dir/$artifact_name.dmg.sha256"

staging_dir="$(mktemp -d "${TMPDIR:-/tmp}/notemanager-macos.XXXXXX")"
mounted=false
mount_dir="$staging_dir/mount"

cleanup() {
  if [[ "$mounted" == true ]]; then
    hdiutil detach "$mount_dir" -quiet || true
  fi
  rm -rf "$staging_dir"
}
trap cleanup EXIT

publish_dir="$staging_dir/publish"
app_dir="$staging_dir/NoteManager.app"
dmg_root="$staging_dir/dmg-root"
staged_dmg="$staging_dir/$artifact_name.dmg"
staged_checksum="$staging_dir/$artifact_name.dmg.sha256"

version_parts=(${version//./ })
while [[ ${#version_parts[@]} -lt 4 ]]; do
  version_parts+=("0")
done
version_info="${version_parts[0]}.${version_parts[1]}.${version_parts[2]}.${version_parts[3]}"

echo "Publishing NoteManager $version for $runtime..."
dotnet publish "$project" \
  --nologo \
  -c Release \
  -r "$runtime" \
  --self-contained true \
  -o "$publish_dir" \
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

if [[ ! -x "$publish_dir/NoteManager" ]]; then
  echo "Publish did not produce the NoteManager executable." >&2
  exit 1
fi

mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
ditto "$publish_dir" "$app_dir/Contents/MacOS"
sed "s/__VERSION__/$version/g" \
  "$plist_template" > "$app_dir/Contents/Info.plist"

sign_code() {
  local code_path="$1"
  if [[ "$public_distribution" == true ]]; then
    codesign --force --options runtime --timestamp \
      --sign "$codesign_identity" "$code_path"
  else
    codesign --force --options runtime --sign - "$code_path"
  fi
}

sign_app_bundle() {
  if [[ "$public_distribution" == true ]]; then
    codesign --force --options runtime --timestamp \
      --entitlements "$entitlements_file" \
      --sign "$codesign_identity" "$app_dir"
  else
    codesign --force --options runtime \
      --entitlements "$entitlements_file" \
      --sign - "$app_dir"
  fi
}

echo "Signing nested .NET application content from the inside out..."
while IFS= read -r -d '' code_path; do
  [[ "$code_path" == "$app_dir/Contents/MacOS/NoteManager" ]] && continue
  sign_code "$code_path"
done < <(find "$app_dir/Contents/MacOS" -type f -print0)

sign_app_bundle
codesign --verify --deep --strict --verbose=2 "$app_dir"

mkdir -p "$dmg_root"
ditto "$app_dir" "$dmg_root/NoteManager.app"
ln -s /Applications "$dmg_root/Applications"

echo "Creating $artifact_name.dmg..."
hdiutil create \
  -srcfolder "$dmg_root" \
  -volname "NoteManager $version" \
  -format UDZO \
  -ov \
  "$staged_dmg"

if [[ "$public_distribution" == true ]]; then
  codesign --force --timestamp --sign "$codesign_identity" "$staged_dmg"

  echo "Submitting the disk image for notarization..."
  xcrun notarytool submit "$staged_dmg" \
    --keychain-profile "$notary_profile" \
    --wait
  xcrun stapler staple "$staged_dmg"
  xcrun stapler validate "$staged_dmg"
else
  codesign --force --sign - "$staged_dmg"
  echo "Warning: created an ad-hoc-signed internal test package." >&2
  echo "Gatekeeper-friendly public distribution requires Developer ID signing and notarization." >&2
fi

hdiutil verify "$staged_dmg"
codesign --verify --verbose=2 "$staged_dmg"

mkdir -p "$mount_dir"
hdiutil attach -readonly -nobrowse -mountpoint "$mount_dir" "$staged_dmg" -quiet
mounted=true
codesign --verify --deep --strict --verbose=2 "$mount_dir/NoteManager.app"
if [[ ! -L "$mount_dir/Applications" || "$(readlink "$mount_dir/Applications")" != "/Applications" ]]; then
  echo "The disk image does not contain a valid Applications shortcut." >&2
  exit 1
fi

if [[ "$public_distribution" == true ]]; then
  spctl --assess --type execute --verbose=4 "$mount_dir/NoteManager.app"
  spctl --assess --type open --context context:primary-signature \
    --verbose=4 "$staged_dmg"
  if command -v syspolicy_check >/dev/null 2>&1; then
    syspolicy_check distribution "$mount_dir/NoteManager.app"
  fi
fi

hdiutil detach "$mount_dir" -quiet
mounted=false

(
  cd "$staging_dir"
  shasum -a 256 "$(basename "$staged_dmg")" > "$(basename "$staged_checksum")"
)

mkdir -p "$output_dir"
mv -f "$staged_dmg" "$final_dmg"
mv -f "$staged_checksum" "$final_checksum"

echo
echo "macOS package created successfully:"
du -h "$final_dmg" "$final_checksum"
if [[ "$public_distribution" == false ]]; then
  echo "Distribution status: internal testing only (ad-hoc signature, not notarized)"
else
  echo "Distribution status: Developer ID signed and notarized"
fi
