namespace SnuWatcher.Application.Presentation.Reads;

/// <summary>Bounded consistent reads only. No source request, watch mutation or read transaction across UI scrolling.</summary>
public interface IWatchDashboardReader
{
    /// <summary>Reads 1..50 nonremoved watches in Guid-N ordinal order; a changed cursor epoch requires restart.</summary>
    Task<WatchPageReadResult> ReadWatchPageAsync(WatchPageCursor? after = null, int pageSize = 25,
        CancellationToken cancellationToken = default);
    /// <summary>Null means missing/removed. States cover exactly current-generation membership, at most 2000 sections.</summary>
    Task<WatchDetailsSnapshot?> ReadWatchDetailsAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    /// <summary>Physical request evidence and logical check order are separate domains; neither read refreshes counts.</summary>
    Task<SourceStatusSnapshot> ReadSourceStatusAsync(CancellationToken cancellationToken = default);
}
