# Publish the actual WPF executable and exercise only its private, offline runtime-validation entry.
# Preserves the fresh publish, marked profile and process evidence. It never launches the default app.
[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-OrdinaryDirectoryChain([string]$Path, [bool]$AllowMissing) {
    $full = [IO.Path]::GetFullPath($Path)
    $current = [IO.Path]::GetPathRoot($full)
    foreach ($part in $full.Substring($current.Length).Split([char[]]@([IO.Path]::DirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
            if ($AllowMissing) { continue }
            throw 'A required private validation directory is missing.'
        }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or ($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
            throw 'Private validation directories must be ordinary directories, without reparse points.'
        }
    }
}

function Read-BoundedJson([string]$Path) {
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -lt 1 -or $file.Length -gt 65536) {
        throw 'Private runtime JSON evidence must be an ordinary bounded file.'
    }
    $text = [IO.File]::ReadAllText($file.FullName, [Text.UTF8Encoding]::new($false, $true))
    $value = ConvertFrom-Json -InputObject $text
    if ($null -eq $value -or $value -is [Array] -or $value -isnot [PSCustomObject]) { throw 'Private runtime evidence must be one JSON object.' }
    return $value
}

function Get-RequiredField($Object, [string]$Name) {
    $matches = @($Object.PSObject.Properties | Where-Object { $_.Name -ceq $Name })
    if ($matches.Count -ne 1) { throw ('Missing exact runtime evidence field: ' + $Name) }
    return $matches[0].Value
}

function Assert-TrueField($Object, [string]$Name) {
    $value = Get-RequiredField $Object $Name
    if ($value -isnot [bool] -or !$value) { throw ('Runtime evidence is not true: ' + $Name) }
}

function Assert-IntegerField($Object, [string]$Name, [long]$Expected, [bool]$AtLeast = $false) {
    $value = Get-RequiredField $Object $Name
    if (($value -isnot [int] -and $value -isnot [long]) -or (!$AtLeast -and $value -ne $Expected) -or ($AtLeast -and $value -lt $Expected)) {
        throw ('Runtime evidence has an unexpected integer: ' + $Name)
    }
}

function Assert-PassedResult($Result, [string]$RunId) {
    Assert-IntegerField $Result 'schemaVersion' 1
    if ((Get-RequiredField $Result 'codePath') -cne 'monitor-validation' -or
        (Get-RequiredField $Result 'testStatus') -cne 'PASSED' -or
        (Get-RequiredField $Result 'code') -cne 'desktop_runtime_validation_passed' -or
        (Get-RequiredField $Result 'runId') -cne $RunId) { throw 'The runtime result does not identify this exact private validation run.' }
    foreach ($name in @('profileGuarded', 'noLiveHttp', 'noOsNotifications', 'simulatedAcceptance', 'eventAccepted',
            'baselineStable', 'reopenStable', 'workersDrained', 'ownerHandoff')) { Assert-TrueField $Result $name }
    Assert-IntegerField $Result 'httpRequests' 0
    Assert-IntegerField $Result 'nativeNotificationCalls' 0
    Assert-IntegerField $Result 'simulatedSendCount' 1 $true
}

function Invoke-OwnedDesktop([string]$Exe, [string]$Argument, [string]$Name, [string]$RunRoot) {
    $stdout = Join-Path $RunRoot ($Name + '.stdout.txt')
    $stderr = Join-Path $RunRoot ($Name + '.stderr.json')
    $started = [DateTimeOffset]::UtcNow
    $process = Start-Process -FilePath $Exe -ArgumentList $Argument -WorkingDirectory ([IO.Path]::GetDirectoryName($Exe)) -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        # Keep the actual handle alive: Windows PowerShell 5.1 can otherwise lose redirected ExitCode.
        $ownedHandle = $process.Handle
        if ($null -eq $ownedHandle -or $ownedHandle -eq [IntPtr]::Zero) { throw 'The owned desktop process handle is unavailable.' }
        $ownedPid = $process.Id
        $timedOut = !$process.WaitForExit(45000)
        if ($timedOut) {
            $process.Refresh()
            if (!$process.HasExited) {
                $actualExe = [IO.Path]::GetFullPath($process.MainModule.FileName)
                if (![string]::Equals($actualExe, $Exe, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to terminate an unverified desktop process.' }
                $process.Kill()
                if (!$process.WaitForExit(5000)) { throw 'The owned desktop process did not stop after its deadline.' }
            }
        }
        $process.WaitForExit()
        $exitCode = $process.ExitCode
        if ($null -eq $exitCode) { throw 'The owned desktop process exit code is unavailable.' }
        $evidence = [PSCustomObject]@{ processId = $ownedPid; exitCode = $exitCode; executablePath = $Exe;
            handleObtained = $true; timedOut = $timedOut; startedAtUtc = $started.ToString('O'); exitedAtUtc = [DateTimeOffset]::UtcNow.ToString('O') }
        [IO.File]::WriteAllText((Join-Path $RunRoot ($Name + '-process.json')), ($evidence | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        if ($timedOut) { throw 'Private desktop validation exceeded 45 seconds; its artifacts and profile are preserved.' }
        return [PSCustomObject]@{ ProcessId = $ownedPid; ExitCode = $exitCode; Stdout = $stdout; Stderr = $stderr }
    } finally { $process.Dispose() }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or [IntPtr]::Size -ne 8) { throw 'Run this smoke test from 64-bit Windows PowerShell.' }
$repoRoot = [IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
$runId = [Guid]::NewGuid().ToString('N')
$artifactParent = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/DesktopRuntime'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $artifactParent $runId))
$publishRoot = Join-Path $runRoot 'publish'
$validationParent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'SnuWatcher-runtime-validation'))
$profileRoot = [IO.Path]::GetFullPath((Join-Path $validationParent $runId))
if (![string]::Equals([IO.Path]::GetDirectoryName($runRoot), $artifactParent, [StringComparison]::OrdinalIgnoreCase) -or
    ![string]::Equals([IO.Path]::GetDirectoryName($profileRoot), $validationParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing a runtime validation directory outside its fixed parent.'
}
Assert-OrdinaryDirectoryChain $runRoot $true
Assert-OrdinaryDirectoryChain $profileRoot $true
if ([IO.Directory]::Exists($runRoot) -or [IO.File]::Exists($runRoot) -or [IO.Directory]::Exists($profileRoot) -or [IO.File]::Exists($profileRoot)) {
    throw 'A fresh artifact and private validation run are required; existing files are never overlaid.'
}
$null = [IO.Directory]::CreateDirectory($publishRoot)
Assert-OrdinaryDirectoryChain $runRoot $false
Write-Output ('Desktop runtime artifacts: ' + $runRoot)
Write-Output ('Private validation profile: ' + $profileRoot)

Push-Location $repoRoot
try {
    $expectedSdk = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = (& $Dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -cne $expectedSdk) { throw 'Use the SDK pinned by global.json.' }
    & $Dotnet publish 'src/SnuWatcher.Desktop/SnuWatcher.Desktop.csproj' --configuration Release --runtime win-x64 --self-contained true `
        --output $publishRoot --property:RestoreLockedMode=true -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Desktop runtime publish failed; inspect its preserved artifacts.' }
} finally { Pop-Location }
$exe = [IO.Path]::GetFullPath((Join-Path $publishRoot 'SnuWatcher.Desktop.exe'))
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The actual published desktop executable is missing.' }
if (!(Test-Path -LiteralPath (Join-Path $publishRoot 'SnuWatcher.Desktop.runtimeconfig.json') -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path $publishRoot 'coreclr.dll') -PathType Leaf)) { throw 'The published desktop runtime is not self contained.' }

if ([IO.Directory]::Exists($profileRoot) -or [IO.File]::Exists($profileRoot)) { throw 'The private run appeared during publish; refusing to overlay it.' }
$null = [IO.Directory]::CreateDirectory($profileRoot)
Assert-OrdinaryDirectoryChain $profileRoot $false
$markerPath = Join-Path $profileRoot 'runtime-validation.json'
$markerText = '{"format":"snu-runtime-validation-v1","runId":"' + $runId + '"}'
$markerBytes = [Text.UTF8Encoding]::new($false).GetBytes($markerText)
$marker = [IO.File]::Open($markerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $marker.Write($markerBytes, 0, $markerBytes.Length); $marker.Flush() } finally { $marker.Dispose() }

$valid = Invoke-OwnedDesktop $exe ('--monitor-validation=' + $runId) 'valid' $runRoot
if ($valid.ExitCode -ne 0) { throw ('The private desktop runtime exited unsuccessfully; inspect ' + $valid.Stderr) }
if ([IO.File]::ReadAllText($valid.Stdout).Trim().Length -ne 0 -or [IO.File]::ReadAllText($valid.Stderr).Trim().Length -ne 0) {
    throw 'Successful private runtime validation must keep stdout and stderr empty.'
}
Assert-OrdinaryDirectoryChain $profileRoot $false
$markerInfo = Get-Item -LiteralPath $markerPath -Force
if ($markerInfo.PSIsContainer -or ($markerInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $markerInfo.Length -ne $markerBytes.Length) {
    throw 'The private validation marker is no longer an ordinary bounded file.'
}
if ([IO.File]::ReadAllText($markerPath, [Text.UTF8Encoding]::new($false, $true)) -cne $markerText) { throw 'The private validation marker changed.' }
$resultPath = Join-Path $profileRoot ('result-' + $valid.ProcessId + '.json')
$result = Read-BoundedJson $resultPath
Assert-PassedResult $result $runId
$database = Get-Item -LiteralPath (Join-Path $profileRoot 'watches.sqlite3') -Force
if ($database.PSIsContainer -or ($database.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $database.Length -lt 16) {
    throw 'The private runtime did not preserve an ordinary database file.'
}
[IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), ($result | ConvertTo-Json -Depth 8 -Compress), [Text.UTF8Encoding]::new($false))

# A malformed validation prefix must exit through its private rejection path, before native/M2 setup.
$invalid = Invoke-OwnedDesktop $exe '--monitor-validation-wrong' 'invalid' $runRoot
if ($invalid.ExitCode -ne 2 -or [IO.File]::ReadAllText($invalid.Stdout).Trim().Length -ne 0) { throw 'Malformed validation input did not exit privately with code 2.' }
$rejection = Read-BoundedJson $invalid.Stderr
Assert-IntegerField $rejection 'schemaVersion' 1
Assert-IntegerField $rejection 'httpRequests' 0
Assert-IntegerField $rejection 'nativeNotificationCalls' 0
if ((Get-RequiredField $rejection 'codePath') -cne 'monitor-validation' -or
    (Get-RequiredField $rejection 'testStatus') -cne 'REJECTED' -or
    (Get-RequiredField $rejection 'code') -cne 'invalid_validation_argument') { throw 'Malformed validation input fell outside its private rejection path.' }
if (Test-Path -LiteralPath (Join-Path $profileRoot ('result-' + $invalid.ProcessId + '.json'))) { throw 'An invalid validation prefix wrote to the marked profile.' }
$result | ConvertTo-Json -Depth 8 -Compress
Write-Output 'Published desktop private-runtime validation passed with simulated notification acceptance. No live HTTP or OS notification was used.'
