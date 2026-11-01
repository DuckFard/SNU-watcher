using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Hosting;

public enum RuntimeSessionState { Created, Starting, Running, Stopping, StopBlocked, Stopped }
public enum RuntimeSessionStopDisposition { Stopped, Blocked }

/// <summary>Logical workers are stopped in either result; blocked canonical writes retain their owning session/resources.</summary>
public sealed record RuntimeSessionStopResult
{
    internal RuntimeSessionStopResult(RuntimeSessionStopDisposition disposition, ImmutableArray<SectionKey> pendingKnownSections,
        RuntimeFailure? failure)
    {
        Disposition = disposition;
        PendingKnownSections = pendingKnownSections;
        Failure = failure;
    }
    public RuntimeSessionStopDisposition Disposition { get; }
    public ImmutableArray<SectionKey> PendingKnownSections { get; }
    public RuntimeFailure? Failure { get; }
    /// <summary>Only session-owned logical work; this never claims the external gateway/source/store/owner is closed.</summary>
    public bool IsFinalized => Disposition == RuntimeSessionStopDisposition.Stopped;
}
