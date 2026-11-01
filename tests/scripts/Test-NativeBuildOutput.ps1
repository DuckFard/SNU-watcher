$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixtureRoot = Join-Path $repoRoot ('artifacts/native-build-output-tests/' + [Guid]::NewGuid().ToString('N'))
$fixtureRepo = Join-Path $fixtureRoot 'repository with spaces'
$toolsRoot = Join-Path $fixtureRoot 'tools with spaces'
$fixtureScripts = Join-Path $fixtureRepo 'scripts'
$fixtureProject = Join-Path $fixtureRepo 'src/SnuWatcher.Desktop'
$fixtureInstaller = Join-Path $fixtureRepo 'installer'
New-Item -ItemType Directory -Path $fixtureScripts, $fixtureProject, $fixtureInstaller, $toolsRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/Build-Native.ps1'), (Join-Path $repoRoot 'scripts/Release.Common.ps1') -Destination $fixtureScripts
Copy-Item -LiteralPath (Join-Path $repoRoot 'release.json'), (Join-Path $repoRoot 'global.json') -Destination $fixtureRepo
Set-Content -LiteralPath (Join-Path $fixtureRepo 'LICENSE') 'Fixture original project license.'
Set-Content -LiteralPath (Join-Path $fixtureScripts 'Distribution.Metadata.ps1') @'
function Write-DistributionMetadata([string] $RepositoryPath, [string] $PublishPath, $Configuration, $SourceIdentity) {
    Set-Content -LiteralPath (Join-Path $PublishPath 'DISTRIBUTION-LICENSES.txt') 'Fixture distribution terms.'
    Set-Content -LiteralPath (Join-Path $PublishPath 'sbom.cdx.json') '{}'
}
'@
Set-Content -LiteralPath (Join-Path $fixtureProject 'SnuWatcher.Desktop.csproj') -Value '<Project />'
Set-Content -LiteralPath (Join-Path $fixtureInstaller 'SnuWatcher.iss') -Value '; Fake tool fixture; never compiled.'
$fakeDotNet = Join-Path $toolsRoot 'fake dotnet.ps1'
$fakeIscc = Join-Path $toolsRoot 'fake iscc.ps1'
$fakeGit = Join-Path $toolsRoot 'fake git.ps1'
Set-Content -LiteralPath $fakeGit @'
if ($args -contains 'rev-parse') { Write-Output ('a' * 40); exit 0 }
if ($args -contains 'status') { Write-Output ' M development-change'; exit 0 }
throw 'Unexpected fixture Git command.'
'@
$stagePath = Join-Path $toolsRoot 'stage.txt'

# Emulate publish's overlay behavior: an existing output directory is never cleaned.
Set-Content -LiteralPath $fakeDotNet -Value @'
$ErrorActionPreference = 'Stop'
$stage = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'stage.txt')).Trim()
if ($args[0] -eq '--version') {
    if ($stage -eq 'wrong-sdk') { Write-Output '10.0.999' } else { Write-Output '10.0.403' }
    exit 0
}
if ($args[0] -eq 'restore') {
    if ($args -notcontains '--locked-mode' -or $args -notcontains '-p:RuntimeFrameworkVersion=10.0.14') { throw 'Restore must be locked with an exact runtime.' }
    Set-Content -LiteralPath (Join-Path $PSScriptRoot ('restore-' + $stage + '.txt')) ($args -join "`n")
    if ($stage -eq 'fail-restore') { exit 7 }
    exit 0
}
$outputIndex = [Array]::IndexOf($args, '-o')
if ($args[0] -ne 'publish' -or $outputIndex -lt 0 -or !(Test-Path -LiteralPath $args[1])) {
    throw 'Fake dotnet received an invalid publish command.'
}
if ($args -notcontains '--no-restore' -or $args -notcontains '-p:Version=0.1.0-preview.1' -or
    $args -notcontains '-p:FileVersion=0.1.0.0' -or $args -notcontains '-p:RuntimeFrameworkVersion=10.0.14') { throw 'Publish lost release or runtime identity.' }
$outputPath = [IO.Path]::GetFullPath($args[$outputIndex + 1])
$fixturePrefix = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)) + [IO.Path]::DirectorySeparatorChar
if (!$outputPath.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fake publish refused an output outside its fixture.'
}
@{ PublishDirectory = $outputPath } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $PSScriptRoot ('dotnet-' + $stage + '.json'))
if ($stage -eq 'fail-publish') { exit 9 }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
Set-Content -LiteralPath (Join-Path $outputPath ('current-' + $stage + '.dll')) -Value 'Fake assembly marker.'
exit 0
'@

