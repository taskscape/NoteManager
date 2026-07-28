# NoteManager

NoteManager is a cross-platform .NET 10 desktop application built with Avalonia.
It provides tag navigation, a searchable note list, Markdown editing, note
metadata, PDF embeds, and public-link publishing on Windows and macOS.

## Run

From the repository root on Windows or macOS:

```bash
dotnet run --project src/NoteManager.Desktop/NoteManager.Desktop.csproj
```

The repository pins the .NET 10 SDK through `global.json`. On startup, the application recursively loads the Obsidian vault at:

```text
SampleNotes
```

Use **File → Open folder…**, `Ctrl+O` on Windows, or `Command+O` on macOS to
switch to another Markdown folder.

For a dialog-free automated launch, inject the startup folder:

```bash
dotnet run --project src/NoteManager.Desktop/NoteManager.Desktop.csproj -- \
  --folder SampleNotes
```

## Automated regression tests

The portable service and view-model suite runs on both operating systems:

```bash
dotnet test tests/NoteManager.App.Tests/NoteManager.App.Tests.csproj
```

The former WPF/FlaUI suite remains under `tests/NoteManager.App.UiTests` as
migration reference, but is not part of the cross-platform solution build.

## Package a release for team sharing

Run the release packager from the repository root on macOS. Supply a new
three- or four-part numeric version for every release:

```bash
./installer/package-release.sh 1.2.0
```

The script runs the Release test suite, publishes self-contained macOS ARM64
and Windows x64 applications, signs the macOS application bundle, and creates:

```text
installer/Output/NoteManager-1.2.0-osx-arm64.zip
installer/Output/NoteManager-1.2.0-win-x64.zip
installer/Output/NoteManager-1.2.0-SHA256SUMS.txt
```

Teammates can extract the appropriate archive and run `NoteManager.app` on
macOS or `NoteManager.exe` on Windows. They do not need to install .NET.

For Intel macOS or Windows on ARM64, override the default runtime identifiers:

```bash
./installer/package-release.sh 1.2.0 osx-x64 win-arm64
```

The script refuses to overwrite an existing release. For every subsequent
release, choose the next version, run the same command, and verify the artifacts
before sharing them:

```bash
cd installer/Output
shasum -a 256 -c NoteManager-1.2.0-SHA256SUMS.txt
```

By default, the macOS app receives an ad-hoc signature suitable for internal
team sharing. A release engineer can apply an installed Developer ID
certificate by setting `NOTEMANAGER_CODESIGN_IDENTITY`:

```bash
NOTEMANAGER_CODESIGN_IDENTITY="Developer ID Application: Example Company (TEAMID)" \
  ./installer/package-release.sh 1.2.0
```

Public macOS distribution additionally requires Apple's notarization process,
which is intentionally outside this internal packaging script.

## Build platform installers

Inno Setup 6 or 7 can package a self-contained Avalonia Release build:

```powershell
.\installer\build-installer.ps1
```

The finished artifact is written to:

```text
installer\Output\NoteManager-1.0.0-win-x64-Setup.exe
```

Pass `-Version 1.2.0` to version a release. See
[`installer\README.md`](installer/README.md) for prerequisites, ARM64 builds,
team archives, and unattended installation.

On macOS:

```bash
./installer/build-macos.sh 1.0.0 osx-arm64
```

Use `osx-x64` for Intel Macs.

## Included interactions

- Recursively load every `.md` file from the selected folder and all subfolders.
- Browse an alphabetized tag rail whose counts are calculated from the loaded Markdown files.
- Select a tag to filter the middle pane by exact tag membership.
- Use the virtual **All notes** and **Untagged** tags to show the complete vault or notes without tags.
- Type in **Search notes** (or press `Ctrl+F`) to search titles, file names, tags, paths, and complete Markdown contents.
- Select a note to display its original Markdown source as plain, selectable text.
- Edit Markdown directly; dirty notes are saved atomically when selecting another note or view, changing folders, publishing, or closing the application.
- Drag one or more PDF files onto a note row or the Markdown editor to insert Obsidian `![[...]]` embeds. PDFs dropped from outside the open folder are copied to its root and receive `(1)`, `(2)`, and later suffixes when names collide.
- View each Obsidian PDF and PNG, JPG, JPEG, or BMP image transclusion beneath the Markdown source, in the same order as the embeds. Images are scaled to fit while preserving their aspect ratio.
- Use the platform PDF viewer for scrolling, zooming, selection, printing, and
  saving, or click **Open PDF** to use the default desktop application. Viewer
  toolbar capabilities vary by operating system; PDF text-search parity is not
  a migration requirement.
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
dotnet run --project .\src\NoteManager.Desktop\NoteManager.Desktop.csproj -- `
  --folder .\SampleNotes `
  --automation-pipe NoteManager.UiTest

.\tools\Set-NoteManagerFolder.ps1 `
  -PipeName NoteManager.UiTest `
  -Path .\SampleNotes\projects
```

The pipe listener is disabled unless `--automation-pipe` is explicitly supplied. Both the injected startup path and runtime commands call the same `ChangeFolderAsync` path as the production folder picker.

The FlaUI suite also uses an `import-pdf|<path>` automation command. It is
available only when the opt-in pipe is enabled, is dispatched on the Avalonia UI
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
4. Reads the returned post `id`, constructs `https://shr.infostacker.com/sharing/{id}`, and copies it to the platform clipboard.

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

## Markdown metadata and embedded media

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

An inline Obsidian PDF or image transclusion uses:

```markdown
![[Documents/Report.pdf]]
![[assets/Pasted image 20250727091803.png]]
```

Multiple PDF and PNG, JPG, JPEG, or BMP transclusions are supported in one note and appear beneath the editor in Markdown order. Targets may be absolute, relative to the note, relative to the vault root, or filename-only; filename-only links are resolved against the vault media index. Changes typed into the editor refresh the previews shortly after typing stops.

## Sample documents

The repository also includes a compact `SampleNotes` fixture. The original visual-demo mode creates valid lightweight PDF and DOCX files in:

```text
%LOCALAPPDATA%\NorthstarNoteManager\SampleDocuments
```

These files contain synthetic business, research, invoice, receipt, parcel, and planning content matching the document-heavy character of the reference UI.

## Project structure

```text
src/NoteManager.Core/
  NoteManager.Core.csproj  platform-neutral models, services, and view model
src/NoteManager.Desktop/
  Controls/                Avalonia PDF viewer
  Dialogs/                 cross-platform tag and confirmation dialogs
  MainWindow.axaml          Avalonia three-pane desktop interface
  Program.cs               Windows/macOS application entry point
src/NoteManager.App/
  Legacy WPF UI retained as migration reference; not in the portable solution
tests/
  NoteManager.App.Tests/     portable parser, editor, index, PDF, and view-model tests
  NoteManager.App.UiTests/   legacy Windows-only FlaUI regression reference
```

Visual comparison evidence and the final design review are documented in `design-qa.md`.
