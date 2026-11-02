# Publish the actual persistence modules and exercise their native boundary without UI or notifications.
[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$runRoot = Join-Path $repoRoot ('artifacts/persistence-publish/' + [Guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $runRoot 'project'
$publishRoot = Join-Path $runRoot 'publish'
$profileRoot = Join-Path $runRoot 'profile'
$null = New-Item -ItemType Directory -Path $projectRoot, $publishRoot
$infrastructure = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'src/SnuWatcher.Infrastructure/SnuWatcher.Infrastructure.csproj'))
$windows = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'src/SnuWatcher.Windows/SnuWatcher.Windows.csproj'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <RestoreLockedMode>true</RestoreLockedMode>
    <AssemblyName>SnuWatcher.Persistence.PublishSmoke</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$infrastructure" />
    <ProjectReference Include="$windows" />
  </ItemGroup>
</Project>
"@
$program = @'
using System.Diagnostics;
using System.Text.Json;
using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Windows;

try
{
    var appDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
    var expectedProfile = Path.Combine(Path.GetDirectoryName(appDirectory)!, "profile");
    if (args.Length != 1 || !string.Equals(Path.GetFullPath(args[0]), expectedProfile, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The smoke profile must be its private artifact directory.");
    Directory.CreateDirectory(expectedProfile);
    var applicationId = "DuckFard.SnuWatcher.PersistenceSmoke." + Guid.NewGuid().ToString("N");
    var ownership = await DatabaseOwnerGate.TryAcquireAsync(applicationId);
    if (ownership.Disposition != DatabaseOwnerDisposition.Acquired || ownership.Lease is null)
        throw new InvalidOperationException("The smoke database owner was not acquired.");
    await using var lease = ownership.Lease;
    var secondOwner = await DatabaseOwnerGate.TryAcquireAsync(applicationId);
    if (secondOwner.Disposition != DatabaseOwnerDisposition.AlreadyOwned)
        throw new InvalidOperationException("The smoke database owner did not exclude another owner.");
    var factory = new SqliteConnectionFactory(Path.Combine(expectedProfile, "watches.sqlite3"));
    var identity = factory.EngineIdentity.RequireSupported();
    var key = new SectionKey(2026, "U000200002", "U000300001", "F32.102", "012", "000");
    PreparedSectionCommit prepared;
    SectionCommitReceipt receipt;
    await using (var store = await SqliteWatchStore.OpenAsync(factory))
    {
        await store.CreateSubscriptionAsync(new NewSubscription(Guid.NewGuid(), "Published persistence smoke",
            SubscriptionKind.Section, [key], true, null, DateTimeOffset.UtcNow), default);
        var issued = await store.IssueCheckAsync(new CheckIssue(DateTimeOffset.UtcNow, [key]), default);
        var completion = new SectionCompletion(issued.Token, SeatObservation.Valid(key, issued.Token.Ordinal,
            DateTimeOffset.UtcNow, new SeatCount(119, 120), QuotaMeaning.EnrolledOverQuota), CheckOutcome.Success);
        var context = await store.ReadCommitContextAsync(issued.Token, key, default);
        prepared = ObservationPreparer.Prepare(context, completion, DateTimeOffset.UtcNow);
        var committed = await store.TryCommitSectionAsync(prepared, default);
        receipt = committed.Receipt ?? throw new InvalidOperationException("Commit returned no receipt.");
        if (committed.Status != CommitWriteStatus.Committed || receipt.EventId is null)
            throw new InvalidOperationException("The first observation did not commit an opening event.");
    }
    await using (var reopened = await SqliteWatchStore.OpenAsync(factory))
    {
        var replay = await reopened.TryCommitSectionAsync(prepared, default);
        var pending = await reopened.ReadPendingDeliveriesAsync(2, default);
        if (replay.Status != CommitWriteStatus.Replayed || replay.Receipt != receipt || pending.Length != 1
            || pending[0].EventId != receipt.EventId || pending[0].Key != key || pending[0].Trigger.CurrentSeats != 1)
            throw new InvalidOperationException("Reopen/replay did not preserve exactly one stable opening.");
    }
    var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Where(m => string.Equals(m.ModuleName, "e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
    var appLocalNative = native.Length == 1 && Path.GetFullPath(native[0].FileName)
        .StartsWith(appDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    if (!appLocalNative) throw new InvalidOperationException("SQLite was not loaded from the published application folder.");
    Console.WriteLine(JsonSerializer.Serialize(new { testStatus = "PASSED", nativeVersion = identity.Version,
        nativeSourceId = identity.SourceId, appLocalNative, pendingCount = 1, eventStable = true,
        replayStable = true, privateOwnerGate = true }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { testStatus = "FAILED", error = error.ToString() }));
    return 1;
}
'@
$utf8 = New-Object Text.UTF8Encoding($false)
$projectPath = Join-Path $projectRoot 'PersistenceSmoke.csproj'
[IO.File]::WriteAllText($projectPath, $project, $utf8)
[IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), $program, $utf8)
Write-Output ('Persistence smoke artifacts: ' + $runRoot)
Push-Location $repoRoot
try {
    # Running inside the repository selects global.json and inherits Directory.Build.props.
    $expectedSdk = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = (& $Dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) { throw 'Use the SDK pinned by global.json for the publish smoke.' }
    & $Dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true --output $publishRoot --property:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Persistence smoke publish failed; inspect the preserved artifact folder.' }
} finally { Pop-Location }

$exe = [IO.Path]::GetFullPath((Join-Path $publishRoot 'SnuWatcher.Persistence.PublishSmoke.exe'))
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The published smoke executable is missing.' }
$stdout = Join-Path $runRoot 'result.json'
$stderr = Join-Path $runRoot 'stderr.json'
# Windows paths cannot contain quotes; the owned profile path has no trailing slash.
$process = Start-Process -FilePath $exe -ArgumentList ('"' + $profileRoot + '"') -WorkingDirectory $publishRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
try {
    # Windows PowerShell 5.1 can otherwise lose redirected-process ExitCode (PowerShell issue 5421).
    $ownedHandle = $process.Handle
    if ($null -eq $ownedHandle -or $ownedHandle -eq [IntPtr]::Zero) { throw 'The smoke process handle is unavailable.' }
    if (!$process.WaitForExit(45000)) {
        $process.Refresh()
        if (!$process.HasExited) {
            $actualExe = [IO.Path]::GetFullPath($process.MainModule.FileName)
            if (![string]::Equals($actualExe, $exe, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to terminate an unverified process.' }
            $process.Kill()
            if (!$process.WaitForExit(5000)) { throw 'The owned smoke process did not stop after its deadline.' }
        }
        throw 'Persistence smoke exceeded its execution deadline; artifacts are preserved.'
    }
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    if ($null -eq $exitCode) { throw 'The smoke process exit code is unavailable.' }
    [IO.File]::WriteAllText((Join-Path $runRoot 'process.json'), (@{ exitCode = $exitCode } | ConvertTo-Json -Compress), $utf8)
    if ($exitCode -ne 0) { throw ('Persistence smoke failed; inspect ' + $stderr) }
} finally { $process.Dispose() }
$result = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json
$expectedSource = '2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76'
if ($result.testStatus -cne 'PASSED' -or $result.nativeVersion -cne '3.54.1' -or $result.nativeSourceId -cne $expectedSource -or
    $result.appLocalNative -ne $true -or $result.pendingCount -ne 1 -or $result.eventStable -ne $true -or
    $result.replayStable -ne $true -or $result.privateOwnerGate -ne $true) { throw 'The published persistence evidence did not meet its contract.' }
$result | ConvertTo-Json -Compress
Write-Output 'Published production persistence smoke passed. This does not establish installed-app acceptance.'
