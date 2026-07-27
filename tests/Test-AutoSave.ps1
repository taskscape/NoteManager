[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [ValidateRange(5, 120)]
    [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\NoteManager.App\NoteManager.App.csproj'
$executablePath = Join-Path $repositoryRoot "src\NoteManager.App\bin\$Configuration\net8.0-windows10.0.19041.0\NoteManager.exe"
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryVault = Join-Path $temporaryBase "NoteManager.AutoSaveTest.$([Guid]::NewGuid().ToString('N'))"

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    & dotnet build $projectPath --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "NoteManager build failed with exit code $LASTEXITCODE."
    }
}

[void] (New-Item -ItemType Directory -Path $temporaryVault)
$firstNotePath = Join-Path $temporaryVault 'First.md'
$secondNotePath = Join-Path $temporaryVault 'Second.md'
Set-Content -LiteralPath $firstNotePath -Value '# First original' -NoNewline
Set-Content -LiteralPath $secondNotePath -Value '# Second original' -NoNewline
$fixtureTimestamp = [DateTime]::UtcNow
[System.IO.File]::SetLastWriteTimeUtc($firstNotePath, $fixtureTimestamp)
[System.IO.File]::SetLastWriteTimeUtc(
    $secondNotePath,
    $fixtureTimestamp.AddMinutes(-1))

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-ProcessWindow {
    param([int] $ProcessId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    return $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
}

function Wait-ForElement {
    param(
        [int] $ProcessId,
        [System.Windows.Automation.AutomationProperty] $Property,
        [string] $Value,
        [DateTime] $Deadline
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        $Property,
        $Value)
    do {
        $window = Get-ProcessWindow -ProcessId $ProcessId
        if ($null -ne $window) {
            $element = $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $condition)
            if ($null -ne $element) {
                return $element
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Timed out waiting for UI element: $Value"
}

function Wait-ForListItem {
    param(
        [int] $ProcessId,
        [string] $Name,
        [DateTime] $Deadline
    )

    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name))
    do {
        $window = Get-ProcessWindow -ProcessId $ProcessId
        if ($null -ne $window) {
            $element = $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $condition)
            if ($null -ne $element) {
                return $element
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Timed out waiting for note list item: $Name"
}

function Wait-ForFileContent {
    param(
        [string] $Path,
        [string] $ExpectedContent,
        [DateTime] $Deadline
    )

    do {
        if ((Get-Content -LiteralPath $Path -Raw) -eq $ExpectedContent) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Timed out waiting for saved content in: $Path"
}

function Set-EditorText {
    param(
        [int] $ProcessId,
        [string] $Text,
        [DateTime] $Deadline
    )

    $editor = Wait-ForElement `
        -ProcessId $ProcessId `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'MarkdownEditor' `
        -Deadline $Deadline
    $valuePattern = $editor.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    if ($valuePattern.Current.IsReadOnly) {
        throw 'The Markdown editor is unexpectedly read-only.'
    }

    $valuePattern.SetValue($Text)
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new($executablePath)
$startInfo.UseShellExecute = $false
[void] $startInfo.ArgumentList.Add('--folder')
[void] $startInfo.ArgumentList.Add($temporaryVault)
$process = [System.Diagnostics.Process]::Start($startInfo)

try {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value '2 notes' `
        -Deadline $deadline)

    $firstSwitchContent = "# First saved on note switch`nwith another line."
    Set-EditorText `
        -ProcessId $process.Id `
        -Text $firstSwitchContent `
        -Deadline $deadline
    $secondNote = Wait-ForListItem `
        -ProcessId $process.Id `
        -Name 'Second.md' `
        -Deadline $deadline
    $secondNote.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-ForFileContent `
        -Path $firstNotePath `
        -ExpectedContent $firstSwitchContent `
        -Deadline $deadline

    $secondViewContent = '# Second saved on view switch'
    Set-EditorText `
        -ProcessId $process.Id `
        -Text $secondViewContent `
        -Deadline $deadline
    $cardViewButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'CardViewButton' `
        -Deadline $deadline
    $cardViewButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-ForFileContent `
        -Path $secondNotePath `
        -ExpectedContent $secondViewContent `
        -Deadline $deadline

    $secondCloseContent = '# Second saved during application close'
    Set-EditorText `
        -ProcessId $process.Id `
        -Text $secondCloseContent `
        -Deadline $deadline
    if (-not $process.CloseMainWindow()) {
        throw 'The application window did not accept the close request.'
    }

    if (-not $process.WaitForExit(5000)) {
        throw 'The application did not close after saving the selected note.'
    }

    Wait-ForFileContent `
        -Path $secondNotePath `
        -ExpectedContent $secondCloseContent `
        -Deadline $deadline

    $temporarySaveFiles = Get-ChildItem `
        -LiteralPath $temporaryVault `
        -Filter '*.tmp' `
        -File
    if ($temporarySaveFiles.Count -ne 0) {
        throw 'Atomic-save temporary files were left in the vault.'
    }

    Write-Output 'PASS: note-switch, view-switch, and application-close boundaries saved Markdown edits immediately.'
}
finally {
    if (-not $process.HasExited) {
        [void] $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id
        }
    }

    $process.Dispose()

    $resolvedTemporaryVault = [System.IO.Path]::GetFullPath($temporaryVault)
    if ($resolvedTemporaryVault.StartsWith(
            $temporaryBase,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -and (Split-Path -Leaf $resolvedTemporaryVault).StartsWith(
            'NoteManager.AutoSaveTest.',
            [System.StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporaryVault -Recurse -Force
    }
}
