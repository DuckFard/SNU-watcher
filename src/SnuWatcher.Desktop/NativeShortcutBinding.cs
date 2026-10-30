using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using SnuWatcher.Windows;

namespace SnuWatcher.Desktop;

/// <summary>
/// Binds only our existing installer-owned shortcut to the SDK's actual notification activator.
/// Shell visibility is verified manually; writing these properties is not a visibility guarantee.
/// </summary>
internal static class NativeShortcutBinding
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid AppModelProperties = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    internal static bool BindExisting()
    {
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "SNU Watcher", "SNU Watcher.lnk");
        if (!File.Exists(shortcut)) return false;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + NativeAppIdentity.AppId);
        var registeredActivator = key?.GetValue("CustomActivator") as string;
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass, throwOnError: true)!)!;
        try
        {
            var file = (IPersistFile)instance;
            file.Load(shortcut, 2); // STGM_READWRITE; do not create or replace another shortcut.
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var activator = ShortcutBindingPolicy.Validate(target.ToString(), Environment.ProcessPath!, registeredActivator);
            var properties = (IPropertyStore)instance;
            var activatorKey = new PropertyKey(AppModelProperties, 26);
            var idKey = new PropertyKey(AppModelProperties, 5);
            using var activatorValue = PropertyValue.FromGuid(activator);
            using var idValue = PropertyValue.FromString(NativeAppIdentity.AppId);
            properties.SetValue(in activatorKey, in activatorValue);
            properties.SetValue(in idKey, in idValue);
            properties.Commit();
            file.Save(shortcut, true);
            // Reopen from disk and verify both identity fields, not just a successful setter return.
            file.Load(shortcut, 0);
            properties.GetValue(in idKey, out var actualId);
            properties.GetValue(in activatorKey, out var actualActivator);
            try
            {
                if (actualId.ReadString() != NativeAppIdentity.AppId || actualActivator.ReadGuid() != activator)
                    throw new NativeShortcutRegistrationException(
                        $"Shortcut read-back mismatch; app-id variant={actualId.TypeCode}; activator variant={actualActivator.TypeCode}.");
            }
            finally { actualId.Dispose(); actualActivator.Dispose(); }
            SHChangeNotify(0x00002000, 0x0005, shortcut, IntPtr.Zero); // updated item, Unicode path
            return true;
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        private readonly Guid _format = format;
        private readonly uint _id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue : IDisposable
    {
        [FieldOffset(0)] private ushort _type;
        [FieldOffset(8)] private IntPtr _value;
        internal readonly ushort TypeCode => _type;
        internal static PropertyValue FromString(string value) => new() { _type = 31, _value = Marshal.StringToCoTaskMemUni(value) };
        internal static PropertyValue FromGuid(Guid value)
        {
            var memory = Marshal.AllocCoTaskMem(16);
            Marshal.StructureToPtr(value, memory, false);
            return new PropertyValue { _type = 72, _value = memory };
        }
        internal readonly string? ReadString() => _type switch
        {
            31 => Marshal.PtrToStringUni(_value), // VT_LPWSTR
            8 => Marshal.PtrToStringBSTR(_value), // VT_BSTR after Shell property normalization
            _ => null
        };
        internal readonly Guid ReadGuid()
        {
            if (_type == 72 && _value != IntPtr.Zero) // VT_CLSID
                return Marshal.PtrToStructure<Guid>(_value);
            // Shell can persist/read a Guid-valued property as its canonical string form.
            return Guid.TryParse(ReadString(), out var value) ? value : Guid.Empty;
        }
        public void Dispose() => Marshal.ThrowExceptionForHR(PropVariantClear(ref this));
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropertyValue value);
        void SetValue(in PropertyKey key, in PropertyValue value);
        void Commit();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr item);
        void SetIDList(IntPtr item);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropertyValue value);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item, IntPtr secondItem);
}

/// <summary>A bounded app-owned diagnostic without executable/user paths or arbitrary source content.</summary>
internal sealed class NativeShortcutRegistrationException(string diagnostic) : InvalidOperationException(diagnostic)
{
    internal string Diagnostic { get; } = diagnostic;
}
