# NoteManager UI regression suite

The UI suite launches the compiled NoteManager executable and drives it through
Windows UI Automation. It uses FlaUI 5 with the UIA3 provider and NUnit 4.
Fixtures are serialized because desktop applications, native modal dialogs, and
the Windows clipboard are shared resources.

## Why FlaUI

FlaUI is a native .NET automation library that supports WPF through Microsoft
UI Automation. It can launch the real executable, locate controls by stable
automation identifiers, invoke buttons and list selections, inspect popup and
native windows, and capture diagnostics. UIA3 is used because it is the current
Windows automation provider and works with the WPF and WebView2 controls used by
NoteManager.

## Run

From an unlocked interactive Windows session:

```powershell
.\tests\Run-UiTests.ps1 -Configuration Debug
```

Run one fixture or scenario with a normal `dotnet test` filter:

```powershell
.\tests\Run-UiTests.ps1 `
  -Filter "FullyQualifiedName~TagAssignmentUiTests"
```

Run the service tests and UI suite together:

```powershell
.\tests\Run-AllTests.ps1 -Configuration Debug
```

The test process and NoteManager must run at the same Windows integrity level.
The suite deliberately avoids physical mouse input, so it does not depend on
screen coordinates or the foreground window. An interactive session is still
required for WPF, WebView2, native dialogs, and clipboard access.

## Disposable test vault

Every test receives its own guarded directory under the current user's
temporary folder. The standard dataset includes:

- root and nested Markdown notes;
- tagged, untagged, mixed-case, duplicate, and multiple `tags:` blocks;
- lowercase, dotted, dashed, Unicode, and recent-tag catalog values;
- title, path, tag, body-only, missing, and Unicode search terms;
- one and multiple Obsidian PDF transclusions;
- two valid synthetic PDF files;
- filename-collision inputs for external PDF import;
- a publishing note and attachment;
- enough generated notes to keep background indexing active during a folder
  switch.

The vault and its `.notes\search.db` index are deleted after the test, even when
an assertion fails. Path guards prevent teardown from deleting anything outside
the test-owned temporary root.

## Regression matrix

| Fixture | User-visible contract |
| --- | --- |
| `VaultNavigationUiTests` | Recursive loading, index creation, tag counts and filtering, untagged/all views, body-only and Unicode full-text search |
| `NoteLifecycleUiTests` | Immediate save on note/view/close boundaries, atomic-save cleanup, create, delete cancellation, and confirmed deletion |
| `FolderSwitchingUiTests` | Dialog-free folder injection, cancellation of an in-flight old index, new index/search results, and no picker |
| `TagAssignmentUiTests` | Recent 50/all tag catalogs, lower-case display, validation, multi-entry addition, removal, and one-block merge |
| `PublishingUiTests` | Public-link-only panel, multipart note/attachment upload to a loopback fake, status, and clipboard URL |
| `PdfAndDragDropUiTests` | Multiple initialized WebView2 PDF surfaces plus external-PDF copy, collision rename, Markdown embed, save, and viewer refresh |

The PDF import scenario uses the same opt-in current-user-only automation pipe
as folder switching. Its command is dispatched on the WPF UI thread and calls
the same `ImportPdfFilesAsync` path as a real `PreviewDrop`. This avoids
environment-dependent `SendInput` while proving the file-operation and UI
refresh contract.

## Failure evidence

Failed scenarios attach:

- `failure-screen.png`, a desktop capture when Windows permits capture;
- `automation-tree.txt`, the current UIA control tree;
- NUnit `.trx` results from the runner.

Artifacts are written below `artifacts\ui-tests`. A failure artifact directory
is named after the test and truncated to a filesystem-safe length.

## Adding a scenario

Derive the fixture from `UiTestBase`, add `[Apartment(ApartmentState.STA)]` and
`[Category("EndToEnd")]`, populate only the disposable `Vault`, then launch through
the base class. Prefer stable `AutomationProperties.AutomationId` values and
observable files/status over sleeps or screen coordinates. Use `UiWait` for
eventual background work and let teardown own every registered disposable
resource.
