$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repoRoot 'scripts/Release.Common.ps1')
. (Join-Path $repoRoot 'scripts/Distribution.Metadata.ps1')

function Assert-Release([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}

function Assert-Rejected([scriptblock] $Action, [string] $Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Release $rejected $Message
}

$fixtureRoot = Join-Path $repoRoot ('artifacts/release-assets-tests/' + [Guid]::NewGuid().ToString('N'))
$fixtureRepo = Join-Path $fixtureRoot 'repository with spaces'
$buildRoot = Join-Path $fixtureRepo 'artifacts/native/test-build'
$publish = Join-Path $buildRoot 'publish'
$cache = Join-Path $fixtureRoot 'package cache'
New-Item -ItemType Directory -Path $publish, $cache, (Join-Path $fixtureRepo 'scripts'),
    (Join-Path $fixtureRepo 'installer'), (Join-Path $fixtureRepo 'src/SnuWatcher.Desktop/obj') | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/Release.Common.ps1'),
    (Join-Path $repoRoot 'scripts/Distribution.Metadata.ps1'),
    (Join-Path $repoRoot 'scripts/New-ReleaseAssets.ps1') -Destination (Join-Path $fixtureRepo 'scripts')
$configuration = [ordered]@{ schemaVersion = 1; version = '0.1.0-preview.1'; installerVersion = '0.1.0.0'; runtimeFrameworkVersion = '10.0.14' }
Write-ReleaseJson (Join-Path $fixtureRepo 'release.json') $configuration
Write-ReleaseJson (Join-Path $fixtureRepo 'global.json') @{ sdk = @{ version = '10.0.403' } }
Set-Content -LiteralPath (Join-Path $fixtureRepo 'LICENSE') 'MIT fixture license. Copyright Fixture.'
Set-Content -LiteralPath (Join-Path $publish 'SnuWatcher.Desktop.exe') 'Fixture executable bytes.'
Set-Content -LiteralPath (Join-Path $publish 'Example.dll') 'Fixture library bytes.'
Set-Content -LiteralPath (Join-Path $fixtureRepo 'installer/SnuWatcher.iss') '; Fixture installer input.'

$config = Get-ReleaseConfiguration $fixtureRepo
Assert-Release ($config.version -eq '0.1.0-preview.1' -and $config.installerVersion -eq '0.1.0.0') 'The prerelease display and numeric versions were conflated.'
foreach ($invalid in @('0.1.0-preview.01', '../bad', '0.1.0";bad', '0.1', '01.1.0', "0.1.0-preview.1`n")) {
    Write-ReleaseJson (Join-Path $fixtureRepo 'release.json') ([ordered]@{ schemaVersion = 1; version = $invalid; installerVersion = '0.1.0.0'; runtimeFrameworkVersion = '10.0.14' })
    Assert-Rejected { Get-ReleaseConfiguration $fixtureRepo } ('Unsafe version accepted: ' + $invalid)
}
Write-ReleaseJson (Join-Path $fixtureRepo 'release.json') $configuration

# One ordinary dependency and one self-contained runtime: the lock alone is insufficient.
$packageDefinitions = @(
    @{ Name = 'Example.Library'; Version = '1.2.3'; Type = 'package'; License = 'MIT'; Text = 'MIT library license. Copyright Example.' },
    @{ Name = 'Microsoft.NETCore.App.Runtime.win-x64'; Version = '10.0.14'; Type = 'runtimepack'; License = 'MIT'; Text = 'MIT runtime license. Copyright Runtime.' }
)
foreach ($package in $packageDefinitions) {
    $folder = Join-Path $cache ($package.Name.ToLowerInvariant() + '/' + $package.Version)
    New-Item -ItemType Directory -Path $folder | Out-Null
    Set-Content -LiteralPath (Join-Path $folder ($package.Name + '.nuspec')) ('<package><metadata><id>' + $package.Name + '</id><version>' + $package.Version + '</version><license type="expression">MIT</license><authors>Fixture</authors></metadata></package>')
    Set-Content -LiteralPath (Join-Path $folder 'LICENSE.txt') $package.Text
    Set-Content -LiteralPath (Join-Path $folder 'THIRD-PARTY-NOTICES.txt') ('Notice for ' + $package.Name)
    Set-Content -LiteralPath (Join-Path $folder ($package.Name.ToLowerInvariant() + '.' + $package.Version + '.nupkg')) 'Actual package bytes, distinct from NuGet contentHash.'
}
$lockPath = Join-Path $fixtureRepo 'src/SnuWatcher.Desktop/packages.lock.json'
Write-ReleaseJson $lockPath @{ version = 1; dependencies = @{ net10 = @{ 'Example.Library' = @{ type = 'Direct'; resolved = '1.2.3'; contentHash = 'NotARawArchiveHash'; dependencies = @{} } } } }
Write-ReleaseJson (Join-Path $fixtureRepo 'src/SnuWatcher.Desktop/obj/project.assets.json') @{ packageFolders = @{ $cache = @{} } }
Write-ReleaseJson (Join-Path $publish 'SnuWatcher.Desktop.deps.json') @{ libraries = @{
    'Example.Library/1.2.3' = @{ type = 'package' }
    'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.14' = @{ type = 'runtimepack' }
} }

$identity = @{ revision = ('a' * 40); dirty = $false }
Write-DistributionMetadata -RepositoryPath $fixtureRepo -PublishPath $publish -Configuration $config -SourceIdentity $identity
$sbom = Get-Content -LiteralPath (Join-Path $publish 'sbom.cdx.json') -Raw | ConvertFrom-Json
Assert-Release ($sbom.bomFormat -eq 'CycloneDX' -and $sbom.specVersion -eq '1.6') 'Missing standard SBOM identity.'
Assert-Release (@($sbom.components).Count -eq 2) 'Self-contained runtime or production package missing from SBOM.'
Assert-Release ($sbom.compositions[0].aggregate -eq 'incomplete') 'Inventory must not claim a full native transitive audit.'
$library = @($sbom.components | Where-Object name -eq 'Example.Library')[0]
Assert-Release ($library.hashes[0].alg -eq 'SHA-256' -and $library.hashes[0].content -ne 'NotARawArchiveHash') 'NuGet contentHash was mislabeled as a standard package checksum.'
Assert-Release (@($library.properties | Where-Object { $_.name -eq 'snu:nuget:contentHash' -and $_.value -eq 'NotARawArchiveHash' }).Count -eq 1) 'Named NuGet integrity metadata was lost.'
$notices = Get-Content -LiteralPath (Join-Path $publish 'DISTRIBUTION-LICENSES.txt') -Raw
Assert-Release ($notices.Contains('Copyright Fixture') -and $notices.Contains('Copyright Example') -and $notices.Contains('Copyright Runtime')) 'Distribution omitted original or runtime license text.'
Assert-Release ($notices.Contains('Notice for Example.Library')) 'Package NOTICE text was omitted.'

# Unknown package terms and traversal-bearing license paths must stop packaging.
$license = Join-Path $cache 'example.library/1.2.3/LICENSE.txt'
Move-Item -LiteralPath $license -Destination ($license + '.saved')
Assert-Rejected { Write-DistributionMetadata $fixtureRepo $publish $config $identity } 'Missing required redistribution terms were accepted.'
Move-Item -LiteralPath ($license + '.saved') -Destination $license
$nuspec = Join-Path $cache 'example.library/1.2.3/Example.Library.nuspec'
$safeNuspec = Get-Content -LiteralPath $nuspec -Raw
Set-Content -LiteralPath $nuspec ($safeNuspec.Replace('<license type="expression">MIT</license>', '<license type="file">../outside.txt</license>'))
Assert-Rejected { Write-DistributionMetadata $fixtureRepo $publish $config $identity } 'A license file escaped its package root.'
Set-Content -LiteralPath $nuspec $safeNuspec
Write-DistributionMetadata $fixtureRepo $publish $config $identity
$depsPath = Join-Path $publish 'SnuWatcher.Desktop.deps.json'
$safeDeps = Get-Content -LiteralPath $depsPath -Raw
foreach ($badIdentity in @('../1.2.3', 'Example.Library/../secret', "Example.Library/1.2.3`n")) {
    Write-ReleaseJson $depsPath @{ libraries = @{ $badIdentity = @{ type = 'package' } } }
    Assert-Rejected { Write-DistributionMetadata $fixtureRepo $publish $config $identity } 'Unsafe dependency identity was accepted.'
}
Set-Content -LiteralPath $depsPath $safeDeps

$inventory = @(Get-ReleaseInventory $publish)
Assert-Release (@($inventory | Where-Object path -eq 'SnuWatcher.Desktop.exe').Count -eq 1) 'Payload executable missing from inventory.'
Assert-Release (@($inventory | Where-Object { $_.path -match '^[A-Za-z]:|^[/\\]|\.\.' }).Count -eq 0) 'Inventory leaked absolute paths.'
Assert-ReleaseInventory $publish $inventory
Set-Content -LiteralPath (Join-Path $publish 'Example.dll') 'Tampered fixture.'
Assert-Rejected { Assert-ReleaseInventory $publish $inventory } 'Changed payload bytes were accepted.'
Set-Content -LiteralPath (Join-Path $publish 'Example.dll') 'Fixture library bytes.'

# A fake Git boundary supplies identities; the release staging command never publishes.
$fakeGit = Join-Path $fixtureRoot 'fake git.ps1'
Set-Content -LiteralPath $fakeGit @'
$ErrorActionPreference = 'Stop'
if ($args -contains 'rev-parse') { Write-Output ('a' * 40); exit 0 }
if ($args -contains 'status') {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'dirty')) { Write-Output ' M tracked.txt' }
    exit 0
}
throw 'Unexpected fake Git invocation.'
'@
$installerDir = Join-Path $buildRoot 'installer'
New-Item -ItemType Directory -Path $installerDir | Out-Null
$installer = Join-Path $installerDir 'SnuCourseWatcher-0.1.0-preview.1-windows-x64-Setup.exe'
Set-Content -LiteralPath $installer 'Fixture installer bytes.'
$manifest = [ordered]@{ schemaVersion = 1; version = $config.version; installerVersion = $config.installerVersion;
    runtimeFrameworkVersion = $config.runtimeFrameworkVersion; source = $identity; dotnetSdk = '10.0.403';
    innoSetup = '6.7.3'; rid = 'win-x64'; configuration = 'Release'; signing = 'unsigned';
    operatingSystem = '10.0.26200.1'; ciRunUrl = $null; recipe = @(Get-ReleaseBuildRecipe); versionProperties = @(Get-ReleaseVersionProperties $config ('a' * 40));
    builtAtUtc = '2026-11-02T00:00:00Z'; repository = 'https://github.com/DuckFard/SNU-watcher';
    inputs = @(Get-ReleaseInputInventory $fixtureRepo); payload = @(Get-ReleaseInventory $publish);
    installer = @{ name = (Split-Path $installer -Leaf); sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() } }
