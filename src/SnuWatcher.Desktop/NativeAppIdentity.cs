using System;
using System.Runtime.InteropServices;

namespace SnuWatcher.Desktop;

/// <summary>
/// Sets the app's own stable unpackaged notification identity before the SDK resolves its manager.
/// Without it, Windows App SDK can derive a different GUID for each executable path.
/// </summary>
internal static class NativeAppIdentity
{
    internal const string AppId = "DuckFard.SnuWatcher";

    internal static void Initialize() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));

    internal static string ReadCurrent()
    {
        Marshal.ThrowExceptionForHR(GetCurrentProcessExplicitAppUserModelID(out var identity));
        try { return Marshal.PtrToStringUni(identity) ?? "Unavailable"; }
        finally { Marshal.FreeCoTaskMem(identity); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appId);
}
