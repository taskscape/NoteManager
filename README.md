# NoteManager

NoteManager is a .NET 8 WPF desktop application inspired by the supplied three-pane notebook screenshot. It recreates the dense tag navigation, searchable note list, formatting toolbar, selected-note metadata, and a fully interactive in-app PDF preview.

## Run

From PowerShell in the repository root:

```powershell
dotnet run --project .\src\NoteManager.App\NoteManager.App.csproj
```

The repository pins the .NET 8 SDK through `global.json`. On startup, the application recursively loads the Obsidian vault at:

```text
C:\Projects\Obsidian
```

Use **File → Open folder…** or `Ctrl+O` to switch to another Markdown folder.

For a dialog-free automated launch, inject the startup folder:

```powershell
dotnet run --project .\src\NoteManager.App\NoteManager.App.csproj -- --folder .\SampleNotes
```

## Automated regression tests

The primary UI regression lane uses
[FlaUI 5](https://github.com/FlaUI/FlaUI) with the UIA3 provider and
[NUnit 4](https://nunit.org/). It launches the real WPF executable and verifies
the application through Windows UI Automation, native dialogs, the clipboard,
filesystem outcomes, a loopback Infostacker fake, and initialized WebView2 PDF
surfaces.

Run all service and UI tests from an unlocked interactive Windows session:

```powershell
.\tests\Run-AllTests.ps1 -Configuration Debug
```

Run only the serialized UI suite, or filter it to one fixture:

```powershell
.\tests\Run-UiTests.ps1 -Configuration Debug

.\tests\Run-UiTests.ps1 `
  -Filter "FullyQualifiedName~TagAssignmentUiTests"
```

Every UI test creates a separate guarded vault below the user's temporary
folder. The data covers recursive folders, tagged and untagged notes, multiple
tag blocks, recent/all tag catalogs, Unicode and body-only searches, multiple
PDF embeds, filename collisions, publishing attachments, and a large indexing
set. The vault and its `.notes` database are removed after the scenario.

The suite covers recursive loading and indexing; tag/search navigation;
create/delete and all automatic-save boundaries; folder switching during
background indexing; tag validation and block merging; public-link publishing
and clipboard output; multiple PDF viewers; and external-PDF copy, collision
rename, embed, save, and preview refresh. Failures attach a screen capture and
UI Automation tree below `artifacts\ui-tests`; runner `.trx` files are stored in
the same area.

See
[`tests\NoteManager.App.UiTests\README.md`](tests/NoteManager.App.UiTests/README.md)
for the complete regression matrix, test-vault design, runner requirements, and
guidance for adding scenarios.

## Build the Windows installer

Inno Setup 6 or 7 can package a self-contained, startup-optimized Release build:

```powershell
.\installer\build-installer.ps1
```

The finished artifact is written to:

```text
installer\Output\NoteManager-1.0.0-win-x64-Setup.exe
```

Pass `-Version 1.2.0` to version a release. The publish enables composite
ReadyToRun and disables tiered compilation, trimming, and single-file extraction
to favor predictable WPF startup. See
[`installer\README.md`](installer/README.md) for prerequisites, ARM64 builds, and
unattended installation.

## Included interactions

- Recursively load every `.md` file from the selected folder and all subfolders.
- Browse an alphabetized tag rail whose counts are calculated from the loaded Markdown files.
- Select a tag to filter the middle pane by exact tag membership.
- Use the virtual **All notes** and **Untagged** tags to show the complete vault or notes without tags.
- Type in **Search notes** (or press `Ctrl+F`) to search titles, file names, tags, paths, and complete Markdown contents.
- Select a note to display its original Markdown source as plain, selectable text.
- Edit Markdown directly; dirty notes are saved atomically when selecting another note or view, changing folders, publishing, or closing the application.
- Drag one or more PDF files onto a note row or the Markdown editor to insert Obsidian `![[...]]` embeds. PDFs dropped from outside the open folder are copied to its root and receive `(1)`, `(2)`, and later suffixes when names collide.
- View each Obsidian PDF transclusion in an interactive Edge PDF viewer below the Markdown source.
- Use the PDF viewer's page navigation, scrolling, zoom, search, text selection, outline, print, save, and full-screen controls, or click **Open PDF** to use the default desktop application.
- Click **Share** to open a public-link panel directly beneath the main toolbar button. Publishing sends the selected note and its embedded attachments to Infostacker, then copies the returned public URL to the clipboard.
- Click **Create** (or press `Ctrl+N`) to create and select an empty `Untitled note.md` in the selected folder's root. Numbered names are used when needed.
- Click **Delete**, then confirm the warning, to permanently remove the selected Markdown file from disk.
- Click **Tags** to assign or remove tags on the selected note. The dialog offers the 50 most recently used repository tags, a searchable list of every repository tag, and entry of several new tags at once.
- Click the attachment card to open the generated local PDF or DOCX sample.

Folder-backed notes and their tag metadata are editable. The only database is a local, disposable full-text index stored inside the selected vault.

## Full-text index

Opening a folder immediately loads the note and tag metadata, then updates a SQLite FTS5 index on worker threads so the interface remains usable. The index is stored at:

```text
<selected folder>\.notes\search.db
```

The update runs on every startup and whenever **File → Open folder…** is used. Unchanged files are retained, changed or new Markdown files are reindexed, and deleted files are removed. Progress appears beside the note count. Searches are debounced while typing and begin returning results from completed batches even while a large first-time index is still being built.

The database uses SQLite write-ahead logging so searches can read committed batches while indexing continues. Delete the selected folder's `.notes` directory at any time to force a complete rebuild; it contains no source notes and is excluded by the repository `.gitignore`.

## Planned background Git synchronization

The planned synchronization feature assumes Git for Windows is installed and
the selected folder is already the root of a configured repository with a
current branch, upstream remote, author identity, credentials, and trust
settings.

For a valid repository, NoteManager will run a non-overlapping background cycle
at a configurable interval (five minutes by default): save the active note,
pull remote changes, stage and commit every eligible local change, and push to
the configured upstream. The next interval begins only after the preceding
cycle's final Git command exits. A folder that is not Git-versioned remains
idle.

Folders whose names begin with a dot are excluded at every depth, and effective
root/nested `.gitignore` rules are respected. Git failures and conflicts stop
the cycle without automatic conflict resolution and are written to daily text
logs in the application directory, retained for one calendar month.

The command sequence, exclusion rules, configuration schema, concurrency
design, logging format, installer change, conflict behavior, acceptance
criteria, and comprehensive implementation checklist are specified in
[`synchronization.md`](synchronization.md).

## Dialog-free folder automation

UI tests can opt into a current-user-only named pipe and change the folder in the running application without invoking the native picker:

```powershell
dotnet run --project .\src\NoteManager.App\NoteManager.App.csproj -- `
  --folder .\SampleNotes `
  --automation-pipe NoteManager.UiTest

.\tools\Set-NoteManagerFolder.ps1 `
  -PipeName NoteManager.UiTest `
  -Path .\SampleNotes\projects
```

The pipe listener is disabled unless `--automation-pipe` is explicitly supplied. Both the injected startup path and runtime commands call the same `ChangeFolderAsync` path as the production folder picker.

The FlaUI suite also uses an `import-pdf|<path>` automation command. It is
available only when the opt-in pipe is enabled, is dispatched on the WPF UI
thread, and calls the same PDF import path as a real drop. This makes collision,
copy, embed, save, and viewer assertions deterministic without synthetic mouse
input.

Run the end-to-end smoke test with:

```powershell
.\tests\Test-FolderInjection.ps1 -Configuration Debug
```

The test starts the application with four notes, injects a change to the nested one-note folder, confirms that no folder dialog appeared, and verifies a body-only full-text query through native UI Automation.

The destructive toolbar flow has a separate disposable-vault test:

```powershell
.\tests\Test-ToolbarActions.ps1 -Configuration Debug
```

It verifies the Share popup position, creates a zero-byte Markdown file in a temporary vault root, proves that declining Delete preserves the file, accepts the second confirmation, verifies that the file was removed, and then removes the disposable vault.

## Infostacker public links

The Share panel does not display collaborators or an access list. It has one operation: **Publish and copy public link**.

Following the [`taskscape/InfostackerPlugin`](https://github.com/taskscape/InfostackerPlugin) implementation, NoteManager:

1. Reads the selected Markdown file and prefixes it with the filename without `.md`.
2. Resolves files referenced by Obsidian `![[...]]` embeds from the selected vault.
3. Sends a multipart `POST` to `https://shr.infostacker.com/sharing/uploadmarkdownwithfiles` using the `markdown` field and repeated `files` fields.
4. Reads the returned post `id`, constructs `https://shr.infostacker.com/sharing/{id}`, and copies it to the Windows clipboard.

The request is made only after the user presses the publish button. The note and combined attachments are checked against the plugin's 100 MB limit before upload. An unreadable attachment is skipped, matching the plugin behavior, while an unavailable service or rejected request is reported inside the Share panel.

The publishing contract and clipboard flow are tested against a local mock server—no real note data is uploaded:

```powershell
.\tests\Test-InfostackerPublishing.ps1 -Configuration Debug
```

The test-only `--infostacker-base-url` argument redirects the endpoint to that mock server.

## Automatic note saving

The Markdown editor updates the selected note model on every text change. A dirty note is written immediately before NoteManager changes the selected note, tag/view, or folder; it is also written before Infostacker publishing and during application shutdown.

Saves use a write-through temporary file in the note's own directory followed by an overwrite move, preventing an interrupted write from truncating the original. If a save fails, the requested navigation or shutdown is cancelled and the current editor remains open with its unsaved text.

The complete boundary behavior is exercised against a disposable vault:

```powershell
.\tests\Test-AutoSave.ps1 -Configuration Debug
```

## Markdown metadata and PDF embeds

Tags can appear anywhere in a Markdown file using a YAML-style block:

```yaml
tags:
  - szablon
  - szablon-poleceń
  - szablon-komend
  - tailscale
```

A note may contain any number of `tags:` blocks. NoteManager merges every block
into one tag list in encounter order and removes duplicate tag names
case-insensitively, so `shared` and `Shared` are treated as the same tag. Tag
names are always shown and saved in lowercase.

When the **Tags** dialog is accepted, all tag blocks in the selected note are
rewritten into the first block. If no block exists, one is appended after one
blank line. Clearing every selection removes all existing tag blocks. New tag
names may contain letters, numbers, dots, and dashes; spaces and other special
characters are not permitted. Separate several new names with spaces, commas,
or semicolons.

The native dialog and tag-block rewrite have a disposable-vault UI test:

```powershell
.\tests\Test-TagAssignment.ps1 -Configuration Debug
```

It verifies the recent/all repository lists, lowercase display, validation,
multi-tag entry, tag removal, immediate file saving, and one-block merge.

An inline Obsidian PDF transclusion uses:

```markdown
![[Documents/Report.pdf]]
```

Multiple transclusions are supported in one note. PDF targets may be absolute, relative to the note, relative to the vault root, or filename-only; filename-only links are resolved against the vault index. Viewers initialize as they approach the visible area so notes containing many PDFs remain responsive.

## Sample documents

The repository also includes a compact `SampleNotes` fixture. The original visual-demo mode creates valid lightweight PDF and DOCX files in:

```text
%LOCALAPPDATA%\NorthstarNoteManager\SampleDocuments
```

These files contain synthetic business, research, invoice, receipt, parcel, and planning content matching the document-heavy character of the reference UI.

## Project structure

```text
src/NoteManager.App/
  Assets/          original synthetic PDF artwork
  Controls/        note thumbnails and document preview UI
  Infrastructure/ binding helpers and commands
  Models/          tag-navigation and note models
  Services/        Markdown loading, metadata/PDF parsing, SQLite FTS indexing, and sample generation
  ViewModels/      folder loading, background indexing, full-text/tag filtering, and selection behavior
  MainWindow.xaml  high-fidelity three-pane interface
tests/
  NoteManager.App.Tests/     parser, editor, index, and PDF service tests
  NoteManager.App.UiTests/   FlaUI/NUnit executable-level regression suite
  Run-AllTests.ps1           serialized build, service, and UI entry point
  Run-UiTests.ps1            focused UI runner with TRX and failure artifacts
```

Visual comparison evidence and the final design review are documented in `design-qa.md`.
