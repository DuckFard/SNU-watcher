using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Persistence;

/// <summary>
/// Command-oriented persistence. Implementations run bounded synchronous database work on a background lane.
/// No source request or OS notification call occurs inside a store transaction.
/// </summary>
public interface IWatchStore
{
    /// <summary>Atomically allocate global order, token and confirmed scope before the network request.</summary>
    Task<IssuedCheck> IssueCheckAsync(CheckIssue issue, CancellationToken cancellationToken);
    /// <summary>Create and read the activation watermark in the same transaction as attempt issuance uses.</summary>
    Task<SubscriptionSnapshot> CreateSubscriptionAsync(NewSubscription command, CancellationToken cancellationToken);
    /// <summary>CAS definition revision; resume starts a generation, rename/expiry edits preserve initialization.</summary>
    Task<SubscriptionSnapshot> ChangeSubscriptionAsync(SubscriptionChange command, CancellationToken cancellationToken);
    Task<ImmutableArray<SubscriptionSnapshot>> ListSubscriptionsAsync(CancellationToken cancellationToken);
    Task<SubscriptionSnapshot?> FindSubscriptionAsync(Guid id, CancellationToken cancellationToken);
    Task<SectionSnapshot> ReadSectionAsync(SectionKey key, CancellationToken cancellationToken);
    Task<CommitContext> ReadCommitContextAsync(CheckAttemptToken token, SectionKey key, CancellationToken cancellationToken);
    /// <summary>
    /// Replay lookup precedes version checks. Identical source facts return the original receipt; conflicting
    /// replay fails. Otherwise CAS section/catalog versions and commit completion, health, baseline, member
    /// initialization, event, recipients and outbox atomically. Guard latest completion order before Core policy.
    /// </summary>
    Task<TryCommitResult> TryCommitSectionAsync(PreparedSectionCommit command, CancellationToken cancellationToken);
    /// <summary>Bound limit to 1..MaxPendingRead. Terminal history remains separate from pending delivery.</summary>
    Task<ImmutableArray<PendingDelivery>> ReadPendingDeliveriesAsync(int limit, CancellationToken cancellationToken);
    Task<DeliveryContext> ReadDeliveryContextAsync(Guid eventId, CancellationToken cancellationToken);
    /// <summary>CAS delivery revision; attempts cannot regress and terminal results cannot return to pending.</summary>
    Task<DeliveryWriteResult> RecordDeliveryResultAsync(DeliveryResultCommand command, CancellationToken cancellationToken);
}
