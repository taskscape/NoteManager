[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot "NoteManager.sln"
$serviceTests = Join-Path $PSScriptRoot "NoteManager.App.Tests\NoteManager.App.Tests.csproj"
$documentConversionPluginTests = Join-Path $PSScriptRoot "NoteManager.Plugin.DocumentConversion.Tests\NoteManager.Plugin.DocumentConversion.Tests.csproj"
$gitPluginTests = Join-Path $PSScriptRoot "NoteManager.Plugin.GitIntegration.Tests\NoteManager.Plugin.GitIntegration.Tests.csproj"
$resultDirectory = Join-Path $repositoryRoot "artifacts\test-results"

New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null

& dotnet build $solution `
    -c $Configuration `
    -m:1 `
    /p:BuildInParallel=false `
    /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    throw "The solution build failed with exit code $LASTEXITCODE."
}

& dotnet test $serviceTests `
    -c $Configuration `
    --no-build `
    --no-restore `
    --logger "console;verbosity=minimal" `
    --logger "trx;LogFileName=NoteManager.ServiceTests.$Configuration.trx" `
    --results-directory $resultDirectory `
    -m:1 `
    /p:BuildInParallel=false `
    /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& dotnet test $documentConversionPluginTests `
    -c $Configuration `
    --no-build `
    --no-restore `
    --logger "console;verbosity=minimal" `
    --logger "trx;LogFileName=NoteManager.DocumentConversionPluginTests.$Configuration.trx" `
    --results-directory $resultDirectory `
    -m:1 `
    /p:BuildInParallel=false `
    /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& dotnet test $gitPluginTests `
    -c $Configuration `
    --no-build `
    --no-restore `
    --logger "console;verbosity=minimal" `
    --logger "trx;LogFileName=NoteManager.GitPluginTests.$Configuration.trx" `
    --results-directory $resultDirectory `
    -m:1 `
    /p:BuildInParallel=false `
    /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& (Join-Path $PSScriptRoot "Run-UiTests.ps1") `
    -Configuration $Configuration
exit $LASTEXITCODE
