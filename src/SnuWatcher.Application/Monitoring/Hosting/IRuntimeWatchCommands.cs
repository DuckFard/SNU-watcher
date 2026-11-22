using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Hosting;

/// <summary>Typed watch operations through the runtime's existing admission and lifecycle ordering.</summary>
public interface IRuntimeWatchCommands
{
    Task<SubscriptionSnapshot> CreateAsync(NewSubscription command, CancellationToken cancellationToken = default);
    Task<SubscriptionSnapshot> ChangeAsync(SubscriptionChange command, CancellationToken cancellationToken = default);
    Task<OwnedRefreshResult> RefreshAsync(SectionKey key, CancellationToken cancellationToken = default);
}
