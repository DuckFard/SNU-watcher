# Explicit installed fixture-native qualification. Never use this script to monitor live seats.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)] [string]$InstalledExecutablePath,
    [Parameter(Mandatory=$true)] [string]$BuildManifestPath,
    [Parameter(Mandatory=$true)] [string]$ExpectedBuildManifestSha256,
    [ValidateSet('en-sound','ko-muted','permission-disabled')] [string]$Case = 'en-sound',
    [ValidateRange(120,300)] [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NativeDirectory([string]$Path, [bool]$AllowMissing=$false) {
    $full = [IO.Path]::GetFullPath($Path); $current = [IO.Path]::GetPathRoot($full)
    foreach ($part in $full.Substring($current.Length).Split([char[]]@('\','/'),[StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] { if ($AllowMissing) { continue }; throw 'A required qualification directory is missing.' }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or ($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
            throw 'Qualification directories must be ordinary directories without reparse points.'
        }
    }
}

function Read-NativeBoundedBytes([string]$Path, [long]$Maximum) {
    Assert-NativeDirectory ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path)))
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Qualification files must be ordinary files.' }
    $stream = [IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 1 -or $stream.Length -gt $Maximum) { throw 'Qualification file size is outside its bounds.' }
        $bytes = New-Object byte[] ([int]$stream.Length); $offset = 0
        while ($offset -lt $bytes.Length) { $read = $stream.Read($bytes,$offset,$bytes.Length-$offset); if ($read -eq 0) { throw 'Qualification file ended early.' }; $offset += $read }
        if ($stream.ReadByte() -ne -1) { throw 'Qualification file changed during read.' }
        return ,$bytes
    } finally { $stream.Dispose() }
}

function Get-NativeSha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}

function Assert-NativeJsonUniqueNames([string]$Text) {
    $state = [PSCustomObject]@{ Text=$Text; Offset=0; Nodes=0 }
    Read-NativeJsonValue $state 0
    Skip-NativeJsonSpace $state
    if ($state.Offset -ne $Text.Length) { throw 'Native qualification JSON has trailing tokens.' }
}

function Skip-NativeJsonSpace($State) {
    while ($State.Offset -lt $State.Text.Length -and [int][char]$State.Text[$State.Offset] -in @(9,10,13,32)) { $State.Offset++ }
}

function Read-NativeJsonHex($State) {
    if ($State.Text.Length - $State.Offset -lt 4) { throw 'Incomplete JSON Unicode escape.' }
    $hex = $State.Text.Substring($State.Offset,4)
    if (![regex]::IsMatch($hex,'\A[0-9A-Fa-f]{4}\z')) { throw 'Invalid JSON Unicode escape.' }
    $State.Offset += 4
    return [Convert]::ToInt32($hex,16)
}

function Read-NativeJsonString($State) {
    if ($State.Offset -ge $State.Text.Length -or [int][char]$State.Text[$State.Offset] -ne 34) { throw 'A JSON string was required.' }
    $State.Offset++
    $decoded = [Text.StringBuilder]::new()
    while ($State.Offset -lt $State.Text.Length) {
        $character = [char]$State.Text[$State.Offset]
        $State.Offset++
        if ([int]$character -eq 34) { return $decoded.ToString() }
        if ([int]$character -lt 32) { throw 'Unescaped control character in native qualification JSON.' }
        if ([int]$character -eq 92) {
            if ($State.Offset -ge $State.Text.Length) { throw 'Incomplete JSON string escape.' }
            $escape = [char]$State.Text[$State.Offset]
            $State.Offset++
            switch ([int]$escape) {
                34 { [void]$decoded.Append([char]34) }
                92 { [void]$decoded.Append([char]92) }
                47 { [void]$decoded.Append([char]47) }
                98 { [void]$decoded.Append([char]8) }
                102 { [void]$decoded.Append([char]12) }
                110 { [void]$decoded.Append([char]10) }
                114 { [void]$decoded.Append([char]13) }
                116 { [void]$decoded.Append([char]9) }
                117 {
                    $unit = Read-NativeJsonHex $State
                    if ($unit -ge 55296 -and $unit -le 56319) {
                        if ($State.Text.Length - $State.Offset -lt 6 -or [int][char]$State.Text[$State.Offset] -ne 92 -or
                            [int][char]$State.Text[$State.Offset+1] -ne 117) { throw 'Unpaired JSON Unicode surrogate.' }
                        $State.Offset += 2
                        $low = Read-NativeJsonHex $State
                        if ($low -lt 56320 -or $low -gt 57343) { throw 'Unpaired JSON Unicode surrogate.' }
                        [void]$decoded.Append([char]$unit); [void]$decoded.Append([char]$low)
                    } elseif ($unit -ge 56320 -and $unit -le 57343) { throw 'Unpaired JSON Unicode surrogate.' }
                    else { [void]$decoded.Append([char]$unit) }
                }
                default { throw 'Invalid JSON string escape.' }
            }
        } elseif ([char]::IsHighSurrogate($character)) {
            if ($State.Offset -ge $State.Text.Length -or ![char]::IsLowSurrogate([char]$State.Text[$State.Offset])) { throw 'Unpaired JSON Unicode surrogate.' }
            [void]$decoded.Append($character); [void]$decoded.Append([char]$State.Text[$State.Offset]); $State.Offset++
        } elseif ([char]::IsLowSurrogate($character)) { throw 'Unpaired JSON Unicode surrogate.' }
        else { [void]$decoded.Append($character) }
    }
    throw 'Unterminated JSON string.'
}

