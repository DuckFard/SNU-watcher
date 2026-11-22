using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Windows.Notifications;
using Xunit;

namespace SnuWatcher.Windows.Tests.Notifications;

public sealed class NativeControlContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StatusCannotOfferATestWithoutReadyEvidence()
    {
        Assert.Throws<ArgumentException>(() => new NativeNotificationStatus(BatchChannelReadiness.Disabled, true, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeNotificationStatus((BatchChannelReadiness)99, false, Now));
        var status = new NativeNotificationStatus(BatchChannelReadiness.Ready, true, Now.ToOffset(TimeSpan.FromHours(9)));
        Assert.Equal(TimeSpan.Zero, status.CheckedAtUtc.Offset);
    }

    [Fact]
    public void ReceiptSeparatesAcceptanceFromHumanObservation()
    {
        var accepted = new BatchChannelResult(BatchChannelOutcome.OsAccepted);
        Assert.Throws<ArgumentException>(() => new NativeNotificationTestReceipt(Guid.NewGuid(), accepted, null, Now));
        Assert.Throws<ArgumentException>(() => new NativeNotificationTestReceipt(Guid.NewGuid(), accepted, 0, Now));
        var unknown = new BatchChannelResult(BatchChannelOutcome.AcceptanceUnknown);
        Assert.Throws<ArgumentException>(() => new NativeNotificationTestReceipt(Guid.NewGuid(), unknown, 42, Now));
        Assert.Throws<ArgumentException>(() => new NativeNotificationTestReceipt(Guid.Empty, accepted, 42, Now));
        Assert.Throws<ArgumentNullException>(() => new NativeNotificationTestReceipt(Guid.NewGuid(), null!, null, Now));
        var receipt = new NativeNotificationTestReceipt(Guid.NewGuid(), accepted, 42, Now.ToOffset(TimeSpan.FromHours(9)));
        Assert.Equal((uint)42, receipt.PlatformNotificationId);
        Assert.Equal(TimeSpan.Zero, receipt.CompletedAtUtc.Offset);
        Assert.DoesNotContain(receipt.GetType().GetProperties(), x => x.Name.Contains("Seen") || x.Name.Contains("Heard"));
    }
}
