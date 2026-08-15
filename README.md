# NoteManager

NoteManager is a cross-platform .NET 10 desktop application built with Avalonia.
It provides tag navigation, a searchable note list, Markdown editing, note
metadata, PDF embeds, and public-link publishing on Windows and macOS.

It's repository compatible with Obsidian but it's far easier to use and also 
far faster to search repository contents than your Obsidian would ever be.

## Run

From the repository root on Windows or macOS:

```bash
dotnet run --project src/NoteManager.Desktop/NoteManager.Desktop.csproj
```

The repository pins the .NET 8 SDK through `global.json`. On startup, the application recursively loads the Obsidian vault at:
On macOS or Linux, the included launcher compiles the app and starts it:

```bash
./run-notemanager.sh
```

Application arguments are forwarded unchanged:

```bash
./run-notemanager.sh --folder SampleNotes
```

Set `NOTEMANAGER_CONFIGURATION=Release` to build and start a Release build.

The repository pins the .NET 10 SDK through `global.json`. On startup, the application recursively loads the Obsidian vault at:

```text
C:\Projects\Obsidian
```

Use **File → Open folder…** or `Ctrl+O` to switch to another Markdown folder.
The repository pins the .NET 10 SDK through `global.json`. On startup, the
application opens the folder supplied with `--folder`, or the last folder that
was opened successfully. If neither is available, use **File → Open folder…**,
`Ctrl+O` on Windows, or `Command+O` on macOS to select an Obsidian vault or
another folder containing Markdown notes.

For a dialog-free automated launch, inject the startup folder:

```bash
dotnet run --project src/NoteManager.Desktop/NoteManager.Desktop.csproj -- \
  --folder /path/to/your/vault
```

On Windows PowerShell, a typical invocation is:

```powershell
dotnet run --project .\src\NoteManager.Desktop\NoteManager.Desktop.csproj -- `
  --folder C:\Notes\MyVault
