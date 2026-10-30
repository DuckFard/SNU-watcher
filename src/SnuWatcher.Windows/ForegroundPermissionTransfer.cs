using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SnuWatcher.Windows;

internal static class ForegroundPermissionPolicy
{
    internal static bool RequiresForeground(ActivationAction action) =>
        action is ActivationAction.Show or ActivationAction.ViewSection;

    internal static bool IsValidProcessId(uint processId) => processId is > 0 and <= int.MaxValue;

    internal static bool TryAllow(uint processId, uint ownerSession, uint currentSession, Func<uint, bool> allow)
    {
        ArgumentNullException.ThrowIfNull(allow);
        // Never pass ASFW_ANY (0xffffffff), an invalid PID, or another session's process.
        return IsValidProcessId(processId) && ownerSession == currentSession && allow(processId);
    }
}

internal readonly record struct PipeServerIdentity(uint ProcessId, uint SessionId);

internal static class ForegroundPermissionTransfer
{
    internal static bool TryAllow(SafePipeHandle pipe)
    {
        if (!TryGetServerIdentity(pipe, out var identity)) return false;
        using var current = Process.GetCurrentProcess();
        return ForegroundPermissionPolicy.TryAllow(identity.ProcessId, identity.SessionId,
            (uint)current.SessionId, AllowSetForegroundWindow);
    }

    internal static bool TryGetServerIdentity(SafePipeHandle? pipe, out PipeServerIdentity identity)
    {
        identity = default;
        if (pipe is null || pipe.IsClosed || pipe.IsInvalid) return false;
        // Query the connected kernel handle, never a PID supplied by the IPC payload or acknowledgement.
        if (!GetNamedPipeServerProcessId(pipe, out var processId) ||
            !ForegroundPermissionPolicy.IsValidProcessId(processId) ||
            !ProcessIdToSessionId(processId, out var sessionId)) return false;
        identity = new PipeServerIdentity(processId, sessionId);
        return true;
    }

    // Microsoft requires the forwarding process to grant rights before the owner tries to activate.
    // Rights remain subject to Windows policy and may be revoked by later user input.
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    // https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
