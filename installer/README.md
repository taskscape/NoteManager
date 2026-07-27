# NoteManager Installer

This folder publishes the .NET 8 WPF application and packages the complete
publish output as an Inno Setup installer.

## Prerequisites

- The .NET 8 SDK selected by the repository `global.json`.
- Inno Setup 6 or 7. The script searches `PATH` and the standard 32-bit and
  64-bit installation folders, or accepts an explicit `-IsccPath`.
- The Microsoft Edge WebView2 Evergreen Runtime on the target computer. It is
  normally present on Windows 11; it can be obtained from the
  [official WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/).

## Build

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
- disables trimming because WPF and WebView2 use runtime-discovered types;
- omits debug symbols and creates a deterministic release build.

The resulting installer is larger than a framework-dependent or trimmed build,
but the target computer does not need a separately installed .NET runtime.

## Install

Run the generated setup executable. It installs NoteManager for all users under
`Program Files`, adds a Start Menu shortcut, optionally adds a desktop shortcut,
and can launch the application when setup completes.

For unattended deployment:

```powershell
.\NoteManager-1.0.0-win-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```