```

## Activity and plugin logs

NoteManager writes local daily activity logs. On Windows, the application log
is stored outside the selected repository:

```text
%LOCALAPPDATA%\NoteManager\logs\Application-YYYY-MM-DD.log
```

It records application startup, a repository folder selected by the user, a
repository folder restored from a previous session, and unhandled exceptions
(type, message, and stack). Crash records are flushed to disk immediately.
Each enabled plugin
writes its own daily log inside the selected vault:

```text
<vault>\.note\plugins\git-integration\logs\GitSync-YYYY-MM-DD.log
<vault>\.note\plugins\document-conversion\logs\DocumentConversion-YYYY-MM-DD.log
```

Git Integration logs pull, staging, commit, and push outcomes. Document
Conversion logs scan and conversion outcomes, including failure cleanup. All
three log streams use thread-safe writes and remove daily log files older than
12 months when a new entry is written.

## Automated regression tests

The default pull-request tier runs the fast, deterministic `Unit`, `Contract`,
and embedded-SQLite `Database` categories:

```powershell
.\tests\Run-Tests.ps1
```

Run the real Git integration boundary separately in the slower integration
tier:

```powershell
.\tests\Run-Tests.ps1 -Tier Integration -Configuration Release
```

On Windows, the end-to-end tier launches the current Avalonia application and
drives it through UI Automation:

```powershell
.\tests\Run-Tests.ps1 -Tier EndToEnd -Configuration Release
```

It requires an unlocked interactive Windows session. The project remains
outside the cross-platform solution because FlaUI/UIA3 is Windows-specific.
See
[`tests/NoteManager.Desktop.UiTests/README.md`](tests/NoteManager.Desktop.UiTests/README.md)
for its user-visible scenarios and failure artifacts, and
[`docs/testing.md`](docs/testing.md) for the category definitions, full tier
matrix, required services, and focused commands.

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

This creates a re-runnable, drag-to-Applications DMG and SHA-256 checksum under
`installer/Output`. The runtime argument is optional and defaults to the build
Mac's architecture. With no signing environment variables, the package is
ad-hoc signed for internal testing. For a public, Gatekeeper-friendly release,
set both `NOTEMANAGER_CODESIGN_IDENTITY` and `NOTEMANAGER_NOTARY_PROFILE`; the
script then applies Hardened Runtime signing, notarizes and staples the DMG, and
performs final policy checks. See [`installer/README.md`](installer/README.md)
for credential setup and Intel Mac builds.

## Included interactions

- Recursively load every `.md` file from the selected folder and all subfolders.
- Browse an alphabetized tag rail whose counts are calculated from the loaded Markdown files.
- Select a tag to filter the middle pane by exact tag membership.
- Use the virtual **All notes** and **Untagged** tags to show the complete vault or notes without tags.
- Type in **Search notes** (or press `Ctrl+F`) to search note file names, tags, relative paths, and complete Markdown contents using strict or best-match expressions described below.
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

### How search works

The index stores the file name, path relative to the opened folder, parsed
tags, and complete Markdown source for every note. A Unicode FTS5 index handles
word and prefix searches. A second punctuation-preserving trigram index
handles phrases, paths, email addresses, versions, and other literal text.
Matching is case-insensitive and diacritic-insensitive, so `café` can match
`cafe`.

NoteManager waits 250 milliseconds after the latest keystroke before searching,
or you can press `Enter` to run the current expression immediately. A later
keystroke, folder change, or shutdown cancels the older query. The last
completed result remains visible while a new valid expression is running.
Malformed expressions are not executed and produce a readable status message.
A valid search with no matches clears the note list and displays **No notes
found**. While the full-text index is being built, the search box is disabled
and displays **Indexing in progress**. It becomes available when the status bar
reports **Full-text ready**.

The selected tag, **All notes**, or **Untagged** navigation filter remains in
effect. Text search therefore searches within the current navigation scope,
not outside it.

#### Search modes

An unqualified query is a strict search. Strict mode requires every adjacent
term and shows the most recently modified matching note first:

```text
project plan
all: project plan
= project plan
```

`all:` and `=` are equivalent explicit strict-mode selectors.

Best-match mode is selected with `best:` or `~`. Adjacent terms become
alternatives, and notes matching the most distinct terms and strongest fields
are displayed first:

```text
best: project plan
~ project plan
```

Best match normally returns notes matching at least one positive term. Add `*`
to include every note in the current navigation scope and place zero-score
notes after relevant notes:

```text
~ * project plan
```

#### Terms, phrases, and operators

Letter, number, and underscore terms are prefix searches. `plan` therefore
matches `plan`, `plans`, and `planning`, with an exact word ranked above a
prefix-only match.

Text enclosed in double quotes is one contiguous phrase:

```text
"quarterly project plan"
```

Use two double quotes for a quote inside a phrase:

```text
"the ""approved"" plan"
```

The supported operators are:

| Operator | Meaning |
| --- | --- |
| `AND` | Both operands must match |
| `OR` | At least one operand must match |
| `NOT` | Exclude the following operand |
| `+term` | Require the following term or group |
| `-term` | Short form of `NOT term` |
| `( ... )` | Group an expression |
| `*` | Include the complete current scope |

Explicit operators have the same meaning in both search modes. Operator words
are case-insensitive and are recognized only as complete, unquoted words.
`NOT` and `-` exclusions apply to the complete expression.

Examples:

```text
(invoice OR receipt) NOT draft
~ +invoice paid -archived
all: "project plan" AND approved
```

`NOT`, then `AND`, then `OR` is the operator precedence. Parentheses should be
used whenever the intended grouping would otherwise be unclear.

#### Search specific fields

Prefix a term or phrase with a field operator to limit where it can match:

| Field operator | Searches |
| --- | --- |
| `name:` | Note file name |
| `tag:` | Parsed note tags |
| `path:` | Path relative to the opened folder |
| `body:` | Markdown source |

Examples:

```text
name:"project plan"
tag:active body:roadmap
path:Clients/Acme
~ +tag:active body:roadmap -path:archive/
```

`title:` is accepted as an alias for `name:`. Only these known field names are
operators; other colon-containing text remains an ordinary search term.

#### Literal symbols and paths

A bare term containing punctuation is matched as a literal substring.
Forward slash and backslash are ordinary searchable characters, not operators
or escape characters:

```text
docs/search.md
C:\Projects\NoteManager
customer@example.com
release-1.2
```

The slash direction is significant. On Windows, a relative path normally uses
backslashes, while a forward-slash path can still match text in a note body.
Quote literal text when it contains spaces.

#### Relevance and note sorting

Best-match scoring favors file-name matches, followed by tags, relative paths,
and Markdown content. Matching more distinct positive terms provides the
largest coverage advantage; phrases, exact words, and repeated occurrences
provide additional ranking signals. Modification time is used only to break
relevance ties.

When a valid search result is accepted, the normal Title, Created, Updated,
and Size sort selections are cleared and the sort button is disabled because
the search mode controls ordering. Strict search uses modification time
descending. Best match uses relevance descending. Clearing the search box
restores the vault's saved normal sort selection and check mark.

The complete grammar, ordering rules, implementation design, and acceptance
criteria are documented in [`search.md`](search.md).

## Document Conversion plugin

Automatic document-to-Markdown conversion is supplied as an optional plugin.
Open a notes folder, choose **Tools → Plugins…**, and select **Document
Conversion**. Activation is stored per vault in
`<vault>\.note\plugins\activated.json`. The plugin creates its configuration
and daily log files below:

```text
<vault>\.note\plugins\document-conversion
```

Activation starts one scan immediately and schedules later scans every five
minutes. Each scan finds pending PDF, Word, spreadsheet, presentation, RTF,
OpenDocument, text, HTML, and EPUB files, orders them by modification time with
the newest first, and invokes DOC2MD once per document. A source is converted
only when the sibling path produced by replacing its extension with `.md` does
not already exist. The plugin never passes DOC2MD's `--overwrite` option, so an
existing Markdown counterpart is not changed. A failure affects only that
document; successful outputs from the same scan are preserved.

The generated `settings.json` uses recursive conversion, local PDF processing,
and `eng+pol` OCR by default. Deactivate the plugin before editing that file,
then reactivate it to load the changes. DOC2MD owns its MarkItDown,
LibreOffice, and Tesseract dependencies.

The NoteManager Windows installer downloads the latest published DOC2MD release,
verifies its SHA-256 digest, and installs it silently. Manual or non-Windows
NoteManager installations must provide the DOC2MD CLI at:

```text
C:\Program Files\Taskscape\DOC2MD\DOC2MD.Cli.exe
```

Plugin initialization fails with installation guidance when that executable is
missing. NoteManager publishes only the plugin assembly under
`Plugins\DocumentConversion`; its Windows installer keeps DOC2MD as a separate
installed product instead of copying DOC2MD binaries into the plugin folder.

## Git Integration plugin

Git synchronization is supplied as an optional plugin. Open a notes folder,
then choose **Tools → Plugins…**. The plugin window lists **Git Integration**;
select its checkbox to activate it for the current folder. Activation is
per-vault and is stored in:

```text
<vault>\.note\plugins\activated.json
```

The plugin creates its settings and daily logs below
`<vault>\.note\plugins\git-integration`. Edit `settings.json` while the plugin
is inactive to change the interval, Git executable, command timeout, or commit
message prefix. The default interval is five minutes. Activation schedules the
plugin; it does not run a synchronization immediately. The first cycle starts
after the configured interval has elapsed.

Git Integration uses the installed `git` executable and requires the selected
folder to already be the exact root of a configured repository. The current
branch must have an upstream, `user.name` and `user.email` must be configured,
and credentials must work without an interactive terminal prompt. NoteManager
does not initialize repositories, configure remotes or credentials, change
`safe.directory`, or resolve conflicts.

Each non-overlapping cycle saves the active note, runs
`git pull --rebase --autostash`, stages eligible changes, commits when required,
and pushes the configured upstream. Pull or conflict failures stop the cycle
before further mutations. A folder that is not a repository remains idle.

New, untracked files or folders whose names begin with a dot are excluded at
every depth, including `.note`, `.env`, `.obsidian`, and `.github`. If a dot-
prefixed file or a file below a dot-prefixed folder is already tracked, it stays
tracked: modifications and deletions are staged, committed, and pushed normally.
Normal `.gitignore` rules are also respected for untracked content without
preventing updates to files already in the repository.

The separately buildable plugin project is
`src/NoteManager.Plugin.GitIntegration/NoteManager.Plugin.GitIntegration.csproj`.
Building it copies its binary to the host's configuration-specific directory:

```text
src\NoteManager.Desktop\bin\<Configuration>\net10.0\Plugins\GitIntegration
```

Published application output contains the same `Plugins\GitIntegration`
directory next to `NoteManager.exe`. The original safety analysis and command
sequence are documented in [`synchronization.md`](synchronization.md).

## Dialog-free folder automation

UI tests can opt into a current-user-only named pipe and change the folder in the running application without invoking the native picker:

```powershell
$vaultPath = 'C:\Notes\MyVault'
$otherVaultPath = 'C:\Notes\AnotherVault'

