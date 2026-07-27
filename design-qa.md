# Design QA

## Current Markdown-vault validation

- Search-box regression capture: `C:\Users\TaskscapeLtd\Documents\NoteManager\artifacts\notemanager-full-text-search.png`.
- The 40 px search row and vertically centered input remove the placeholder/query clipping shown in the latest source screenshot.
- A body-only query, `loader searches subfolders`, returned `second-template.md`; those words do not occur in its file name or tags.
- The per-vault SQLite FTS5 index was created at `.notes\search.db`, reported progress without blocking the window, and reached **Full-text ready**.
- Switching folders during the initial 8,087-note Obsidian indexing pass cancelled the superseded pass and completed the new vault index while the window remained responsive.
- `tests\Test-FolderInjection.ps1` passed: the app started from an injected four-note path, changed to the nested one-note path over the opt-in current-user pipe, opened no folder dialog, and returned the note for a body-only search.
- `tests\Test-ToolbarActions.ps1` passed against a disposable vault: Share opened directly beneath its toolbar button, Create wrote a zero-byte root Markdown file, declining Delete preserved it, and confirming Delete permanently removed it.
- `tests\Test-InfostackerPublishing.ps1` passed against a local mock server: the panel showed no access list, the multipart endpoint/fields/Markdown/attachment matched `taskscape/InfostackerPlugin`, and the returned public URL was copied to the Windows clipboard.
- `tests\Test-AutoSave.ps1` passed against a disposable vault: edits were persisted on note selection, view selection, and window close, with no atomic-save temporary files left behind.
- Current implementation capture: `C:\Users\TaskscapeLtd\Documents\NoteManager\artifacts\notemanager-final.png`.
- Startup recursively loaded 8,087 Markdown files from `C:\Projects\Obsidian`.
- **File → Open folder…** was exercised with `SampleNotes` and loaded all 4 Markdown files, including the nested `projects` folder.
- The sample `szablon` tag filtered the middle list to 2 notes; virtual `Untagged` filtered it to 1 note; `All notes` restored all 4.
- The selected note exposed its unchanged plain Markdown text and recognized `![[documents/orbital-guide.pdf]]`.
- A real Obsidian note with two PDF transclusions produced 2 resolved interactive viewers.
- Repeatedly switching between one- and two-PDF notes remained responsive.
- The Edge PDF toolbar visibly provides outline, page selection, zoom, fit, rotate, search, print, save, full-screen, and settings controls.
- The release build completed with zero warnings and zero errors.

## Baseline fidelity findings

The final comparison has no actionable P0, P1, or P2 mismatches. The requested hierarchy is present and legible: counted tags on the left, notes in the middle, and the selected note with a direct two-page PDF preview on the right.

## Comparison target

- Source visual truth: `C:\Users\TASKSC~1\AppData\Local\Temp\codex-clipboard-c47fe6bb-2370-4b7e-ab97-6da4749894e6.png`
- Implementation screenshot: `C:\Users\TaskscapeLtd\Documents\NoteManager\artifacts\notemanager-updated-final.png`
- Source pixels: 3840 × 2304.
- Raw implementation pixels: 3866 × 2330, including the 13 px maximized-window shadow on every edge.
- Normalized implementation pixels: 3840 × 2304 after cropping the 13 px shadow. No density resampling was required.
- Application design viewport at the time of this baseline capture: 1920 × 1130 rendered at the active 200% Windows scale.
- Baseline state: maximized native window; no tag filter; 13 synthetic notes; `img20230118_12404076.pdf` selected.

## Evidence

- Full-view comparison: `C:\Users\TaskscapeLtd\Documents\NoteManager\artifacts\comparison-updated-final-full.png`
- Focused tag and note-list comparison: `C:\Users\TaskscapeLtd\Documents\NoteManager\artifacts\comparison-updated-final-tags-list.png`
- Focused editor and PDF comparison: `C:\Users\TaskscapeLtd\Documents\NoteManager\artifacts\comparison-updated-final-editor.png`

