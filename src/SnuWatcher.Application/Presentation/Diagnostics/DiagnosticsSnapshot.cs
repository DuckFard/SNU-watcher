using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Application.Presentation.Diagnostics;

public enum DiagnosticsSourceCondition { Unknown, NoKnownBlock, Offline, Suspended, Cooldown, AccessRestricted, StorageFailed, ActorFailed, OtherBlocked }
public enum DiagnosticsNotificationCondition { Unknown, Initializing, Ready, Disabled, Unavailable }

/// <summary>Only known saved choices. Academic year/term, drafts, labels and identities are deliberately absent.</summary>
public sealed record DiagnosticPreferenceFacts
{
    public DiagnosticPreferenceFacts(UiLanguage? language, WatchMonitorProfile? desiredProfile, UiCloseBehavior? closeBehavior, bool? soundMuted)
    {
        DiagnosticsValidation.Defined(language); DiagnosticsValidation.Defined(desiredProfile); DiagnosticsValidation.Defined(closeBehavior);
        Language = language; DesiredProfile = desiredProfile; CloseBehavior = closeBehavior; SoundMuted = soundMuted;
    }
    public UiLanguage? Language { get; }
    public WatchMonitorProfile? DesiredProfile { get; }
    public UiCloseBehavior? CloseBehavior { get; }
    public bool? SoundMuted { get; }
}

/// <summary>A closed scalar whitelist. Original source payloads and diagnostic strings cannot enter this value.</summary>
public sealed record DiagnosticsRuntimeFacts
{
    public DiagnosticsRuntimeFacts(RuntimeSessionState state, DiagnosticsSourceCondition source, DiagnosticsNotificationCondition notifications,
        WatchMonitorProfile? appliedProfile, DiagnosticPreferenceFacts preferences, bool? isOnline, bool? isSuspended)
    {
        DiagnosticsValidation.Defined<RuntimeSessionState>(state); DiagnosticsValidation.Defined<DiagnosticsSourceCondition>(source);
        DiagnosticsValidation.Defined<DiagnosticsNotificationCondition>(notifications); DiagnosticsValidation.Defined(appliedProfile);
        ArgumentNullException.ThrowIfNull(preferences);
        State = state; Source = source; Notifications = notifications; AppliedProfile = appliedProfile;
        Preferences = preferences; IsOnline = isOnline; IsSuspended = isSuspended;
    }
    public RuntimeSessionState State { get; }
    public DiagnosticsSourceCondition Source { get; }
    public DiagnosticsNotificationCondition Notifications { get; }
    public WatchMonitorProfile? AppliedProfile { get; }
    public DiagnosticPreferenceFacts Preferences { get; }
    public bool? IsOnline { get; }
    public bool? IsSuspended { get; }
}

/// <summary>Local observations, not a coherent source/database snapshot. Versions contain numeric components only.</summary>
public sealed record DiagnosticsSnapshot
{
    public DiagnosticsSnapshot(Version applicationVersion, Version runtimeVersion, DateTimeOffset capturedAtUtc,
        DiagnosticsRuntimeFacts runtime, StorageFootprint storage)
    {
        ArgumentNullException.ThrowIfNull(applicationVersion); ArgumentNullException.ThrowIfNull(runtimeVersion);
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(storage);
        ApplicationVersion = applicationVersion; RuntimeVersion = runtimeVersion; CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Runtime = runtime; Storage = storage;
    }
    public Version ApplicationVersion { get; }
    public Version RuntimeVersion { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public DiagnosticsRuntimeFacts Runtime { get; }
    public StorageFootprint Storage { get; }
}

public enum DiagnosticsExportOutcome { Saved, Canceled }
public interface IDiagnosticsReader { Task<DiagnosticsSnapshot> ReadAsync(CancellationToken cancellationToken = default); }
/// <summary>Explicit user-selected export. A dialog remains owned until dismissed; late cancellation cannot erase a completed write.</summary>
public interface IDiagnosticsExportAction
{
    Task<DiagnosticsExportOutcome> ExportAsync(DiagnosticsSnapshot snapshot, CancellationToken cancellationToken = default);
}
internal static class DiagnosticsValidation
{
    internal static void Defined<T>(T? value) where T : struct, Enum
    { if (value is { } item && !Enum.IsDefined(item)) throw new ArgumentOutOfRangeException(nameof(value)); }
}
