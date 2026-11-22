using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Windows.Notifications;

/// <summary>Bounded serialized native ownership. A blocked platform call remains owned until it actually returns.</summary>
public sealed partial class NativeNotificationChannel : IBatchDesktopAlertChannel, INativeNotificationControls, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly INativeNotificationDriver driver;
    private readonly TimeProvider clock;
    private readonly int maxPending;
    private readonly Action<string>? diagnostic;
    private readonly Queue<Action> pending = new();
    private readonly AutoResetEvent signal = new(false);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private UiPreferencesSnapshot? configuration;
    private BatchChannelReadiness readiness = BatchChannelReadiness.Initializing;
    private bool closing, busy, probeRequested;

    /// <summary>Creates the single native worker; the provided driver is not disposed by the channel.</summary>
    public NativeNotificationChannel(INativeNotificationDriver driver, TimeProvider? timeProvider = null, int maxPending = 32, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (maxPending is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maxPending));
        this.driver = driver; clock = timeProvider ?? TimeProvider.System;
        this.maxPending = maxPending; this.diagnostic = diagnostic;
        // Pinned AppNotification and Manager metadata are Both/Agile. SDK calls never use the WPF dispatcher.
        var worker = new Thread(WorkerMain) { IsBackground = true, Name = "SNU Watcher native notifications" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }
    /// <summary>Signals known availability transitions; observers run outside locks and cannot alter submission evidence.</summary>
    public event Action? AvailabilityChanged;
    /// <summary>Atomically adopts known saved revision, language and mute; never applies an editor draft.</summary>
    public bool ApplySavedPreferences(UiPreferencesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            if (configuration is { } current)
            {
                if (snapshot.Revision < current.Revision) return false;
                if (snapshot.Revision == current.Revision)
                {
                    if (snapshot.Values != current.Values)
                        throw new ArgumentException("Known notification preferences conflict at the same revision.", nameof(snapshot));
                    return false;
                }
            }
            configuration = snapshot; readiness = BatchChannelReadiness.Initializing;
            probeRequested = true; signal.Set();
        }
        NotifyAvailability();
        return true;
    }
    /// <summary>Fast known readiness; never waits behind SDK calls or queues a platform operation.</summary>
    public Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(EffectiveReadiness());
    }
    /// <inheritdoc />
    public Task<NativeNotificationStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => Enqueue(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = ProbeKnown(false);
        return new NativeNotificationStatus(value, value == BatchChannelReadiness.Ready, clock.GetUtcNow());
    });
    /// <inheritdoc />
    public Task<NativeNotificationTestReceipt> SendTestAsync(CancellationToken cancellationToken = default) => SendTestCore(false, cancellationToken);
    /// <summary>Preserves the original M2 fixed test payload without creating known production configuration.</summary>
    public Task<NativeNotificationTestReceipt> SendLegacyTestAsync(CancellationToken cancellationToken = default) => SendTestCore(true, cancellationToken);
    /// <summary>Returns only after the actual platform operation, including late acceptance, has completed.</summary>
    public Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        return Enqueue(() => ExecuteNative(config => NativeNotificationPayload.CreateOpening(submission,
            config!.Values.Language, config.Values.NotificationSoundMuted), submission, false, cancellationToken).Result,
            () => NotSubmitted(BatchNoSubmissionReason.ChannelNotReady));
    }
    /// <summary>Stops admission and drains every actual native operation without manufacturing a timeout.</summary>
    public ValueTask DisposeAsync()
    {
        bool changed;
        lock (sync)
        {
            changed = !closing; closing = true;
            if (changed) signal.Set();
        }
        if (changed) NotifyAvailability();
        return new ValueTask(stopped.Task);
    }
}
