# Packaged DOC2MD CLI

These deployment files are copied from a framework-dependent Windows x64
Release publish of `C:\Projects\DOC2MD\src\DOC2MD.Cli\DOC2MD.Cli.csproj`.
The isolated publish was produced under
`C:\Projects\DOC2MD\artifacts\notemanager-plugin-cli`; package PDBs and x86
native files are intentionally excluded from the plugin assets.

`DOC2MD.Cli.dll` SHA-256:
`F65F775E2F7EB08A319C2229A213880CB84B5B58239ADBEB2A9313CA60FA315A`.

The plugin invokes `DOC2MD.Cli.exe convert-folder` recursively without
`--overwrite`, using local PDF processing and `eng+pol` OCR by default. The
vault-local plugin settings can override the CLI, MarkItDown, LibreOffice, and
Tesseract data paths. DOC2MD's Python/MarkItDown, LibreOffice, and Tesseract
trained-data prerequisites remain external dependencies as documented in
`C:\Projects\DOC2MD\readme.md`.
