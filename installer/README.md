# NoteManager Installer

This folder packages the cross-platform .NET 10 Avalonia application for
Windows and macOS.

## Prerequisites

- The .NET 10 SDK selected by the repository `global.json`.
- macOS, including the built-in `ditto`, `codesign`, and `shasum` tools, to
  create the cross-platform team-sharing archives.
- Inno Setup 6 or 7. The script searches `PATH` and the standard 32-bit and
  64-bit installation folders, or accepts an explicit `-IsccPath`. Inno Setup
  is required only for the Windows setup executable.
- The platform web view used by Avalonia (WebView2 on Windows and WKWebView on
  macOS) for embedded PDF display.
- macOS 14 or newer for macOS builds, matching the supported .NET 10 runtime
  platforms.

## Package a release for team sharing

From the repository root on macOS, pass the release version:

```bash
./installer/package-release.sh 1.2.0
```

The script tests the solution, cross-publishes self-contained macOS ARM64 and
Windows x64 payloads, creates versioned ZIP archives, and writes a SHA-256
checksum manifest under `installer/Output`. Existing artifacts are never
overwritten.

Override the architecture defaults when needed:

```bash
./installer/package-release.sh 1.2.0 osx-x64 win-arm64
```

Verify the finished archives before sharing them:

```bash
cd installer/Output
shasum -a 256 -c NoteManager-1.2.0-SHA256SUMS.txt
```

The macOS archive is signed ad hoc by default. Set
`NOTEMANAGER_CODESIGN_IDENTITY` to the name of an installed Developer ID
Application certificate when a real signing identity is available. Public
distribution also requires notarization.

## Build platform installers

### Windows

From the repository root:

```powershell
.\installer\build-installer.ps1
```

To set a release version or target Windows on ARM:

```powershell
.\installer\build-installer.ps1 -Version 1.2.0
.\installer\build-installer.ps1 -Version 1.2.0 -Runtime win-arm64
```

An explicit compiler path can also be supplied:

```powershell
.\installer\build-installer.ps1 `
  -IsccPath "C:\Program Files\Inno Setup 7\ISCC.exe"
```

Generated files:

| Path | Purpose |
|------|---------|
| `installer\publish\` | Temporary self-contained application payload. |
| `installer\Output\NoteManager-<version>-<runtime>-Setup.exe` | Finished installer. |

Both generated folders are excluded from source control.

## Startup optimization profile

The build script favors cold startup over output size:

- publishes a self-contained, architecture-specific Release application;
- precompiles managed assemblies with ReadyToRun and composite ReadyToRun;
- disables tiered compilation so startup does not wait for later JIT tiers;
- keeps the application folder-based, avoiding single-file extraction at launch;
- disables trimming because Avalonia and native web views use
  runtime-discovered types;
- omits debug symbols and creates a deterministic release build.

The resulting installer is larger than a framework-dependent or trimmed build,
but the target computer does not need a separately installed .NET runtime.

### macOS

From macOS, build a self-contained application bundle for the current
architecture:

```bash
./installer/build-macos.sh 1.0.0 osx-arm64
```

Use `osx-x64` for Intel Macs. The application bundle is written to
`installer/Output/NoteManager-<version>-<runtime>.app`. Set
`NOTEMANAGER_CODESIGN_IDENTITY` to sign the bundle during the build; otherwise
an ad-hoc signature is applied when `codesign` is available.

## Install

Run the generated setup executable. It installs NoteManager for all users under
`Program Files`, adds a Start Menu shortcut, optionally adds a desktop shortcut,
and can launch the application when setup completes.

For unattended deployment:

```powershell
.\NoteManager-1.0.0-win-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```
