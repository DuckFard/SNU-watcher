using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SnuWatcher.Integration.Tests;

/// <summary>Creates only test-owned junctions, without a shell process or symbolic-link privileges.</summary>
internal static class NativeJunctionFixture
{
    private const string FixturePrefix = "SnuWatcher-legacy-source-";
    private const int MaximumPathCharacters = 1024;
    private const int MaximumReparseBytes = 16 * 1024;
    private const uint MountPointTag = 0xA0000003;
    private const uint SetReparsePoint = 0x000900A4;

    internal static void Create(string fixtureRoot, string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var root = ValidateFixtureRoot(fixtureRoot);
        link = ValidateChild(root, link);
        target = ValidateChild(root, target);
        RequireRegularDirectory(target);
        if (Path.Exists(link)) throw new IOException("The fixture junction path must not exist.");
        var buffer = CreateMountPointBuffer(target);

        Directory.CreateDirectory(link);
        try
        {
            // Open the newly created directory itself, without following a reparse point.
            using var handle = CreateFileW(link, 0x40000000, 0, IntPtr.Zero, 3,
                0x02000000 | 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the fixture junction.");
            if (!DeviceIoControl(handle, SetReparsePoint, buffer, (uint)buffer.Length,
                IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the fixture junction.");
        }
        catch
        {
            // Delete only this child directory/link; never recurse into the target.
            Directory.Delete(link, recursive: false);
            throw;
        }
    }

    private static byte[] CreateMountPointBuffer(string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        // REPARSE_DATA_BUFFER: 8-byte header, 8-byte mount-point fields, then two terminated UTF-16 names.
        var dataLength = checked(8 + substitute.Length + 2 + print.Length + 2);
        var buffer = new byte[checked(8 + dataLength)];
        if (buffer.Length > MaximumReparseBytes) throw new ArgumentException("The fixture junction target is too long.");
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);
        return buffer;
    }

    private static string ValidateFixtureRoot(string path)
    {
        var root = LocalPath(path);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), temporary, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(FixturePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[FixturePrefix.Length..], "N", out _))
            throw new ArgumentException("A generated legacy fixture root is required.");
        RequireRegularDirectory(root);
        return root;
    }

    private static string ValidateChild(string root, string path)
    {
        var child = LocalPath(path);
        if (!string.Equals(Path.GetDirectoryName(child), root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Junctions and targets must be direct children of the owned fixture.");
        return child;
    }

    private static string LocalPath(string path)
    {
        if (path.Length is 0 or > MaximumPathCharacters || path.Contains('\0') || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A bounded absolute local fixture path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length > MaximumPathCharacters || Path.GetPathRoot(full) is not { Length: 3 }
            || full[1] != ':' || full.AsSpan(2).Contains(':'))
            throw new ArgumentException("A bounded absolute local fixture path is required.");
        return full;
    }

    private static void RequireRegularDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The owned fixture and target must be ordinary directories.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint controlCode, byte[] input, uint inputSize,
        IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}
