# Avalonia search UI tests

This Windows-only NUnit/FlaUI suite launches the current Avalonia
`NoteManager.Desktop` executable and operates the real **Search notes** text
box through UI Automation.

Run it from an unlocked interactive Windows session:

```powershell
.\tests\Run-UiTests.ps1 -Configuration Debug
```

The six scenarios verify:

- a large-vault index build disables search with an indexing message until ready;
- physical typing followed by Enter, zero results, and the visible empty state;
- strict implicit `AND`, newest-modified ordering, phrases, field operators,
  literal slash matching, and exclusions;
- best-match implicit `OR`, relevance ordering, `+`, `-`, and `*`;
- search-owned sorting, invalid-expression retention, and clearing search;
- composition of tag navigation and full-text search.

Each scenario creates a disposable vault and waits for the real background
index to report **Full-text ready**. Failures write a screenshot, UI Automation
tree, and TRX results below `artifacts\ui-tests`.

The project is intentionally separate from the cross-platform solution because
FlaUI/UIA3 requires Windows and an interactive desktop session. The portable
parser, service, and view-model tests remain in `NoteManager.App.Tests`.
