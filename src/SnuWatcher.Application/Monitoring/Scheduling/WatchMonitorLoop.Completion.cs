using System.Collections.Immutable;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Scheduling;

public sealed partial class WatchMonitorLoop
{
    private async Task<ImmutableArray<SectionKey>> DrainCompletedAsync()
    {
        var committed = ImmutableArray.CreateBuilder<SectionKey>();
        foreach (var pair in _catalogJobs.Where(pair => pair.Value.Work.IsCompleted).ToArray())
        {
            var job = pair.Value;
            try
            {
                var read = await job.Work.ConfigureAwait(false);
                if (!job.Cancellation.IsCancellationRequested && _catalogs.TryGetValue(pair.Key, out var entry))
                {
                    var failures = read.Status == SourceReadStatus.Complete ? 0 : Math.Min(31, entry.Failures + 1);
                    var parse = read.Problems.Any(problem => problem.Kind is SourceProblemKind.ParseChanged or SourceProblemKind.ResponseTooLarge);
                    var retryAfter = read.Problems.Select(problem => problem.RetryAfter).Max() ?? TimeSpan.Zero;
                    var due = failures == 0 ? Add(job.Start, _options.CatalogRefreshInterval)
                        : Later(Add(job.Start, Backoff(failures, parse)), Add(MonotonicNow, retryAfter));
                    _catalogs = _catalogs.SetItem(pair.Key, entry with { Due = due, Failures = failures,
                        Data = read.Data ?? entry.Data, LastSuccessfulAtUtc = read.Data is not null ? read.CompletedAtUtc : entry.LastSuccessfulAtUtc,
                        ReasonCode = failures == 0 ? null : read.Problems[0].DiagnosticCode });
                }
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { }
            _catalogJobs = _catalogJobs.Remove(pair.Key);
            job.Cancellation.Dispose();
        }
        foreach (var pair in _jobs.Where(pair => pair.Value.Work?.IsCompleted == true
                     || pair.Value.Work is null && pair.Value.Cancellation.IsCancellationRequested).ToArray())
        {
            var job = pair.Value;
            try
            {
                if (job.Cancellation.IsCancellationRequested)
                {
                    if (job.Work is not null) try { await job.Work.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { }
                    RemoveJob(pair.Key, job, new(WatchRefreshDisposition.Deferred, pair.Key, "check_canceled"));
                    continue;
                }
                var progress = await job.Work!.ConfigureAwait(false);
                committed.AddRange(progress.Committed);
                if (progress.Outcome is { } outcome && _queries.TryGetValue(pair.Key, out var entry))
                {
                    var failures = outcome.Failed ? Math.Min(31, entry.Failures + 1) : 0;
                    var fence = Later(entry.NotBefore, Add(job.Start, outcome.Failed ? Backoff(failures, outcome.ParseChanged) : StartFloor));
                    fence = Later(fence, Add(outcome.ReceivedAt, outcome.RetryAfter ?? TimeSpan.Zero));
                    var next = entry with { Failures = failures, NotBefore = fence };
                    var due = next.Requested ? Later(fence, MonotonicNow) : AutomaticDue(pair.Key, next);
                    _queries = _queries.SetItem(pair.Key, next with { Due = due,
                        ReasonCode = outcome.Failed ? outcome.ParseChanged ? "source_parse_changed" : "source_unavailable" : null });
                }
                if (progress.Pending.IsEmpty)
                    RemoveJob(pair.Key, job, new(WatchRefreshDisposition.Completed, pair.Key));
                else _jobs = _jobs.SetItem(pair.Key, job with { Work = null, Pending = progress.Pending,
                    CommitDue = Add(MonotonicNow, _options.CommitRetryDelay) });
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            { RemoveJob(pair.Key, job, new(WatchRefreshDisposition.Deferred, pair.Key, "check_canceled")); }
            catch (MonitorSourceDeferredException)
            { RemoveJob(pair.Key, job, new(WatchRefreshDisposition.Deferred, pair.Key, "source_policy_changed")); }
        }
        UpdateOwned();
        return committed.Distinct().ToImmutableArray();
    }

    private void RemoveJob(CourseSearchQuery key, QueryJob job, WatchRefreshResult result)
    {
        _jobs = _jobs.Remove(key);
        FinishWaiters(job, result);
        job.Cancellation.Dispose();
    }

    private static void FinishWaiters(QueryJob job, WatchRefreshResult result)
    {
        job.Completion.TrySetResult(result);
        foreach (var promise in job.Waiters) promise.TrySetResult(result);
    }

    private void Publish((bool Online, bool Suspended, long CancelEpoch) controls, ImmutableArray<SectionKey> committed)
    {
        var reason = AdmissionReason(controls);
        var now = MonotonicNow;
        var queries = OrderedQueries().Select(pair =>
        {
            var running = _jobs.TryGetValue(pair.Key, out var job);
            var delayed = running && job!.Work is null;
            var queued = !running && _jobs.Count + _catalogJobs.Count >= _options.MaxConcurrentQueries;
            var state = delayed ? WatchMonitorWorkState.CommitDelayed : running ? WatchMonitorWorkState.Running
                : reason is not null ? WatchMonitorWorkState.Blocked : WatchMonitorWorkState.Waiting;
            var code = delayed ? "commit_delayed" : reason ?? (queued ? "logical_queue" : pair.Value.ReasonCode);
            return new WatchMonitorQuerySnapshot(pair.Key, pair.Value.Plan.Scope, state, pair.Value.Failures,
                pair.Value.LastStartedUtc, running || queued ? null : reason is null ? Remaining(pair.Value.Due, now) : null, code);
        }).ToImmutableArray();
        var snapshot = new WatchMonitorSnapshot(_clock.GetUtcNow(), controls.Online, controls.Suspended, queries, _blocked,
            _catalogs.Values.OrderBy(entry => entry.Year).Select(entry =>
                new WatchMonitorCatalogSnapshot(entry.Year, entry.LastSuccessfulAtUtc, entry.ReasonCode)).ToImmutableArray(), reason,
            _appliedProfile);
        Volatile.Write(ref _snapshot, snapshot);
        try { _observer?.TryWrite(new(snapshot, committed)); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* A bounded observer cannot own monitoring or delivery. */ }
    }
}
