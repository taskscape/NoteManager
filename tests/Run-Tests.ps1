[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("PullRequest", "Integration", "EndToEnd", "Full")]
    [string]$Tier = "PullRequest"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot "NoteManager.sln"
$resultDirectory = Join-Path $repositoryRoot "artifacts/test-results"
$applicationTests = Join-Path $PSScriptRoot (
    "NoteManager.App.Tests/NoteManager.App.Tests.csproj")
$documentConversionTests = Join-Path $PSScriptRoot (
    "NoteManager.Plugin.DocumentConversion.Tests/NoteManager.Plugin.DocumentConversion.Tests.csproj")
$gitIntegrationTests = Join-Path $PSScriptRoot (
    "NoteManager.Plugin.GitIntegration.Tests/NoteManager.Plugin.GitIntegration.Tests.csproj")

if ($Tier -in @("Integration", "Full") -and
    -not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) {
    throw "The $Tier tier requires Git to be available on PATH."
}

if ($Tier -eq "EndToEnd") {
    & (Join-Path $PSScriptRoot "Run-UiTests.ps1") `
        -Configuration $Configuration
    exit $LASTEXITCODE
}

if ($Tier -eq "Full") {
    & (Join-Path $PSScriptRoot "Run-AllTests.ps1") `
        -Configuration $Configuration
    exit $LASTEXITCODE
}

$filter = if ($Tier -eq "PullRequest") {
    "Category=Unit|Category=Contract|Category=Database"
}
else {
    "Category=Integration"
}
$testProjects = if ($Tier -eq "PullRequest") {
    @($applicationTests, $documentConversionTests)
}
else {
    @($documentConversionTests, $gitIntegrationTests)
}

New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null

& dotnet build $solution `
    -c $Configuration `
    -m:1 `
    /p:BuildInParallel=false `
    /p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    throw "The solution build failed with exit code $LASTEXITCODE."
}

foreach ($testProject in $testProjects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($testProject)

    & dotnet test $testProject `
        -c $Configuration `
        --no-build `
        --no-restore `
        --filter $filter `
        --logger "console;verbosity=minimal" `
        --logger "trx;LogFileName=$projectName.$Tier.$Configuration.trx" `
        --results-directory $resultDirectory `
        -m:1 `
        /p:BuildInParallel=false `
        /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

exit 0
