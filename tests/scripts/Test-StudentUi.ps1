# Publishes the actual WPF app, then runs only its marked offline UI-validation entry.
# Fresh artifacts/profile are preserved; no normal profile or native diagnostics are opened.
[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-UiDirectory([string]$Path, [bool]$AllowMissing) {
    $full = [IO.Path]::GetFullPath($Path)
    $current = [IO.Path]::GetPathRoot($full)
    foreach ($part in $full.Substring($current.Length).Split([char[]]@([IO.Path]::DirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
            if ($AllowMissing) { continue }
            throw 'A required private UI directory is missing.'
        }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or ($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
            throw 'Private UI directories must be ordinary directories without reparse points.'
        }
    }
}

function Read-UiJson([string]$Path) {
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -lt 1 -or $file.Length -gt 65536) {
        throw 'Private UI evidence must be an ordinary bounded JSON file.'
    }
    $json = [IO.File]::ReadAllText($file.FullName, [Text.UTF8Encoding]::new($false, $true))
    Assert-UiJsonUniqueNames $json
    $value = ConvertFrom-Json -InputObject $json
    if ($null -eq $value -or $value -is [Array] -or $value -isnot [PSCustomObject]) { throw 'Private UI evidence must contain one object.' }
    return $value
}

# PowerShell 5.1 collapses duplicate names during ConvertFrom-Json. Validate JSON grammar and
# decoded per-object names first, with no extra runtime/module requirement or permissive parser.
function Assert-UiJsonUniqueNames([string]$Text) {
    $state = [PSCustomObject]@{ Text=$Text; Offset=0; Nodes=0 }
    Read-UiJsonValue $state 0
    Skip-UiJsonSpace $state
    if ($state.Offset -ne $Text.Length) { throw 'Private UI JSON has trailing tokens.' }
}

function Skip-UiJsonSpace($State) {
    while ($State.Offset -lt $State.Text.Length -and [int][char]$State.Text[$State.Offset] -in @(9,10,13,32)) { $State.Offset++ }
}

function Read-UiJsonHex($State) {
    if ($State.Text.Length - $State.Offset -lt 4) { throw 'Incomplete JSON Unicode escape.' }
    $hex = $State.Text.Substring($State.Offset,4)
    if (![regex]::IsMatch($hex,'\A[0-9A-Fa-f]{4}\z')) { throw 'Invalid JSON Unicode escape.' }
    $State.Offset += 4
    return [Convert]::ToInt32($hex,16)
}

function Read-UiJsonString($State) {
    if ($State.Offset -ge $State.Text.Length -or [int][char]$State.Text[$State.Offset] -ne 34) { throw 'A JSON string was required.' }
    $State.Offset++
    $decoded = [Text.StringBuilder]::new()
    while ($State.Offset -lt $State.Text.Length) {
        $character = [char]$State.Text[$State.Offset]
        $State.Offset++
        if ([int]$character -eq 34) { return $decoded.ToString() }
        if ([int]$character -lt 32) { throw 'Unescaped control character in private UI JSON.' }
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
                    $unit = Read-UiJsonHex $State
                    if ($unit -ge 55296 -and $unit -le 56319) {
                        if ($State.Text.Length - $State.Offset -lt 6 -or [int][char]$State.Text[$State.Offset] -ne 92 -or
                            [int][char]$State.Text[$State.Offset+1] -ne 117) { throw 'Unpaired JSON Unicode surrogate.' }
                        $State.Offset += 2
                        $low = Read-UiJsonHex $State
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

function Read-UiJsonValue($State, [int]$Depth) {
    $State.Nodes++
    if ($Depth -gt 16 -or $State.Nodes -gt 4096) { throw 'Private UI JSON exceeds its structural bounds.' }
    Skip-UiJsonSpace $State
    if ($State.Offset -ge $State.Text.Length) { throw 'Incomplete private UI JSON.' }
    $first = [char]$State.Text[$State.Offset]
    if ($first -eq '{') {
        $State.Offset++
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        Skip-UiJsonSpace $State
        if ($State.Offset -lt $State.Text.Length -and $State.Text[$State.Offset] -eq '}') { $State.Offset++; return }
        while ($true) {
            $name = Read-UiJsonString $State
            if (!$names.Add($name)) { throw 'Duplicate decoded JSON property name.' }
            Skip-UiJsonSpace $State
            if ($State.Offset -ge $State.Text.Length -or $State.Text[$State.Offset] -ne ':') { throw 'A JSON property colon was required.' }
            $State.Offset++
            Read-UiJsonValue $State ($Depth+1)
            Skip-UiJsonSpace $State
            if ($State.Offset -ge $State.Text.Length) { throw 'Incomplete JSON object.' }
            $separator = $State.Text[$State.Offset]; $State.Offset++
            if ($separator -eq '}') { return }
            if ($separator -ne ',') { throw 'Invalid JSON object separator.' }
            Skip-UiJsonSpace $State
        }
    }
    if ($first -eq '[') {
        $State.Offset++
        Skip-UiJsonSpace $State
        if ($State.Offset -lt $State.Text.Length -and $State.Text[$State.Offset] -eq ']') { $State.Offset++; return }
        while ($true) {
            Read-UiJsonValue $State ($Depth+1)
            Skip-UiJsonSpace $State
            if ($State.Offset -ge $State.Text.Length) { throw 'Incomplete JSON array.' }
            $separator = $State.Text[$State.Offset]; $State.Offset++
            if ($separator -eq ']') { return }
            if ($separator -ne ',') { throw 'Invalid JSON array separator.' }
        }
    }
    if ([int]$first -eq 34) { $null = Read-UiJsonString $State; return }
    foreach ($literal in @('true','false','null')) {
        if ($State.Text.Length - $State.Offset -ge $literal.Length -and
            [string]::CompareOrdinal($State.Text,$State.Offset,$literal,0,$literal.Length) -eq 0) { $State.Offset += $literal.Length; return }
    }
    $number = [regex]::new('\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant,[TimeSpan]::FromMilliseconds(50)).Match($State.Text,$State.Offset)
    if (!$number.Success) { throw 'Invalid private UI JSON value.' }
    $State.Offset += $number.Length
}

function Get-UiField($Object, [string]$Name) {
    $fields = @($Object.PSObject.Properties | Where-Object { $_.Name -ceq $Name })
    if ($fields.Count -ne 1) { throw ('Missing exact private UI evidence field: ' + $Name) }
    return $fields[0].Value
}

function Assert-UiTrue($Object, [string]$Name) {
    $value = Get-UiField $Object $Name
    if ($value -isnot [bool] -or !$value) { throw ('Private UI proof is not true: ' + $Name) }
}

function Assert-UiInteger($Object, [string]$Name, [long]$Minimum, [long]$Maximum) {
    $value = Get-UiField $Object $Name
    if (($value -isnot [int] -and $value -isnot [long]) -or $value -lt $Minimum -or $value -gt $Maximum) {
        throw ('Private UI evidence has an invalid integer: ' + $Name)
    }
    return [long]$value
}

function Get-UiRealNumber($Object, [string]$Name, [double]$Minimum, [double]$Maximum) {
    $value = Get-UiField $Object $Name
    if (($value -isnot [int] -and $value -isnot [long] -and $value -isnot [double] -and $value -isnot [decimal]) -or
        [double]::IsNaN([double]$value) -or [double]::IsInfinity([double]$value) -or $value -lt $Minimum -or $value -gt $Maximum) {
        throw ('Private UI evidence has an invalid real number: ' + $Name)
    }
    return [double]$value
}

function Get-UiBox($Object) {
    return [PSCustomObject]@{ left=(Get-UiRealNumber $Object 'left' -65536 65536); top=(Get-UiRealNumber $Object 'top' -65536 65536);
        width=(Get-UiRealNumber $Object 'width' 1 32768); height=(Get-UiRealNumber $Object 'height' 1 32768) }
}

function Assert-UiContained($Window, $Area) {
    if ($Window.left -lt ($Area.left - 1) -or $Window.top -lt ($Area.top - 1) -or
        ($Window.left + $Window.width) -gt ($Area.left + $Area.width + 1) -or
        ($Window.top + $Window.height) -gt ($Area.top + $Area.height + 1)) {
        throw 'Actual UI window bounds do not fit their recorded OS work area.'
    }
}

function Assert-UiWorkArea($Result) {
    Assert-UiTrue $Result 'actualWorkAreaVerified'
    $area = Get-UiField $Result 'workArea'
    foreach ($name in @('startupBoundsWithinWorkArea','restoredBoundsWithinWorkArea','searchRowReachable','searchControlsReachable',
            'watchesRowReachable','watchesControlsReachable','settingsControlsReachable','quitControlReachable')) { Assert-UiTrue $area $name }
    $null = Get-UiRealNumber $area 'actualOsDpiX' 72 768
    $null = Get-UiRealNumber $area 'actualOsDpiY' 72 768
    $null = Assert-UiInteger $area 'primaryScreenPixelWidth' 1 65536
    $null = Assert-UiInteger $area 'primaryScreenPixelHeight' 1 65536
    Assert-UiContained (Get-UiBox (Get-UiField $area 'startupWindowBounds')) (Get-UiBox (Get-UiField $area 'workAreaBounds'))
    Assert-UiContained (Get-UiBox (Get-UiField $area 'restoredWindowBounds')) (Get-UiBox (Get-UiField $area 'restoredWorkAreaBounds'))
}

function Assert-UiResult($Result, [string]$RunId, [int]$OwnedProcessId) {
    $null = Assert-UiInteger $Result 'schemaVersion' 1 1
    $null = Assert-UiInteger $Result 'processId' $OwnedProcessId $OwnedProcessId
    if ((Get-UiField $Result 'codePath') -cne 'ui-validation' -or (Get-UiField $Result 'runId') -cne $RunId -or
        (Get-UiField $Result 'testStatus') -cne 'PASSED' -or (Get-UiField $Result 'code') -cne 'student_ui_validation_passed') {
        throw 'The private UI result does not identify this exact run and executable process.'
    }
    foreach ($name in @('profileGuarded','isDemo','noLiveHttp','noOsNotifications','noBrowser','noClipboard','noSimulatedAcceptance',
            'deliveryDisabled','localStartupOnly','initializationJoined','graphDrained','hostDrained','reopenStable','ownerHandoff',
            'ownerRetainedDuringDrain','sourceDisposed','quitObserved','glyphGuardsVerified')) { Assert-UiTrue $Result $name }
    foreach ($name in @('httpRequests','nativeNotificationCalls','browserCalls','clipboardCalls','channelCalls','bindingErrors')) {
        $null = Assert-UiInteger $Result $name 0 0
    }
    $null = Assert-UiInteger $Result 'pendingAlertsPreserved' 1 100
    if ((Get-UiField $Result 'keyboardScope') -cne 'in-process WPF focus and routed events') { throw 'The keyboard proof scope is missing.' }
    $journey = Get-UiField $Result 'journey'
    foreach ($name in @('onboarding','englishKorean','leadingZeroLookup','detailsQuotaSeparated','groupWatch','autoRoutedWatch',
            'pauseResume','rename','expiry','profileApplied','fullVsUnknown','interactiveUnavailable','invalidYearBlocked',
            'tabTraversal','enterSearch','fixedActionIntents','terminalStoppingVisible')) { Assert-UiTrue $journey $name }
    $source = Get-UiField $Result 'source'
    $null = Assert-UiInteger $source 'catalogCalls' 1 100
    $null = Assert-UiInteger $source 'interactiveSearchCalls' 3 100
    $null = Assert-UiInteger $source 'watchSearchCalls' 1 100
    $null = Assert-UiInteger $source 'detailsCalls' 2 100
    Assert-UiWorkArea $Result
}

function Copy-UiCaptures($Result, [string]$ProfileRoot, [string]$RunRoot) {
    $captures = @(Get-UiField $Result 'captures')
    if ($captures.Count -ne 12) { throw 'Unexpected number of private UI captures.' }
    $allowed = @('ui-onboarding-en.png','ui-search-en-normal.png','ui-search-ko-normal.png','ui-search-ko-narrow.png',
        'ui-watches-en-normal.png','ui-watches-ko-narrow.png','ui-settings-en-normal.png','ui-expired-ko.png',
        'ui-actual-search.png','ui-actual-watches-list.png','ui-actual-watches-details.png','ui-actual-settings.png')
    $seen = @{}
    $destination = Join-Path $RunRoot 'captures'
    $null = [IO.Directory]::CreateDirectory($destination)
    Assert-UiDirectory $destination $false
    foreach ($capture in $captures) {
        $leaf = Get-UiField $capture 'fileName'
        if ($leaf -isnot [string] -or $allowed -cnotcontains $leaf -or $seen.ContainsKey($leaf)) { throw 'Invalid or repeated capture leaf.' }
        $seen[$leaf] = $true
        $width = Assert-UiInteger $capture 'width' 320 2000
        $height = Assert-UiInteger $capture 'height' 240 1600
        $null = Assert-UiInteger $capture 'exportDpi' 96 96
        foreach ($axis in @('actualOsDpiX','actualOsDpiY')) {
            $dpi = Get-UiField $capture $axis
            if (($dpi -isnot [int] -and $dpi -isnot [long] -and $dpi -isnot [double] -and $dpi -isnot [decimal]) -or
                [double]::IsNaN([double]$dpi) -or [double]::IsInfinity([double]$dpi) -or $dpi -lt 72 -or $dpi -gt 768) { throw 'Invalid actual Windows DPI evidence.' }
        }
        $path = Join-Path $ProfileRoot $leaf
        $file = Get-Item -LiteralPath $path -Force
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -lt 24 -or $file.Length -gt 16777216) {
            throw 'Private UI capture is not an ordinary bounded PNG.'
        }
        $expectedHash = Get-UiField $capture 'sha256'
        if ($expectedHash -isnot [string] -or $expectedHash -cnotmatch '\A[A-F0-9]{64}\z' -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $expectedHash) { throw 'Private UI capture hash mismatch.' }
        $header = New-Object byte[] 24
        $stream = [IO.File]::OpenRead($path)
        try { if ($stream.Read($header, 0, 24) -ne 24) { throw 'Incomplete PNG header.' } } finally { $stream.Dispose() }
        $signature = @(137,80,78,71,13,10,26,10)
        for ($index = 0; $index -lt 8; $index++) { if ($header[$index] -ne $signature[$index]) { throw 'The capture is not a PNG.' } }
        $pngWidth = [long]$header[16]*16777216 + [long]$header[17]*65536 + [long]$header[18]*256 + $header[19]
        $pngHeight = [long]$header[20]*16777216 + [long]$header[21]*65536 + [long]$header[22]*256 + $header[23]
        if ($pngWidth -ne $width -or $pngHeight -ne $height) { throw 'Capture dimensions do not match its PNG header.' }
        [IO.File]::Copy($path, (Join-Path $destination $leaf), $false)
    }
    foreach ($required in $allowed) { if (!$seen.ContainsKey($required)) { throw 'A required UI capture is missing.' } }
}

function Get-UiPublishManifest([string]$PublishRoot, [string]$RunId) {
    Assert-UiDirectory $PublishRoot $false
    $root = [IO.Path]::GetFullPath($PublishRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $pending = [Collections.Generic.Queue[string]]::new()
    $paths = [Collections.Generic.List[string]]::new()
    $pending.Enqueue($root)
    $directories = 0
    while ($pending.Count -ne 0) {
        $directory = $pending.Dequeue()
        Assert-UiDirectory $directory $false
        $directories++
        if ($directories -gt 2048) { throw 'The published UI directory inventory is too large.' }
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The published UI inventory contains a reparse point.' }
            if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
            else {
                if ($paths.Count -ge 4096) { throw 'The published UI file inventory is too large.' }
                $paths.Add($entry.FullName)
            }
        }
    }
    $ordered = $paths.ToArray()
    [Array]::Sort($ordered, [StringComparer]::Ordinal)
    $files = [Collections.Generic.List[object]]::new()
    [long]$totalBytes = 0
    foreach ($path in $ordered) {
        $file = Get-Item -LiteralPath $path -Force
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -gt 536870912) {
            throw 'The published UI inventory contains an invalid file.'
        }
        $totalBytes += $file.Length
        if ($totalBytes -gt 2147483648) { throw 'The published UI byte inventory is too large.' }
        $relative = $path.Substring($root.Length + 1).Replace([char]92,[char]47)
        if ($relative.Length -gt 512 -or $relative.StartsWith('/') -or $relative.Split('/') -contains '..') { throw 'A published UI inventory path is invalid.' }
        $files.Add([PSCustomObject]@{ relativePath=$relative; length=[long]$file.Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash })
    }
    if ($files.Count -eq 0) { throw 'The published UI inventory is empty.' }
    return ([PSCustomObject]@{ schemaVersion=1; runId=$RunId; files=$files.ToArray() } | ConvertTo-Json -Depth 4 -Compress)
}

function Write-UiNewText([string]$Path, [string]$Text) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    if ($bytes.Length -lt 1 -or $bytes.Length -gt 1048576) { throw 'Private UI proof text is outside its bounds.' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush() } finally { $stream.Dispose() }
}

function Invoke-OwnedStudentUi([string]$Exe, [string]$Argument, [string]$Name, [string]$RunRoot) {
    $stdout = Join-Path $RunRoot ($Name + '.stdout.txt')
    $stderr = Join-Path $RunRoot ($Name + '.stderr.json')
    $started = [DateTimeOffset]::UtcNow
    $process = Start-Process -FilePath $Exe -ArgumentList $Argument -WorkingDirectory ([IO.Path]::GetDirectoryName($Exe)) -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        $ownedHandle = $process.Handle
        if ($null -eq $ownedHandle -or $ownedHandle -eq [IntPtr]::Zero) { throw 'The private UI process handle is unavailable.' }
        $ownedProcessId = $process.Id
        $timedOut = !$process.WaitForExit(90000)
        if ($timedOut) {
            $process.Refresh()
            if (!$process.HasExited) {
                if (![string]::Equals([IO.Path]::GetFullPath($process.MainModule.FileName), $Exe, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Refusing to terminate an unverified private UI process.'
                }
                $process.Kill()
                if (!$process.WaitForExit(5000)) { throw 'The verified owned UI process did not stop.' }
            }
        }
        $process.WaitForExit()
        $exitCode = $process.ExitCode
        if ($null -eq $exitCode) { throw 'The actual private UI exit code is unavailable.' }
        $evidence = [PSCustomObject]@{ runId=[IO.Path]::GetFileName($RunRoot); processId=$ownedProcessId; exitCode=$exitCode; executablePath=$Exe; handleObtained=$true;
            timedOut=$timedOut; startedAtUtc=$started.ToString('O'); exitedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
        [IO.File]::WriteAllText((Join-Path $RunRoot ($Name + '-process.json')), ($evidence | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        if ($timedOut) { throw 'Private UI validation exceeded 90 seconds; its artifacts and profile are preserved.' }
        return [PSCustomObject]@{ ProcessId=$ownedProcessId; ExitCode=$exitCode; Stdout=$stdout; Stderr=$stderr }
    } finally { $process.Dispose() }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or [IntPtr]::Size -ne 8) { throw 'Use 64-bit Windows PowerShell.' }
$scriptInfo = Get-Item -LiteralPath $PSCommandPath -Force
if ($scriptInfo.PSIsContainer -or ($scriptInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The UI validator must be an ordinary script file.' }
$scriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$repoRoot = [IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$runId = [Guid]::NewGuid().ToString('N')
$artifactParent = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/StudentUi'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $artifactParent $runId))
$publishRoot = Join-Path $runRoot 'publish'
$validationParent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'SnuWatcher-runtime-validation'))
$profileRoot = [IO.Path]::GetFullPath((Join-Path $validationParent $runId))
if ([IO.Path]::GetDirectoryName($runRoot) -cne $artifactParent -or [IO.Path]::GetDirectoryName($profileRoot) -cne $validationParent) {
    throw 'The private UI paths escaped their fixed parents.'
}
Assert-UiDirectory $artifactParent $true
Assert-UiDirectory $validationParent $true
if ([IO.Directory]::Exists($runRoot) -or [IO.File]::Exists($runRoot) -or [IO.Directory]::Exists($profileRoot) -or [IO.File]::Exists($profileRoot)) {
    throw 'Refusing to overlay an existing UI artifact or profile.'
}
$null = [IO.Directory]::CreateDirectory($runRoot)
Assert-UiDirectory $runRoot $false
Push-Location $repoRoot
try {
    $expectedSdk = (Read-UiJson (Join-Path $repoRoot 'global.json')).sdk.version
    if ((& $Dotnet --version).Trim() -cne $expectedSdk) { throw 'The private UI publisher requires the pinned SDK.' }
    & $Dotnet publish 'src/SnuWatcher.Desktop/SnuWatcher.Desktop.csproj' --configuration Release --runtime win-x64 --self-contained true `
        --output $publishRoot --property:RestoreLockedMode=true --property:UseSharedCompilation=false -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'The private UI publish failed; artifacts are preserved.' }
} finally { Pop-Location }
$exe = [IO.Path]::GetFullPath((Join-Path $publishRoot 'SnuWatcher.Desktop.exe'))
foreach ($leaf in @('SnuWatcher.Desktop.exe','SnuWatcher.Desktop.runtimeconfig.json','coreclr.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $publishRoot $leaf) -PathType Leaf)) { throw 'The private UI publish is not self contained.' }
}
$publishManifestText = Get-UiPublishManifest $publishRoot $runId
$publishManifestPath = Join-Path $runRoot 'publish-manifest.json'
Write-UiNewText $publishManifestPath $publishManifestText
if ([IO.Directory]::Exists($profileRoot) -or [IO.File]::Exists($profileRoot)) { throw 'The private profile appeared during publish.' }
$null = [IO.Directory]::CreateDirectory($profileRoot)
Assert-UiDirectory $profileRoot $false
$markerPath = Join-Path $profileRoot 'runtime-validation.json'
$markerText = '{"format":"snu-runtime-validation-v1","runId":"' + $runId + '"}'
$markerBytes = [Text.UTF8Encoding]::new($false).GetBytes($markerText)
$marker = [IO.File]::Open($markerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $marker.Write($markerBytes,0,$markerBytes.Length); $marker.Flush() } finally { $marker.Dispose() }

$valid = Invoke-OwnedStudentUi $exe ('--ui-validation=' + $runId) 'valid' $runRoot
if ($valid.ExitCode -ne 0) { throw ('The private UI child failed; inspect preserved evidence in ' + $runRoot) }
if ((Get-Item -LiteralPath $valid.Stdout -Force).Length -ne 0 -or (Get-Item -LiteralPath $valid.Stderr -Force).Length -ne 0) {
    throw 'A successful private UI run must leave stdout/stderr empty.'
}
Assert-UiDirectory $profileRoot $false
$markerInfo = Get-Item -LiteralPath $markerPath -Force
if ($markerInfo.PSIsContainer -or ($markerInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $markerInfo.Length -ne $markerBytes.Length -or [IO.File]::ReadAllText($markerPath,[Text.UTF8Encoding]::new($false,$true)) -cne $markerText) {
    throw 'The private UI marker changed.'
}
$result = Read-UiJson (Join-Path $profileRoot ('result-' + $valid.ProcessId + '.json'))
Assert-UiResult $result $runId $valid.ProcessId
$database = Get-Item -LiteralPath (Join-Path $profileRoot 'watches.sqlite3') -Force
if ($database.PSIsContainer -or ($database.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $database.Length -lt 16 -or $database.Length -gt 20971520) {
    throw 'The private UI database is not an ordinary bounded file.'
}
Copy-UiCaptures $result $profileRoot $runRoot
$resultCopyPath = Join-Path $runRoot 'result.json'
Write-UiNewText $resultCopyPath ($result | ConvertTo-Json -Depth 10 -Compress)

$invalid = Invoke-OwnedStudentUi $exe '--ui-validation-wrong' 'invalid' $runRoot
if ($invalid.ExitCode -ne 2 -or (Get-Item -LiteralPath $invalid.Stdout -Force).Length -ne 0) { throw 'Invalid UI input did not exit privately with code 2.' }
$rejection = Read-UiJson $invalid.Stderr
$null = Assert-UiInteger $rejection 'schemaVersion' 1 1
foreach ($name in @('httpRequests','nativeNotificationCalls','browserCalls','clipboardCalls')) { $null = Assert-UiInteger $rejection $name 0 0 }
if ((Get-UiField $rejection 'codePath') -cne 'ui-validation' -or (Get-UiField $rejection 'testStatus') -cne 'REJECTED' -or
    (Get-UiField $rejection 'code') -cne 'invalid_ui_validation_argument') { throw 'Invalid UI input escaped its private rejection route.' }
if (Test-Path -LiteralPath (Join-Path $profileRoot ('result-' + $invalid.ProcessId + '.json'))) { throw 'Invalid UI input wrote a profile result.' }
if ((Get-UiPublishManifest $publishRoot $runId) -cne $publishManifestText) { throw 'Published UI bytes changed during validation.' }
if ((Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash -cne $scriptSha256) { throw 'The UI validation script changed during its run.' }
$runManifest = [PSCustomObject]@{ schemaVersion=1; runId=$runId; validProcessId=$valid.ProcessId; invalidProcessId=$invalid.ProcessId;
    validExitCode=$valid.ExitCode; invalidExitCode=$invalid.ExitCode;
    publishManifestSha256=(Get-FileHash -LiteralPath $publishManifestPath -Algorithm SHA256).Hash;
    resultSha256=(Get-FileHash -LiteralPath $resultCopyPath -Algorithm SHA256).Hash;
    scriptSha256=$scriptSha256 }
Write-UiNewText (Join-Path $runRoot 'run-manifest.json') ($runManifest | ConvertTo-Json -Compress)
$result | ConvertTo-Json -Depth 10 -Compress
Write-Output 'Published offline UI validation passed. Browser/clipboard intentions were recorded; no HTTP or OS notifications were used.'
Write-Output 'PNG export DPI is recorded separately from actual Windows DPI. These are not separate Windows DPI-mode qualification tests.'
