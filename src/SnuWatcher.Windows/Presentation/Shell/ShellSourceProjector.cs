using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Presentation.Reads;

namespace SnuWatcher.Windows.Presentation.Shell;

/// <summary>Pure display projection. No clocks, requests, source-policy changes or saved-state writes occur here.</summary>
internal static class ShellSourceProjector
{
    internal static ShellSourceStatus Project(SourceStatusSnapshot? stored, ShellRuntimeFacts? runtime,
        RuntimeFailure? failure, DateTimeOffset now, TimeSpan elapsedSinceRuntime, bool localSavedStateError,
        bool controlsAvailable)
    {
        var policy = stored?.Policy;
        var successAt = policy?.Progress.LastSuccessfulAtUtc;
        var suppressEstimate = localSavedStateError || failure is not null || runtime is { IsOnline: false }
            or { IsSuspended: true } || policy?.AccessPause is not null;
        return new(Message(policy, runtime, failure, now, localSavedStateError), successAt,
            successAt is { } time && time <= now ? now - time : null,
            policy?.Progress.LatestCompletedAtUtc, stored?.LatestCompletedTransport?.ActualStartedAtUtc,
            stored?.LatestCompletedTransport?.ReceivedAtUtc, policy?.Cooldown?.NotBeforeUtc,
            suppressEstimate ? null : Countdown(runtime?.NextCheckIn, elapsedSinceRuntime),
            runtime?.IsOnline, runtime?.IsSuspended ?? false,
            controlsAvailable && policy?.AccessPause is not null && failure is null && !localSavedStateError,
            controlsAvailable && failure?.Code == "source_policy_storage_failed");
    }

    private static string Message(SourcePolicySnapshot? policy, ShellRuntimeFacts? runtime, RuntimeFailure? failure,
        DateTimeOffset now, bool localSavedStateError)
    {
        if (localSavedStateError || failure?.Kind == RuntimeFailureKind.SavedState) return "Ui.SavedStateError";
        if (failure is not null) return "Ui.RuntimeError";
        if (runtime is { IsOnline: false }) return "Ui.Offline";
        if (runtime is { IsSuspended: true }) return "Ui.Suspended";
        if (policy?.AccessPause is not null) return "Ui.SourceRestricted";
        if (policy?.Cooldown is { } cooldown && now < cooldown.NotBeforeUtc) return "Ui.SourceRateLimited";
        if (runtime?.ReasonCode is "term_unavailable" or "catalog_term_unavailable") return "Ui.TermUnavailable";
        return policy?.Progress.LatestOutcome switch
        {
            SourceRequestOutcome.Success => "Ui.SourceAvailable",
            SourceRequestOutcome.ParseChanged => "Ui.SourceChanged",
            SourceRequestOutcome.RateLimited => "Ui.SourceRateLimited",
            SourceRequestOutcome.Maintenance => "Ui.SourceMaintenance",
            SourceRequestOutcome.AccessRestricted => "Ui.SourceRestricted",
            null => "Ui.SourceUnknown",
            _ => "Ui.SourceUnavailable",
        };
    }

    private static TimeSpan? Countdown(TimeSpan? next, TimeSpan elapsed)
    {
        if (next is not { } estimate) return null;
        var progressed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        return estimate <= TimeSpan.Zero || progressed >= estimate ? TimeSpan.Zero : estimate - progressed;
    }
}
