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
$temporaryVault = Join-Path $temporaryBase "NoteManager.InfostackerTest.$([Guid]::NewGuid().ToString('N'))"

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    & dotnet build $projectPath --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "NoteManager build failed with exit code $LASTEXITCODE."
    }
}

[void] (New-Item -ItemType Directory -Path $temporaryVault)
$attachmentFolder = Join-Path $temporaryVault 'assets'
[void] (New-Item -ItemType Directory -Path $attachmentFolder)
Set-Content `
    -LiteralPath (Join-Path $temporaryVault 'Published note.md') `
    -Value "# Published body`n`n![[assets/sample.txt]]" `
    -NoNewline
Set-Content `
    -LiteralPath (Join-Path $attachmentFolder 'sample.txt') `
    -Value 'embedded attachment payload' `
    -NoNewline

$portProbe = [System.Net.Sockets.TcpListener]::new(
    [System.Net.IPAddress]::Loopback,
    0)
$portProbe.Start()
$port = ([System.Net.IPEndPoint] $portProbe.LocalEndpoint).Port
$portProbe.Stop()

$baseUrl = "http://127.0.0.1:$port/"
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($baseUrl)
$listener.Start()
$requestTask = $listener.GetContextAsync()

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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

$startInfo = [System.Diagnostics.ProcessStartInfo]::new($executablePath)
$startInfo.UseShellExecute = $false
[void] $startInfo.ArgumentList.Add('--folder')
[void] $startInfo.ArgumentList.Add($temporaryVault)
[void] $startInfo.ArgumentList.Add('--infostacker-base-url')
[void] $startInfo.ArgumentList.Add($baseUrl)
$process = [System.Diagnostics.Process]::Start($startInfo)

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
    $publishButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $remainingMilliseconds = [Math]::Max(
        1,
        [int] ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
    if (-not $requestTask.Wait($remainingMilliseconds)) {
        throw 'Timed out waiting for the Infostacker publish request.'
    }

    $context = $requestTask.GetAwaiter().GetResult()
    try {
        $reader = [System.IO.StreamReader]::new(
            $context.Request.InputStream,
            $context.Request.ContentEncoding)
        try {
            $requestBody = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        if ($context.Request.HttpMethod -ne 'POST') {
            throw "Expected POST, received $($context.Request.HttpMethod)."
        }

        if ($context.Request.RawUrl -ne '/sharing/uploadmarkdownwithfiles') {
            throw "Unexpected publish endpoint: $($context.Request.RawUrl)"
        }

        if ($context.Request.ContentType -notlike 'multipart/form-data*') {
            throw "Expected multipart/form-data, received $($context.Request.ContentType)."
        }

        $contractMatches =
            ($requestBody -match 'name="?markdown"?') -and
            ($requestBody -match "Published note`n`n# Published body") -and
            ($requestBody -match 'name="?files"?') -and
            ($requestBody -match 'filename="?sample\.txt"?') -and
            ($requestBody -match 'embedded attachment payload')
        if (-not $contractMatches) {
            throw 'The multipart request did not match the InfostackerPlugin publishing contract.'
        }

        $responseBytes = [System.Text.Encoding]::UTF8.GetBytes(
            '{"id":"public-note-123","secret":"secret-456"}')
        $context.Response.StatusCode = 200
        $context.Response.ContentType = 'application/json'
        $context.Response.ContentLength64 = $responseBytes.Length
        $context.Response.OutputStream.Write(
            $responseBytes,
            0,
            $responseBytes.Length)
        $context.Response.OutputStream.Close()
    }
    finally {
        $context.Response.Close()
    }

    [void] (Wait-ForElement `
        -ProcessId $process.Id `
        -Property ([System.Windows.Automation.AutomationElement]::NameProperty) `
        -Value 'Public link copied to the clipboard.' `
        -Deadline $deadline)

    $expectedUrl = "${baseUrl}sharing/public-note-123"
    do {
        $clipboardText = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($clipboardText -eq $expectedUrl) {
            break
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($clipboardText -ne $expectedUrl) {
        throw "Expected clipboard URL '$expectedUrl', received '$clipboardText'."
    }

    Write-Output 'PASS: Infostacker multipart publishing and public-URL clipboard copy match the plugin contract.'
}
finally {
    if (-not $process.HasExited) {
        [void] $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id
        }
    }

    $process.Dispose()
    $listener.Stop()
    $listener.Close()

    $resolvedTemporaryVault = [System.IO.Path]::GetFullPath($temporaryVault)
    if ($resolvedTemporaryVault.StartsWith(
            $temporaryBase,
            [System.StringComparison]::OrdinalIgnoreCase) `
        -and (Split-Path -Leaf $resolvedTemporaryVault).StartsWith(
            'NoteManager.InfostackerTest.',
            [System.StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporaryVault -Recurse -Force
    }
}
