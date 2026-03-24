[CmdletBinding()]
param(
    [string]$ProjectPath = "NetImage.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $true,
    [string]$OutputRoot = "artifacts\release",
    [switch]$UploadToGitHubRelease
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory
Set-Location $repoRoot

$resolvedProjectPath = Join-Path $repoRoot $ProjectPath
if (-not (Test-Path $resolvedProjectPath)) {
    throw "Project file not found: $resolvedProjectPath"
}

[xml]$projectXml = Get-Content $resolvedProjectPath
$version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not read <Version> from $ProjectPath"
}

$projectName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedProjectPath)
$releaseName = "$projectName-$version-$Runtime"
$outputRootPath = Join-Path $repoRoot $OutputRoot
$publishDirectory = Join-Path $outputRootPath $releaseName
$zipPath = Join-Path $outputRootPath "$releaseName.zip"
$tagName = "v$version"

if (Test-Path $publishDirectory) {
    Remove-Item $publishDirectory -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

$publishArguments = @(
    "publish",
    $resolvedProjectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $SelfContained.ToString().ToLowerInvariant(),
    "-o", $publishDirectory
)

Write-Host "Publishing $projectName $version for $Runtime..."
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host "Creating release archive $zipPath..."
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishDirectory,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

$hash = Get-FileHash $zipPath -Algorithm SHA256

Write-Host ""
Write-Host "Release package created successfully."
Write-Host "Version : $version"
Write-Host "Folder  : $publishDirectory"
Write-Host "Zip     : $zipPath"
Write-Host "SHA256  : $($hash.Hash)"

if ($UploadToGitHubRelease) {
    $ghCommand = Get-Command gh.exe -ErrorAction SilentlyContinue
    if ($null -eq $ghCommand) {
        $defaultGhPath = "C:\Program Files\GitHub CLI\gh.exe"
        if (Test-Path $defaultGhPath) {
            $ghPath = $defaultGhPath
        }
        else {
            throw "gh.exe was not found in PATH or in the default GitHub CLI install location."
        }
    }
    else {
        $ghPath = $ghCommand.Source
    }

    Write-Host ""
    Write-Host "Uploading archive to GitHub release $tagName..."
    & $ghPath release upload $tagName $zipPath --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "gh release upload failed with exit code $LASTEXITCODE"
    }
}
