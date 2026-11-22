namespace SnuWatcher.Application.Monitoring.Hosting;

public sealed partial class MonitoringRuntimeSession
{
    private async Task SuperviseAsync()
    {
        var children = _actors;
        var ended = await Task.WhenAny(children.Values.Append(_hub.FailureTask)).ConfigureAwait(false);
        bool stopping;
        lock (_sync) stopping = _stopping;
        if (!stopping && ended != _hub.FailureTask)
        {
            var name = children.First(pair => pair.Value == ended).Key;
            _hub.ReportFailure(RuntimeFailureKind.Actor, name switch
            {
                "monitor" => "monitor_failed",
                "delivery" => "delivery_actor_stopped",
                "refresh" => "refresh_actor_failed",
                _ => "runtime_update_failed",
            });
        }
        Task<RuntimeSessionStopResult> stop;
        lock (_sync) stop = EnsureStopCoreLocked();
        // Stop-core drains the actual children, never this supervisor itself.
        await stop.ConfigureAwait(false);
    }

    private void OnPumpDiagnostic(SnuWatcher.Core.SectionKey? key, string code)
    {
        if (code is "delivery_work_unavailable" or "delivery_work_failed" or "known_result_not_recorded"
            or "shutdown_results_pending" or "recovery_required" or "known_result_limit")
            _hub.ReportFailure(RuntimeFailureKind.SavedState, code);
    }

    private async Task ObserveAsync(Task task, string code)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { _hub.ReportFailure(RuntimeFailureKind.Actor, code); }
    }

    private Task RequestCancellation(CancellationTokenSource cancellation)
    {
        try { return cancellation.CancelAsync(); }
        catch (Exception)
        {
            _hub.ReportFailure(RuntimeFailureKind.Actor, "runtime_cancellation_failed");
            return Task.CompletedTask;
        }
    }
}
