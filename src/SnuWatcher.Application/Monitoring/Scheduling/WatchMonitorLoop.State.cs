using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Planning;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Scheduling;

public sealed partial class WatchMonitorLoop
{
    private sealed record ActivationDemand(Guid Id, long Generation, SectionKey Key, long Watermark);
    private sealed record QueryEntry(PlannedWatchQuery Plan, ImmutableArray<ActivationDemand> Demand, TimeSpan Due,
        int Failures = 0, int Attempts = 0, TimeSpan? LastStart = null, DateTimeOffset? LastStartedUtc = null,
        bool Requested = false, string? ReasonCode = null);
    private sealed record CatalogEntry(int Year, TimeSpan Due, CourseCatalogData? Data = null,
        DateTimeOffset? LastSuccessfulAtUtc = null, int Failures = 0, string? ReasonCode = null);
    private sealed record CatalogJob(TimeSpan Start, CancellationTokenSource Cancellation,
        Task<SourceRead<CourseCatalogData>> Work);
    private sealed record QueryJob(PlannedWatchQuery Plan, TimeSpan Start, CancellationTokenSource Cancellation,
        TaskCompletionSource<WatchRefreshResult> Completion, ImmutableArray<TaskCompletionSource<WatchRefreshResult>> Waiters,
        Task<CommitProgress>? Work, ImmutableArray<SectionCompletion> Pending, TimeSpan CommitDue);
    private sealed record AttemptOutcome(bool Failed, bool ParseChanged, TimeSpan? RetryAfter, TimeSpan ReceivedAt);
    private sealed record CommitProgress(ImmutableArray<SectionCompletion> Pending, ImmutableArray<SectionKey> Committed,
        AttemptOutcome? Outcome = null);
    private sealed class MonitorSourceDeferredException : Exception { }

    private TimeSpan MonotonicNow => _clock.GetElapsedTime(_origin, _clock.GetTimestamp());
    private static readonly TimeSpan StartFloor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumTimer = TimeSpan.FromHours(12);
    private ImmutableDictionary<CourseSearchQuery, QueryEntry> _queries = ImmutableDictionary<CourseSearchQuery, QueryEntry>.Empty;
    private ImmutableDictionary<CourseSearchQuery, QueryJob> _jobs = ImmutableDictionary<CourseSearchQuery, QueryJob>.Empty;
    private ImmutableDictionary<int, CatalogEntry> _catalogs = ImmutableDictionary<int, CatalogEntry>.Empty;
    private ImmutableDictionary<int, CatalogJob> _catalogJobs = ImmutableDictionary<int, CatalogJob>.Empty;
    private ImmutableDictionary<CourseSearchQuery, TimeSpan> _lastStarts = ImmutableDictionary<CourseSearchQuery, TimeSpan>.Empty;
    private ImmutableArray<BlockedWatchSection> _blocked = [];
    private SourcePolicySnapshot _policy = SourcePolicySnapshot.Initial(SourcePolicyIds.SnuSugang);
    private SourceCooldown? _observedCooldown;
    private TimeSpan _cooldownDue;
    private long _seenCancelEpoch;
    private bool _draining;

    private static TimeSpan Add(TimeSpan value, TimeSpan delay) => delay > TimeSpan.MaxValue - value
        ? TimeSpan.MaxValue : value + delay;
    private static TimeSpan Remaining(TimeSpan due, TimeSpan now) => due > now ? due - now : TimeSpan.Zero;
    private static TimeSpan Later(TimeSpan first, TimeSpan second) => first > second ? first : second;
    private static TimeSpan Backoff(int failures, bool parse) => TimeSpan.FromSeconds(
        Math.Min(900, (parse ? 120 : 30) * Math.Pow(2, Math.Min(10, Math.Max(0, failures - 1)))));

    private TimeSpan Target(CourseSearchQuery query, int attempt)
    {
        // Stable across processes; framework HashCode deliberately randomizes its seed.
        uint hash = 2166136261;
        foreach (var character in $"{query.Year}/{query.TermSelector}/{query.CourseCode}/{attempt}")
            hash = unchecked((hash ^ character) * 16777619);
        var fraction = (hash % 20001) / 10000.0 - 1;
        var ticks = (long)(_options.TargetInterval.Ticks * (1 + fraction * _options.JitterPercent / 100.0));
        return Later(StartFloor, TimeSpan.FromTicks(ticks));
    }

    private void ObservePolicy(SourcePolicySnapshot policy)
    {
        _policy = policy;
        if (policy.Cooldown is not { } cooldown || cooldown == _observedCooldown) return;
        _observedCooldown = cooldown;
        _cooldownDue = Later(_cooldownDue, Add(MonotonicNow,
            cooldown.NotBeforeUtc > _clock.GetUtcNow() ? cooldown.NotBeforeUtc - _clock.GetUtcNow() : TimeSpan.Zero));
    }

    private string? AdmissionReason((bool Online, bool Suspended, long CancelEpoch) controls) =>
        !controls.Online ? "offline" : controls.Suspended ? "suspended" : _draining ? "draining"
            : _policy.AccessPause is not null ? "source_access_restricted"
            : MonotonicNow < _cooldownDue || _policy.Cooldown is { } cooldown && _clock.GetUtcNow() < cooldown.NotBeforeUtc
                ? "source_cooldown" : null;
}
