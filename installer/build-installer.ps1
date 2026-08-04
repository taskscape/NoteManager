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

function Resolve-LatestDoc2MdRelease {
    # Resolve the dependency during packaging so the generated installer is reproducible.
    $releaseApiUri = "https://api.github.com/repos/taskscape/DOC2MD/releases/latest"
    $headers = @{
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent" = "NoteManager-Installer-Build"
    }

    # An optional token raises GitHub API rate limits without becoming a build requirement.
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $headers["Authorization"] = "Bearer $($env:GITHUB_TOKEN)"
    }

    try {
        $release = Invoke-RestMethod `
            -Method Get `
            -Uri $releaseApiUri `
            -Headers $headers `
            -TimeoutSec 30
    }
    catch {
        throw "Cannot resolve the latest DOC2MD release from '$releaseApiUri': $($_.Exception.Message)"
    }

    if ($release.draft -or $release.prerelease) {
        throw "GitHub returned DOC2MD release '$($release.tag_name)', but it is not a published full release."
    }

    $tagMatch = [System.Text.RegularExpressions.Regex]::Match(
        [string]$release.tag_name,
        "^v(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$")
    if (-not $tagMatch.Success) {
        throw "The latest DOC2MD release tag '$($release.tag_name)' is not a supported numeric version."
    }

    $releaseVersion = $tagMatch.Groups["version"].Value
    $stableAssetName = "DOC2MD-win-x64-Setup.exe"
    $versionedAssetName = "DOC2MD-$releaseVersion-win-x64-Setup.exe"
    $assets = @($release.assets)
    $matchingAssets = @($assets | Where-Object { $_.name -eq $stableAssetName })
    if ($matchingAssets.Count -eq 0) {
        # Releases created before the stable-alias workflow remain valid dependencies.
        $matchingAssets = @($assets | Where-Object { $_.name -eq $versionedAssetName })
    }

    if ($matchingAssets.Count -ne 1) {
        throw "Release '$($release.tag_name)' must contain exactly one '$stableAssetName' or '$versionedAssetName' asset."
    }

    $asset = $matchingAssets[0]
    $digestMatch = [System.Text.RegularExpressions.Regex]::Match(
        [string]$asset.digest,
        "^sha256:(?<digest>[0-9a-fA-F]{64})$")
    if (-not $digestMatch.Success) {
        throw "Release asset '$($asset.name)' does not provide a valid GitHub SHA-256 digest."
    }

    try {
        $downloadUri = [System.Uri]$asset.browser_download_url
    }
    catch {
        throw "Release asset '$($asset.name)' has an invalid browser download URL."
    }

    $expectedPathPrefix = "/taskscape/DOC2MD/releases/download/"
    if ($downloadUri.Scheme -ne [System.Uri]::UriSchemeHttps -or
        $downloadUri.Host -ne "github.com" -or
        -not $downloadUri.AbsolutePath.StartsWith(
            $expectedPathPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release asset '$($asset.name)' does not use the expected GitHub HTTPS download location."
    }

    $versionComponents = @($releaseVersion -split "\." | ForEach-Object { [int]$_ })
    while ($versionComponents.Count -lt 4) {
        $versionComponents += 0
    }

    if ($versionComponents | Where-Object { $_ -lt 0 -or $_ -gt [UInt16]::MaxValue }) {
        throw "DOC2MD version '$releaseVersion' cannot be represented by Windows file-version components."
    }

    return [pscustomobject]@{
        Version = $releaseVersion
        VersionMajor = $versionComponents[0]
        VersionMinor = $versionComponents[1]
        VersionRevision = $versionComponents[2]
        VersionBuild = $versionComponents[3]
        InstallerName = [string]$asset.name
        InstallerUrl = $downloadUri.AbsoluteUri
        InstallerSha256 = $digestMatch.Groups["digest"].Value.ToLowerInvariant()
    }
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Cannot find the NoteManager project at '$projectPath'."
}

if (-not (Test-Path -LiteralPath $issFile -PathType Leaf)) {
    throw "Cannot find the Inno Setup definition at '$issFile'."
}

$doc2MdRelease = Resolve-LatestDoc2MdRelease

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
Write-Host "DOC2MD version  : $($doc2MdRelease.Version)"
Write-Host "DOC2MD asset    : $($doc2MdRelease.InstallerName)"
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

$requiredPluginFiles = @(
    "Plugins\GitIntegration\NoteManager.Plugin.GitIntegration.dll",
    "Plugins\DocumentConversion\NoteManager.Plugin.DocumentConversion.dll"
)
foreach ($relativePath in $requiredPluginFiles) {
    $pluginFile = Join-Path $publishDir $relativePath
    if (-not (Test-Path -LiteralPath $pluginFile -PathType Leaf)) {
        throw "Publish completed without required plugin file '$relativePath'."
    }
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
    "/DDoc2MdVersion=$($doc2MdRelease.Version)",
    "/DDoc2MdVersionMajor=$($doc2MdRelease.VersionMajor)",
    "/DDoc2MdVersionMinor=$($doc2MdRelease.VersionMinor)",
    "/DDoc2MdVersionRevision=$($doc2MdRelease.VersionRevision)",
    "/DDoc2MdVersionBuild=$($doc2MdRelease.VersionBuild)",
    "/DDoc2MdInstallerName=$($doc2MdRelease.InstallerName)",
    "/DDoc2MdInstallerUrl=$($doc2MdRelease.InstallerUrl)",
    "/DDoc2MdInstallerSha256=$($doc2MdRelease.InstallerSha256)",
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
