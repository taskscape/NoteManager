[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [ValidateRange(5, 120)]
    [int] $TimeoutSeconds = 30,

    [string] $ScreenshotPath,

    [string] $ComparisonPath,

    [string] $SourceScreenshot =
        'C:\Users\TASKSC~1\AppData\Local\Temp\codex-clipboard-0c998ffd-96df-449e-ae47-9e1a8a690daa.png'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\NoteManager.App\NoteManager.App.csproj'
$executablePath = Join-Path $repositoryRoot "src\NoteManager.App\bin\$Configuration\net8.0-windows10.0.19041.0\NoteManager.exe"
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryVault = Join-Path $temporaryBase "NoteManager.TagTest.$([Guid]::NewGuid().ToString('N'))"
$targetNotePath = Join-Path $temporaryVault 'Current note.md'

if ([string]::IsNullOrWhiteSpace($ScreenshotPath)) {
    $ScreenshotPath = Join-Path $repositoryRoot 'artifacts\design-qa\assign-tags-dialog.png'
}

if ([string]::IsNullOrWhiteSpace($ComparisonPath)) {
    $ComparisonPath = Join-Path $repositoryRoot 'artifacts\design-qa\assign-tags-comparison.png'
}

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    & dotnet build $projectPath --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "NoteManager build failed with exit code $LASTEXITCODE."
    }
}

[void] (New-Item -ItemType Directory -Path $temporaryVault)
for ($index = 0; $index -lt 55; $index++) {
    $notePath = Join-Path $temporaryVault ('Repository tag {0:00}.md' -f $index)
    $tagName = 'tag-{0:00}' -f $index
    [System.IO.File]::WriteAllText(
        $notePath,
        "Repository note $index`r`n`r`ntags:`r`n  - $tagName")
    [System.IO.File]::SetLastWriteTimeUtc(
        $notePath,
        [DateTime]::UtcNow.AddMinutes(-($index + 2)))
}

