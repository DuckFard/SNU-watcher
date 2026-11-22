using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Windows;
using SnuWatcher.Windows.Hosting;

namespace SnuWatcher.Desktop.Runtime;

internal sealed class StudentUiRetainedHostException(WindowsMonitoringHost host) : InvalidOperationException("ui_reopen_stop_blocked")
{
    internal WindowsMonitoringHost Host { get; } = host;
}

internal static partial class StudentUiValidation
{
    private static async Task<(bool OwnerRetained, bool Drained)> QuitAndDrainAsync(RuntimeProfile profile, StudentWindow window,
        StudentDesktopSession graph, WindowsMonitoringHost host, StudentUiValidationSource source,
        TaskCompletionSource quitRequested, CancellationToken ct)
    {
        source.HoldDetails();
        var detail = graph.Search.Details.LoadAsync(StudentUiValidationSource.Key012, ct);
        try
        {
            await source.DetailsEntered.Task.WaitAsync(ct);
            var quitText = window.TryFindResource("Ui.Quit") as string;
            InvokeButton(Visuals<Button>(window).First(button => button.IsVisible && button.Command is null && button.Content as string == quitText));
            await quitRequested.Task.WaitAsync(ct);
            var graphDrain = graph.DisposeAsync().AsTask();
            var hostDrain = host.StopAsync();
            var contender = await DatabaseOwnerGate.TryAcquireAsync(profile.ApplicationId, ct);
            var retained = contender.Disposition == DatabaseOwnerDisposition.AlreadyOwned && contender.Lease is null;
            if (contender.Lease is not null) await contender.Lease.DisposeAsync();
            Require(retained, "ui_owner_released_before_actual_source_drain");
            source.ReleaseDetails();
            await Task.WhenAll(graphDrain, hostDrain).WaitAsync(ct);
            var result = await hostDrain;
            for (var retry = 0; retry < 2 && !result.IsFinalized; retry++) result = await host.RetryFinalizeStopAsync(ct);
            Require(result.IsFinalized, "ui_host_stop_blocked");
            await host.DisposeAsync();
            return (retained, true);
        }
        finally
        {
            source.ReleaseDetails();
            try { await detail.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { /* Expected cancellation of the closing details waiter. */ }
        }
    }

    private static async Task<bool> TryCleanupUiAsync(StudentDesktopSession? graph, WindowsMonitoringHost? host)
    {
        try
        {
            // Start both before awaiting: shell routes may still depend on admitted backend commands.
            var graphDrain = graph?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            var hostDrain = host?.StopAsync();
            await Task.WhenAll(graphDrain, (Task?)hostDrain ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
            if (host is not null)
            {
                var result = await hostDrain!;
                if (!result.IsFinalized) result = await host.RetryFinalizeStopAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (!result.IsFinalized) return false;
                await host.DisposeAsync();
            }
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private static async Task<(bool OwnerHandoff, int PendingAlerts)> VerifyUiReopenAsync(RuntimeProfile profile,
        StudentUiJourney proof, StudentUiValidationClock clock, RuntimeSessionOptions options, CancellationToken ct)
    {
        profile.ValidateExisting();
        var fresh = new StudentUiValidationSource(clock);
        var guard = new StudentUiValidationChannel();
        WindowsMonitoringHost? reopened = null;
        try
        {
            var opened = await WindowsMonitoringHost.OpenValidationAsync(profile, () => fresh, guard, clock, options, ct);
            reopened = opened.Host ?? throw new InvalidOperationException("ui_reopen_owner_unavailable");
            var watch = await reopened.Dashboard.ReadWatchDetailsAsync(proof.WatchId, ct);
            var preferences = await reopened.Preferences.ReadAsync(ct);
            Require(watch is not null && watch.Subscription.Label == proof.Label && watch.Subscription.Members.Length == 2
                && watch.Subscription.ActivationGeneration == 2 && watch.Subscription.Expiry?.IsExpiredAt(clock.GetUtcNow()) == true,
                "ui_reopen_watch_changed");
            Require(watch!.Sections.All(section => section.Baseline is not null)
                && preferences.Revision == proof.PreferencesRevision && preferences.Values.Language == UiLanguage.Korean
                && preferences.Values.DesiredProfile == WatchMonitorProfile.Faster
                && preferences.Values.NotificationSoundMuted && proof.SavedSoundMute
                && reopened.RefreshProfile.AppliedRefreshProfile.PreferencesRevision == preferences.Revision
                && fresh.Calls == 0 && guard.Calls == 0, "ui_reopen_preferences_or_baseline_changed");
            var result = await reopened.StopAsync();
            if (!result.IsFinalized) result = await reopened.RetryFinalizeStopAsync(ct);
            Require(result.IsFinalized, "ui_reopen_stop_blocked");
            await reopened.DisposeAsync();
            reopened = null;
            Require(fresh.IsDisposed, "ui_reopen_source_not_drained");
        }
        catch (Exception error)
        {
            if (error is WindowsMonitoringHostStartupException retained) reopened = retained.Host;
            if (!await TryCleanupUiAsync(null, reopened) && reopened is not null) throw new StudentUiRetainedHostException(reopened);
            throw;
        }
        var ownership = await DatabaseOwnerGate.TryAcquireAsync(profile.ApplicationId, ct);
        Require(ownership.Disposition == DatabaseOwnerDisposition.Acquired && ownership.Lease is not null, "ui_final_owner_handoff_failed");
        await using var owner = ownership.Lease!;
        profile.ValidateExisting();
        using var read = new SqliteConnectionFactory(profile.DatabasePath).Open(SqliteOpenMode.ReadOnly);
        using var command = read.CreateCommand();
        command.CommandText = "SELECT count(*) FROM Outbox WHERE status=$pending;";
        command.Parameters.AddWithValue("$pending", (int)DeliveryStatus.Pending);
        var pending = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "SELECT count(*) FROM Outbox WHERE status=$accepted;";
        command.Parameters.Clear(); command.Parameters.AddWithValue("$accepted", (int)DeliveryStatus.OsAccepted);
        Require(Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0, "ui_validation_must_not_acknowledge_alerts");
        return (true, pending);
    }
}
