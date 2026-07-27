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
$sampleRoot = (Resolve-Path -LiteralPath (Join-Path $repositoryRoot 'SampleNotes')).Path
$nestedFolder = (Resolve-Path -LiteralPath (Join-Path $sampleRoot 'projects')).Path
$pipeName = "NoteManager.Test.$PID.$([Guid]::NewGuid().ToString('N'))"

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    & dotnet build $projectPath --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "NoteManager build failed with exit code $LASTEXITCODE."
    }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-AppWindow {
    param([int] $ProcessId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    return $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
}

function Wait-ForWindow {
    param(
        [int] $ProcessId,
        [DateTime] $Deadline
    )

    do {
        $window = Find-AppWindow -ProcessId $ProcessId
        if ($null -ne $window) {
            return $window
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw 'Timed out waiting for the NoteManager window.'
}

function Wait-ForText {
    param(
        [System.Windows.Automation.AutomationElement] $Window,
        [string] $ExpectedText,
        [DateTime] $Deadline
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)

    do {
        $textElements = $Window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        foreach ($element in $textElements) {
            if ($element.Current.Name -eq $ExpectedText) {
                return
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Timed out waiting for UI text: $ExpectedText"
}

function Set-SearchText {
    param(
        [System.Windows.Automation.AutomationElement] $Window,
        [string] $Text
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'SearchBox')
    $searchBox = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $searchBox) {
        throw 'Could not find the Search notes text box.'
    }

    $valuePattern = $searchBox.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($Text)
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new($executablePath)
$startInfo.UseShellExecute = $false
[void] $startInfo.ArgumentList.Add('--folder')
[void] $startInfo.ArgumentList.Add($sampleRoot)
[void] $startInfo.ArgumentList.Add('--automation-pipe')
[void] $startInfo.ArgumentList.Add($pipeName)
$process = [System.Diagnostics.Process]::Start($startInfo)

try {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $window = Wait-ForWindow -ProcessId $process.Id -Deadline $deadline
    Wait-ForText -Window $window -ExpectedText '4 notes' -Deadline $deadline
    Wait-ForText -Window $window -ExpectedText 'Full-text ready' -Deadline $deadline

    & (Join-Path $repositoryRoot 'tools\Set-NoteManagerFolder.ps1') `
        -PipeName $pipeName `
        -Path $nestedFolder `
        -TimeoutMilliseconds ($TimeoutSeconds * 1000) | Out-Host

    Wait-ForText -Window $window -ExpectedText '1 notes' -Deadline $deadline
    Wait-ForText -Window $window -ExpectedText 'second-template.md' -Deadline $deadline

    Set-SearchText -Window $window -Text 'missing-token-9281'
    Wait-ForText -Window $window -ExpectedText '0 notes' -Deadline $deadline
    Set-SearchText -Window $window -Text 'loader searches subfolders'
    Wait-ForText -Window $window -ExpectedText '1 notes' -Deadline $deadline
    Wait-ForText -Window $window -ExpectedText 'second-template.md' -Deadline $deadline

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id)
    $applicationWindows = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition)
    if ($applicationWindows.Count -ne 1) {
        throw "Expected one application window and no folder dialog; found $($applicationWindows.Count)."
    }

    Write-Output 'PASS: folder injection avoided the dialog and body-only full-text search returned the expected note.'
}
finally {
    if (-not $process.HasExited) {
        [void] $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id
        }
    }

    $process.Dispose()
}
