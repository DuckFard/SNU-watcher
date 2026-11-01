using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Core;
using SnuWatcher.Windows.Notifications;

namespace SnuWatcher.Windows.Tests.Notifications;

internal static class NativeNotificationFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    internal static BatchDesktopSubmission Submission(DateTimeOffset? expiry = null, Guid? batchId = null) => new(
        new BatchDeliveryEnvelope(batchId ?? Guid.NewGuid(), Guid.NewGuid(), new SectionKey(2026, "U000200002", "S", "F32.102", "012", "000"),
            new SeatCount(116, 120), Now, 1, [new BatchRecipient(Guid.NewGuid(), 1, [OpeningReason.IncreasedSeats])],
            new BatchCoverageSummary(1, new string('a', 64), 1, 1)), Now, expiry, TimeSpan.FromMinutes(2));
    internal static UiPreferencesSnapshot Preferences(long revision = 1, UiLanguage language = UiLanguage.English, bool muted = false) =>
        new(revision, new UiPreferencesValues(language, null, null, WatchMonitorProfile.Normal, UiCloseBehavior.MinimizeToTray, 0, muted), Now);
    internal static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
}

internal sealed class NativeTestClock : TimeProvider
{
    private long ticks = NativeNotificationFixture.Now.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    internal void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
}

internal sealed class NativeTestDriver : INativeNotificationDriver
{
    internal BatchChannelReadiness Readiness = BatchChannelReadiness.Ready;
    internal Func<NativeToastPayload, IPreparedNativeNotification>? PrepareCallback;
    internal Func<BatchChannelReadiness>? ProbeCallback;
    internal int Shows;
    internal List<NativeToastPayload> Payloads { get; } = [];
    public BatchChannelReadiness Probe() => ProbeCallback?.Invoke() ?? Readiness;
    public IPreparedNativeNotification Prepare(NativeToastPayload payload)
    {
        lock (Payloads) Payloads.Add(payload);
        return PrepareCallback?.Invoke(payload) ?? new NativePrepared(() => { Interlocked.Increment(ref Shows); return new NativeShowResult(42); });
    }
}

internal sealed class NativePrepared(Func<NativeShowResult> show, Action? dispose = null) : IPreparedNativeNotification
{
    public NativeShowResult Show() => show();
    public void Dispose() => dispose?.Invoke();
}
