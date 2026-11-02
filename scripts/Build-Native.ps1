param(
    [string] $DotNetPath = 'dotnet',
    [string] $IsccPath = 'iscc',
    [string] $GitPath = 'git',
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoPath = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
. (Join-Path $PSScriptRoot 'Distribution.Metadata.ps1')
$config = Get-ReleaseConfiguration $repoPath
$source = Get-ReleaseSourceIdentity $repoPath $GitPath
$inputs = @(Get-ReleaseInputInventory $repoPath)
$sdk = (& $DotNetPath --version | Out-String).Trim()
$expectedSdk = (Get-Content -LiteralPath (Join-Path $repoPath 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ($LASTEXITCODE -ne 0 -or $sdk -cne $expectedSdk) { throw 'Native packaging requires the exact SDK recorded in global.json.' }
$versionProperties = @(Get-ReleaseVersionProperties $config $source.revision)
$ciRunUrl = $null
if ($env:GITHUB_REPOSITORY -ceq 'DuckFard/SNU-watcher' -and $env:GITHUB_SERVER_URL -ceq 'https://github.com' -and $env:GITHUB_RUN_ID -match '^[0-9]+$') {
    $ciRunUrl = 'https://github.com/DuckFard/SNU-watcher/actions/runs/' + $env:GITHUB_RUN_ID
}
# A new folder prevents publish's overlay behavior from retaining removed SDK DLLs.
# Preserve prior candidates and failed build evidence instead of cleaning them.
$buildRoot = Join-Path $repoPath ('artifacts/native/' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Assert-ReleaseChildPath $repoPath $buildRoot
$publishPath = Join-Path $buildRoot 'publish'
$installerPath = Join-Path $buildRoot 'installer'
$projectPath = Join-Path $repoPath 'src/SnuWatcher.Desktop/SnuWatcher.Desktop.csproj'
New-Item -ItemType Directory -Path $buildRoot | Out-Null
New-Item -ItemType Directory -Path $publishPath, $installerPath | Out-Null
Write-Output "Build output root: $buildRoot"

& $DotNetPath restore $projectPath --locked-mode -r win-x64 '-p:SelfContained=true' @versionProperties
if ($LASTEXITCODE -ne 0) { throw 'Native locked restore failed.' }
& $DotNetPath publish $projectPath -c Release -r win-x64 --self-contained true --no-restore -o $publishPath @versionProperties
if ($LASTEXITCODE -ne 0) { throw 'Native publish failed.' }
Assert-ReleaseRuntimePacks $publishPath $config.runtimeFrameworkVersion
Write-DistributionMetadata $repoPath $publishPath $config $source
$manifest = [ordered]@{ schemaVersion = 1; version = $config.version; installerVersion = $config.installerVersion;
    runtimeFrameworkVersion = $config.runtimeFrameworkVersion; source = $source; dotnetSdk = $sdk; innoSetup = $null;
    rid = 'win-x64'; configuration = 'Release'; signing = 'unsigned'; builtAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ');
    repository = 'https://github.com/DuckFard/SNU-watcher'; inputs = $inputs;
    operatingSystem = [Environment]::OSVersion.Version.ToString(); ciRunUrl = $ciRunUrl;
    recipe = @(Get-ReleaseBuildRecipe);
    versionProperties = $versionProperties;
    payload = @(Get-ReleaseInventory $publishPath); installer = $null }

if (!$SkipInstaller) {
    # ISCC's PE version can be 0.0.0.0; its engine banner reports the real version.
    $compilerOutput = @(& $IsccPath "/DSourceDir=$publishPath" "/DOutputDir=$installerPath" "/DReleaseVersion=$($config.version)" "/DInstallerVersion=$($config.installerVersion)" (Join-Path $repoPath 'installer/SnuWatcher.iss') 2>&1)
    $compilerExit = $LASTEXITCODE
    $compilerOutput | ForEach-Object { Write-Output $_ }
    if ($compilerExit -ne 0) { throw 'Native installer compilation failed.' }
    if (($compilerOutput | Out-String) -notmatch '(?m)^Compiler engine version: Inno Setup (6\.[0-9]+\.[0-9]+)\r?$') {
        throw 'Cannot identify a supported Inno Setup 6 compiler version.'
    }
    $manifest.innoSetup = $Matches[1]
    $name = 'SnuCourseWatcher-' + $config.version + '-windows-x64-Setup.exe'
    $installerFile = Join-Path $installerPath $name
    $manifest.installer = @{ name = $name; sha256 = (Get-FileHash -LiteralPath $installerFile -Algorithm SHA256).Hash.ToLowerInvariant() }
}
Assert-ReleaseInputInventory $repoPath $inputs
$endSource = Get-ReleaseSourceIdentity $repoPath $GitPath
if ($source.revision -cne $endSource.revision -or $source.dirty -ne $endSource.dirty) {
    throw 'Source identity changed during packaging; repeat the build from a stable tree.'
}
Write-ReleaseJson (Join-Path $buildRoot 'build-manifest.json') $manifest

Write-Output "Published unsigned Windows preview folder: $publishPath"
if (!$SkipInstaller) { Write-Output "Installer output: $installerPath" }
