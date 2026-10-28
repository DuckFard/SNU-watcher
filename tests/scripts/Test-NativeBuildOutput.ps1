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
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/Build-Native.ps1') -Destination $fixtureScripts
Set-Content -LiteralPath (Join-Path $fixtureProject 'SnuWatcher.Desktop.csproj') -Value '<Project />'
Set-Content -LiteralPath (Join-Path $fixtureInstaller 'SnuWatcher.iss') -Value '; Fake tool fixture; never compiled.'
$fakeDotNet = Join-Path $toolsRoot 'fake dotnet.ps1'
$fakeIscc = Join-Path $toolsRoot 'fake iscc.ps1'
$stagePath = Join-Path $toolsRoot 'stage.txt'

# Emulate publish's overlay behavior: an existing output directory is never cleaned.
Set-Content -LiteralPath $fakeDotNet -Value @'
$ErrorActionPreference = 'Stop'
$stage = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'stage.txt')).Trim()
$outputIndex = [Array]::IndexOf($args, '-o')
if ($args[0] -ne 'publish' -or $outputIndex -lt 0 -or !(Test-Path -LiteralPath $args[1])) {
    throw 'Fake dotnet received an invalid publish command.'
}
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
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outputPath 'packaged-manifest.json')
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
    & (Join-Path $fixtureScripts 'Build-Native.ps1') -DotNetPath $fakeDotNet -IsccPath $fakeIscc -SkipInstaller:$SkipInstaller | Out-Null
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
Assert-Condition ($firstManifest.Files.Count -eq 1 -and $firstManifest.Files[0] -eq 'current-first.dll') 'The earlier installer output was overwritten.'
foreach ($build in @($first, $second)) {
    Assert-Condition ((Split-Path $build.SourceDirectory -Parent) -eq (Split-Path $build.OutputDirectory -Parent)) 'One build must share a dedicated publish/installer root.'
}

Invoke-FixtureBuild 'publish-only' -SkipInstaller
$publishOnly = Read-ToolRecord 'dotnet' 'publish-only'
Assert-Condition ($publishOnly.PublishDirectory -notin @($first.SourceDirectory, $second.SourceDirectory)) 'Publish-only output reused an existing build.'
Assert-Condition (!(Test-Path -LiteralPath (Join-Path $toolsRoot 'iscc-publish-only.json'))) 'SkipInstaller still invoked the compiler.'

$publishFailure = $false
try { Invoke-FixtureBuild 'fail-publish' } catch { $publishFailure = $_.Exception.Message -eq 'Native publish failed.' }
Assert-Condition $publishFailure 'A failed publish did not propagate its failure.'
Assert-Condition (!(Test-Path -LiteralPath (Join-Path $toolsRoot 'iscc-fail-publish.json'))) 'A failed publish still invoked packaging.'

$installerFailure = $false
try { Invoke-FixtureBuild 'fail-installer' } catch { $installerFailure = $_.Exception.Message -eq 'Native installer compilation failed.' }
Assert-Condition $installerFailure 'A failed installer compilation did not propagate its failure.'
Write-Output "Native build output regression passed. Preserved fixture: $fixtureRoot"
# Expected fake tool failures must not leak into GitHub's PowerShell exit-code check.
exit 0
