param(
    [string] $DotNetPath = 'dotnet',
    [string] $IsccPath = 'iscc',
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repoPath = Split-Path $PSScriptRoot -Parent
# A new folder prevents publish's overlay behavior from retaining removed SDK DLLs.
# Preserve prior candidates and failed build evidence instead of cleaning them.
$buildRoot = Join-Path $repoPath ('artifacts/native/' + [Guid]::NewGuid().ToString('N'))
$publishPath = Join-Path $buildRoot 'publish'
$installerPath = Join-Path $buildRoot 'installer'
$projectPath = Join-Path $repoPath 'src/SnuWatcher.Desktop/SnuWatcher.Desktop.csproj'
New-Item -ItemType Directory -Path $buildRoot | Out-Null
New-Item -ItemType Directory -Path $publishPath, $installerPath | Out-Null
Write-Output "Build output root: $buildRoot"

& $DotNetPath publish $projectPath -c Release -r win-x64 --self-contained true -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Native publish failed.' }

if (!$SkipInstaller) {
    & $IsccPath "/DSourceDir=$publishPath" "/DOutputDir=$installerPath" (Join-Path $repoPath 'installer/SnuWatcher.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Native installer compilation failed.' }
}

Write-Output "Published native feasibility folder: $publishPath"
if (!$SkipInstaller) { Write-Output "Installer output: $installerPath" }
