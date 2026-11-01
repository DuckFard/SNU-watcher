namespace SnuWatcher.Windows.Presentation.Shell;

/// <summary>Source health and original evidence for display; a success is not proof of current seats or registration.</summary>
/// <param name="MessageKey">Bounded localization key for the highest-priority current condition.</param>
/// <param name="LastSourceSuccessAtUtc">Original last successful physical request time, if known.</param>
/// <param name="LastSourceSuccessAge">Elapsed age when the successful time does not lie in the future.</param>
/// <param name="LastCompletedAtUtc">Original latest completed physical request time, including failures.</param>
/// <param name="ActualStartedAtUtc">Actual transport start evidence, rather than issued request intent.</param>
/// <param name="ReceivedAtUtc">Original transport receipt evidence, if known.</param>
/// <param name="CooldownUntilUtc">Exact saved cooldown deadline, including a deadline that has elapsed.</param>
/// <param name="NextCheckIn">Nonnegative remaining runtime estimate; unavailable while work is blocked.</param>
/// <param name="IsOnline">Runtime network state, or null when no runtime facts have been observed.</param>
/// <param name="IsSuspended">Whether the observed runtime is suspended.</param>
/// <param name="CanResumeAccess">Whether an explicit access-resume control is currently available.</param>
/// <param name="CanRecoverPolicy">Whether the exact sticky source-policy storage fault offers recovery.</param>
public sealed record ShellSourceStatus(string MessageKey, DateTimeOffset? LastSourceSuccessAtUtc,
    TimeSpan? LastSourceSuccessAge, DateTimeOffset? LastCompletedAtUtc, DateTimeOffset? ActualStartedAtUtc,
    DateTimeOffset? ReceivedAtUtc, DateTimeOffset? CooldownUntilUtc, TimeSpan? NextCheckIn, bool? IsOnline,
    bool IsSuspended, bool CanResumeAccess, bool CanRecoverPolicy);

/// <summary>Runtime facts captured together; elapsed time is supplied independently when projecting a countdown.</summary>
internal sealed record ShellRuntimeFacts(bool IsOnline, bool IsSuspended, string? ReasonCode, TimeSpan? NextCheckIn);
