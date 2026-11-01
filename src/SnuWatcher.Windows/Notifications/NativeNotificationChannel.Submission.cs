using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Windows.Notifications;

public sealed partial class NativeNotificationChannel
{
    private sealed record Execution(BatchChannelResult Result, uint? PlatformId = null);

    private Task<NativeNotificationTestReceipt> SendTestCore(bool legacy, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        NativeNotificationTestReceipt Receipt(Execution result) => new(id, result.Result, result.PlatformId, clock.GetUtcNow());
        return Enqueue(() => Receipt(ExecuteNative(config => legacy ? NativeNotificationPayload.LegacyTest()
            : NativeNotificationPayload.CreateTest(id, config!.Values.Language, config.Values.NotificationSoundMuted),
            null, legacy, cancellationToken)), () => Receipt(new(NotSubmitted(BatchNoSubmissionReason.ChannelNotReady))));
    }

    private Execution ExecuteNative(Func<UiPreferencesSnapshot?, NativeToastPayload> payloadFactory,
        BatchDesktopSubmission? submission, bool legacy, CancellationToken cancellationToken)
    {
        if (IsStoppingOrCancelled(cancellationToken)) return new(NotSubmitted(BatchNoSubmissionReason.Cancelled));
        UiPreferencesSnapshot? config;
        lock (sync) config = configuration;
        if (!legacy && config is null) return new(NotSubmitted(BatchNoSubmissionReason.ChannelNotReady));
        if (!IsCurrent(submission)) return new(NotSubmitted(BatchNoSubmissionReason.ValidityChanged));
        if (ProbeKnown(legacy) != BatchChannelReadiness.Ready) return new(NotSubmitted(BatchNoSubmissionReason.ChannelNotReady));
        IPreparedNativeNotification prepared;
        try { prepared = driver.Prepare(payloadFactory(config)) ?? throw new InvalidOperationException(); }
        catch
        {
            SetReadiness(BatchChannelReadiness.Unavailable); Report("native_payload_rejected");
            return new(NotSubmitted(BatchNoSubmissionReason.PayloadRejected));
        }
        try
        {
            // Preparation may have waited on platform work. Re-probe permission/identity after that wait too.
            if (ProbeKnown(legacy) != BatchChannelReadiness.Ready) return new(NotSubmitted(BatchNoSubmissionReason.ChannelNotReady));
            // No await or external observer occurs between this final window check and the entered-Show boundary.
            if (IsStoppingOrCancelled(cancellationToken)) return new(NotSubmitted(BatchNoSubmissionReason.Cancelled));
            if (!IsCurrent(submission)) return new(NotSubmitted(BatchNoSubmissionReason.ValidityChanged));
            // A known preference update during probe/preparation invalidates this prepared presentation.
            // Refuse it without a rebuild loop; a subsequent admitted operation captures the new snapshot.
            lock (sync)
                if (!legacy && configuration?.Revision != config!.Revision)
                    return new(NotSubmitted(BatchNoSubmissionReason.ChannelNotReady));
            // Entered Show: from here exception, null or zero ID is uncertain, never proof of no submission.
            try
            {
                var result = prepared.Show();
                if (result is { PlatformNotificationId: > 0 })
                    return new(new(BatchChannelOutcome.OsAccepted), result.PlatformNotificationId);
                if (result is { RejectionVerified: true }) return new(new(BatchChannelOutcome.RetryableFailure));
            }
            catch { Report("native_show_unknown"); }
            SetReadiness(BatchChannelReadiness.Unavailable);
            return new(new(BatchChannelOutcome.AcceptanceUnknown));
        }
        finally { try { prepared.Dispose(); } catch { Report("native_object_cleanup_failed"); } }
    }

    private bool IsStoppingOrCancelled(CancellationToken cancellationToken)
    { lock (sync) return closing || cancellationToken.IsCancellationRequested; }

    private bool IsCurrent(BatchDesktopSubmission? submission)
    {
        if (submission is null) return true;
        var now = clock.GetUtcNow();
        return now >= submission.ValidatedAtUtc && now >= submission.Envelope.CurrentSuccessAtUtc
            && now - submission.Envelope.CurrentSuccessAtUtc <= submission.MaxSourceAge
            && (submission.EligibilityExpiresAtUtc is null || now < submission.EligibilityExpiresAtUtc);
    }

    private static BatchChannelResult NotSubmitted(BatchNoSubmissionReason reason) => new(BatchChannelOutcome.NotSubmitted, reason);
}
