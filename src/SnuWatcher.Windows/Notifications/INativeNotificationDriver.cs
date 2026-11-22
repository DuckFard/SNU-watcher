using SnuWatcher.Application.Delivery.Batches;

namespace SnuWatcher.Windows.Notifications;

/// <summary>Synchronous platform boundary invoked exclusively by the owned native worker.</summary>
public interface INativeNotificationDriver
{
    /// <summary>Checks installed identity, registration and current OS permissions without submitting.</summary>
    BatchChannelReadiness Probe();
    /// <summary>Creates the platform object before the explicit entered-Show boundary.</summary>
    IPreparedNativeNotification Prepare(NativeToastPayload payload);
}

/// <summary>A prepared native object owned through its actual synchronous Show and cleanup.</summary>
public interface IPreparedNativeNotification : IDisposable
{
    /// <summary>Invokes the OS boundary; exceptions after entry are uncertain acceptance, never proof of no call.</summary>
    NativeShowResult Show();
}

/// <summary>Actual native evidence; an explicit rejection requires driver-specific proof.</summary>
public sealed record NativeShowResult
{
    /// <summary>Zero without verified rejection is uncertain. A positive ID means known platform acceptance.</summary>
    public NativeShowResult(uint platformNotificationId, bool rejectionVerified = false)
    {
        if (platformNotificationId != 0 && rejectionVerified) throw new ArgumentException("Acceptance cannot also be rejection.");
        PlatformNotificationId = platformNotificationId; RejectionVerified = rejectionVerified;
    }
    /// <summary>Platform assigned ID, zero when acceptance is not known.</summary>
    public uint PlatformNotificationId { get; }
    /// <summary>True only with concrete driver proof of OS rejection after invocation.</summary>
    public bool RejectionVerified { get; }
}
