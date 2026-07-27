# Design QA — contextual Markdown editor actions

## Comparison target

- Source visual truth: `/Users/mzag/NoteManager/artifacts/modern-ui-option-1-final-pass.png`
- User-directed change: move Tags above and Import PDF below the Markdown editor, align both to its right edge, and make both actions smaller and more contextual.
- Final implementation: `/Users/mzag/NoteManager/artifacts/contextual-editor-actions-final.png`
- Full-view comparison: `/Users/mzag/NoteManager/artifacts/contextual-editor-actions-comparison.png`
- Focused editor comparison: `/Users/mzag/NoteManager/artifacts/contextual-editor-actions-focus.png`
- State: macOS light theme, SampleNotes loaded, All notes and `untagged-note.md` selected, editor unfocused.

## Viewport and normalization

- Source pixels: 1312 × 768.
- Implementation pixels: 1312 × 768.
- Native desktop capture; browser CSS viewport and `deviceScaleFactor` do not apply.
- Both captures use the same window size, application state, theme, note, and density.
- The 3040 × 1040 full comparison fits both undistorted captures into equal logical slots.
- The 2200 × 1360 focused comparison uses matching, aspect-preserved editor-pane crops.

## Findings

No actionable P0, P1, or P2 differences remain.

- Typography: both contextual buttons use the existing Inter hierarchy at 12 px medium weight. They remain legible while staying subordinate to the editor title and document content.
- Spacing and layout: Tags is directly above the editor and right-aligned with its edge. Import PDF is directly below the editor and shares the same right alignment. Both remain visible at the tested 768 px window height.
- Colors and tokens: default controls use transparent backgrounds and secondary text, with the existing subtle surface and primary text applied on hover. Light/dark theme resources remain unchanged.
- Asset fidelity: both buttons use 16 px Fluent UI System Icons. No handcrafted images, SVGs, emoji, or placeholder assets were introduced.
- Copy and content: labels remain “Tags” and “Import PDF”; concise tooltips clarify that they affect the selected note.
- Information architecture: removing both controls from the global command strip reduces scope ambiguity and correctly groups content operations with the Markdown editor.
- Accessibility: visible text labels remain in addition to icons, tooltips describe intent, and existing disabled-state bindings are preserved.

## Comparison history

### Iteration 1

- Finding: P2 — Import PDF was placed correctly but clipped below the fold at the common 1312 × 768 window size.
- Fix: reduced the editor minimum height from 520 to 470 logical pixels, preserving a generous writing surface while keeping the contextual footer action visible.
- Post-fix evidence: `/Users/mzag/NoteManager/artifacts/contextual-editor-actions-final.png` and `/Users/mzag/NoteManager/artifacts/contextual-editor-actions-comparison.png`.

## Interaction checks

- Tags opened the existing Assign Tags dialog and Cancel returned cleanly to the editor.
- Import PDF opened the native PDF file picker and Cancel returned cleanly to the editor.
- Existing File/Edit menu entries remain available.
- The application built with zero warnings or errors.
- All 16 automated tests passed.

## Residual P3 / test gaps

- Windows and dark-theme rendering were not visually captured in this macOS environment; the change uses only existing theme tokens and cross-platform Avalonia controls.

## Final result

final result: passed
