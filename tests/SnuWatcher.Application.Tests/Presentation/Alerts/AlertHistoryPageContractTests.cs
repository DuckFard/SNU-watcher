using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Presentation.Alerts;

public sealed class AlertHistoryPageContractTests
{
    private static readonly Guid Event = Guid.ParseExact("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "N");
    private static readonly Guid Watch = Guid.ParseExact("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "N");
    private static readonly Guid Other = Guid.ParseExact("cccccccccccccccccccccccccccccccc", "N");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static UiReadStamp Stamp => new(0, 1, Now);
    private static AlertHistoryRecipient Recipient(Guid id, long generation = 1) => new(id, generation, OpeningReason.InitialAvailability, null);
    private static AlertHistoryAttempt Batch(long sequence) => new(Event, new(Other, Watch, sequence, Now,
        new(new(2026, "U000200002", "U000300002", "F32.102", "012", "0"), new(39, 40), Now, 1, 1), null, null), null, false);
    private static AlertHistoryAttempt Result(Guid? id = null, long revision = 2) => new(id ?? Event, null, new(revision, DeliveryStatus.Pending, 0, Now), true);

    [Fact]
    public void RecipientContinuationsKeepEventIdentityOriginalGenerationAndCanonicalOrdering()
    {
        var a = Recipient(Watch); var a2 = Recipient(Watch, 2); var b = Recipient(Other);
        var page = new AlertRecipientPage(Event, Stamp, [a, a2, b], true, new(Event, Other, 1));
        Assert.Equal(Event, page.EventId); Assert.Equal(Stamp, page.Stamp); Assert.True(page.HasMore);
        Assert.Equal(1, page.NextCursor!.Version);
        Assert.Throws<ArgumentException>(() => new AlertRecipientPage(Event, Stamp, [b, a], false, null));
        Assert.Throws<ArgumentException>(() => new AlertRecipientPage(Event, Stamp, [a2, a], false, null));
        Assert.Throws<ArgumentException>(() => new AlertRecipientPage(Event, Stamp, [a, a], false, null));
        Assert.Throws<ArgumentException>(() => new AlertRecipientPage(Event, Stamp, [a], true, null));
        Assert.Throws<ArgumentException>(() => new AlertRecipientPage(Event, Stamp, [], true, new(Event, Watch, 1)));
        Assert.Throws<ArgumentException>(() => new AlertRecipientPage(Event, Stamp, [a], true, new(Other, Watch, 1)));
        Assert.Throws<ArgumentException>(() => new AlertRecipientPage(Event, Stamp, [a], true, new(Event, Other, 1)));
        Assert.Throws<ArgumentException>(() => new AlertRecipientPage(Event, Stamp, [a], true, new(Event, Watch, 2)));
        Assert.Throws<ArgumentException>(() => new AlertRecipientPage(Event, Stamp, [a], false, new(Event, Watch, 1)));
        Assert.Throws<ArgumentNullException>(() => new AlertRecipientPage(Event, null!, [a], false, null));
    }

    [Fact]
    public void RecordPagesRejectForeignContextsOrPositionsWithoutPretendingToBeChronological()
    {
        var batch = Batch(2); var lower = Batch(1); var result = Result();
        var page = new AlertAttemptPage(Event, Stamp, [batch, lower, result], true, new(Event, AlertAttemptOrigin.EventResult, 2));
        Assert.Equal(Event, page.EventId); Assert.Equal(Stamp, page.Stamp); Assert.Equal(1, page.NextCursor!.Version);
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [Result(Other)], false, null));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [result, batch], false, null));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [lower, batch], false, null));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [batch, batch], false, null));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [result, Result(revision: 3)], false, null));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [batch], true, null));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [], true, new(Event, AlertAttemptOrigin.BatchAttempt, 2)));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [batch], true, new(Other, AlertAttemptOrigin.BatchAttempt, 2)));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [batch], true, new(Event, AlertAttemptOrigin.EventResult, 2)));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [batch], true, new(Event, AlertAttemptOrigin.BatchAttempt, 1)));
        Assert.Throws<ArgumentException>(() => new AlertAttemptPage(Event, Stamp, [batch], false, new(Event, AlertAttemptOrigin.BatchAttempt, 2)));
        Assert.Throws<ArgumentNullException>(() => new AlertAttemptPage(Event, null!, [batch], false, null));
    }
}
