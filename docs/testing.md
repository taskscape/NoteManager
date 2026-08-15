# Test categories and execution tiers

Run test commands from the repository root. Test categories describe the
largest boundary crossed by a scenario; execution tiers describe when a set of
categories is required. Categories do not change test behavior.

## Categories

| Category | Scope | Requirements |
| --- | --- | --- |
| `Unit` | In-process logic with test-owned memory or temporary files | .NET 10 SDK; no external service |
| `Contract` | Stable settings, command-line, and automation-protocol shapes | .NET 10 SDK; no external service |
| `Database` | The real SQLite FTS schema, indexing, and database-backed view-model behavior | .NET 10 SDK; SQLite is embedded and each test owns its temporary database |
| `Integration` | Multiple production components or an external executable working together | .NET 10 SDK; Git must be available on `PATH` for Git integration scenarios; no remote server or credentials are used |
| `EndToEnd` | The compiled desktop executable driven through Windows UI Automation | Windows, .NET 10 SDK, and an unlocked interactive desktop session; test and app must run at the same integrity level |

xUnit tests use `[Trait("Category", "...")]`; NUnit executable tests use
`[Category("...")]`. Use exactly one of the supported category names for a
test fixture. The former `UI` label has been replaced by `EndToEnd`.

There are currently no browser or mobile test targets in this repository, so
`Browser` and `Mobile` are not supported categories.

## Execution tiers

| Tier | Required stage | Included categories | Command |
| --- | --- | --- | --- |
| `PullRequest` (default) | Every pull request | `Unit`, `Contract`, `Database` | `.\tests\Run-Tests.ps1` |
| `Integration` | Merge/nightly validation and before release | `Integration` | `.\tests\Run-Tests.ps1 -Tier Integration -Configuration Release` |
| `EndToEnd` | Windows desktop validation before release | `EndToEnd` | `.\tests\Run-Tests.ps1 -Tier EndToEnd -Configuration Release` |
| `Full` | Windows release sign-off | All supported categories | `.\tests\Run-Tests.ps1 -Tier Full -Configuration Release` |

On macOS or Linux with PowerShell 7, invoke the portable tiers with `pwsh`, for
example:

```bash
pwsh ./tests/Run-Tests.ps1 -Tier PullRequest -Configuration Release
```

The pull-request tier is deterministic and does not require Git, a server,
credentials, a browser, or an interactive desktop. The `Database` category is
included because it uses only test-owned embedded SQLite databases.

The integration tier creates local temporary Git repositories and bare remotes;
it never contacts a network remote. The end-to-end tier launches the current
Avalonia application and writes TRX results, screenshots, and UI Automation
trees below `artifacts/ui-tests` when applicable. Portable tier TRX files are
written below `artifacts/test-results`.

`tests/Run-AllTests.ps1` remains the direct full-suite entry point for existing
automation. `tests/Run-UiTests.ps1` remains the direct `EndToEnd` entry point and
accepts normal `dotnet test` filters for focused scenarios.

## Focused category runs

Use the normal VSTest filter syntax when diagnosing one test project:

```powershell
dotnet test .\tests\NoteManager.App.Tests\NoteManager.App.Tests.csproj `
  --filter "Category=Database"

.\tests\Run-UiTests.ps1 `
  -Filter "Category=EndToEnd&FullyQualifiedName~SearchUiTests"
```

Do not silently skip a slower tier because its environment is unavailable.
Record the tier as not run and complete it in the required stage above.