Write-ReleaseJson (Join-Path $buildRoot 'build-manifest.json') $manifest
$stageCommand = Join-Path $fixtureRepo 'scripts/New-ReleaseAssets.ps1'
& $stageCommand -BuildRoot $buildRoot -GitPath $fakeGit | Out-Null
$releasePath = Join-Path $buildRoot 'release'
$releaseFiles = @(Get-ChildItem -LiteralPath $releasePath -File | Select-Object -ExpandProperty Name | Sort-Object)
Assert-Release ($releaseFiles.Count -eq 6) 'Unexpected release asset set.'
Assert-Release ($releaseFiles -contains 'SHA256SUMS.txt') 'Release checksums missing.'
$sumLines = @(Get-Content -LiteralPath (Join-Path $releasePath 'SHA256SUMS.txt'))
Assert-Release ($sumLines.Count -eq 5) 'Checksums must cover every other asset exactly once.'
foreach ($line in $sumLines) {
    Assert-Release ($line -match '^([a-f0-9]{64})  ([A-Za-z0-9._-]+)$') 'Checksum line has unsafe syntax.'
    Assert-Release ((Get-FileHash -LiteralPath (Join-Path $releasePath $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Matches[1]) 'Release checksum does not match final asset bytes.'
}
$publicMetadata = Get-Content -LiteralPath (Join-Path $releasePath 'BUILD-PROVENANCE.json') -Raw
Assert-Release (!$publicMetadata.Contains($fixtureRoot)) 'Public provenance leaked a local path.'
Assert-Release ($publicMetadata.Contains('descriptive') -and $publicMetadata.Contains('unsigned')) 'Provenance falsely implies signing or attestation.'
Assert-ReleaseAssetSet $releasePath $config ('a' * 40)
Assert-Rejected { Assert-ReleaseAssetSet $releasePath $config ('b' * 40) } 'Download verification accepted an unexpected source revision.'
$publicCopy = Join-Path $fixtureRoot 'download with spaces'
Copy-Item -LiteralPath $releasePath -Destination $publicCopy -Recurse
Add-Content -LiteralPath (Join-Path $publicCopy 'THIRD-PARTY-NOTICES.txt') 'tamper'
Assert-Rejected { Assert-ReleaseAssetSet $publicCopy $config ('a' * 40) } 'Download verification accepted tampered notices.'
Set-Content -LiteralPath (Join-Path $publicCopy 'unexpected.txt') 'Unexpected public data'
Assert-Rejected { Assert-ReleaseAssetSet $publicCopy $config ('a' * 40) } 'Download verification accepted extra public files.'
Assert-Rejected { & $stageCommand -BuildRoot $buildRoot -GitPath $fakeGit } 'An existing release folder was overwritten.'

# Failures are validated before a release destination is created.
function Assert-StageFailure([scriptblock] $Mutation, [string] $Message) {
    $copyRoot = Join-Path $fixtureRepo ('artifacts/native/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $copyRoot | Out-Null
    Copy-Item -LiteralPath $publish, $installerDir, (Join-Path $buildRoot 'build-manifest.json') -Destination $copyRoot -Recurse
    & $Mutation $copyRoot
    Assert-Rejected { & $stageCommand -BuildRoot $copyRoot -GitPath $fakeGit } $Message
    Assert-Release (!(Test-Path -LiteralPath (Join-Path $copyRoot 'release'))) 'A rejected build left apparently releasable assets.'
}
Assert-StageFailure { param($p) Add-Content -LiteralPath (Join-Path $p 'installer/SnuCourseWatcher-0.1.0-preview.1-windows-x64-Setup.exe') 'Tamper' } 'Changed installer bytes were accepted.'
Assert-StageFailure { param($p) Add-Content -LiteralPath (Join-Path $p 'publish/Example.dll') 'Tamper' } 'Changed payload bytes were staged.'
Assert-StageFailure { param($p) Remove-Item -LiteralPath (Join-Path $p 'publish/DISTRIBUTION-LICENSES.txt') } 'Missing distribution notices were staged.'
Assert-StageFailure { param($p) $m = Get-Content -LiteralPath (Join-Path $p 'build-manifest.json') -Raw | ConvertFrom-Json; $m.source.revision = 'b' * 40; Write-ReleaseJson (Join-Path $p 'build-manifest.json') $m } 'A different source revision was staged.'
foreach ($field in 'recipe', 'versionProperties', 'operatingSystem', 'ciRunUrl', 'builtAtUtc') {
    $script:tamperedField = $field
    Assert-StageFailure { param($p) $m = Get-Content -LiteralPath (Join-Path $p 'build-manifest.json') -Raw | ConvertFrom-Json; $m.$script:tamperedField = 'C:\private-profile\secret-token'; Write-ReleaseJson (Join-Path $p 'build-manifest.json') $m } ('Unvalidated public provenance accepted: ' + $field)
}
Set-Content -LiteralPath (Join-Path $fixtureRoot 'dirty') 'Fixture dirty tree marker.'
Assert-StageFailure { param($p) } 'A dirty working tree was staged.'
Remove-Item -LiteralPath (Join-Path $fixtureRoot 'dirty')
$projectedRoot = Join-Path $fixtureRepo 'artifacts/native/projected-private-fields'
New-Item -ItemType Directory -Path $projectedRoot | Out-Null
Copy-Item -LiteralPath $publish, $installerDir, (Join-Path $buildRoot 'build-manifest.json') -Destination $projectedRoot -Recurse
$privateManifest = Get-Content -LiteralPath (Join-Path $projectedRoot 'build-manifest.json') -Raw | ConvertFrom-Json
$privateManifest.installer | Add-Member -NotePropertyName privateData -NotePropertyValue 'C:\private-profile\secret-token'
$privateManifest.payload[0] | Add-Member -NotePropertyName privateData -NotePropertyValue 'C:\private-profile\secret-token'
Write-ReleaseJson (Join-Path $projectedRoot 'build-manifest.json') $privateManifest
& $stageCommand -BuildRoot $projectedRoot -GitPath $fakeGit | Out-Null
$projectedPublic = @(Get-ChildItem -LiteralPath (Join-Path $projectedRoot 'release') -File -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
Assert-Release (!$projectedPublic.Contains('private-profile') -and !$projectedPublic.Contains('secret-token')) 'Unrecognized ignored-manifest properties leaked into public assets.'
Write-Output 'Release metadata and staging regression passed.'