dotnet run --project .\src\NoteManager.Desktop\NoteManager.Desktop.csproj -- `
  --folder $vaultPath `
  --automation-pipe NoteManager.UiTest

.\tools\Set-NoteManagerFolder.ps1 `
  -PipeName NoteManager.UiTest `
  -Path $otherVaultPath
```

The pipe listener is disabled unless `--automation-pipe` is explicitly supplied. Both the injected startup path and runtime commands call the same `ChangeFolderAsync` path as the production folder picker.

The FlaUI suite also uses an `import-pdf|<path>` automation command. It is
available only when the opt-in pipe is enabled, is dispatched on the Avalonia UI
thread, and calls the same PDF import path as a real drop. This makes collision,
copy, embed, save, and viewer assertions deterministic without synthetic mouse
input.

The current Avalonia UI suite creates its own disposable vaults and runs through
`tests/Run-UiTests.ps1`; it does not require a repository-level sample vault.

## Infostacker public links

The Share panel does not display collaborators or an access list. It has one operation: **Publish and copy public link**.

Following the [`taskscape/InfostackerPlugin`](https://github.com/taskscape/InfostackerPlugin) implementation, NoteManager:

1. Reads the selected Markdown file and prefixes it with the filename without `.md`.
2. Resolves files referenced by Obsidian `![[...]]` embeds from the selected vault.
3. Sends a multipart `POST` to `https://shr.infostacker.com/sharing/uploadmarkdownwithfiles` using the `markdown` field and repeated `files` fields.
4. Reads the returned post `id`, constructs `https://shr.infostacker.com/sharing/{id}`, and copies it to the platform clipboard.

