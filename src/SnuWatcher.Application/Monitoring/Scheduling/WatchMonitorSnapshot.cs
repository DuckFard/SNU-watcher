using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Planning;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Scheduling;

public enum WatchMonitorWorkState { Waiting, Running, CommitDelayed, Blocked }
public enum WatchRefreshDisposition { Completed, NotWatched, Blocked, Deferred }

public sealed record WatchRefreshResult
{
    internal WatchRefreshResult(WatchRefreshDisposition disposition, CourseSearchQuery? query = null, string? reasonCode = null)
    { Disposition = disposition; Query = query; ReasonCode = reasonCode; }
    public WatchRefreshDisposition Disposition { get; }
    public CourseSearchQuery? Query { get; }
    public string? ReasonCode { get; }
}

/// <summary>Estimated logical admission, not an actual HTTP start or a promise that a seat remains available.</summary>
public sealed record WatchMonitorQuerySnapshot
{
    internal WatchMonitorQuerySnapshot(CourseSearchQuery query, ImmutableArray<SectionKey> scope, WatchMonitorWorkState state,
        int failureCount, DateTimeOffset? lastStartedAtUtc, TimeSpan? nextCheckIn, string? reasonCode)
    {
        Query = query; Scope = scope; State = state; FailureCount = failureCount;
        LastStartedAtUtc = lastStartedAtUtc; NextCheckIn = nextCheckIn; ReasonCode = reasonCode;
    }
    public CourseSearchQuery Query { get; }
    public ImmutableArray<SectionKey> Scope { get; }
    public WatchMonitorWorkState State { get; }
    public int FailureCount { get; }
    public DateTimeOffset? LastStartedAtUtc { get; }
    public TimeSpan? NextCheckIn { get; }
    public string? ReasonCode { get; }
}

public sealed record WatchMonitorCatalogSnapshot
{
    internal WatchMonitorCatalogSnapshot(int year, DateTimeOffset? lastSuccessfulAtUtc, string? reasonCode)
    { Year = year; LastSuccessfulAtUtc = lastSuccessfulAtUtc; ReasonCode = reasonCode; }
    public int Year { get; }
    /// <summary>Original gateway result time, including metadata-cache hits; never restamped as a fresh source success.</summary>
    public DateTimeOffset? LastSuccessfulAtUtc { get; }
    public string? ReasonCode { get; }
}

public sealed record WatchMonitorSnapshot
{
    internal WatchMonitorSnapshot(DateTimeOffset observedAtUtc, bool isOnline, bool isSuspended,
        ImmutableArray<WatchMonitorQuerySnapshot> queries, ImmutableArray<BlockedWatchSection> blockedSections,
        ImmutableArray<WatchMonitorCatalogSnapshot> catalogs, string? reasonCode)
    {
        ObservedAtUtc = observedAtUtc; IsOnline = isOnline; IsSuspended = isSuspended;
        Queries = queries; BlockedSections = blockedSections; Catalogs = catalogs; ReasonCode = reasonCode;
    }
    public DateTimeOffset ObservedAtUtc { get; }
    public bool IsOnline { get; }
    public bool IsSuspended { get; }
    public ImmutableArray<WatchMonitorQuerySnapshot> Queries { get; }
    public ImmutableArray<BlockedWatchSection> BlockedSections { get; }
    public ImmutableArray<WatchMonitorCatalogSnapshot> Catalogs { get; }
    public string? ReasonCode { get; }
}

/// <summary>A lossy bounded wake/read hint. Durable outbox state, rather than receipt of this hint, owns delivery.</summary>
public sealed record WatchMonitorUpdate
{
    internal WatchMonitorUpdate(WatchMonitorSnapshot snapshot, ImmutableArray<SectionKey> committedSections)
    { Snapshot = snapshot; CommittedSections = committedSections; }
    public WatchMonitorSnapshot Snapshot { get; }
    public ImmutableArray<SectionKey> CommittedSections { get; }
}

public sealed class WatchMonitorException : Exception
{
    internal WatchMonitorException(string code) : base(code) => Code = code;
    public string Code { get; }
}
