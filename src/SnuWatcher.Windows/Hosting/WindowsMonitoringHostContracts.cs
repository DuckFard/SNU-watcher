using SnuWatcher.Application.Monitoring.Hosting;

namespace SnuWatcher.Windows.Hosting;

/// <summary>Whether this process acquired and started a runtime.</summary>
public enum WindowsHostOpenDisposition
{
    /// <summary>No database/source factory was called.</summary>
    AlreadyOwned,
    /// <summary>Recovery completed and logical actors were started.</summary>
    Started,
}

/// <summary>Physical resource ownership state.</summary>
public enum WindowsMonitoringHostState
{
    /// <summary>Resource acquisition is in progress.</summary>
    Starting,
    /// <summary>The owned logical session is running.</summary>
    Running,
    /// <summary>Admission is closed and actual work is draining.</summary>
    Stopping,
    /// <summary>Workers are quiesced; canonical writes/resources require explicit retry.</summary>
    StopBlocked,
    /// <summary>All owned resources and the database owner lease have closed.</summary>
    Stopped,
}

/// <summary>Acquisition result. A started host must be explicitly stopped/disposed.</summary>
public sealed record WindowsHostOpenResult
{
    internal WindowsHostOpenResult(WindowsHostOpenDisposition disposition, WindowsMonitoringHost? host)
    { Disposition = disposition; Host = host; }
    /// <summary>Started or excluded by an existing owner.</summary>
    public WindowsHostOpenDisposition Disposition { get; }
    /// <summary>Present only after successful startup.</summary>
    public WindowsMonitoringHost? Host { get; }
}

/// <summary>Logical completion never alone proves external resources or ownership were released.</summary>
public sealed record WindowsHostStopResult
{
    internal WindowsHostStopResult(WindowsMonitoringHostState state, RuntimeSessionStopResult? logicalResult, string? failureCode)
    { State = state; LogicalResult = logicalResult; FailureCode = failureCode; }
    /// <summary>Physical resource state after this stop/finalization attempt.</summary>
    public WindowsMonitoringHostState State { get; }
    /// <summary>The session's exact logical result, if startup reached it.</summary>
    public RuntimeSessionStopResult? LogicalResult { get; }
    /// <summary>Bounded machine code only, never an exception/path payload.</summary>
    public string? FailureCode { get; }
    /// <summary>True only after the store and owner lease also closed.</summary>
    public bool IsFinalized => State == WindowsMonitoringHostState.Stopped;
}

/// <summary>A failed startup whose canonical finalization still owns resources; the caller must retain and retry the host.</summary>
public sealed class WindowsMonitoringHostStartupException : InvalidOperationException
{
    internal WindowsMonitoringHostStartupException(WindowsMonitoringHost host, Exception cause)
        : base("Runtime startup finalization is blocked; ownership was retained.", cause) => Host = host;
    /// <summary>The retained owner, never an abandoned background cleanup operation.</summary>
    public WindowsMonitoringHost Host { get; }
}