function Read-NativeJsonValue($State, [int]$Depth) {
    $State.Nodes++
    if ($Depth -gt 16 -or $State.Nodes -gt 20000) { throw 'Native qualification JSON exceeds its structural bounds.' }
    Skip-NativeJsonSpace $State
    if ($State.Offset -ge $State.Text.Length) { throw 'Incomplete native qualification JSON.' }
    $first = [char]$State.Text[$State.Offset]
    if ($first -eq '{') {
        $State.Offset++
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        Skip-NativeJsonSpace $State
        if ($State.Offset -lt $State.Text.Length -and $State.Text[$State.Offset] -eq '}') { $State.Offset++; return }
        while ($true) {
            $name = Read-NativeJsonString $State
            if (!$names.Add($name)) { throw 'Duplicate decoded JSON property name.' }
            Skip-NativeJsonSpace $State
            if ($State.Offset -ge $State.Text.Length -or $State.Text[$State.Offset] -ne ':') { throw 'A JSON property colon was required.' }
            $State.Offset++
            Read-NativeJsonValue $State ($Depth+1)
            Skip-NativeJsonSpace $State
            if ($State.Offset -ge $State.Text.Length) { throw 'Incomplete JSON object.' }
            $separator = $State.Text[$State.Offset]; $State.Offset++
            if ($separator -eq '}') { return }
            if ($separator -ne ',') { throw 'Invalid JSON object separator.' }
            Skip-NativeJsonSpace $State
        }
    }
    if ($first -eq '[') {
        $State.Offset++
        Skip-NativeJsonSpace $State
        if ($State.Offset -lt $State.Text.Length -and $State.Text[$State.Offset] -eq ']') { $State.Offset++; return }
        while ($true) {
            Read-NativeJsonValue $State ($Depth+1)
            Skip-NativeJsonSpace $State
            if ($State.Offset -ge $State.Text.Length) { throw 'Incomplete JSON array.' }
            $separator = $State.Text[$State.Offset]; $State.Offset++
            if ($separator -eq ']') { return }
            if ($separator -ne ',') { throw 'Invalid JSON array separator.' }
        }
    }
    if ([int]$first -eq 34) { $null = Read-NativeJsonString $State; return }
    foreach ($literal in @('true','false','null')) {
        if ($State.Text.Length - $State.Offset -ge $literal.Length -and
            [string]::CompareOrdinal($State.Text,$State.Offset,$literal,0,$literal.Length) -eq 0) { $State.Offset += $literal.Length; return }
    }
    $number = [regex]::new('\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant,[TimeSpan]::FromMilliseconds(50)).Match($State.Text,$State.Offset)
    if (!$number.Success) { throw 'Invalid native qualification JSON value.' }
    $State.Offset += $number.Length
}


function Get-NativeField($Object, [string]$Name, [bool]$AllowArray=$false) {
    if ($null -eq $Object -or $Object -isnot [PSCustomObject]) { throw 'Qualification evidence requires an object.' }
    $matches = @($Object.PSObject.Properties | Where-Object { $_.Name -ceq $Name })
    if ($matches.Count -ne 1) { throw ('Missing exact qualification field: '+$Name) }
    $value=$matches[0].Value
    if (($AllowArray -and $value -isnot [Array]) -or (!$AllowArray -and $value -is [Array])) { throw 'Unexpected qualification array/scalar field shape.' }
    return $value
}

function Convert-NativeJson([byte[]]$Bytes) {
    $text = [Text.UTF8Encoding]::new($false,$true).GetString($Bytes)
    Assert-NativeJsonUniqueNames $text
    $value = ConvertFrom-Json -InputObject $text
    if ($null -eq $value -or $value -isnot [PSCustomObject]) { throw 'Qualification JSON must contain one object.' }
    return $value
}
function Read-NativeBoundedJson([string]$Path, [long]$Maximum=65536) { return Convert-NativeJson (Read-NativeBoundedBytes $Path $Maximum) }

function Get-NativePipelineProof([string]$Exe, [string]$ManifestPath, [string]$ExpectedManifestSha256) {
    if ($ExpectedManifestSha256 -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Supply the independently pinned lowercase build-manifest SHA-256.' }
    $actualExe = [IO.Path]::GetFullPath($Exe)
    if ([IO.Path]::GetFileName($actualExe) -cne 'SnuWatcher.Desktop.exe') { throw 'Only the installed SNU Watcher executable can be qualified.' }
    $directory = [IO.Path]::GetDirectoryName($actualExe); Assert-NativeDirectory $directory
    $manifestBytes = Read-NativeBoundedBytes $ManifestPath 2097152
    if ((Get-NativeSha256 $manifestBytes) -cne $ExpectedManifestSha256) { throw 'The supplied build manifest does not match its independently pinned SHA-256.' }
    $manifest = Convert-NativeJson $manifestBytes
    Assert-NativeInteger $manifest 'schemaVersion' 1 1
    $source = Get-NativeField $manifest 'source'
    $revision = Get-NativeField $source 'revision'; $dirty = Get-NativeField $source 'dirty'
    if ($revision -isnot [string] -or $revision -cnotmatch '\A[a-f0-9]{40}\z' -or $dirty -isnot [bool]) { throw 'The trusted build source identity is invalid.' }
    foreach ($field in @('version','installerVersion')) {
        $value = Get-NativeField $manifest $field
        if ($value -isnot [string] -or $value.Length -lt 1 -or $value.Length -gt 80 -or $value -match '[\x00-\x20]') { throw 'The trusted build version identity is invalid.' }
    }
    $payload = @(Get-NativeField $manifest 'payload' $true)
    if ($payload.Count -lt 6 -or $payload.Count -gt 2000) { throw 'The trusted payload inventory is outside its bounds.' }
    $names = @('SnuWatcher.Desktop.exe','SnuWatcher.Desktop.dll','SnuWatcher.Windows.dll','SnuWatcher.Infrastructure.dll','SnuWatcher.Application.dll','SnuWatcher.Core.dll')
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); $required = @{}
    foreach ($entry in $payload) {
        $path = Get-NativeField $entry 'path'; $bytes = Get-NativeField $entry 'bytes'; $hash = Get-NativeField $entry 'sha256'
        if ($path -isnot [string] -or $path.Length -gt 512 -or $path -notmatch '\A[^:\\\x00-\x1F]+\z' -or $path.StartsWith('/') -or
            @($path.Split('/') | Where-Object { $_ -in @('','.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -gt 0 -or !$seen.Add($path) -or
            ($bytes -isnot [int] -and $bytes -isnot [long]) -or $bytes -lt 0 -or $hash -isnot [string] -or $hash -cnotmatch '\A[a-f0-9]{64}\z') {
            throw 'The trusted payload inventory contains an invalid or duplicate entry.'
        }
        if ($names -ccontains $path) { $required[$path] = $entry }
    }
    $lines = [Text.StringBuilder]::new()
    foreach ($name in $names) {
        if (!$required.ContainsKey($name)) { throw 'The trusted payload inventory is missing an exact application file.' }
        $bytes = Read-NativeBoundedBytes (Join-Path $directory $name) 67108864
        $hash = Get-NativeSha256 $bytes; $expected = $required[$name]
        if ($bytes.LongLength -ne $expected.bytes -or $hash -cne $expected.sha256) { throw 'The installed application differs from the trusted compiled pipeline; no process was launched.' }
        [void]$lines.Append($name).Append('|').Append($hash).Append("`n")
    }
    return [PSCustomObject]@{ ExecutablePath=$actualExe; ExecutableSha256=$required[$names[0]].sha256.ToUpperInvariant();
        PipelineSha256=(Get-NativeSha256 ([Text.UTF8Encoding]::new($false).GetBytes($lines.ToString())));
        BuildManifestSha256=$ExpectedManifestSha256; SourceRevision=$revision; SourceDirty=$dirty }
}

function Open-NativePipelineLease($Proof) {
    # Keep these exact six files read-only and deny overwrite/delete until the owned child has exited.
    # Rehash the held streams: a library swapped between preflight and lease acquisition cannot launch.
    $streams=[Collections.Generic.List[IO.FileStream]]::new();$lines=[Text.StringBuilder]::new()
    $directory=[IO.Path]::GetDirectoryName($Proof.ExecutablePath);Assert-NativeDirectory $directory
    try {
        foreach($name in @('SnuWatcher.Desktop.exe','SnuWatcher.Desktop.dll','SnuWatcher.Windows.dll','SnuWatcher.Infrastructure.dll','SnuWatcher.Application.dll','SnuWatcher.Core.dll')) {
            $path=Join-Path $directory $name;$attributes=[IO.File]::GetAttributes($path)
            if (($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)) -ne 0) { throw 'The application payload is no longer ordinary.' }
            $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$streams.Add($stream)
            if ($stream.Length -lt 1 -or $stream.Length -gt 67108864) { throw 'Application payload size changed before launch.' }
            $sha=[Security.Cryptography.SHA256]::Create()
            try { $hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
            [void]$lines.Append($name).Append('|').Append($hash).Append("`n")
        }
        if ((Get-NativeSha256 ([Text.UTF8Encoding]::new($false).GetBytes($lines.ToString()))) -cne $Proof.PipelineSha256) { throw 'The compiled pipeline changed before launch.' }
        return [PSCustomObject]@{Streams=$streams.ToArray()}
    } catch { foreach($stream in $streams){$stream.Dispose()};throw }
}

function Write-NativeNewJson([string]$Path, $Value, [int]$Maximum=65536) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 12 -Compress))
    if ($bytes.Length -gt $Maximum) { throw 'Qualification JSON exceeds its write bound.' }
    $stream = [IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function New-NativePrivateRun($Proof, [string]$CaseName, [string]$RunId) {
    if ($RunId -cnotmatch '\A[a-f0-9]{32}\z' -or $RunId -ceq ('0'*32) -or $CaseName -cnotin @('en-sound','ko-muted','permission-disabled')) { throw 'Invalid private qualification identity.' }
    $parent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'SnuWatcher-runtime-validation'))
    $root = [IO.Path]::GetFullPath((Join-Path $parent $RunId))
    if (![string]::Equals([IO.Path]::GetDirectoryName($root),$parent,[StringComparison]::OrdinalIgnoreCase)) { throw 'Private qualification escaped its fixed parent.' }
    Assert-NativeDirectory $root $true
    if ([IO.File]::Exists($root) -or [IO.Directory]::Exists($root)) { throw 'Qualification requires a fresh private profile; existing data is never overlaid.' }
    $null = [IO.Directory]::CreateDirectory($root); Assert-NativeDirectory $root
    Write-NativeNewJson (Join-Path $root 'runtime-validation.json') ([ordered]@{format='snu-runtime-validation-v1';runId=$RunId}) 1024
    Write-NativeNewJson (Join-Path $root 'native-delivery-validation.json') ([ordered]@{format='snu-installed-native-delivery-v1';runId=$RunId;case=$CaseName;
        executableSha256=$Proof.ExecutableSha256;pipelineSha256=$Proof.PipelineSha256}) 1024
    return [PSCustomObject]@{RunId=$RunId;ProfileRoot=$root}
}

function Assert-NativeInteger($Object, [string]$Name, [long]$Minimum, [long]$Maximum) {
    $value = Get-NativeField $Object $Name
    if (($value -isnot [int] -and $value -isnot [long]) -or $value -lt $Minimum -or $value -gt $Maximum) { throw ('Unexpected qualification integer: '+$Name) }
}
function Assert-NativeBoolean($Object, [string]$Name, [bool]$Expected) {
    $value=Get-NativeField $Object $Name
    if ($value -isnot [bool] -or $value -ne $Expected) { throw ('Unexpected qualification boolean: '+$Name) }
}
function Assert-NativeString($Object, [string]$Name, [string]$Expected) {
    $value=Get-NativeField $Object $Name
    if ($value -isnot [string] -or $value -cne $Expected) { throw ('Unexpected qualification string: '+$Name) }
}
function Assert-NativeIdentity($Object, $Proof, [string]$RunId, [string]$CaseName, [int]$OwnedProcessId) {
    foreach ($pair in @(@('runId',$RunId),@('case',$CaseName),@('executableSha256',$Proof.ExecutableSha256),@('pipelineSha256',$Proof.PipelineSha256))) {
        Assert-NativeString $Object $pair[0] $pair[1]
    }
    Assert-NativeInteger $Object 'processId' $OwnedProcessId $OwnedProcessId
}
function Assert-NativeClaim($Claim, $Proof, [string]$RunId, [string]$CaseName, [int]$OwnedProcessId) {
    $fields=@('format','runId','case','executableSha256','pipelineSha256','processId')
    if (@($Claim.PSObject.Properties).Count -ne 6 -or @($Claim.PSObject.Properties | Where-Object { $fields -cnotcontains $_.Name }).Count -ne 0) { throw 'The private native claim must contain exactly six protocol fields.' }
    Assert-NativeString $Claim 'format' 'snu-installed-native-delivery-claim-v1'
    Assert-NativeIdentity $Claim $Proof $RunId $CaseName $OwnedProcessId
}

function Assert-NativePassedResult($Result, $Proof, [string]$RunId, [string]$CaseName, [int]$OwnedProcessId) {
    $fields=@('schemaVersion','codePath','testStatus','code','runId','case','processId','executableSha256','pipelineSha256','fixtureOnly','liveHttpRequests',
        'fixtureHttpRequests','rejectedFixtureRequests','fullBaselineCommitted','initialEvents','openingCommitted','noResendOnReopen','nativeShowCalls','channelSendCalls',
        'nativePlatformId','channelOutcome','eventStatus','chargedAttempts','unresolvedAttempt','readiness','disabledOpeningReadObserved','savedLanguage','savedSoundMuted',
        'nativePayloadSha256','nativeSoundMuted','workersDrained','ownerHandoff','visibleBannerConfirmed','soundConfirmed','buttonClickConfirmed','bodyClickConfirmed',
        'cleanVmGatePassed','eventId','batchId','attemptId','triggerObservedAtUtc','nativeResultRecordedAtUtc')
    if (@($Result.PSObject.Properties | Where-Object { $fields -cnotcontains $_.Name }).Count -ne 0) { throw 'Unexpected native qualification result field.' }
    Assert-NativeIdentity $Result $Proof $RunId $CaseName $OwnedProcessId
    Assert-NativeInteger $Result 'schemaVersion' 1 1
    foreach ($pair in @(@('codePath','native-delivery-validation'),@('testStatus','PASSED'),@('code','installed_native_delivery_passed'))) {
        Assert-NativeString $Result $pair[0] $pair[1]
    }
    foreach ($field in @('fixtureOnly','fullBaselineCommitted','openingCommitted','noResendOnReopen','workersDrained','ownerHandoff')) { Assert-NativeBoolean $Result $field $true }
    foreach ($field in @('unresolvedAttempt','visibleBannerConfirmed','soundConfirmed','buttonClickConfirmed','bodyClickConfirmed','cleanVmGatePassed')) { Assert-NativeBoolean $Result $field $false }
    foreach ($field in @('liveHttpRequests','rejectedFixtureRequests','initialEvents')) { Assert-NativeInteger $Result $field 0 0 }
    Assert-NativeInteger $Result 'fixtureHttpRequests' 1 64
    $disabled = $CaseName -ceq 'permission-disabled'; $muted = $CaseName -ceq 'ko-muted'
    $count = if ($disabled) { 0 } else { 1 }
    foreach ($field in @('nativeShowCalls','channelSendCalls','chargedAttempts')) { Assert-NativeInteger $Result $field $count $count }
    foreach ($pair in @(@('eventStatus',$(if($disabled){'Pending'}else{'OsAccepted'})),@('readiness',$(if($disabled){'Disabled'}else{'Ready'})),@('savedLanguage',$(if($muted){'Korean'}else{'English'})))) {
        Assert-NativeString $Result $pair[0] $pair[1]
    }
    Assert-NativeBoolean $Result 'disabledOpeningReadObserved' $disabled
    Assert-NativeBoolean $Result 'savedSoundMuted' $muted
    $eventId = Get-NativeField $Result 'eventId'
    if ($eventId -isnot [string] -or $eventId -cnotmatch '\A[a-f0-9]{32}\z' -or $eventId -ceq ('0'*32)) { throw 'A canonical opening event identity is required.' }
    if ($disabled) {
        foreach ($field in @('nativePlatformId','channelOutcome','nativePayloadSha256','nativeSoundMuted','attemptId')) {
            if ($null -ne (Get-NativeField $Result $field)) { throw 'Disabled qualification must not invent native submission or reservation facts.' }
        }
        $batchId=Get-NativeField $Result 'batchId'
        if ($null -ne $batchId -and ($batchId -isnot [string] -or $batchId -cnotmatch '\A[a-f0-9]{32}\z' -or $batchId -ceq ('0'*32))) { throw 'A projected pending batch identity must be canonical.' }
    } else {
        Assert-NativeInteger $Result 'nativePlatformId' 1 4294967295
        Assert-NativeString $Result 'channelOutcome' 'OsAccepted'
        $payloadHash=Get-NativeField $Result 'nativePayloadSha256'
        if ($payloadHash -isnot [string] -or $payloadHash -cnotmatch '\A[a-f0-9]{64}\z') { throw 'An exact labelled payload hash is required.' }
        Assert-NativeBoolean $Result 'nativeSoundMuted' $muted
        foreach ($field in @('batchId','attemptId')) {
            $id=Get-NativeField $Result $field
            if ($id -isnot [string] -or $id -cnotmatch '\A[a-f0-9]{32}\z' -or $id -ceq ('0'*32)) { throw 'Accepted native qualification requires canonical batch and attempt identities.' }
        }
    }
}

function Invoke-NativeOwnedProcess([string]$Exe, [string]$Argument, [string]$ArtifactRoot, [int]$DeadlineSeconds) {
    $stdout=Join-Path $ArtifactRoot 'stdout.txt'; $stderr=Join-Path $ArtifactRoot 'stderr.json'; $started=[DateTimeOffset]::UtcNow
    $process=Start-Process -FilePath $Exe -ArgumentList $Argument -WorkingDirectory ([IO.Path]::GetDirectoryName($Exe)) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        $ownedHandle=$process.Handle; $ownedProcessId=$process.Id; $timedOut=$false; $terminationAttempted=$false
        if ($ownedHandle -eq [IntPtr]::Zero) { throw 'The owned qualification process handle is unavailable.' }
        $timedOut = !$process.WaitForExit($DeadlineSeconds*1000)
        if ($timedOut) {
            $process.Refresh()
            if (!$process.HasExited) {
                if (![string]::Equals([IO.Path]::GetFullPath($process.MainModule.FileName),$Exe,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing termination of an unverified process.' }
                $terminationAttempted=$true; $process.Kill()
                if (!$process.WaitForExit(5000)) { throw 'The owned qualification process did not terminate after its watchdog.' }
            }
        }
        $process.WaitForExit(); $exitCode=$process.ExitCode
        Write-NativeNewJson (Join-Path $ArtifactRoot 'process.json') ([ordered]@{processId=$ownedProcessId;executablePath=$Exe;argument=$Argument;
            exitCode=$exitCode;timedOut=$timedOut;terminationAttempted=$terminationAttempted;startedAtUtc=$started.ToString('O');exitedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');
            watchdogProvesNativeDrain=$false})
        if ($timedOut) { throw 'Native qualification timed out; terminating its owned process does not prove the synchronous SDK call drained. Evidence is preserved.' }
        if ($null -eq $exitCode) { throw 'The owned qualification process exit code is unavailable.' }
        return [PSCustomObject]@{ProcessId=$ownedProcessId;ExitCode=$exitCode;Stdout=$stdout;Stderr=$stderr}
    } finally { $process.Dispose() }
}

function Invoke-InstalledNativeDelivery([string]$Exe, [string]$ManifestPath, [string]$ManifestSha256, [string]$CaseName, [int]$DeadlineSeconds) {
    # This MUST precede marker creation and process launch: an old apphost can match while its managed entry route does not.
    $proof=Get-NativePipelineProof $Exe $ManifestPath $ManifestSha256
    if ($CaseName -cnotin @('en-sound','ko-muted','permission-disabled') -or $DeadlineSeconds -lt 120 -or $DeadlineSeconds -gt 300) { throw 'Invalid native qualification case or deadline.' }
    $payloadLease=Open-NativePipelineLease $proof
    try {
    $runId=[Guid]::NewGuid().ToString('N'); $privateRun=New-NativePrivateRun $proof $CaseName $runId
    $artifactRoot=Join-Path $script:NativeQualificationRepository ('artifacts/InstalledNativeDelivery/'+$runId)
    Assert-NativeDirectory $artifactRoot $true
    if ([IO.Directory]::Exists($artifactRoot) -or [IO.File]::Exists($artifactRoot)) { throw 'A fresh qualification artifact directory is required.' }
    $null=[IO.Directory]::CreateDirectory($artifactRoot); Assert-NativeDirectory $artifactRoot
    Write-NativeNewJson (Join-Path $artifactRoot 'trusted-build.json') ([ordered]@{schemaVersion=1;buildManifestSha256=$proof.BuildManifestSha256;sourceRevision=$proof.SourceRevision;
        sourceDirty=$proof.SourceDirty;pipelineSha256=$proof.PipelineSha256;executableSha256=$proof.ExecutableSha256;runId=$runId;case=$CaseName})
    Write-Output ('Native qualification artifacts: '+$artifactRoot)
    Write-Output ('Marked private fixture profile: '+$privateRun.ProfileRoot)
    $process=Invoke-NativeOwnedProcess $proof.ExecutablePath ('--native-delivery-validation='+$runId) $artifactRoot $DeadlineSeconds
    if ($process.ExitCode -ne 0) { throw 'Installed native qualification failed; inspect its preserved fixed-code result and process evidence.' }
    foreach($path in @($process.Stdout,$process.Stderr)) {
        $file=Get-Item -LiteralPath $path -Force
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -ne 0) { throw 'Successful native qualification must keep ordinary stdout and stderr files empty.' }
    }
    Assert-NativeDirectory $privateRun.ProfileRoot
    $claim=Read-NativeBoundedJson (Join-Path $privateRun.ProfileRoot 'native-delivery-claim.json') 1024
    Assert-NativeClaim $claim $proof $runId $CaseName $process.ProcessId
    $result=Read-NativeBoundedJson (Join-Path $privateRun.ProfileRoot ('result-'+$process.ProcessId+'.json'))
    Assert-NativePassedResult $result $proof $runId $CaseName $process.ProcessId
    Write-NativeNewJson (Join-Path $artifactRoot 'result.json') $result
    $result | ConvertTo-Json -Depth 12 -Compress
    Write-Output 'Machine pipeline qualification passed. Banner, sound or silence, and separate body/button destination observations remain unconfirmed; this is not clean-VM acceptance.'
    } finally { foreach($stream in $payloadLease.Streams){$stream.Dispose()} }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or [IntPtr]::Size -ne 8) { throw 'Use 64-bit Windows PowerShell for installed native qualification.' }
$script:NativeQualificationRepository=Split-Path $PSScriptRoot -Parent
Invoke-InstalledNativeDelivery $InstalledExecutablePath $BuildManifestPath $ExpectedBuildManifestSha256 $Case $TimeoutSeconds
