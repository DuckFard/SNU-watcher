using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class PendingDeliveryWorkPagingTests
{
    private static IPendingDeliveryWorkReader Reader(StoreFixture f) => f.Store;

    [Fact]
    public async Task EqualTriggerOrdinalsUseExactBinaryFullKeyOrderingAndExclusivePages()
    {
        await using var f = await StoreFixture.CreateAsync();
        var keys = new[] { StoreFixture.Key("012", "a.1"), StoreFixture.Key("012", "Z.1"), StoreFixture.Key("012", "A.1"), StoreFixture.Key("002", "A.1") };
        foreach (var key in keys) await f.AddAsync(key);
        var issued = await f.IssueAsync(keys);
        foreach (var key in keys) await f.CommitAsync(f.Counts(issued, key, 119));
        var expected = keys.OrderBy(StoreWire.Key, StringComparer.Ordinal).ToArray();
        List<SectionKey> actual = [];
        PendingDeliveryWorkCursor? after = null;
        while (true)
        {
            var page = await Reader(f).ReadPageAsync(after, 1, default);
            var item = Assert.Single(page.Items);
            Assert.Equal(issued.Token.Ordinal, item.OldestPendingOrdinal);
            actual.Add(item.Key);
            if (!page.HasMore) break;
            after = Assert.IsType<PendingDeliveryWorkCursor>(page.NextCursor);
        }
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Count, actual.Distinct().Count());
        await f.ReopenAsync();
        Assert.Equal(expected.Take(2), (await Reader(f).ReadPageAsync(null, 2, default)).Items.Select(item => item.Key));
    }

    [Fact]
    public async Task RemovalDuringScanDoesNotSkipLaterRowsAndLateOlderEventAppearsOnWrap()
    {
        await using var f = await StoreFixture.CreateAsync();
        var behind = StoreFixture.Key(course: "A.0");
        await f.AddAsync(behind);
        var old = await f.IssueAsync(behind);
        var keys = new[] { StoreFixture.Key(course: "A.1"), StoreFixture.Key(course: "B.1"), StoreFixture.Key(course: "C.1") };
        foreach (var key in keys) await f.AddAsync(key);
        var newer = await f.IssueAsync(keys);
        foreach (var key in keys) await f.CommitAsync(f.Counts(newer, key, 119));
        var first = await Reader(f).ReadPageAsync(null, 1, default);
        Assert.Equal(keys[0], Assert.Single(first.Items).Key);
        var eventToRemove = (await f.Store.ReadPendingDeliveriesAsync(20, default)).Single(item => item.Key == keys[0]);
        await f.Store.RecordDeliveryResultAsync(new(eventToRemove.EventId, eventToRemove.DeliveryRevision,
            DeliveryStatus.OsAccepted, 1, f.Clock.GetUtcNow()), default);
        await f.CommitAsync(f.Counts(old, behind, 119));
        var remainder = await Reader(f).ReadPageAsync(first.NextCursor, 2, default);
        Assert.Equal(keys.Skip(1), remainder.Items.Select(item => item.Key));
        Assert.False(remainder.HasMore);
        var wrapped = await Reader(f).ReadPageAsync(null, 20, default);
        Assert.Equal(behind, wrapped.Items[0].Key);
        Assert.DoesNotContain(wrapped.Items, item => item.Key == keys[0]);
        Assert.Equal(wrapped.Items.Length, wrapped.Items.Select(item => item.Key).Distinct().Count());
        Assert.Equal(new[] { old.Token.Ordinal, newer.Token.Ordinal, newer.Token.Ordinal }, wrapped.Items.Select(item => item.OldestPendingOrdinal));
    }

    [Fact]
    public async Task EmptyLimitAndCanceledReadsHaveExplicitBoundedResults()
    {
        await using var f = await StoreFixture.CreateAsync();
        var empty = await Reader(f).ReadPageAsync(null, 1, default);
        Assert.Empty(empty.Items);
        Assert.False(empty.HasMore);
        Assert.Null(empty.NextCursor);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader(f).ReadPageAsync(null, 201, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(f).ReadPageAsync(null, 1, cancellation.Token));
    }
}
