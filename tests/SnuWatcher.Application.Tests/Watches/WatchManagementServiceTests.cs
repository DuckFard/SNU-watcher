using System.Collections.Immutable;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Watches;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Watches;

public sealed class WatchManagementServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly SectionKey Key = new(2026, "U000200002", "U000300002", "F32.102", "012", "0");

    [Fact]
    public void DependenciesAndCommandsAreRequired()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var store = new MutationStore();
        Assert.Throws<ArgumentNullException>(() => new WatchManagementService(null!, gate));
        Assert.Throws<ArgumentNullException>(() => new WatchManagementService(store, null!));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("change")]
    [InlineData("import")]
    public async Task MutationUsesTheSharedGateThroughCompletionAndForwardsExactCommandAndToken(string operation)
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MutationStore { OnMutation = async () => { entered.SetResult(); await release.Task; } };
        var service = new WatchManagementService(store, gate);
        var command = Command(operation);

        var mutation = InvokeAsync(service, operation, command, cancellation.Token);
        await entered.Task.WaitAsync(Timeout);
        try
        {
            Assert.Same(command, store.Command);
            Assert.Equal(cancellation.Token, store.Token);
            Assert.False(mutation.IsCompleted);
            Assert.False(gate.Wait(0));
        }
        finally { release.TrySetResult(); }

        var result = await mutation.WaitAsync(Timeout);
        Assert.Same(store.Result, result);
        Assert.True(gate.Wait(0));
        gate.Release();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("change")]
    [InlineData("import")]
    public async Task CanceledWaitCannotInvokeStoreOrReleaseAnotherOwnersGate(string operation)
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        var store = new MutationStore();
        var service = new WatchManagementService(store, gate);
        var mutation = InvokeAsync(service, operation, Command(operation), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.Null(store.Command);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("change")]
    [InlineData("import")]
    public async Task StoreFailurePropagatesUnchangedAndReleasesGate(string operation)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var failure = new StoreVersionConflictException();
        var store = new MutationStore { OnMutation = () => Task.FromException(failure) };
        var service = new WatchManagementService(store, gate);

        var actual = await Assert.ThrowsAsync<StoreVersionConflictException>(() =>
            InvokeAsync(service, operation, Command(operation), default));

        Assert.Same(failure, actual);
        Assert.True(gate.Wait(0));
        gate.Release();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("change")]
    [InlineData("import")]
    public async Task NullCommandFailsWithoutAcquiringGate(string operation)
    {
        using var gate = new SemaphoreSlim(0, 1);
        var store = new MutationStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            InvokeAsync(new(store, gate), operation, null!, default));

        Assert.Null(store.Command);
        Assert.Equal(0, gate.CurrentCount);
    }

    private static object Command(string operation)
    {
        var creation = new NewSubscription(Guid.NewGuid(), "Statistics", SubscriptionKind.Section, [Key], true, null, Now);
        return operation switch
        {
            "create" => creation,
            "change" => new PauseSubscription(creation.Id, 7, Now),
            _ => new LegacyImportBatch([new(new(2026, "U000200002", "F32.102", "012"), Key, creation)]),
        };
    }

    private static async Task<object> InvokeAsync(WatchManagementService service, string operation, object command,
        CancellationToken token) => operation switch
    {
        "create" => await service.CreateAsync((NewSubscription)command, token),
        "change" => await service.ChangeAsync((SubscriptionChange)command, token),
        _ => await service.ImportLegacyAsync((LegacyImportBatch)command, token),
    };

    private sealed class MutationStore : IWatchStore
    {
        public Func<Task> OnMutation { get; init; } = () => Task.CompletedTask;
        public object? Command { get; private set; }
        public CancellationToken Token { get; private set; }
        public object? Result { get; private set; }

        public async Task<SubscriptionSnapshot> CreateSubscriptionAsync(NewSubscription command, CancellationToken token)
        {
            await MutateAsync(command, token);
            var result = Snapshot(command.Id);
            Result = result;
            return result;
        }

        public async Task<SubscriptionSnapshot> ChangeSubscriptionAsync(SubscriptionChange command, CancellationToken token)
        {
            await MutateAsync(command, token);
            var result = Snapshot(command.Id);
            Result = result;
            return result;
        }

        public async Task<LegacyImportReceipt> ImportLegacyBatchAsync(LegacyImportBatch command, CancellationToken token)
        {
            await MutateAsync(command, token);
            var result = new LegacyImportReceipt(command.Items.Select(item =>
                new LegacyImportItemResult(item.Source, item.ConfirmedTarget, Snapshot(item.Creation.Id), true)));
            Result = result;
            return result;
        }

        private async Task MutateAsync(object command, CancellationToken token)
        {
            Command = command;
            Token = token;
            await OnMutation();
        }

        private static SubscriptionSnapshot Snapshot(Guid id) =>
            new(id, 1, 1, SubscriptionKind.Section, "Statistics", WatchStatus.Active, true, null, Now, [new(Key, 0, null)]);

        public Task<IssuedCheck> IssueCheckAsync(CheckIssue issue, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImmutableArray<SubscriptionSnapshot>> ListSubscriptionsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SubscriptionSnapshot?> FindSubscriptionAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SectionSnapshot> ReadSectionAsync(SectionKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CommitContext> ReadCommitContextAsync(CheckAttemptToken token, SectionKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TryCommitResult> TryCommitSectionAsync(PreparedSectionCommit command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImmutableArray<PendingDelivery>> ReadPendingDeliveriesAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeliveryContext> ReadDeliveryContextAsync(Guid eventId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DeliveryWriteResult> RecordDeliveryResultAsync(DeliveryResultCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
