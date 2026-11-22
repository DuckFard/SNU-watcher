# Offline guard regressions. No executable, native API, source request or OS setting is used.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$launcherPath = Join-Path $repoRoot 'scripts/Test-InstalledNativeDelivery.ps1'
if (!(Test-Path -LiteralPath $launcherPath -PathType Leaf)) { throw 'Native qualification launcher is missing (expected test-first RED).' }
$parseErrors = $null; $tokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($launcherPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'The native qualification launcher has a syntax error.' }
# Import only declared helpers: do not execute the launcher or its parameter block.
foreach ($definition in $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] }) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$script:launches = 0
function Invoke-NativeOwnedProcess { $script:launches++; throw 'Offline tests must never start a process.' }
function Assert-NativeTest([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; $script:assertions++ }
function Assert-NativeRejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false; try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-NativeTest $rejected $Message
}
$script:assertions = 0
$fixtureRoot = Join-Path $repoRoot ('artifacts/native-delivery-script-tests/' + [Guid]::NewGuid().ToString('N'))
$installed = Join-Path $fixtureRoot 'installed application with spaces'
$null = [IO.Directory]::CreateDirectory($installed)
$names = @('SnuWatcher.Desktop.exe', 'SnuWatcher.Desktop.dll', 'SnuWatcher.Windows.dll', 'SnuWatcher.Infrastructure.dll', 'SnuWatcher.Application.dll', 'SnuWatcher.Core.dll')
foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $installed $name), ('fixture bytes ' + $name), [Text.UTF8Encoding]::new($false)) }
$exe = Join-Path $installed $names[0]
$manifestPath = Join-Path $fixtureRoot 'build-manifest.json'
function Write-NativeFixtureManifest($Payload) {
    $value = [ordered]@{ schemaVersion=1; source=@{ revision=('a'*40); dirty=$false }; version='0.1.0-preview.1'; installerVersion='0.1.0.0'; payload=@($Payload) }
    [IO.File]::WriteAllText($manifestPath, ($value | ConvertTo-Json -Depth 8 -Compress), [Text.UTF8Encoding]::new($false))
    return (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$payload = @($names | ForEach-Object { $file=Get-Item -LiteralPath (Join-Path $installed $_); [PSCustomObject]@{ path=$_; bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
$pinned = Write-NativeFixtureManifest $payload
$proof = Get-NativePipelineProof $exe $manifestPath $pinned
Assert-NativeTest ($proof.ExecutableSha256 -ceq $payload[0].sha256.ToUpperInvariant()) 'The apphost hash is not the actual raw executable hash.'
$lines = ($payload | ForEach-Object { $_.path + '|' + $_.sha256 + "`n" }) -join ''
$sha = [Security.Cryptography.SHA256]::Create()
try { $expectedPipeline = ([BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($lines)))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
Assert-NativeTest ($proof.PipelineSha256 -ceq $expectedPipeline) 'The fixed-six digest lost its order, lowercase hashes, or final LF.'

# Same apphost, changed managed pipeline: reject before marker creation or launch.
$desktopPath = Join-Path $installed 'SnuWatcher.Desktop.dll'
$desktopBefore = [IO.File]::ReadAllBytes($desktopPath)
[IO.File]::AppendAllText($desktopPath, 'tampered managed pipeline')
Assert-NativeRejected { Invoke-InstalledNativeDelivery $exe $manifestPath $pinned 'en-sound' 180 } 'A changed Desktop DLL was admitted behind an unchanged EXE.'
Assert-NativeTest ($script:launches -eq 0) 'A changed managed pipeline reached process creation.'
[IO.File]::WriteAllBytes($desktopPath, $desktopBefore)
Assert-NativeRejected { Invoke-InstalledNativeDelivery $exe $manifestPath ('b'*64) 'en-sound' 180 } 'An unpinned manifest was trusted.'
Assert-NativeTest ($script:launches -eq 0) 'An unpinned manifest reached process creation.'
foreach ($mutation in @('duplicate','missing','wrong_size','wrong_hash','unsafe_path','case_alias')) {
    $items = @($payload | ForEach-Object { [PSCustomObject]@{ path=$_.path; bytes=$_.bytes; sha256=$_.sha256 } })
    switch ($mutation) {
        'duplicate' { $items += $items[0] }
        'missing' { $items = $items[0..4] }
        'wrong_size' { $items[1].bytes = 1L }
        'wrong_hash' { $items[1].sha256 = 'f'*64 }
        'unsafe_path' { $items += [PSCustomObject]@{ path='../outside'; bytes=1L; sha256=('a'*64) } }
        'case_alias' { $items += [PSCustomObject]@{ path='snuwatcher.desktop.dll'; bytes=$items[1].bytes; sha256=$items[1].sha256 } }
    }
    $hash = Write-NativeFixtureManifest $items
    Assert-NativeRejected { Get-NativePipelineProof $exe $manifestPath $hash } ('Unsafe inventory accepted: '+$mutation)
}
$pinned = Write-NativeFixtureManifest $payload
$text = [IO.File]::ReadAllText($manifestPath)
[IO.File]::WriteAllText($manifestPath, $text.Replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1'), [Text.UTF8Encoding]::new($false))
$hash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-NativeRejected { Get-NativePipelineProof $exe $manifestPath $hash } 'Duplicate JSON names were silently collapsed.'
$pinned = Write-NativeFixtureManifest $payload
$proof = Get-NativePipelineProof $exe $manifestPath $pinned
$lease=Open-NativePipelineLease $proof
try {
    Assert-NativeTest (@($lease.Streams).Count -eq 6) 'The entire fixed pipeline is not pinned through launch.'
    Assert-NativeRejected { [IO.File]::AppendAllText($desktopPath,'must not overwrite') } 'An installer could replace a verified DLL during the owned process lifetime.'
    Assert-NativeRejected { [IO.File]::Delete($desktopPath) } 'A verified DLL could be deleted while its owned process was running.'
} finally { foreach($stream in $lease.Streams){$stream.Dispose()} }
[IO.File]::AppendAllText($desktopPath,'swap after verification')
Assert-NativeRejected { Open-NativePipelineLease $proof } 'A library swap between manifest verification and the retained lease was not detected.'
[IO.File]::WriteAllBytes($desktopPath,$desktopBefore)
$lease=Open-NativePipelineLease $proof
foreach($stream in $lease.Streams){$stream.Dispose()}
$runId = [Guid]::NewGuid().ToString('N')
$privateRun = New-NativePrivateRun $proof 'en-sound' $runId
try {
    $intent = Read-NativeBoundedJson (Join-Path $privateRun.ProfileRoot 'native-delivery-validation.json') 1024
    Assert-NativeTest (@($intent.PSObject.Properties).Count -eq 5 -and $intent.pipelineSha256 -ceq $expectedPipeline) 'The intent is not the exact five-field pipeline-bound protocol.'
    Assert-NativeRejected { New-NativePrivateRun $proof 'en-sound' $runId } 'A reused private profile was overlaid.'
} finally {
    $target = [IO.Path]::GetFullPath($privateRun.ProfileRoot)
    $parent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'SnuWatcher-runtime-validation'))
    if (![string]::Equals([IO.Path]::GetDirectoryName($target),$parent,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($target) -cne $runId) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $target -Recurse -Force
}

function New-NativeResultFixture([string]$CaseName) {
    $disabled=$CaseName -ceq 'permission-disabled'; $muted=$CaseName -ceq 'ko-muted'; $calls=if($disabled){0}else{1}
    return [PSCustomObject][ordered]@{
        schemaVersion=1;codePath='native-delivery-validation';testStatus='PASSED';code='installed_native_delivery_passed';runId=$runId;case=$CaseName;
        processId=1234;executableSha256=$proof.ExecutableSha256;pipelineSha256=$proof.PipelineSha256;
        fixtureOnly=$true;liveHttpRequests=0;fixtureHttpRequests=4;rejectedFixtureRequests=0;fullBaselineCommitted=$true;initialEvents=0;
        openingCommitted=$true;noResendOnReopen=$true;nativeShowCalls=$calls;channelSendCalls=$calls;
        nativePlatformId=$(if($disabled){$null}else{42});channelOutcome=$(if($disabled){$null}else{'OsAccepted'});
        eventStatus=$(if($disabled){'Pending'}else{'OsAccepted'});chargedAttempts=$calls;unresolvedAttempt=$false;
        readiness=$(if($disabled){'Disabled'}else{'Ready'});disabledOpeningReadObserved=$disabled;savedLanguage=$(if($muted){'Korean'}else{'English'});
        savedSoundMuted=$muted;nativePayloadSha256=$(if($disabled){$null}else{'a'*64});nativeSoundMuted=$(if($disabled){$null}else{$muted});
        workersDrained=$true;ownerHandoff=$true;visibleBannerConfirmed=$false;soundConfirmed=$false;buttonClickConfirmed=$false;bodyClickConfirmed=$false;cleanVmGatePassed=$false;
        eventId=('a'*32);batchId=('b'*32);attemptId=$(if($disabled){$null}else{'c'*32});triggerObservedAtUtc='2026-11-02T01:00:00Z';nativeResultRecordedAtUtc=$(if($disabled){$null}else{'2026-11-02T01:00:01Z'})
    }
}
foreach ($caseName in @('en-sound','ko-muted','permission-disabled')) {
    Assert-NativePassedResult (New-NativeResultFixture $caseName) $proof $runId $caseName 1234
    Assert-NativeTest $true ('Case matrix accepted: '+$caseName)
}
foreach($field in @('runId','case','executableSha256','pipelineSha256','codePath','testStatus','code','eventStatus','readiness','savedLanguage','channelOutcome','nativePayloadSha256')) {
    $result=New-NativeResultFixture 'en-sound';$result.$field=$true
    Assert-NativeRejected { Assert-NativePassedResult $result $proof $runId 'en-sound' 1234 } ('Boolean was coerced into a bound string field: '+$field)
}
$claim=[PSCustomObject]@{format='snu-installed-native-delivery-claim-v1';runId=$runId;case='en-sound';executableSha256=$proof.ExecutableSha256;pipelineSha256=$proof.PipelineSha256;processId=1234}
Assert-NativeClaim $claim $proof $runId 'en-sound' 1234
Assert-NativeTest $true 'Exact six-field pipeline claim accepted.'
$claim.format=$true
Assert-NativeRejected { Assert-NativeClaim $claim $proof $runId 'en-sound' 1234 } 'Boolean was coerced into the native claim format.'
$claim.format='snu-installed-native-delivery-claim-v1';$claim | Add-Member -NotePropertyName other -NotePropertyValue 'unexpected'
Assert-NativeRejected { Assert-NativeClaim $claim $proof $runId 'en-sound' 1234 } 'Additional native claim fields were accepted.'
foreach ($mutation in @('different_pipeline','different_process','live_http','human_confirmed','charge','zero_platform','wrong_language','array_scalar','missing_fact','extra_fact')) {
    $result=New-NativeResultFixture 'en-sound'
    switch($mutation) {
        'different_pipeline' { $result.pipelineSha256='b'*64 }
        'different_process' { $result.processId=5678 }
        'live_http' { $result.liveHttpRequests=1 }
        'human_confirmed' { $result.visibleBannerConfirmed=$true }
        'charge' { $result.chargedAttempts=0 }
        'zero_platform' { $result.nativePlatformId=0 }
        'wrong_language' { $result.savedLanguage='Korean' }
        'array_scalar' { $result.eventStatus=@('OsAccepted') }
        'missing_fact' { $result.PSObject.Properties.Remove('workersDrained') }
        'extra_fact' { $result | Add-Member -NotePropertyName claimedClean -NotePropertyValue $true }
    }
    Assert-NativeRejected { Assert-NativePassedResult $result $proof $runId 'en-sound' 1234 } ('Dishonest or malformed machine evidence accepted: '+$mutation)
}
$result=New-NativeResultFixture 'permission-disabled';$result.attemptId='c'*32
Assert-NativeRejected { Assert-NativePassedResult $result $proof $runId 'permission-disabled' 1234 } 'Disabled readiness fabricated a submission reservation.'
$result=New-NativeResultFixture 'permission-disabled';$result.nativePlatformId=42
Assert-NativeRejected { Assert-NativePassedResult $result $proof $runId 'permission-disabled' 1234 } 'Disabled readiness fabricated platform acceptance.'
Write-Output ('Native qualification script guards passed: '+$script:assertions+' assertions; process launches=0, SDK calls=0, live HTTP=0.')
