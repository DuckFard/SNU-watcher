using System.Collections.Immutable;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Tests.Delivery.Batches;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

/// <summary>Existing monitor policy fakes plus narrowly scripted delivery/mutation port responses.</summary>
internal sealed class MonitoringRuntimeSessionFixture : IAsyncDisposable
{
    internal MonitoringRuntimeSessionFixture(IEnumerable<SubscriptionSnapshot>? watches = null, bool deliveryEnabled = true)
    {
        Store = new(watches);
        Mutations = new(Store);
        Source = new(Store, Clock);
        Session = new(Mutations, Policy, Batches, Work, Recovery, Source, Channel, Clock,
            new(deliveryEnabled: deliveryEnabled), Gate);
    }
    internal SplitMonitorClock Clock { get; } = new();
    internal FakeMonitorStore Store { get; }
    internal ScriptedRuntimeMutations Mutations { get; }
    internal FakeMonitorPolicy Policy { get; } = new();
    internal FakeMonitorSource Source { get; }
    internal BatchPort Batches { get; } = new();
    internal BatchChannel Channel { get; } = new();
    internal EmptyRuntimeWork Work { get; } = new();
    internal RuntimeRecoveryDiscovery Recovery { get; } = new();
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal MonitoringRuntimeSession Session { get; }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        Gate.Dispose();
    }
}

internal sealed class EmptyRuntimeWork : IPendingDeliveryWorkReader
{
    private int _reads;
    internal int Reads => Volatile.Read(ref _reads);
    public Task<PendingDeliveryWorkPage> ReadPageAsync(PendingDeliveryWorkCursor? after, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _reads);
        return Task.FromResult(new PendingDeliveryWorkPage([], null, false));
    }
}

internal sealed class RuntimeRecoveryDiscovery : IDeliveryRecoveryStore
{
    internal int Reads { get; private set; }
    internal Func<CancellationToken, Task<ImmutableArray<UnresolvedDeliveryAttempt>>> Read { get; set; } = _ =>
        Task.FromResult(ImmutableArray<UnresolvedDeliveryAttempt>.Empty);
    public Task<ImmutableArray<UnresolvedDeliveryAttempt>> ReadUnresolvedAttemptsAsync(Guid? after, int limit, CancellationToken token)
    { Reads++; return Read(token); }
}

internal sealed class ScriptedRuntimeMutations(IWatchStore inner) : IWatchStore
{
    internal int Creates { get; private set; }
    internal Func<NewSubscription, CancellationToken, Task<SubscriptionSnapshot>> Create { get; set; } = (command, _) =>
        Task.FromResult(Receipt(command));
    internal Func<SubscriptionChange, CancellationToken, Task<SubscriptionSnapshot>> Change { get; set; } = (_, _) =>
        throw new StoreVersionConflictException();
    internal static SubscriptionSnapshot Receipt(NewSubscription command) => new(command.Id, 1, 1, command.Kind,
        command.Label, WatchStatus.Active, command.InitialNoticeEnabled, command.Expiry, command.CreatedAtUtc,
        command.Sections.Select(key => new MemberSnapshot(key, 0, null)));

    public Task<SubscriptionSnapshot> CreateSubscriptionAsync(NewSubscription command, CancellationToken token)
    { Creates++; return Create(command, token); }
    public Task<SubscriptionSnapshot> ChangeSubscriptionAsync(SubscriptionChange command, CancellationToken token) => Change(command, token);
    public Task<IssuedCheck> IssueCheckAsync(CheckIssue command, CancellationToken token) => inner.IssueCheckAsync(command, token);
    public Task<ImmutableArray<SubscriptionSnapshot>> ListSubscriptionsAsync(CancellationToken token) => inner.ListSubscriptionsAsync(token);
    public Task<SubscriptionSnapshot?> FindSubscriptionAsync(Guid id, CancellationToken token) => inner.FindSubscriptionAsync(id, token);
    public Task<SectionSnapshot> ReadSectionAsync(SnuWatcher.Core.SectionKey key, CancellationToken token) => inner.ReadSectionAsync(key, token);
    public Task<CommitContext> ReadCommitContextAsync(CheckAttemptToken id, SnuWatcher.Core.SectionKey key, CancellationToken token) => inner.ReadCommitContextAsync(id, key, token);
    public Task<TryCommitResult> TryCommitSectionAsync(PreparedSectionCommit command, CancellationToken token) => inner.TryCommitSectionAsync(command, token);
    public Task<ImmutableArray<PendingDelivery>> ReadPendingDeliveriesAsync(int limit, CancellationToken token) => inner.ReadPendingDeliveriesAsync(limit, token);
    public Task<DeliveryContext> ReadDeliveryContextAsync(Guid id, CancellationToken token) => inner.ReadDeliveryContextAsync(id, token);
    public Task<DeliveryWriteResult> RecordDeliveryResultAsync(DeliveryResultCommand command, CancellationToken token) => inner.RecordDeliveryResultAsync(command, token);
    public Task<LegacyImportReceipt> ImportLegacyBatchAsync(LegacyImportBatch command, CancellationToken token) => inner.ImportLegacyBatchAsync(command, token);
}
