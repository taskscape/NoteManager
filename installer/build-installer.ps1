<#
.SYNOPSIS
    Publishes NoteManager and builds an Inno Setup installer.

.DESCRIPTION
    Creates an optimized, self-contained Windows publish of the Avalonia application
    and compiles it into a versioned Inno Setup installer.

    Startup-oriented publish settings are enabled by default:
      - Release configuration
      - self-contained Windows runtime
      - ReadyToRun with composite compilation
      - tiered compilation disabled
      - no trimming and no single-file extraction

.PARAMETER Configuration
    Build configuration. Release is the default and recommended value.

.PARAMETER Runtime
    Windows runtime identifier. Supported values are win-x64 and win-arm64.

.PARAMETER Version
    Numeric product version with three or four components, for example 1.2.0.

.PARAMETER IsccPath
    Full path to ISCC.exe. When omitted, PATH and common Inno Setup locations
    are searched automatically.

.EXAMPLE
    .\build-installer.ps1

.EXAMPLE
    .\build-installer.ps1 -Version 1.2.0 -Runtime win-x64
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [ValidatePattern("^\d+\.\d+\.\d+(?:\.\d+)?$")]
    [string]$Version = "1.0.0",

    [string]$IsccPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir ".."))
$projectPath = Join-Path $repoRoot "src\NoteManager.Desktop\NoteManager.Desktop.csproj"
$publishDir = Join-Path $scriptDir "publish"
$outputDir = Join-Path $scriptDir "Output"
$issFile = Join-Path $scriptDir "NoteManager.iss"

function Reset-GeneratedDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$AllowedRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($AllowedRoot)
    $rootPrefix = $fullRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith(
        $rootPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset generated directory outside '$fullRoot': '$fullPath'."
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Resolve-Iscc {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolvedExplicitPath = [System.IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $resolvedExplicitPath -PathType Leaf)) {
            throw "The supplied Inno Setup compiler does not exist: '$resolvedExplicitPath'."
        }

        return $resolvedExplicitPath
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles} "Inno Setup\ISCC.exe"),
        (Join-Path ${env:ProgramFiles} "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles} "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $null
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Cannot find the NoteManager project at '$projectPath'."
}

if (-not (Test-Path -LiteralPath $issFile -PathType Leaf)) {
    throw "Cannot find the Inno Setup definition at '$issFile'."
}

$versionParts = @($Version -split "\.")
while ($versionParts.Count -lt 4) {
    $versionParts += "0"
}
$versionInfoVersion = $versionParts[0..3] -join "."

$installerArchitecture = switch ($Runtime) {
    "win-x64" { "x64compatible" }
    "win-arm64" { "arm64" }
    default { throw "Unsupported runtime '$Runtime'." }
}

$outputBaseFilename = "NoteManager-$Version-$Runtime-Setup"
$expectedInstallerPath = Join-Path $outputDir "$outputBaseFilename.exe"

Write-Host "Repository root : $repoRoot"
Write-Host "Project         : $projectPath"
Write-Host "Runtime         : $Runtime"
Write-Host "Version         : $Version"
Write-Host "Publish folder  : $publishDir"
Write-Host ""

Reset-GeneratedDirectory -Path $publishDir -AllowedRoot $scriptDir
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

Write-Host "Publishing the startup-optimized application ($Configuration)..." -ForegroundColor Cyan
$publishArgs = @(
    "publish", $projectPath,
    "--nologo",
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $publishDir,
    "-p:Version=$Version",
    "-p:FileVersion=$versionInfoVersion",
    "-p:AssemblyVersion=$versionInfoVersion",
    "-p:UseAppHost=true",
    "-p:PublishReadyToRun=true",
    "-p:PublishReadyToRunComposite=true",
    "-p:PublishReadyToRunShowWarnings=true",
    "-p:TieredCompilation=false",
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-p:ContinuousIntegrationBuild=true",
    "-p:Deterministic=true"
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedExecutable = Join-Path $publishDir "NoteManager.exe"
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Publish completed without producing '$publishedExecutable'."
}

$iscc = Resolve-Iscc -ExplicitPath $IsccPath
if (-not $iscc) {
    throw "ISCC.exe was not found. Install Inno Setup 6 or 7, add ISCC.exe to PATH, or pass -IsccPath."
}

Write-Host ""
Write-Host "Compiling installer with: $iscc" -ForegroundColor Cyan
$compilerArgs = @(
    "/DPublishDir=$publishDir",
    "/DOutputDir=$outputDir",
    "/DAppVersion=$Version",
    "/DVersionInfoVersion=$versionInfoVersion",
    "/DInstallerArchitecture=$installerArchitecture",
    "/DOutputBaseFilename=$outputBaseFilename",
    $issFile
)

& $iscc @compilerArgs
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $expectedInstallerPath -PathType Leaf)) {
    throw "Inno Setup completed without producing '$expectedInstallerPath'."
}

$installer = Get-Item -LiteralPath $expectedInstallerPath
$sizeInMb = [Math]::Round($installer.Length / 1MB, 1)

Write-Host ""
Write-Host "Installer created successfully." -ForegroundColor Green
Write-Host "Artifact : $($installer.FullName)"
Write-Host "Size     : $sizeInMb MB"
