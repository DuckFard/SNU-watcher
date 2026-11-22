using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Hosting;

public enum OwnedRefreshDisposition { Completed, Rejected, Stopped, Failed }

public sealed record OwnedRefreshResult
{
    internal OwnedRefreshResult(SectionKey key, OwnedRefreshDisposition disposition, WatchRefreshResult? monitorResult, string reasonCode)
    { Key = key; Disposition = disposition; MonitorResult = monitorResult; ReasonCode = reasonCode; }
    public SectionKey Key { get; }
    public OwnedRefreshDisposition Disposition { get; }
    public WatchRefreshResult? MonitorResult { get; }
    public string ReasonCode { get; }
}
