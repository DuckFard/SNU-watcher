using System.Collections.Immutable;
using SnuWatcher.Application.Delivery.Batches;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

public sealed class BatchDeliveryPumpContractTests
{
    [Fact]
    public void PageSnapshotsItemsAndBindsExclusiveCursorToItsActualLastItem()
    {
        var key = BatchFixture.Key();
        var item = new SectionDeliveryWork(key, 7, null, false);
        var source = new List<SectionDeliveryWork> { item };
        var cursor = new PendingDeliveryWorkCursor(7, key);
        var page = new PendingDeliveryWorkPage(source, cursor, true);
        source.Clear();
        Assert.Same(item, Assert.Single(page.Items));
        Assert.Equal(cursor, page.NextCursor);
        Assert.True(page.HasMore);
        Assert.Throws<NotSupportedException>(() => ((IList<SectionDeliveryWork>)page.Items).Clear());
        Assert.Throws<ArgumentException>(() => new PendingDeliveryWorkPage([item], new(8, key), true));
        Assert.Throws<ArgumentException>(() => new PendingDeliveryWorkPage([], cursor, true));
        Assert.Throws<ArgumentException>(() => new PendingDeliveryWorkPage([item], cursor, false));
        Assert.Throws<ArgumentException>(() => new PendingDeliveryWorkPage([item, item], null, false));
        Assert.Throws<ArgumentException>(() => new PendingDeliveryWorkPage(default(ImmutableArray<SectionDeliveryWork>), null, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PendingDeliveryWorkCursor(0, key));
    }

    [Fact]
    public void PumpOptionsKeepFiniteTurnsAndRetryTrackingWithoutRelaxingSubmissionSpacing()
    {
        var options = new BatchDeliveryPumpOptions();
        Assert.Equal(128, options.PageSize);
        Assert.Equal(32, options.MaxWorkPerTurn);
        Assert.Equal(TimeSpan.FromSeconds(1), options.MinimumSubmissionInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RetryDelay);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchDeliveryPumpOptions(pageSize: 201));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchDeliveryPumpOptions(maxWorkPerTurn: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchDeliveryPumpOptions(maxTrackedSections: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchDeliveryPumpOptions(shutdownFlushAttempts: 11));
    }
}
