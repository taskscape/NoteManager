# Avalonia PDF spike

This spike validates the highest-risk part of migrating NoteManager from WPF
to Avalonia: displaying local PDF files in interactive native web views on
macOS.

## Configuration

- .NET SDK 8.0.423
- Avalonia 12.0.0
- `Avalonia.Controls.WebView` 12.0.0
- Test document: `SampleNotes/documents/orbital-guide.pdf`

## Verified on macOS

- The Avalonia desktop application builds without warnings or errors.
- A local `file://` PDF loads and renders through the macOS WKWebView adapter.
- Two independent `NativeWebView` instances can display PDFs simultaneously.
- Both native views clip and move correctly inside an Avalonia `ScrollViewer`.
- PDF text is exposed through the macOS accessibility tree.
- Reloading the local PDF succeeds.
- `ShowPrintUI()` opens the native macOS print dialog with a correct preview.
- The native PDF surface provides zoom, page/sidebar, and download controls.

## Important limitation

Command-F did not expose a PDF search interface in the WKWebView PDF surface.
The Windows application currently promises Edge PDF toolbar behavior including
search and outline features. Exact cross-platform toolbar parity therefore
requires either:

1. embedding PDF.js in `NativeWebView`, or
2. accepting platform-specific native PDF controls.

The spike confirms that Avalonia itself, local PDF access, multiple embedded
viewers, scrolling, printing, and accessibility are viable on macOS. It does
not establish complete feature parity with the existing Edge WebView2 toolbar.

## Build

```sh
dotnet restore spikes/AvaloniaPdfSpike/AvaloniaPdfSpike.csproj
dotnet run --project spikes/AvaloniaPdfSpike/AvaloniaPdfSpike.csproj
```
