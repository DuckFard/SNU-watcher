using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Core;
using SnuWatcher.Windows.Notifications;
using SnuWatcher.Windows.Validation;
using Xunit;

namespace SnuWatcher.Windows.Tests.Validation;

public sealed class NativeDeliveryFixtureDriverTests
{
    [Theory]
    [InlineData(42U, false)]
    [InlineData(0U, false)]
    [InlineData(0U, true)]
    public void DecoratorLabelsOnlyAndReturnsTheIdenticalActualDriverResult(uint id, bool rejected)
    {
        var inner = new Driver(new(id, rejected));
        var wrapper = new NativeDeliveryFixtureDriver(inner, UiLanguage.Korean);
        Assert.Equal(BatchChannelReadiness.Disabled, wrapper.Probe());
        var now = DateTimeOffset.UtcNow;
        var envelope = new BatchDeliveryEnvelope(Guid.NewGuid(), Guid.NewGuid(), NativeDeliveryFixtureTransport.Key,
            new(39, 40), now, 2, [new(Guid.NewGuid(), 1, [OpeningReason.IncreasedSeats])], new(1, new string('a', 64), 2, 2));
        var payload = NativeNotificationPayload.CreateOpening(new(envelope, now, null, TimeSpan.FromMinutes(2)), UiLanguage.Korean, true);
        using (var prepared = wrapper.Prepare(payload)) Assert.Same(inner.Result, prepared.Show());
        Assert.Equal(1, wrapper.ShowCalls);
        Assert.Equal(id, wrapper.PlatformNotificationId);
        Assert.True(wrapper.SoundMuted);
        Assert.Contains("테스트", inner.Payload!.Xml);
        Assert.Equal(payload.Tag, inner.Payload.Tag);
        Assert.Equal(payload.Group, inner.Payload.Group);
        Assert.Equal(payload.ExpiresAtUtc, inner.Payload.ExpiresAtUtc);
        Assert.Equal(1, inner.Disposals);
        Assert.Equal(64, wrapper.PayloadSha256!.Length);
    }

    [Fact]
    public void ThrowAfterActualShowEntryRemainsUnknownInsteadOfBecomingAcceptance()
    {
        var inner = new Driver(new(0)) { Throw = true };
        var wrapper = new NativeDeliveryFixtureDriver(inner, UiLanguage.English);
        var now = DateTimeOffset.UtcNow;
        var envelope = new BatchDeliveryEnvelope(Guid.NewGuid(), Guid.NewGuid(), NativeDeliveryFixtureTransport.Key,
            new(39, 40), now, 2, [new(Guid.NewGuid(), 1, [OpeningReason.IncreasedSeats])], new(1, new string('a', 64), 2, 2));
        using var prepared = wrapper.Prepare(NativeNotificationPayload.CreateOpening(new(envelope, now, null,
            TimeSpan.FromMinutes(2)), UiLanguage.English, false));
        Assert.Throws<InvalidOperationException>(() => { _ = prepared.Show(); });
        Assert.Equal(1, wrapper.ShowCalls);
        Assert.Null(wrapper.PlatformNotificationId);
    }

    private sealed class Driver(NativeShowResult result) : INativeNotificationDriver
    {
        internal NativeShowResult Result { get; } = result;
        internal NativeToastPayload? Payload;
        internal int Disposals;
        internal bool Throw;
        public BatchChannelReadiness Probe() => BatchChannelReadiness.Disabled;
        public IPreparedNativeNotification Prepare(NativeToastPayload payload) { Payload = payload; return new Prepared(this); }
        private sealed class Prepared(Driver parent) : IPreparedNativeNotification
        {
            public NativeShowResult Show() => parent.Throw ? throw new InvalidOperationException("private_driver_fault") : parent.Result;
            public void Dispose() => parent.Disposals++;
        }
    }
}
