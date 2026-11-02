param(
    [Parameter(Mandatory = $true)] [string] $ExecutablePath,
    [ValidateSet('Diagnostics', 'TestNotification', 'Quit', 'Cleanup')] [string] $Mode = 'Diagnostics'
)

$ErrorActionPreference = 'Stop'
$nativeExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$argument = switch ($Mode) {
    'Diagnostics' { '--diagnostics' }
    'TestNotification' { '--test-notification' }
    'Quit' { '--quit' }
    'Cleanup' { '--unregister-notifications' }
}

$startedAt = [DateTimeOffset]::UtcNow
$nativeProcess = Start-Process -FilePath $nativeExecutable -ArgumentList $argument -WindowStyle Hidden -PassThru
if (!$nativeProcess.WaitForExit(30000)) { throw 'Native smoke process exceeded its 30 second deadline.' }
if ($nativeProcess.ExitCode -ne 0) { throw "Native smoke process failed: $($nativeProcess.ExitCode)" }
$reportPath = Join-Path $env:LOCALAPPDATA 'DuckFard/SnuWatcher/native-diagnostics.json'
if ($Mode -ne 'Quit') {
    if (!(Test-Path -LiteralPath $reportPath)) { throw 'Native smoke did not write a report.' }
    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    if ([DateTimeOffset]::Parse($report.observedAt) -lt $startedAt.AddSeconds(-1)) {
        throw 'Native smoke report is stale. Quit an existing candidate instance before running diagnostics.'
    }
    $expectedStage = switch ($Mode) {
        'Diagnostics' { 'registered' }
        'TestNotification' { 'test-submitted' }
        'Cleanup' { 'unregistered' }
    }
    if ($report.stage -ne $expectedStage) { throw "Unexpected smoke stage: $($report.stage)" }
    $report
}
Write-Output 'A successful API report is not proof of a visible banner. Complete the manual clean-PC checklist.'
