using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Monitoring.SourcePolicy;

public sealed record SourceRequestProgress
{
    public SourceRequestProgress(long lastIssuedOrdinal, DateTimeOffset? lastIssuedAtUtc, long latestCompletedOrdinal,
        SourceRequestOutcome? latestOutcome, DateTimeOffset? latestCompletedAtUtc, long lastSuccessfulOrdinal,
        DateTimeOffset? lastSuccessfulAtUtc)
    {
        LastIssuedOrdinal = PersistenceValidation.Nonnegative(lastIssuedOrdinal, nameof(lastIssuedOrdinal));
        LatestCompletedOrdinal = PersistenceValidation.Nonnegative(latestCompletedOrdinal, nameof(latestCompletedOrdinal));
        LastSuccessfulOrdinal = PersistenceValidation.Nonnegative(lastSuccessfulOrdinal, nameof(lastSuccessfulOrdinal));
        if (latestCompletedOrdinal > lastIssuedOrdinal || lastSuccessfulOrdinal > latestCompletedOrdinal)
            throw new ArgumentOutOfRangeException(nameof(latestCompletedOrdinal));
        if ((lastIssuedOrdinal == 0) != (lastIssuedAtUtc is null)
            || (latestCompletedOrdinal == 0) != (latestCompletedAtUtc is null)
            || (latestCompletedOrdinal == 0) != (latestOutcome is null)
            || (lastSuccessfulOrdinal == 0) != (lastSuccessfulAtUtc is null))
            throw new ArgumentException("Progress ordinals and their evidence must be present together.");
        if (latestCompletedOrdinal > 0 && ((latestOutcome == SourceRequestOutcome.Success) != (lastSuccessfulOrdinal == latestCompletedOrdinal)
            || (latestOutcome == SourceRequestOutcome.Success && latestCompletedAtUtc != lastSuccessfulAtUtc)))
            throw new ArgumentException("Latest success and last-success evidence must identify the same completed attempt.");
        LastIssuedAtUtc = lastIssuedAtUtc?.ToUniversalTime();
        LatestOutcome = latestOutcome is { } outcome ? PersistenceValidation.Defined(outcome, nameof(latestOutcome)) : null;
        LatestCompletedAtUtc = latestCompletedAtUtc?.ToUniversalTime();
        LastSuccessfulAtUtc = lastSuccessfulAtUtc?.ToUniversalTime();
    }
    public long LastIssuedOrdinal { get; }
    public DateTimeOffset? LastIssuedAtUtc { get; }
    public long LatestCompletedOrdinal { get; }
    public SourceRequestOutcome? LatestOutcome { get; }
    public DateTimeOffset? LatestCompletedAtUtc { get; }
    public long LastSuccessfulOrdinal { get; }
    public DateTimeOffset? LastSuccessfulAtUtc { get; }
    public static SourceRequestProgress Empty { get; } = new(0, null, 0, null, null, 0, null);
}

public sealed record SourceAccessPause
{
    public SourceAccessPause(long causingOrdinal, DateTimeOffset pausedAtUtc, string reasonCode)
    {
        CausingOrdinal = PersistenceValidation.Positive(causingOrdinal, nameof(causingOrdinal));
        PausedAtUtc = pausedAtUtc.ToUniversalTime();
        if (reasonCode != "access_restricted") throw new ArgumentException("The access-pause reason is unsupported.", nameof(reasonCode));
        ReasonCode = reasonCode;
    }
    public long CausingOrdinal { get; }
    public DateTimeOffset PausedAtUtc { get; }
    public string ReasonCode { get; }
}

/// <summary>Durable UTC evidence. The running dispatcher additionally enforces its monotonic floor.</summary>
public sealed record SourceCooldown
{
    public SourceCooldown(DateTimeOffset establishedAtUtc, TimeSpan delay)
    {
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        EstablishedAtUtc = establishedAtUtc.ToUniversalTime(); Delay = delay;
        NotBeforeUtc = delay > DateTimeOffset.MaxValue - EstablishedAtUtc ? DateTimeOffset.MaxValue : EstablishedAtUtc.Add(delay);
    }
    public DateTimeOffset EstablishedAtUtc { get; }
    public TimeSpan Delay { get; }
    public DateTimeOffset NotBeforeUtc { get; }
}

public sealed record SourcePolicySnapshot
{
    public SourcePolicySnapshot(string sourceId, long revision, SourceRequestProgress progress,
        SourceAccessPause? accessPause = null, SourceCooldown? cooldown = null, long resumeWatermark = 0)
    {
        SourceId = SourcePolicyIds.Validate(sourceId); Revision = PersistenceValidation.Nonnegative(revision, nameof(revision));
        ArgumentNullException.ThrowIfNull(progress); Progress = progress;
        ResumeWatermark = PersistenceValidation.Nonnegative(resumeWatermark, nameof(resumeWatermark));
        if (resumeWatermark > progress.LastIssuedOrdinal || accessPause?.CausingOrdinal > progress.LastIssuedOrdinal)
            throw new ArgumentOutOfRangeException(nameof(resumeWatermark));
        if (accessPause is { } pause && (pause.CausingOrdinal > progress.LatestCompletedOrdinal || pause.CausingOrdinal <= resumeWatermark))
            throw new ArgumentException("An access pause must identify a completed request after the explicit-resume watermark.", nameof(accessPause));
        if (accessPause?.CausingOrdinal == progress.LatestCompletedOrdinal && progress.LatestOutcome != SourceRequestOutcome.AccessRestricted)
            throw new ArgumentException("A pause caused by the latest request requires its access-restricted outcome.", nameof(accessPause));
        AccessPause = accessPause; Cooldown = cooldown;
    }
    public string SourceId { get; }
    public long Revision { get; }
    public SourceRequestProgress Progress { get; }
    public SourceAccessPause? AccessPause { get; }
    public SourceCooldown? Cooldown { get; }
    public long ResumeWatermark { get; }
    public bool IsBlockedAt(DateTimeOffset now) => AccessPause is not null || Cooldown is { } gate && now < gate.NotBeforeUtc;
    public static SourcePolicySnapshot Initial(string sourceId) => new(sourceId, 0, SourceRequestProgress.Empty);
}