[System.IO.File]::WriteAllText(
    $targetNotePath,
    @'
# Current note

Text before metadata.

tags:
  - FIRST
  - Shared

Text between metadata blocks.

TAGS:
  - SECOND
  - SHARED

Closing text.
'@)
[System.IO.File]::SetLastWriteTimeUtc($targetNotePath, [DateTime]::UtcNow)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

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

function Wait-ForElementToClose {
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
        $found = $false
        foreach ($window in (Get-ProcessWindows -ProcessId $ProcessId)) {
            if ($window.GetCurrentPropertyValue($Property) -eq $Value `
                -or $null -ne $window.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    $condition)) {
                $found = $true
                break
            }
        }

        if (-not $found) {
            return
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Timed out waiting for UI element to close: $Value"
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement] $Element)

    $Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Set-ElementValue {
    param(
        [System.Windows.Automation.AutomationElement] $Element,
        [string] $Value
    )

    $Element.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}

function Save-WindowScreenshot {
    param(
        [System.Windows.Automation.AutomationElement] $Window,
        [string] $Path
    )

    $bounds = $Window.Current.BoundingRectangle
    $width = [Math]::Max(1, [int] [Math]::Ceiling($bounds.Width))
    $height = [Math]::Max(1, [int] [Math]::Ceiling($bounds.Height))
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [void] (New-Item -ItemType Directory -Path $directory)
    }

    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen(
                [int] $bounds.Left,
                [int] $bounds.Top,
                0,
                0,
                [System.Drawing.Size]::new($width, $height))
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Save-Comparison {
    param(
        [string] $ReferencePath,
        [string] $ImplementationPath,
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $ReferencePath -PathType Leaf)) {
        throw "Source screenshot was not found: $ReferencePath"
    }

    $reference = [System.Drawing.Image]::FromFile($ReferencePath)
    $implementation = [System.Drawing.Image]::FromFile($ImplementationPath)
    try {
        $gap = 20
        $width = $reference.Width + $gap + $implementation.Width
        $height = [Math]::Max($reference.Height, $implementation.Height)
        $comparison = [System.Drawing.Bitmap]::new($width, $height)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($comparison)
            try {
                $graphics.Clear([System.Drawing.Color]::White)
                $graphics.DrawImageUnscaled($reference, 0, 0)
                $graphics.DrawImageUnscaled(
                    $implementation,
                    $reference.Width + $gap,
                    0)
            }
            finally {
                $graphics.Dispose()
            }

            $directory = Split-Path -Parent $Path
            if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
                [void] (New-Item -ItemType Directory -Path $directory)
            }

            $comparison.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $comparison.Dispose()
        }
    }
    finally {
        $reference.Dispose()
        $implementation.Dispose()
    }
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
        -Value '56 notes' `
        -Deadline $deadline)

    $tagsButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'TagsToolbarButton' `
        -Deadline $deadline
    Invoke-Element $tagsButton

    $dialog = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'AssignTagsWindow' `
        -Deadline $deadline
    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value 'Assign tags to note: "Current note.md"' `
        -Deadline $deadline)
    Start-Sleep -Milliseconds 350
    Save-WindowScreenshot -Window $dialog -Path $ScreenshotPath

    $uppercaseCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'FIRST')
    if ($null -ne $dialog.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $uppercaseCondition)) {
        throw 'The dialog displayed a mixed-case tag instead of lowercase.'
    }

    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value 'first' `
        -Deadline $deadline)

    $allTagsButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'AllTagsButton' `
        -Deadline $deadline
    Invoke-Element $allTagsButton
    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value 'tag-54' `
        -Deadline $deadline)

    $secondTag = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value 'second' `
        -Deadline $deadline
    $secondTag.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern).Toggle()

    $newTagsTextBox = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'NewTagsTextBox' `
        -Deadline $deadline
    $addTagsButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'AddTagsButton' `
        -Deadline $deadline

    Set-ElementValue $newTagsTextBox 'bad_tag'
    Invoke-Element $addTagsButton
    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'TagValidationMessage' `
        -Deadline $deadline)

    Set-ElementValue $newTagsTextBox 'New.Tag added-tag'
    Invoke-Element $addTagsButton
    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value 'new.tag' `
        -Deadline $deadline)

    $okButton = Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'AssignTagsOkButton' `
        -Deadline $deadline
    Invoke-Element $okButton
    Wait-ForElementToClose `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) `
        -Value 'AssignTagsWindow' `
        -Deadline $deadline

    $updatedMarkdown = [System.IO.File]::ReadAllText($targetNotePath)
    $tagHeaderMatches = [regex]::Matches(
        $updatedMarkdown,
        '(?im)^[ \t]*tags[ \t]*:')
    if ($tagHeaderMatches.Count -ne 1) {
        throw "Expected one merged tag block, found $($tagHeaderMatches.Count)."
    }

    foreach ($expectedLine in @(
            '  - added-tag',
            '  - first',
            '  - new.tag',
            '  - shared')) {
        if (-not $updatedMarkdown.Contains(
                $expectedLine,
                [System.StringComparison]::Ordinal)) {
            throw "The merged block is missing: $expectedLine"
        }
    }

    if ($updatedMarkdown.Contains(
            '  - second',
            [System.StringComparison]::OrdinalIgnoreCase) `
        -or $updatedMarkdown.Contains(
            '  - FIRST',
            [System.StringComparison]::Ordinal)) {
        throw 'The removed tag or original mixed-case spelling remained in the note.'
    }

    Save-Comparison `
        -ReferencePath $SourceScreenshot `
        -ImplementationPath $ScreenshotPath `
        -Path $ComparisonPath

    $captured = [System.Drawing.Image]::FromFile($ScreenshotPath)
    try {
        $passMessage =
            'PASS: Tags dialog listed recent/all lowercase tags, rejected invalid input, ' +
            'added multiple tags, removed a tag, merged all blocks, saved immediately, ' +
            "and captured $($captured.Width) x $($captured.Height) px visual evidence."
        Write-Output $passMessage
    }
    finally {
        $captured.Dispose()
    }
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
            'NoteManager.TagTest.',
            [System.StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporaryVault -Recurse -Force
    }
}
