using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Windows;
using SnuWatcher.Windows.Hosting;

namespace SnuWatcher.Desktop.Runtime;

/// <summary>Early headless private proof. It never initializes M2/native services or a live source.</summary>
internal static class MonitorRuntimeValidation
{
    // A failed headless drain must keep the coordinator's canonical commands strongly rooted,
    // even after OnStartup returns. The private harness may report/terminate only its own child.
    private static WindowsMonitoringHost? _blockedHost;

    internal static async Task<bool> TryHandleAsync(string[] arguments, Action<int> shutdown)
    {
        var route = RuntimeProfile.ParseValidationArguments(arguments, out var runId);
        if (route == ValidationArgumentDisposition.NotRequested) return false;
        if (route == ValidationArgumentDisposition.Invalid)
        {
            WriteRejection();
            shutdown(2);
            return true;
        }

        RuntimeProfile? profile = null;
        WindowsMonitoringHost? host = null;
        var source = new MonitorRuntimeValidationSource();
        var channel = new MonitorRuntimeValidationChannel();
        try
        {
            profile = RuntimeProfile.OpenValidationRun(runId);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var opened = await WindowsMonitoringHost.OpenValidationAsync(profile, () => source, channel,
                cancellationToken: deadline.Token);
            host = opened.Host ?? throw new InvalidOperationException("validation_owner_already_owned");
            if (source.Calls != 0) throw new InvalidOperationException("validation_empty_host_requested_source");
            var watchId = Guid.NewGuid();
            await host.Session.CreateAsync(new(watchId, "Private runtime validation", SubscriptionKind.Section,
                [MonitorRuntimeValidationSource.Key], true, null, DateTimeOffset.UtcNow), deadline.Token);
            var envelope = await channel.Invoked.Task.WaitAsync(deadline.Token);
            var stopped = await StopAndRetryAsync(host);
            if (!stopped) throw new InvalidOperationException("validation_stop_blocked");
            await host.DisposeAsync();
            var reopened = await VerifyReopenAsync(profile, watchId, envelope, deadline.Token);
            var handoff = await DatabaseOwnerGate.TryAcquireAsync(profile.ApplicationId, deadline.Token);
            var handedOff = handoff.Disposition == DatabaseOwnerDisposition.Acquired && handoff.Lease is not null;
            if (handoff.Lease is not null) await handoff.Lease.DisposeAsync();
            if (!reopened || !handedOff || !source.IsDisposed || channel.Calls != 1)
                throw new InvalidOperationException("validation_proof_incomplete");
            WriteResult(profile, new
            {
                schemaVersion = 1, codePath = "monitor-validation", testStatus = "PASSED", code = "desktop_runtime_validation_passed",
                runId = runId.ToString("N"), profileGuarded = true, noLiveHttp = true, httpRequests = 0,
                noOsNotifications = true, nativeNotificationCalls = 0, simulatedAcceptance = true, simulatedSendCount = channel.Calls,
                eventAccepted = true, baselineStable = true, reopenStable = true, workersDrained = true, ownerHandoff = handedOff,
            });
            shutdown(0);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (error is WindowsMonitoringHostStartupException retained) host = retained.Host;
            var drained = host is null || await StopAndRetryAsync(host);
            if (drained && host is not null) await host.DisposeAsync();
            var code = drained ? "desktop_runtime_validation_failed" : "validation_stop_blocked";
            if (profile is not null)
            {
                try
                {
                    WriteResult(profile, new
                    {
                        schemaVersion = 1, codePath = "monitor-validation", testStatus = "FAILED", code,
                        runId = runId.ToString("N"), profileGuarded = true, noLiveHttp = true, httpRequests = 0,
                        noOsNotifications = true, nativeNotificationCalls = 0, simulatedAcceptance = channel.Calls > 0,
                        simulatedSendCount = channel.Calls, workersDrained = drained, ownerHandoff = false,
                    });
                }
                catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException or InvalidOperationException)
                { WritePrivateError("validation_result_write_failed"); }
            }
            else WritePrivateError("invalid_validation_profile");
            // A blocked owner remains alive for explicit retry; the private harness reports its owned
            // child timeout as failure. Never claim clean shutdown by abandoning canonical commands.
            if (drained) shutdown(2);
            else Interlocked.Exchange(ref _blockedHost, host);
        }
        return true;
    }

    private static async Task<bool> StopAndRetryAsync(WindowsMonitoringHost host)
    {
        var result = await host.StopAsync();
        for (var retry = 0; retry < 2 && !result.IsFinalized; retry++) result = await host.RetryFinalizeStopAsync();
        return result.IsFinalized;
    }

    private static async Task<bool> VerifyReopenAsync(RuntimeProfile profile, Guid watchId, BatchDeliveryEnvelope envelope, CancellationToken ct)
    {
        profile.ValidateExisting();
        var ownership = await DatabaseOwnerGate.TryAcquireAsync(profile.ApplicationId, ct);
        if (ownership.Disposition != DatabaseOwnerDisposition.Acquired || ownership.Lease is null) return false;
        await using var owner = ownership.Lease;
        var factory = new SqliteConnectionFactory(profile.DatabasePath);
        await using var store = await SqliteWatchStore.OpenAsync(factory, cancellationToken: ct);
        var section = await store.ReadSectionAsync(MonitorRuntimeValidationSource.Key, ct);
        var watch = await store.FindSubscriptionAsync(watchId, ct);
        if (section.Baseline is not { } baseline || baseline.Seats != new SeatCount(119, 120)
            || baseline.Seats != envelope.CurrentCounts || baseline.AttemptSequence != envelope.CurrentSourceOrdinal
            || baseline.ObservedAt != envelope.CurrentSuccessAtUtc || watch is null || watch.ActivationGeneration != 1
            || watch.Members.Single().InitializedByAttemptOrdinal is null
            || !(await store.ReadPendingDeliveriesAsync(2, ct)).IsEmpty) return false;
        using var read = factory.Open(SqliteOpenMode.ReadOnly);
        using var command = read.CreateCommand();
        command.CommandText = "SELECT count(*) FROM Events e JOIN Outbox o ON o.eventId=e.id WHERE o.status=$accepted;";
        command.Parameters.AddWithValue("$accepted", (int)DeliveryStatus.OsAccepted);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void WriteResult(RuntimeProfile profile, object value)
    {
        profile.ValidateExisting();
        using var stream = new FileStream(profile.ResultPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, value);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteRejection() => WritePrivateError("invalid_validation_argument", "REJECTED");
    private static void WritePrivateError(string code, string status = "FAILED")
    {
        try { Console.Error.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, codePath = "monitor-validation", testStatus = status, code, nativeNotificationCalls = 0, httpRequests = 0 })); }
        catch (IOException) { /* A closed private stderr pipe must not fall through into native startup. */ }
    }
}
