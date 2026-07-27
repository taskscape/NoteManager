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
$temporaryVault = Join-Path $temporaryBase "NoteManager.ToolbarTest.$([Guid]::NewGuid().ToString('N'))"

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    & dotnet build $projectPath --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "NoteManager build failed with exit code $LASTEXITCODE."
    }
}

[void] (New-Item -ItemType Directory -Path $temporaryVault)
Set-Content -LiteralPath (Join-Path $temporaryVault 'Existing note.md') -Value '# Existing note'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NoteManagerTestNative
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
'@

function Get-ProcessWindows {
    param([int] $ProcessId)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    return $root.FindAll(
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
        foreach ($window in (Get-ProcessWindows -ProcessId $ProcessId)) {
            if ($window.GetCurrentPropertyValue($Property) -eq $Value) {
                return $window
            }

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

function Wait-ForFileState {
    param(
        [string] $Path,
        [bool] $ShouldExist,
        [DateTime] $Deadline
    )

    do {
        if ((Test-Path -LiteralPath $Path -PathType Leaf) -eq $ShouldExist) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Timed out waiting for file existence '$ShouldExist': $Path"
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new($executablePath)
$startInfo.UseShellExecute = $false
[void] $startInfo.ArgumentList.Add('--folder')
[void] $startInfo.ArgumentList.Add($temporaryVault)
$process = [System.Diagnostics.Process]::Start($startInfo)
$createdNotePath = Join-Path $temporaryVault 'Untitled note.md'

try {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value '1 notes' `
        -Deadline $deadline)

    $shareButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'ShareToolbarButton' `
        -Deadline $deadline
    $shareButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $publishButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'PublishPublicLinkButton' `
        -Deadline $deadline
    $sharePanel = $publishButton
    do {
        $sharePanel = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent(
            $sharePanel)
    } while ($null -ne $sharePanel -and $sharePanel.Current.ClassName -ne 'Popup')
    if ($null -eq $sharePanel) {
        throw 'Could not resolve the native Share popup.'
    }

    $buttonBounds = $shareButton.Current.BoundingRectangle
    $panelBounds = $sharePanel.Current.BoundingRectangle
    if ([Math]::Abs($panelBounds.Left - $buttonBounds.Left) -gt 2 `
        -or $panelBounds.Top -lt $buttonBounds.Bottom `
        -or $panelBounds.Top -gt ($buttonBounds.Bottom + 4)) {
        throw 'The Share panel is not positioned directly beneath the toolbar button.'
    }

    $peopleCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'People with access')
    foreach ($applicationWindow in (Get-ProcessWindows -ProcessId $process.Id)) {
        $peopleElement = $applicationWindow.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $peopleCondition)
        if ($null -ne $peopleElement) {
            throw 'The Share panel still displays users with access.'
        }
    }

    $closeShareButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'CloseSharePanelButton' `
        -Deadline $deadline
    $closeShareButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $createButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'CreateToolbarButton' `
        -Deadline $deadline
    $createButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-ForFileState -Path $createdNotePath -ShouldExist $true -Deadline $deadline
    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value '2 notes' `
        -Deadline $deadline)
    if ((Get-Item -LiteralPath $createdNotePath).Length -ne 0) {
        throw 'Create did not produce an empty Markdown file.'
    }

    $deleteButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'DeleteToolbarButton' `
        -Deadline $deadline
    $deleteButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $confirmationWindow = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value 'Delete note' `
        -Deadline $deadline

    $noButtonHandle = [NoteManagerTestNative]::GetDlgItem(
        [IntPtr] $confirmationWindow.Current.NativeWindowHandle,
        7)
    if ($noButtonHandle -eq [IntPtr]::Zero) {
        throw 'Could not find the confirmation dialog No button.'
    }

    [void] [NoteManagerTestNative]::SendMessage(
        $noButtonHandle,
        0x00F5,
        [IntPtr]::Zero,
        [IntPtr]::Zero)
    Start-Sleep -Milliseconds 250
    if (-not (Test-Path -LiteralPath $createdNotePath -PathType Leaf)) {
        throw 'Declining the Delete confirmation removed the note.'
    }

    $deleteButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $confirmationWindow = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value 'Delete note' `
        -Deadline $deadline

    $yesButtonHandle = [NoteManagerTestNative]::GetDlgItem(
        [IntPtr] $confirmationWindow.Current.NativeWindowHandle,
        6)
    if ($yesButtonHandle -eq [IntPtr]::Zero) {
        throw 'Could not find the confirmation dialog Yes button.'
    }

    [void] [NoteManagerTestNative]::SendMessage(
        $yesButtonHandle,
        0x00F5,
        [IntPtr]::Zero,
        [IntPtr]::Zero)
    Wait-ForFileState -Path $createdNotePath -ShouldExist $false -Deadline $deadline
    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value '1 notes' `
        -Deadline $deadline)

    Write-Output 'PASS: Share placement/no-access-list, root-note creation, Delete cancellation, and confirmed deletion all behaved correctly.'
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
            'NoteManager.ToolbarTest.',
            [System.StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporaryVault -Recurse -Force
    }
}
