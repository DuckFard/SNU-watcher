using SnuWatcher.Application.Monitoring.Scheduling;

namespace SnuWatcher.Application.Monitoring.Hosting;

public enum RuntimeFailureKind { SavedState, Actor }

/// <summary>Sticky local runtime failure evidence, independent of lossy UI hints.</summary>
public sealed record RuntimeFailure
{
    internal RuntimeFailure(RuntimeFailureKind kind, string code, DateTimeOffset occurredAtUtc)
    { Kind = kind; Code = code; OccurredAtUtc = occurredAtUtc.ToUniversalTime(); }
    public RuntimeFailureKind Kind { get; }
    public string Code { get; }
    /// <summary>Diagnostic clock time; MinValue explicitly denotes an unavailable injected clock.</summary>
    public DateTimeOffset OccurredAtUtc { get; }
}

/// <summary>A bounded latest-state rendering hint; original committed section arrays are not accumulated.</summary>
public sealed record RuntimeUpdate
{
    internal RuntimeUpdate(WatchMonitorSnapshot? snapshot, RuntimeFailure? failure, long committedGeneration)
    { Snapshot = snapshot; Failure = failure; CommittedGeneration = committedGeneration; }
    public WatchMonitorSnapshot? Snapshot { get; }
    public RuntimeFailure? Failure { get; }
    public long CommittedGeneration { get; }
}
