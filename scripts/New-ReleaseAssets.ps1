param(
    [Parameter(Mandatory)] [string] $BuildRoot,
    [string] $GitPath = 'git'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoPath = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$BuildRoot = Assert-ReleaseChildPath (Join-Path $repoPath 'artifacts/native') $BuildRoot
$null = Assert-ReleaseChildPath $repoPath $BuildRoot
$releasePath = Join-Path $BuildRoot 'release'
if (Test-Path -LiteralPath $releasePath) { throw 'Release assets already exist; never overwrite a reviewed candidate.' }
$config = Get-ReleaseConfiguration $repoPath
$source = Get-ReleaseSourceIdentity $repoPath $GitPath
$manifest = Get-Content -LiteralPath (Join-Path $BuildRoot 'build-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.source.dirty -or $source.dirty -or $manifest.source.revision -cne $source.revision) {
    throw 'Release assets require the exact committed, clean source that produced this build.'
}
foreach ($field in 'version', 'installerVersion', 'runtimeFrameworkVersion') {
    if ($manifest.$field -cne $config.$field) { throw 'Release configuration differs from the built candidate.' }
}
$sdk = (Get-Content -LiteralPath (Join-Path $repoPath 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ($manifest.dotnetSdk -cne $sdk -or $manifest.rid -cne 'win-x64' -or $manifest.configuration -cne 'Release' -or
    $manifest.signing -cne 'unsigned' -or $manifest.innoSetup -notmatch '^6\.[0-9]+\.[0-9]+$') {
    throw 'Candidate toolchain, platform or signing identity differs from this preview contract.'
}
Assert-ReleaseInputInventory $repoPath $manifest.inputs
Assert-PublicBuildFields $manifest $config $source.revision
$publish = Join-Path $BuildRoot 'publish'
Assert-ReleaseInventory $publish $manifest.payload
foreach ($required in 'LICENSE', 'DISTRIBUTION-LICENSES.txt', 'sbom.cdx.json', 'SnuWatcher.Desktop.exe') {
    if (!(Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) { throw ('Missing required distribution file: ' + $required) }
}
$installerName = 'SnuCourseWatcher-' + $config.version + '-windows-x64-Setup.exe'
$installer = Join-Path $BuildRoot ('installer/' + $installerName)
if ($manifest.installer.name -cne $installerName -or
    (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.installer.sha256) {
    throw 'Installer bytes differ from the recorded build.'
}
# Stage only a closed public asset set. No logs, profile databases, raw exports or
# environment variables enter these files. Publication is a separate owner action.
New-Item -ItemType Directory -Path $releasePath | Out-Null
Copy-Item -LiteralPath $installer -Destination $releasePath
Copy-Item -LiteralPath (Join-Path $publish 'DISTRIBUTION-LICENSES.txt') -Destination (Join-Path $releasePath 'THIRD-PARTY-NOTICES.txt')
Copy-Item -LiteralPath (Join-Path $publish 'sbom.cdx.json') -Destination $releasePath
$publicPayload = @(Get-ReleaseInventory $publish)
Write-ReleaseJson (Join-Path $releasePath 'PAYLOAD-MANIFEST.json') @{ schemaVersion = 1; files = $publicPayload }
$provenance = [ordered]@{ schemaVersion = 1; claim = 'Unsigned descriptive build metadata; not a signed attestation or byte-reproducibility guarantee.';
    repository = 'https://github.com/DuckFard/SNU-watcher'; version = $manifest.version; installerVersion = $manifest.installerVersion;
    source = @{ revision = $source.revision; dirty = $false }; dotnetSdk = $manifest.dotnetSdk; runtimeFrameworkVersion = $manifest.runtimeFrameworkVersion;
    innoSetup = $manifest.innoSetup; rid = $manifest.rid; configuration = $manifest.configuration;
    operatingSystem = $manifest.operatingSystem; ciRunUrl = $manifest.ciRunUrl; recipe = @(Get-ReleaseBuildRecipe); versionProperties = @(Get-ReleaseVersionProperties $config $source.revision);
    signing = 'unsigned'; builtAtUtc = $manifest.builtAtUtc; inputs = @(Get-ReleaseInputInventory $repoPath);
    installer = @{ name = $installerName; sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() } }
Write-ReleaseJson (Join-Path $releasePath 'BUILD-PROVENANCE.json') $provenance
$lines = @(Get-ReleaseInventory $releasePath | Sort-Object path | ForEach-Object { $_.sha256 + '  ' + $_.path })
[IO.File]::WriteAllText((Join-Path $releasePath 'SHA256SUMS.txt'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Assert-ReleaseAssetSet $releasePath $config $source.revision
Write-Output "Unsigned preview release assets: $releasePath"