# Emulate packaging the complete supplied source directory, recording its manifest.
Set-Content -LiteralPath $fakeIscc -Value @'
$ErrorActionPreference = 'Stop'
$stage = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'stage.txt')).Trim()
$sourceArgument = @($args | Where-Object { $_.StartsWith('/DSourceDir=', [StringComparison]::Ordinal) })
$outputArgument = @($args | Where-Object { $_.StartsWith('/DOutputDir=', [StringComparison]::Ordinal) })
if ($sourceArgument.Count -ne 1 -or $outputArgument.Count -ne 1 -or !(Test-Path -LiteralPath $args[-1])) {
    throw 'Fake ISCC requires explicit source/output defines and an existing script.'
}
if ($args -notcontains '/DReleaseVersion=0.1.0-preview.1' -or $args -notcontains '/DInstallerVersion=0.1.0.0') { throw 'Installer lost product version defines.' }
$sourcePath = [IO.Path]::GetFullPath($sourceArgument[0].Substring('/DSourceDir='.Length))
$outputPath = [IO.Path]::GetFullPath($outputArgument[0].Substring('/DOutputDir='.Length))
$fixturePrefix = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)) + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($sourcePath, $outputPath)) {
    if (!$path.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fake ISCC refused a path outside its fixture.'
    }
}
$manifest = @{ SourceDirectory = $sourcePath; OutputDirectory = $outputPath;
    Files = @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File |
        ForEach-Object { [IO.Path]::GetRelativePath($sourcePath, $_.FullName) } | Sort-Object) }
$manifest | ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath (Join-Path $PSScriptRoot ('iscc-' + $stage + '.json'))
if ($stage -eq 'fail-installer') { exit 11 }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$installerName = 'SnuCourseWatcher-0.1.0-preview.1-windows-x64-Setup.exe'
Set-Content -LiteralPath (Join-Path $outputPath $installerName) 'Fixture installer bytes.'
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outputPath 'packaged-manifest.json')
Write-Output 'Compiler engine version: Inno Setup 6.7.3'
exit 0
'@

