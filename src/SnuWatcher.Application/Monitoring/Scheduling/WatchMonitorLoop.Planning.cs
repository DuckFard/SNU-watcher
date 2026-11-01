using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Planning;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Scheduling;

public sealed partial class WatchMonitorLoop
{
    private void Reconcile(ImmutableArray<SubscriptionSnapshot> watches)
    {
        var now = _clock.GetUtcNow();
        var active = watches.Where(watch => watch.Status == WatchStatus.Active && watch.Expiry?.IsExpiredAt(now) != true).ToImmutableArray();
        var demandsByKey = active.SelectMany(watch => watch.Members.Select(member =>
            new ActivationDemand(watch.Id, watch.ActivationGeneration, member.Key, member.ActivationWatermark))).ToLookup(demand => demand.Key);
        _nextExpiryUtc = active.Select(watch => watch.Expiry?.ExclusiveUtc).Where(expiry => expiry is not null).Min();
        var years = active.SelectMany(watch => watch.Members.Select(member => member.Key.Year)).Distinct().Order().ToImmutableArray();
        foreach (var obsolete in _catalogs.Keys.Except(years).ToArray())
        {
            _catalogs = _catalogs.Remove(obsolete);
            if (_catalogJobs.TryGetValue(obsolete, out var job)) CancelOne(job.Cancellation);
        }
        foreach (var year in years.Where(year => !_catalogs.ContainsKey(year)))
            _catalogs = _catalogs.Add(year, new(year, MonotonicNow));
        var plan = WatchQueryPlanner.Plan(watches, _catalogs.Values.Where(entry => entry.Data is not null).Select(entry => entry.Data!),
            now, _options.MaxUniqueSections);
        _blocked = plan.BlockedSections;
        var keys = plan.Queries.Select(query => query.Query).ToImmutableHashSet();
        foreach (var obsolete in _queries.Keys.Where(query => !keys.Contains(query)).ToArray())
        {
            _queries = _queries.Remove(obsolete);
            if (_jobs.TryGetValue(obsolete, out var job)) CancelOne(job.Cancellation);
        }
        var newIndex = 0;
        foreach (var query in plan.Queries)
        {
            var demand = query.Scope.SelectMany(key => demandsByKey[key].OrderBy(item => item.Id)).ToImmutableArray();
            if (!_queries.TryGetValue(query.Query, out var before))
            {
                var due = Add(MonotonicNow, _options.RecoveryStagger * newIndex++);
                if (_lastStarts.TryGetValue(query.Query, out var start)) due = Later(due, Add(start, StartFloor));
                _queries = _queries.Add(query.Query, new(query, demand, due));
            }
            else
            {
                var changed = !before.Demand.SequenceEqual(demand);
                _queries = _queries.SetItem(query.Query, before with { Plan = query, Demand = demand,
                    Requested = before.Requested || changed, Due = changed ? RequestedDue(before) : before.Due });
            }
        }
    }

    private TimeSpan RequestedDue(QueryEntry entry) => entry.Failures > 0 ? entry.Due
        : entry.LastStart is { } start ? Later(MonotonicNow, Add(start, StartFloor)) : MonotonicNow;

    private void ResolveRefreshes((bool Online, bool Suspended, long CancelEpoch) controls)
    {
        lock (_sync)
        {
            foreach (var request in _refreshes)
            {
                var reason = AdmissionReason(controls);
                var query = _queries.Values.FirstOrDefault(entry => entry.Plan.Scope.Contains(request.Key));
                if (reason is not null)
                { request.Value.TrySetResult(new(WatchRefreshDisposition.Deferred, query?.Plan.Query, reason)); _refreshes = _refreshes.Remove(request.Key); }
                else if (query is null)
                {
                    var blocked = _blocked.FirstOrDefault(section => section.Key == request.Key);
                    request.Value.TrySetResult(new(blocked is null ? WatchRefreshDisposition.NotWatched : WatchRefreshDisposition.Blocked,
                        reasonCode: blocked?.ReasonCode));
                    _refreshes = _refreshes.Remove(request.Key);
                }
                else if (!_jobs.TryGetValue(query.Plan.Query, out var job) || !job.Plan.Scope.Contains(request.Key))
                    _queries = _queries.SetItem(query.Plan.Query, query with { Requested = true, Due = RequestedDue(query) });
            }
        }
    }

    private void StaggerRecovery()
    {
        var index = 0;
        foreach (var pair in OrderedQueries())
        {
            var floor = pair.Value.LastStart is { } start ? Add(start, StartFloor) : TimeSpan.Zero;
            var due = Later(floor, Add(MonotonicNow, _options.RecoveryStagger * index++));
            if (pair.Value.Failures > 0) due = Later(due, pair.Value.Due);
            _queries = _queries.SetItem(pair.Key, pair.Value with { Due = due });
        }
        foreach (var pair in _catalogs) _catalogs = _catalogs.SetItem(pair.Key,
            pair.Value with { Due = pair.Value.Failures > 0 ? Later(pair.Value.Due, MonotonicNow) : MonotonicNow });
    }

