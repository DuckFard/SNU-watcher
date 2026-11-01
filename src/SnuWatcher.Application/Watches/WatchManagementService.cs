using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Watches;

/// <summary>
/// Serializes watch mutations with notification delivery. Production composition must route creation,
/// import and lifecycle changes through this service, and inject the same single-permit gate into
/// <see cref="Delivery.DeliveryCoordinator"/>. Direct store mutations bypass that ordering guarantee.
/// The gate belongs to the caller; polling does not use it. No source or notification call is made here.
/// </summary>
public sealed class WatchManagementService
{
    private readonly IWatchStore _store;
    private readonly SemaphoreSlim _lifecycleGate;

    public WatchManagementService(IWatchStore store, SemaphoreSlim lifecycleGate)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lifecycleGate);
        _store = store;
        _lifecycleGate = lifecycleGate;
    }

    public Task<SubscriptionSnapshot> CreateAsync(NewSubscription command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(token => _store.CreateSubscriptionAsync(command, token), cancellationToken);
    }

    /// <summary>
    /// Waits for any current send and its result write before changing the watch. After pause, removal or
    /// expiry completes, delivery revalidates against that committed state before starting another send.
    /// The store remains responsible for optimistic revision checks and activation-generation semantics.
    /// </summary>
    public Task<SubscriptionSnapshot> ChangeAsync(SubscriptionChange command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(token => _store.ChangeSubscriptionAsync(command, token), cancellationToken);
    }

    public Task<LegacyImportReceipt> ImportLegacyAsync(LegacyImportBatch command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return MutateAsync(token => _store.ImportLegacyBatchAsync(command, token), cancellationToken);
    }

    private async Task<T> MutateAsync<T>(Func<CancellationToken, Task<T>> mutation, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await mutation(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }
}