The request is made only after the user presses the publish button. The note and combined attachments are checked against the plugin's 100 MB limit before upload. An unreadable attachment is skipped, matching the plugin behavior, while an unavailable service or rejected request is reported inside the Share panel.

## Automatic note saving

The Markdown editor updates the selected note model on every text change. A dirty note is written immediately before NoteManager changes the selected note, tag/view, or folder; it is also written before Infostacker publishing and during application shutdown.

Saves use a write-through temporary file in the note's own directory followed by an overwrite move, preventing an interrupted write from truncating the original. If a save fails, the requested navigation or shutdown is cancelled and the current editor remains open with its unsaved text.

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

An inline Obsidian PDF or image transclusion uses:

```markdown
![[Documents/Report.pdf]]
![[assets/Pasted image 20250727091803.png]]
```

Multiple PDF and PNG, JPG, JPEG, or BMP transclusions are supported in one note and appear beneath the editor in Markdown order. Targets may be absolute, relative to the note, relative to the vault root, or filename-only; filename-only links are resolved against the vault media index. Changes typed into the editor refresh the previews shortly after typing stops.

## Sample documents

When no vault is open, the visual-demo mode creates valid lightweight PDF and
DOCX files in:

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
tests/
  NoteManager.App.Tests/     portable parser, editor, index, PDF, and view-model tests
  NoteManager.Desktop.UiTests/ current Windows Avalonia search UI tests
```

Visual comparison evidence and the final design review are documented in `design-qa.md`.