function Assert-Condition([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}

function Read-ToolRecord([string] $Tool, [string] $Stage) {
    Get-Content -Raw -LiteralPath (Join-Path $toolsRoot ($Tool + '-' + $Stage + '.json')) | ConvertFrom-Json
}

function Invoke-FixtureBuild([string] $Stage, [switch] $SkipInstaller) {
    Set-Content -LiteralPath $stagePath -Value $Stage
    & (Join-Path $fixtureScripts 'Build-Native.ps1') -DotNetPath $fakeDotNet -IsccPath $fakeIscc -GitPath $fakeGit -SkipInstaller:$SkipInstaller | Out-Null
}

Invoke-FixtureBuild 'first'
$first = Read-ToolRecord 'iscc' 'first'
$obsoletePath = Join-Path $first.SourceDirectory 'obsolete-sdk-component.dll'
Set-Content -LiteralPath $obsoletePath -Value 'Historical fake SDK component; must be retained only in this old build.'

Invoke-FixtureBuild 'second'
$second = Read-ToolRecord 'iscc' 'second'
Assert-Condition ($second.Files -notcontains 'obsolete-sdk-component.dll') 'A later installer packaged an obsolete SDK component from an earlier build.'
Assert-Condition ($second.Files -notcontains 'current-first.dll') 'A later installer packaged an earlier build assembly.'
Assert-Condition ($second.Files -contains 'current-second.dll') 'The installer omitted the current build assembly.'
Assert-Condition ($first.SourceDirectory -ne $second.SourceDirectory) 'Publish directories must be unique per build.'
Assert-Condition ($first.OutputDirectory -ne $second.OutputDirectory) 'Installer directories must be unique per build.'
Assert-Condition (Test-Path -LiteralPath $obsoletePath) 'The earlier build was deleted or cleaned.'
$firstManifest = Get-Content -Raw -LiteralPath (Join-Path $first.OutputDirectory 'packaged-manifest.json') | ConvertFrom-Json
Assert-Condition ($firstManifest.Files -contains 'current-first.dll' -and $firstManifest.Files -notcontains 'current-second.dll') 'The earlier installer output was overwritten.'
foreach ($required in 'DISTRIBUTION-LICENSES.txt', 'sbom.cdx.json') {
    Assert-Condition ($second.Files -contains $required) ('Installer omitted mandatory distribution metadata: ' + $required)
}
$buildManifest = Get-Content -LiteralPath (Join-Path (Split-Path $second.SourceDirectory -Parent) 'build-manifest.json') -Raw | ConvertFrom-Json
Assert-Condition ($buildManifest.source.dirty -and $buildManifest.source.revision -eq ('a' * 40)) 'Development source identity was not recorded honestly.'
Assert-Condition ($buildManifest.version -eq '0.1.0-preview.1' -and $buildManifest.signing -eq 'unsigned') 'Build manifest lost its preview identity.'
Assert-Condition (@($buildManifest.payload).Count -eq $second.Files.Count) 'Manifest did not hash the final complete payload.'
foreach ($build in @($first, $second)) {
    Assert-Condition ((Split-Path $build.SourceDirectory -Parent) -eq (Split-Path $build.OutputDirectory -Parent)) 'One build must share a dedicated publish/installer root.'
}

Invoke-FixtureBuild 'publish-only' -SkipInstaller
$publishOnly = Read-ToolRecord 'dotnet' 'publish-only'
Assert-Condition ($publishOnly.PublishDirectory -notin @($first.SourceDirectory, $second.SourceDirectory)) 'Publish-only output reused an existing build.'
Assert-Condition (!(Test-Path -LiteralPath (Join-Path $toolsRoot 'iscc-publish-only.json'))) 'SkipInstaller still invoked the compiler.'

foreach ($stage in 'wrong-sdk', 'fail-restore') {
    $failed = $false
    try { Invoke-FixtureBuild $stage } catch { $failed = $true }
    Assert-Condition $failed ('Build accepted ' + $stage)
    Assert-Condition (!(Test-Path -LiteralPath (Join-Path $toolsRoot ('dotnet-' + $stage + '.json')))) 'Preflight failure still invoked publish.'
}

$publishFailure = $false
try { Invoke-FixtureBuild 'fail-publish' } catch { $publishFailure = $_.Exception.Message -eq 'Native publish failed.' }
Assert-Condition $publishFailure 'A failed publish did not propagate its failure.'
Assert-Condition (!(Test-Path -LiteralPath (Join-Path $toolsRoot 'iscc-fail-publish.json'))) 'A failed publish still invoked packaging.'

$installerFailure = $false
try { Invoke-FixtureBuild 'fail-installer' } catch { $installerFailure = $_.Exception.Message -eq 'Native installer compilation failed.' }
Assert-Condition $installerFailure 'A failed installer compilation did not propagate its failure.'

# An artifacts ancestor junction must be rejected before publish or output writes.
$junctionRepo = Join-Path $fixtureRoot 'junction repository'
$junctionTarget = Join-Path $fixtureRoot 'external output target'
New-Item -ItemType Directory -Path $junctionRepo, $junctionTarget | Out-Null
Copy-Item -LiteralPath $fixtureScripts, (Join-Path $fixtureRepo 'src'), $fixtureInstaller -Destination $junctionRepo -Recurse
Copy-Item -LiteralPath (Join-Path $fixtureRepo 'release.json'), (Join-Path $fixtureRepo 'global.json'), (Join-Path $fixtureRepo 'LICENSE') -Destination $junctionRepo
New-Item -ItemType Junction -Path (Join-Path $junctionRepo 'artifacts') -Target $junctionTarget | Out-Null
Set-Content -LiteralPath $stagePath 'junction-rejected'
$junctionRejected = $false
try { & (Join-Path $junctionRepo 'scripts/Build-Native.ps1') -DotNetPath $fakeDotNet -IsccPath $fakeIscc -GitPath $fakeGit | Out-Null } catch { $junctionRejected = $true }
Assert-Condition $junctionRejected 'An artifacts ancestor junction was accepted.'
Assert-Condition (@(Get-ChildItem -LiteralPath $junctionTarget -Force).Count -eq 0) 'Packaging wrote outside its trusted repository before rejecting a junction.'
Assert-Condition (!(Test-Path -LiteralPath (Join-Path $toolsRoot 'dotnet-junction-rejected.json'))) 'A junction output still invoked publish.'
Write-Output "Native build output regression passed. Preserved fixture: $fixtureRoot"
# Expected fake tool failures must not leak into GitHub's PowerShell exit-code check.
exit 0
