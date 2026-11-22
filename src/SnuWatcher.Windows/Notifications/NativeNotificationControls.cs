using SnuWatcher.Application.Delivery.Batches;

namespace SnuWatcher.Windows.Notifications;

/// <summary>Explicit native probes and tests; OS acceptance never asserts that a person saw or heard anything.</summary>
public interface INativeNotificationControls
{
    /// <summary>Queues a serialized actual probe without blocking the caller's UI thread.</summary>
    Task<NativeNotificationStatus> ReadStatusAsync(CancellationToken cancellationToken = default);
    /// <summary>Owns the actual test until its native boundary finishes; late cancellation cannot erase acceptance.</summary>
    Task<NativeNotificationTestReceipt> SendTestAsync(CancellationToken cancellationToken = default);
}

/// <summary>Known native availability at one probe instant, independent of human confirmation.</summary>
public sealed record NativeNotificationStatus
{
    /// <summary>Creates validated native availability evidence.</summary>
    public NativeNotificationStatus(BatchChannelReadiness readiness, bool canSendTest, DateTimeOffset checkedAtUtc)
    {
        if (!Enum.IsDefined(readiness)) throw new ArgumentOutOfRangeException(nameof(readiness));
        if (canSendTest && readiness != BatchChannelReadiness.Ready)
            throw new ArgumentException("Only a ready channel can offer a native test.", nameof(canSendTest));
        Readiness = readiness; CanSendTest = canSendTest; CheckedAtUtc = checkedAtUtc.ToUniversalTime();
    }
    /// <summary>Known configuration, identity and permission availability.</summary>
    public BatchChannelReadiness Readiness { get; }
    /// <summary>Whether a test can currently be offered.</summary>
    public bool CanSendTest { get; }
    /// <summary>The original probe instant.</summary>
    public DateTimeOffset CheckedAtUtc { get; }
}

/// <summary>One identified test's actual native result; the UI keeps any human confirmation separately.</summary>
public sealed record NativeNotificationTestReceipt
{
    /// <summary>Creates a receipt with a nonzero platform identifier only for known OS acceptance.</summary>
    public NativeNotificationTestReceipt(Guid testId, BatchChannelResult result, uint? platformNotificationId, DateTimeOffset completedAtUtc)
    {
        if (testId == Guid.Empty) throw new ArgumentException("A test identifier is required.", nameof(testId));
        ArgumentNullException.ThrowIfNull(result);
        if ((result.Outcome == BatchChannelOutcome.OsAccepted) != (platformNotificationId is > 0)
            || platformNotificationId == 0)
            throw new ArgumentException("Only accepted tests have a nonzero platform identifier.", nameof(platformNotificationId));
        TestId = testId; Result = result; PlatformNotificationId = platformNotificationId;
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
    }
    /// <summary>Application test identity, independent of the OS identifier.</summary>
    public Guid TestId { get; }
    /// <summary>Actual native evidence.</summary>
    public BatchChannelResult Result { get; }
    /// <summary>Nonzero only after Windows accepted the notification.</summary>
    public uint? PlatformNotificationId { get; }
    /// <summary>The actual operation completion instant.</summary>
    public DateTimeOffset CompletedAtUtc { get; }
}
