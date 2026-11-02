using System.Collections.Immutable;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

public sealed class DeliveryStartupRecoveryTests
{
    [Fact]
    public async Task CanonicalExclusiveCursorRecoversEveryPageOnceWithoutUsingPendingSectionWork()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var discovery = new Discovery();
        var rows = Rows(3);
        discovery.Read = (after, limit, _) => Task.FromResult(after is null ? rows.Take(2).ToImmutableArray() : rows.Skip(2).ToImmutableArray());
        var store = new BatchPort();
        store.Record = (command, token) =>
        {
            Assert.False(token.CanBeCanceled);
            Assert.False(gate.Wait(0));
            return Task.FromResult(Receipt(command, rows));
        };
        var recovery = new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate);

        var summary = await recovery.RecoverAsync(pageSize: 2);

        Assert.Equal(3, summary.RecoveredAttempts);
        Assert.Equal(6, summary.CoveredEvents);
        Assert.Equal(new Guid?[] { null, rows[1].AttemptId }, discovery.Cursors);
        Assert.Equal(rows.Select(row => row.AttemptId), store.Results.Select(command => command.AttemptId));
        Assert.All(store.Results, command => Assert.Equal(BatchAttemptResultOutcome.InterruptedUnknown, command.Outcome));
        Assert.Empty(store.Projects);
        Assert.Empty(store.Reservations);
    }

    [Fact]
    public async Task LostCommittedReturnRetriesExactObjectAndTimestampEvenAfterCancellation()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var rows = Rows(1);
        var discovery = new Discovery { Read = (_, _, _) => Task.FromResult(rows) };
        var clock = new CoordinatorClock();
        var store = new BatchPort();
        store.Record = (command, token) =>
        {
            Assert.False(token.CanBeCanceled);
            if (store.Results.Count == 1)
            {
                clock.Now = clock.Now.AddHours(1);
                cancellation.Cancel();
                throw new IOException("lost return after committed result");
            }
            return Task.FromResult(Receipt(command, rows));
        };

        var summary = await new DeliveryStartupRecovery(discovery, store, clock, gate).RecoverAsync(cancellationToken: cancellation.Token);

        Assert.Equal(1, summary.RecoveredAttempts);
        Assert.Equal(2, store.Results.Count);
        Assert.Same(store.Results[0], store.Results[1]);
        Assert.Equal(BatchFixture.Now, store.Results[1].RecordedAtUtc);
    }

    [Fact]
    public async Task UnwritableStoreAbortsStartupWithSanitizedFailureAndOneShotCannotBeReused()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var discovery = new Discovery { Read = (_, _, _) => Task.FromResult(Rows(1)) };
        var store = new BatchPort { Record = (_, _) => throw new IOException("private database path and failure") };
        var recovery = new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RecoverAsync());

        Assert.DoesNotContain("private", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(2, store.Results.Count);
        Assert.Same(store.Results[0], store.Results[1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RecoverAsync());
        Assert.True(gate.Wait(0));
        gate.Release();
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("descending")]
    [InlineData("oversize")]
    [InlineData("default")]
    [InlineData("null")]
    public async Task InvalidDiscoveryPageAbortsBeforeWritingAnyMember(string mode)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var rows = Rows(3);
        var discovery = new Discovery { Read = (_, _, _) => Task.FromResult(mode switch
        {
            "duplicate" => ImmutableArray.Create(rows[0], rows[0]),
            "descending" => ImmutableArray.Create(rows[1], rows[0]),
            "oversize" => rows,
            "null" => ImmutableArray.Create((UnresolvedDeliveryAttempt)null!),
            _ => default,
        }) };
        var store = new BatchPort();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate).RecoverAsync(pageSize: 2));

        Assert.Empty(store.Results);
    }

    [Fact]
    public async Task DiscoveryCannotRepeatOrMoveBehindLastReturnedCursor()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var rows = Rows(1);
        var discovery = new Discovery { Read = (_, _, _) => Task.FromResult(rows) };
        var store = new BatchPort { Record = (command, _) => Task.FromResult(Receipt(command, rows)) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate).RecoverAsync(pageSize: 1));

        Assert.Single(store.Results);
        Assert.Equal(rows[0].AttemptId, discovery.Cursors[1]);
    }

    [Fact]
    public async Task CancellationAfterDiscoveryBeforeFirstWriteIsHonored()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var discovery = new Discovery { Read = (_, _, _) => { cancellation.Cancel(); return Task.FromResult(Rows(1)); } };
        var store = new BatchPort();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate).RecoverAsync(cancellationToken: cancellation.Token));

        Assert.Empty(store.Results);
    }

    [Fact]
    public async Task CancellationDuringRecordPreservesReceiptThenStopsBeforeNextMember()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var rows = Rows(2);
        var discovery = new Discovery { Read = (_, _, _) => Task.FromResult(rows) };
        var store = new BatchPort { Record = (command, _) => { cancellation.Cancel(); return Task.FromResult(Receipt(command, rows)); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate).RecoverAsync(cancellationToken: cancellation.Token));

        Assert.Single(store.Results);
    }

    [Fact]
    public async Task ConcurrentAndRepeatedRecoveryCannotStartAnotherRun()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ImmutableArray<UnresolvedDeliveryAttempt>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new Discovery { Read = (_, _, _) => { started.SetResult(); return finish.Task; } };
        var recovery = new DeliveryStartupRecovery(discovery, new BatchPort(), new CoordinatorClock(), gate);
        var first = recovery.RecoverAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RecoverAsync());
        finish.SetResult([]);
        Assert.Equal(0, (await first.WaitAsync(TimeSpan.FromSeconds(5))).RecoveredAttempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RecoverAsync());
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("batch")]
    [InlineData("outcome")]
    [InlineData("time")]
    [InlineData("null")]
    public async Task ReceiptMustMatchExactRecoveryAuthorityAndCannotInventAcceptance(string field)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var rows = Rows(1);
        var discovery = new Discovery { Read = (_, _, _) => Task.FromResult(rows) };
        var store = new BatchPort { Record = (command, _) => Task.FromResult(field == "null" ? null! : new BatchAttemptReceipt(
            field == "attempt" ? Guid.NewGuid() : command.AttemptId,
            field == "batch" ? Guid.NewGuid() : rows[0].BatchId,
            field == "outcome" ? BatchAttemptResultOutcome.OsAccepted : BatchAttemptResultOutcome.InterruptedUnknown,
            2, field == "outcome" ? 2 : 0, field == "outcome" ? 0 : 2, 0,
            field == "time" ? command.RecordedAtUtc.AddSeconds(1) : command.RecordedAtUtc)) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate).RecoverAsync());

        Assert.Single(store.Results);
    }

    [Fact]
    public async Task DiscoveryFailureIsSanitizedAndDoesNotWriteAnyRecoveryResult()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var discovery = new Discovery { Read = (_, _, _) => throw new IOException("private database path") };
        var store = new BatchPort();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate).RecoverAsync());
        Assert.DoesNotContain("private", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Empty(store.Results);
    }

    [Fact]
    public async Task DiscoveryCallerCancellationRemainsCancellationWithoutInventingAnOutage()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var discovery = new Discovery { Read = (_, _, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ImmutableArray<UnresolvedDeliveryAttempt>>(token);
        } };
        var store = new BatchPort();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate).RecoverAsync(cancellationToken: cancellation.Token));
        Assert.Empty(store.Results);
    }

    [Fact]
    public async Task SummaryCannotOverflowAndClaimStartupCompleted()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var rows = Rows(2);
        var discovery = new Discovery { Read = (_, _, _) => Task.FromResult(rows) };
        var store = new BatchPort { Record = (command, _) => Task.FromResult(new BatchAttemptReceipt(command.AttemptId,
            rows.Single(row => row.AttemptId == command.AttemptId).BatchId, command.Outcome,
            long.MaxValue, 0, long.MaxValue, 0, command.RecordedAtUtc)) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeliveryStartupRecovery(discovery, store, new CoordinatorClock(), gate).RecoverAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task PageResourceBoundsFailBeforeDiscovery(int size)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var discovery = new Discovery();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new DeliveryStartupRecovery(discovery, new BatchPort(), new CoordinatorClock(), gate).RecoverAsync(size));
        Assert.Empty(discovery.Cursors);
    }

    private static ImmutableArray<UnresolvedDeliveryAttempt> Rows(int count) => Enumerable.Range(1, count).Select(index =>
        new UnresolvedDeliveryAttempt(Guid.Parse($"{index:x8}-0000-0000-0000-000000000001"),
            Guid.Parse($"{index:x8}-0000-0000-0000-000000000002"), BatchFixture.Key(index.ToString("000")),
            BatchFixture.Now.AddMinutes(-1))).ToImmutableArray();

    private static BatchAttemptReceipt Receipt(BatchAttemptResultCommand command, ImmutableArray<UnresolvedDeliveryAttempt> rows) =>
        new(command.AttemptId, rows.Single(row => row.AttemptId == command.AttemptId).BatchId, command.Outcome,
            2, 0, 2, 0, command.RecordedAtUtc);

    private sealed class Discovery : IDeliveryRecoveryStore
    {
        internal List<Guid?> Cursors { get; } = [];
        internal Func<Guid?, int, CancellationToken, Task<ImmutableArray<UnresolvedDeliveryAttempt>>> Read { get; set; } = (_, _, _) =>
            Task.FromResult(ImmutableArray<UnresolvedDeliveryAttempt>.Empty);
        public Task<ImmutableArray<UnresolvedDeliveryAttempt>> ReadUnresolvedAttemptsAsync(Guid? afterAttemptId, int limit, CancellationToken cancellationToken)
        {
            Cursors.Add(afterAttemptId);
            return Read(afterAttemptId, limit, cancellationToken);
        }
    }
}
