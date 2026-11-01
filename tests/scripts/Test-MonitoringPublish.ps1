# Publish and run actual monitoring/delivery actors with private storage and entirely offline ports.
[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$runRoot = Join-Path $repoRoot ('artifacts/monitoring-publish/' + [Guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $runRoot 'project'
$publishRoot = Join-Path $runRoot 'publish'
$profileRoot = Join-Path $runRoot 'profile'
$null = New-Item -ItemType Directory -Path $projectRoot, $publishRoot
$app = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'src/SnuWatcher.Application/SnuWatcher.Application.csproj'))
$infra = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'src/SnuWatcher.Infrastructure/SnuWatcher.Infrastructure.csproj'))
$windows = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'src/SnuWatcher.Windows/SnuWatcher.Windows.csproj'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier><SelfContained>true</SelfContained>
    <RestoreLockedMode>true</RestoreLockedMode><AssemblyName>SnuWatcher.Monitoring.PublishSmoke</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$app" /><ProjectReference Include="$infra" /><ProjectReference Include="$windows" />
  </ItemGroup>
</Project>
"@
$program = @'
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Application.Watches;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Windows;

try
{
    var appDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
    var profile = Path.Combine(Path.GetDirectoryName(appDirectory)!, "profile");
    if (args.Length != 1 || !string.Equals(Path.GetFullPath(args[0]), profile, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The probe requires its private artifact profile.");
    Directory.CreateDirectory(profile);
    var owner = await DatabaseOwnerGate.TryAcquireAsync("DuckFard.SnuWatcher.MonitoringSmoke." + Guid.NewGuid().ToString("N"));
    if (owner.Disposition != DatabaseOwnerDisposition.Acquired || owner.Lease is null)
        throw new InvalidOperationException("Private database ownership was not acquired.");
    await using var ownerLease = owner.Lease;
    var factory = new SqliteConnectionFactory(Path.Combine(profile, "watch.sqlite3"));
    var identity = factory.EngineIdentity.RequireSupported();
    var clock = TimeProvider.System;
    var key = new SectionKey(2026, "U000200002", "U000300001", "F32.102", "012", "000");
    var source = new OfflineSource(key);
    var channel = new OfflineChannel();
    var policy = new BatchDeliveryPolicy(TimeSpan.FromMinutes(2), 3);
    Guid eventId;
    SeatBaseline baseline;
    BatchDeliveryEnvelope accepted;
    using var lifecycleGate = new SemaphoreSlim(1, 1);
    await using (var store = await SqliteWatchStore.OpenAsync(factory, clock))
    {
        var recovery = await new DeliveryStartupRecovery(store, store, clock, lifecycleGate).RecoverAsync();
        if (recovery.RecoveredAttempts != 0) throw new InvalidOperationException("A new profile contained interrupted work.");
        var watches = new WatchManagementService(store, lifecycleGate);
        await watches.CreateAsync(new NewSubscription(Guid.NewGuid(), "Published engine smoke", SubscriptionKind.Section,
            [key], true, null, clock.GetUtcNow()));
        var monitor = new WatchMonitorLoop(store, source, store, clock, new WatchMonitorOptions(jitterPercent: 0));
        var coordinator = new BatchDeliveryCoordinator(store, clock, lifecycleGate, channel);
        using var stop = new CancellationTokenSource();
        var refreshWaiters = new List<Task<WatchRefreshResult>>();
        var pump = new BatchDeliveryPump(store, coordinator, clock, policy,
            section => refreshWaiters.Add(monitor.RefreshAsync(section, stop.Token)));
        Task monitorRun = Task.CompletedTask, pumpRun = Task.CompletedTask;
        try
        {
            monitorRun = monitor.RunAsync(stop.Token);
            pumpRun = pump.RunAsync(stop.Token);
            accepted = await channel.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            stop.Cancel();
            // Await actual actors and known-result writes; the outer owned-process deadline handles a hang.
            await Task.WhenAll(monitorRun, pumpRun);
            // Only the delivery actor adds waiters, so this list is stable after both actors have drained.
            foreach (var waiter in refreshWaiters)
                try { await waiter; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        baseline = (await store.ReadSectionAsync(key)).Baseline ?? throw new InvalidOperationException("Monitoring produced no baseline.");
        var coverage = await store.ReadBatchAttemptCoverageAsync(accepted.AttemptId, 0, 2, default);
        if (coverage.Length != 1 || channel.Calls != 1 || source.Searches != 1 || accepted.CurrentSeats != 1)
            throw new InvalidOperationException("The actual actors did not produce one accepted initial opening.");
        eventId = coverage[0].EventId;
        if (!(await store.ReadPendingDeliveriesAsync(2)).IsEmpty) throw new InvalidOperationException("Delivery was not durably acknowledged.");
    }
    await using (var reopened = await SqliteWatchStore.OpenAsync(factory, clock))
    {
        var context = await reopened.ReadDeliveryContextAsync(eventId);
        var batch = await reopened.ReadDeliveryBatchContextAsync(accepted.BatchId, policy, clock.GetUtcNow(), default);
        var noWork = await new BatchDeliveryCoordinator(reopened, clock, lifecycleGate, channel).TryDeliverSectionAsync(key, policy);
        if (context.Delivery.EventId != eventId || context.Delivery.Status != DeliveryStatus.OsAccepted || context.Delivery.Attempts != 1
            || context.Section.Baseline != baseline || baseline.Seats != new SeatCount(119, 120)
            || batch.Batch.Status != DeliveryBatchStatus.Closed || batch.PendingCount != 0
            || noWork.Disposition != BatchDeliveryRunDisposition.NoWork || channel.Calls != 1
            || !(await reopened.ReadPendingDeliveriesAsync(2)).IsEmpty)
            throw new InvalidOperationException("Reopen did not retain the stable baseline and accepted event without resending.");
    }
    using (var connection = factory.Open(SqliteOpenMode.ReadOnly))
    {
        long Count(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        if (Count("PRAGMA user_version;") != 3 || Count("SELECT COUNT(*) FROM Events;") != 1
            || Count("SELECT COUNT(*) FROM DeliveryBatches;") != 1 || Count("SELECT COUNT(*) FROM DeliveryBatchResults;") != 1
            || Count("SELECT COUNT(*) FROM SourceRequests;") != 0)
            throw new InvalidOperationException("The private V3 profile did not retain exactly one event/batch and zero physical HTTP intents.");
    }
    var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Where(module => string.Equals(module.ModuleName, "e_sqlite3.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
    var appLocalNative = native.Length == 1 && Path.GetFullPath(native[0].FileName)
        .StartsWith(appDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    if (!appLocalNative) throw new InvalidOperationException("The published app-local native engine was not loaded.");
    Console.WriteLine(JsonSerializer.Serialize(new { testStatus = "PASSED", nativeVersion = identity.Version,
        nativeSourceId = identity.SourceId, appLocalNative, schemaVersion = 3, eventCount = 1, acceptedBatchCount = 1,
        eventStable = true, baselineStable = true, pendingCount = 0, resendCount = 0, sourceHttpIntents = 0,
        actorsDrained = true, catalogReads = source.Catalogs, searchReads = source.Searches }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { testStatus = "FAILED", error = error.ToString() }));
    return 1;
}

sealed class OfflineSource(SectionKey key) : ICourseSource
{
    private const string Selector = "U000200002U000300001";
    public int Catalogs;
    public int Searches;
    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Catalogs);
        return Task.FromResult(new SourceRead<CourseCatalogData>(SourceReadStatus.Complete,
            new(query, [new CatalogTerm(key.Year, Selector, "Offline smoke term", null)], []), [], DateTimeOffset.UtcNow));
    }
    public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (purpose != SourceRequestPurpose.Watch || query.CourseCode != key.CourseCode || query.TermSelector != Selector)
            throw new InvalidOperationException("The real monitor requested an unexpected fake-source scope.");
        Interlocked.Increment(ref Searches);
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Complete,
            new(query, [new CourseSearchRow(key, "Offline smoke course", null, null, null, 119, 120, null, "Enrollment/Quota", now)], 1), [], now));
    }
    public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken ct) =>
        throw new InvalidOperationException("The monitor unexpectedly requested details.");
}
sealed class OfflineChannel : IBatchDesktopAlertChannel
{
    public TaskCompletionSource<BatchDeliveryEnvelope> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls;
    public Task<DesktopAlertOutcome> SendAsync(BatchDeliveryEnvelope envelope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls); Accepted.TrySetResult(envelope);
        return Task.FromResult(DesktopAlertOutcome.OsAccepted);
    }
}
'@
$utf8 = New-Object Text.UTF8Encoding($false)
$projectPath = Join-Path $projectRoot 'MonitoringSmoke.csproj'
[IO.File]::WriteAllText($projectPath, $project, $utf8)
[IO.File]::WriteAllText((Join-Path $projectRoot 'Program.cs'), $program, $utf8)
Write-Output ('Monitoring smoke artifacts: ' + $runRoot)
Push-Location $repoRoot
try {
    $expectedSdk = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = (& $Dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) { throw 'Use the SDK pinned by global.json.' }
    & $Dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true --output $publishRoot --property:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Monitoring smoke publish failed; inspect its preserved artifacts.' }
} finally { Pop-Location }
$exe = [IO.Path]::GetFullPath((Join-Path $publishRoot 'SnuWatcher.Monitoring.PublishSmoke.exe'))
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The published monitoring executable is missing.' }
$stdout = Join-Path $runRoot 'result.json'
$stderr = Join-Path $runRoot 'stderr.json'
$process = Start-Process -FilePath $exe -ArgumentList ('"' + $profileRoot + '"') -WorkingDirectory $publishRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
try {
    $ownedHandle = $process.Handle
    if ($null -eq $ownedHandle -or $ownedHandle -eq [IntPtr]::Zero) { throw 'The owned smoke process handle is unavailable.' }
    if (!$process.WaitForExit(45000)) {
        $process.Refresh()
        if (!$process.HasExited) {
            $actualExe = [IO.Path]::GetFullPath($process.MainModule.FileName)
            if (![string]::Equals($actualExe, $exe, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to terminate an unverified process.' }
            $process.Kill()
            if (!$process.WaitForExit(5000)) { throw 'The owned smoke process did not stop.' }
        }
        throw 'Monitoring smoke exceeded its deadline; artifacts are preserved.'
    }
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    if ($null -eq $exitCode) { throw 'The smoke process exit code is unavailable.' }
    [IO.File]::WriteAllText((Join-Path $runRoot 'process.json'), (@{ exitCode = $exitCode } | ConvertTo-Json -Compress), $utf8)
    if ($exitCode -ne 0) { throw ('Monitoring smoke failed; inspect ' + $stderr) }
} finally { $process.Dispose() }
if ((Get-Item -LiteralPath $stderr).Length -ne 0) { throw 'The monitoring smoke wrote unexpected stderr; inspect its preserved artifacts.' }
$result = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json
$expectedSource = '2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76'
if ($result.testStatus -cne 'PASSED' -or $result.nativeVersion -cne '3.54.1' -or $result.nativeSourceId -cne $expectedSource -or
    $result.appLocalNative -ne $true -or $result.schemaVersion -ne 3 -or $result.eventCount -ne 1 -or $result.acceptedBatchCount -ne 1 -or
    $result.eventStable -ne $true -or $result.baselineStable -ne $true -or $result.pendingCount -ne 0 -or $result.resendCount -ne 0 -or
    $result.sourceHttpIntents -ne 0 -or $result.actorsDrained -ne $true -or $result.catalogReads -lt 1 -or $result.searchReads -ne 1) {
    throw 'The published monitoring evidence did not meet its contract.'
}
$result | ConvertTo-Json -Compress
Write-Output 'Published monitoring/delivery engine smoke passed. This does not establish installed UI or notification acceptance.'
