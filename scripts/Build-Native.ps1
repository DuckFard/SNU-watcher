param(
    [string] $DotNetPath = 'dotnet',
    [string] $IsccPath = 'iscc',
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repoPath = Split-Path $PSScriptRoot -Parent
$publishPath = Join-Path $repoPath 'artifacts/native/publish'
$installerPath = Join-Path $repoPath 'artifacts/native/installer'
$projectPath = Join-Path $repoPath 'src/SnuWatcher.Desktop/SnuWatcher.Desktop.csproj'

& $DotNetPath publish $projectPath -c Release -r win-x64 --self-contained true -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Native publish failed.' }

if (!$SkipInstaller) {
    & $IsccPath "/DSourceDir=$publishPath" "/DOutputDir=$installerPath" (Join-Path $repoPath 'installer/SnuWatcher.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Native installer compilation failed.' }
}

Write-Output "Published native feasibility folder: $publishPath"
if (!$SkipInstaller) { Write-Output "Installer output: $installerPath" }