    private void AdmitWork((bool Online, bool Suspended, long CancelEpoch) controls, CancellationToken lifetime)
    {
        foreach (var pair in _jobs.Where(pair => pair.Value.Work is null && !pair.Value.Cancellation.IsCancellationRequested
                     && pair.Value.CommitDue <= MonotonicNow).ToArray())
            _jobs = _jobs.SetItem(pair.Key, pair.Value with
                { Work = SignalWhenCompleteAsync(() => CommitAsync(pair.Value.Pending, pair.Value.Cancellation.Token)) });
        if (AdmissionReason(Controls()) is not null || Controls().CancelEpoch != _seenCancelEpoch) return;
        while (_jobs.Count + _catalogJobs.Count < _options.MaxConcurrentQueries)
        {
            lifetime.ThrowIfCancellationRequested();
            if (AdmissionReason(Controls()) is not null || Controls().CancelEpoch != _seenCancelEpoch) break;
            var catalog = _catalogs.Values.Where(entry => !_catalogJobs.ContainsKey(entry.Year) && entry.Due <= MonotonicNow)
                .OrderBy(entry => entry.Due).ThenBy(entry => entry.Year).FirstOrDefault();
            var query = OrderedQueries().FirstOrDefault(pair => !_jobs.ContainsKey(pair.Key) && pair.Value.Due <= MonotonicNow);
            if (catalog is null && query.Value is null) break;
            // Equal deadlines favor watches, so slow metadata refresh cannot repeatedly take the only slot.
            if (query.Value is not null && (catalog is null || query.Value.Due <= catalog.Due)) StartQuery(query.Key, query.Value);
            else StartCatalog(catalog!);
        }
        UpdateOwned();
    }

    private void StartCatalog(CatalogEntry entry)
    {
        var cancellation = new CancellationTokenSource();
        if (!TryOwn(cancellation)) { cancellation.Dispose(); return; }
        var start = MonotonicNow;
        var work = SignalWhenCompleteAsync(async () =>
        {
            cancellation.Token.ThrowIfCancellationRequested();
            return await _source.GetCatalogAsync(new(entry.Year), SourceRequestPurpose.Watch, cancellation.Token).ConfigureAwait(false);
        });
        _catalogJobs = _catalogJobs.Add(entry.Year, new(start, cancellation, work));
    }

    private bool TryOwn(CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            if (!_online || _suspended || _cancelEpoch != _seenCancelEpoch) return false;
            _owned = _owned.Add(cancellation);
            return true;
        }
    }

    private void StartQuery(CourseSearchQuery key, QueryEntry entry)
    {
        var cancellation = new CancellationTokenSource();
        if (!TryOwn(cancellation)) { cancellation.Dispose(); return; }
        var completion = new TaskCompletionSource<WatchRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ImmutableArray<TaskCompletionSource<WatchRefreshResult>> waiters;
        lock (_sync)
        {
            waiters = _refreshes.Where(request => entry.Plan.Scope.Contains(request.Key)).Select(request => request.Value).ToImmutableArray();
            _refreshes = _refreshes.RemoveRange(entry.Plan.Scope);
            _joinable = _joinable.SetItems(entry.Plan.Scope.Select(section =>
                new KeyValuePair<SectionKey, Task<WatchRefreshResult>>(section, completion.Task)));
        }
        var start = MonotonicNow;
        var startedUtc = _clock.GetUtcNow();
        var work = SignalWhenCompleteAsync(() => FetchAndCommitAsync(entry.Plan, cancellation.Token));
        _jobs = _jobs.Add(key, new(entry.Plan, start, cancellation, completion, waiters, work, [], TimeSpan.Zero));
        _queries = _queries.SetItem(key, entry with { LastStart = start, LastStartedUtc = startedUtc,
            Due = Add(start, Target(key, entry.Attempts + 1)), Attempts = entry.Attempts + 1, Requested = false });
        _lastStarts = _lastStarts.Where(pair => start - pair.Value < StartFloor || _queries.ContainsKey(pair.Key) || _jobs.ContainsKey(pair.Key))
            .ToImmutableDictionary().SetItem(key, start);
    }

    private IOrderedEnumerable<KeyValuePair<CourseSearchQuery, QueryEntry>> OrderedQueries() => _queries.OrderBy(pair => pair.Value.Due)
        .ThenBy(pair => pair.Key.Year).ThenBy(pair => pair.Key.TermSelector, StringComparer.Ordinal)
        .ThenBy(pair => pair.Key.CourseCode, StringComparer.Ordinal);
}