The focused comparisons are required because tag counts, note metadata, the selected-row border, toolbar icons, tag pills, PDF page margins, and generated document detail are too small to judge reliably from the full view alone.

## Fidelity review

- Fonts and typography: Segoe UI matches the dense Windows desktop interface. Final title, tag, metadata, count, toolbar, and date sizes preserve the source hierarchy, clipping, and ellipsis behavior.
- Spacing and layout rhythm: the 300 / 1 / 550 / 1 / flexible WPF tracks normalize to the source pane boundaries. The header, search field, 90 px note rows, editor toolbar, PDF canvas, page inset, and scrollbar positions align closely without hidden persistent controls.
- Colors and visual tokens: light-gray rails, white note surfaces, subdued blue-gray metadata, light-blue tag chips, green New Note action, cyan selection border, and charcoal PDF canvas follow the source palette and state treatments.
- Image quality and asset fidelity: the selected note uses two project-bound, high-resolution generated raster pages with the source's monochrome photocopied construction-manual art direction. The cover remains sharp in both its 151 × 80 thumbnail and large PDF view. No placeholder or code-drawn document artwork is used.
- Copy and content: filenames, tags, notebooks, dates, counts, and document copy are coherent synthetic data. `Blocki`, `Manual`, and `Stacja kosmiczna` match the selected-note state in the source.
- Icons: the visible app actions use the closest built-in Segoe MDL2 equivalents, consistently sized and aligned. Document artwork is raster rather than icon approximations.
- Responsiveness and accessibility: the current application uses responsive 300 / 1 / 550 / 1 / flexible pane tracks without a whole-window transform, text does not clip, list regions scroll independently, primary controls expose automation names, and keyboard shortcuts remain available.

## Interaction checks

Native UI Automation checks passed:

- selecting the `Manual` tag reduced the middle pane to 1 note and kept `img20230118_12404076.pdf` selected;
- activating the Tags heading cleared the tag filter and restored all 13 notes;
- entering `energy` in Search notes returned the one energy-report result;
- creating a note increased the count to 14, selected `Untitled note`, and added the `draft` tag to the counted tag rail.

The draft is intentionally in-memory, so restarting the app restored the 13-note reference state for the final capture. Browser and browser-console checks do not apply to this native WPF application.

## Comparison history

### Pass 1

- [P2] Tag counts were aligned to the far edge of the rail rather than immediately following their labels.
- [P2] Single-attachment rows displayed generic `1 attachment` copy instead of the filename and size visible in the updated source.
- [P2] The PDF viewer had a compressed top inset and borderline horizontal overflow, making the page sit too high compared with the source.
- Fixes: changed tag rows to inline label/count layout; added filename-aware list metadata; tightened the PDF viewer width; and matched the dark-canvas and white-page top spacing.
- Evidence: `comparison-updated-full.png`, `comparison-updated-tags-list.png`, and `comparison-updated-editor.png`.

### Pass 2

- Evidence: the three `comparison-updated-final-*` files listed above.
- Result: the earlier P2 differences are resolved. There are no remaining actionable P0, P1, or P2 findings.

## Implementation checklist

- [x] Generate tag rows and counts from note data.
- [x] Filter the note list by exact tag and restore all notes from the Tags heading.
- [x] Keep search, note selection, new-note creation, share, and sync interactions operational.
- [x] Show a thin cyan outline around the selected note.
- [x] Open the selected space-manual note directly into a scrollable multi-page PDF preview.
- [x] Verify Debug and Release builds with zero warnings and zero errors.

## Follow-up polish

- [P3] The application name, sample account text, and native executable icon intentionally differ from the proprietary reference branding.
- [P3] The generated orbital manual is a new synthetic asset rather than a copy of the source document.
- [P3] Some formatting glyphs are the nearest built-in Windows equivalents rather than the original proprietary icon set.

## Final result

final result: passed
