[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$Filter = "Category=UI",

    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $PSScriptRoot "NoteManager.Desktop.UiTests\NoteManager.Desktop.UiTests.csproj"
$applicationPath = Join-Path $repositoryRoot (
    "src\NoteManager.Desktop\bin\{0}\net10.0\NoteManager.exe" -f
        $Configuration)
$artifactRoot = Join-Path $repositoryRoot "artifacts\ui-tests"
$resultDirectory = Join-Path $artifactRoot "results"

New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null

if (-not $NoBuild) {
    & dotnet build $testProject `
        -c $Configuration `
        -m:1 `
        /p:BuildInParallel=false `
        /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "The UI test build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw "The NoteManager executable was not found at '$applicationPath'. Build the requested configuration first."
}

$previousConfiguration = [Environment]::GetEnvironmentVariable(
    "NOTEMANAGER_UI_TEST_CONFIGURATION",
    "Process")
$previousApplication = [Environment]::GetEnvironmentVariable(
    "NOTEMANAGER_UI_TEST_APP",
    "Process")
$previousArtifacts = [Environment]::GetEnvironmentVariable(
    "NOTEMANAGER_UI_TEST_ARTIFACTS",
    "Process")

try {
    $env:NOTEMANAGER_UI_TEST_CONFIGURATION = $Configuration
    $env:NOTEMANAGER_UI_TEST_APP = $applicationPath
    $env:NOTEMANAGER_UI_TEST_ARTIFACTS = $artifactRoot

    & dotnet test $testProject `
        -c $Configuration `
        --no-build `
        --no-restore `
        --filter $Filter `
        --logger "console;verbosity=minimal" `
        --logger "trx;LogFileName=NoteManager.UiTests.$Configuration.trx" `
        --results-directory $resultDirectory `
        -m:1 `
        /p:BuildInParallel=false `
        /p:UseSharedCompilation=false
    $testExitCode = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable(
        "NOTEMANAGER_UI_TEST_CONFIGURATION",
        $previousConfiguration,
        "Process")
    [Environment]::SetEnvironmentVariable(
        "NOTEMANAGER_UI_TEST_APP",
        $previousApplication,
        "Process")
    [Environment]::SetEnvironmentVariable(
        "NOTEMANAGER_UI_TEST_ARTIFACTS",
        $previousArtifacts,
        "Process")
}

exit $testExitCode
