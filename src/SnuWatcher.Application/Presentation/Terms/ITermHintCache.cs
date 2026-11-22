namespace SnuWatcher.Application.Presentation.Terms;

/// <summary>At most four years of nonauthoritative display hints. No observation, monitor catalog or watch initialization writes.</summary>
public interface ITermHintCache
{
    Task<TermHintCacheSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    /// <summary>Global revision includes eviction; evict lowest write revision, never the oldest wall-clock time.</summary>
    Task<TermHintWriteResult> TryReplaceYearAsync(ReplaceTermHintsCommand command, CancellationToken cancellationToken = default);
}
