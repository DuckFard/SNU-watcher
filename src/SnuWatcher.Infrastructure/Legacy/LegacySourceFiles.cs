using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SnuWatcher.Infrastructure.Legacy;

/// <summary>Read-only, bounded access to explicitly selected legacy files on local Windows disks.</summary>
internal static class LegacySourceFiles
{
    internal const int ConfigByteLimit = 256 * 1024;
    internal const int DatabaseByteLimit = 25 * 1024 * 1024;
    private const string PathInvalid = "legacy_source_path_invalid";
    private const string Missing = "legacy_source_missing";
    private const string Unavailable = "legacy_source_unavailable";
    private const string NotRegular = "legacy_source_not_regular";
    private const string TooLarge = "legacy_source_too_large";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] DatabaseHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>Rejects network/device/stream paths and every reparse-point ancestor before opening a source.</summary>
    internal static string ValidateSelectedFile(string path)
    {
        try
        {
            var canonical = NormalizeLocalPath(path);
            var root = Path.GetPathRoot(canonical)!;
            if (new DriveInfo(root).DriveType == DriveType.Network) throw new LegacySourceException(PathInvalid);
            CheckAncestors(canonical);
            using var file = OpenShared(canonical);
            CheckOpenedFile(file, canonical);
            return canonical;
        }
        catch (FileNotFoundException) { throw new LegacySourceException(Missing); }
        catch (DirectoryNotFoundException) { throw new LegacySourceException(Missing); }
        catch (Exception error) when (IsFileFailure(error)) { throw new LegacySourceException(Unavailable); }
    }

    /// <summary>Reads at most the byte cap plus one sentinel byte, without sharing writes or retaining source errors.</summary>
    internal static string ReadConfiguration(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = ValidateSelectedFile(path);
        try
        {
            using var file = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            CheckOpenedFile(file, canonical);
            var bytes = new byte[ConfigByteLimit + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = file.Read(bytes, count, Math.Min(8192, bytes.Length - count));
                if (read == 0) break;
                count += read;
                if (count > ConfigByteLimit) throw new LegacySourceException(TooLarge);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var bom = count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return StrictUtf8.GetString(bytes, bom, count - bom);
        }
        catch (DecoderFallbackException) { throw new LegacySourceException("legacy_config_encoding_invalid"); }
        catch (FileNotFoundException) { throw new LegacySourceException(Missing); }
        catch (DirectoryNotFoundException) { throw new LegacySourceException(Missing); }
        catch (Exception error) when (IsFileFailure(error)) { throw new LegacySourceException(Unavailable); }
    }

    /// <summary>Checks only a fixed SQLite header and current DB/WAL sizes; it opens no SQLite connection and writes no source files.</summary>
    internal static string ValidateDatabase(string path)
    {
        var canonical = ValidateSelectedFile(path);
        try
        {
            using var file = OpenShared(canonical);
            CheckOpenedFile(file, canonical);
            var databaseLength = file.Length;
            if (databaseLength > DatabaseByteLimit) throw new LegacySourceException(TooLarge);
            var header = new byte[DatabaseHeader.Length];
            var count = 0;
            while (count < header.Length)
            {
                var read = file.Read(header, count, header.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count != header.Length || !header.AsSpan().SequenceEqual(DatabaseHeader))
                throw new LegacySourceException("legacy_database_header_invalid");
            var walLength = ExistingSidecarLength(canonical + "-wal");
            _ = ExistingSidecarLength(canonical + "-shm");
            // Neither source is locked against an external writer; this is a bounded admission snapshot.
            databaseLength = Math.Max(databaseLength, file.Length);
            if (databaseLength > DatabaseByteLimit || walLength > DatabaseByteLimit - databaseLength)
                throw new LegacySourceException(TooLarge);
            return canonical;
        }
        catch (FileNotFoundException) { throw new LegacySourceException(Missing); }
        catch (DirectoryNotFoundException) { throw new LegacySourceException(Missing); }
        catch (Exception error) when (IsFileFailure(error)) { throw new LegacySourceException(Unavailable); }
    }

    /// <summary>Keeps an existing sidecar identity alive while allowing the source writer to read and write it.</summary>
    internal static FileStream PinExistingSidecar(string path)
    {
        var canonical = ValidateSelectedFile(path);
        FileStream? file = null;
        try
        {
            file = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            CheckOpenedFile(file, canonical);
            return file;
        }
        catch
        {
            file?.Dispose();
            throw new LegacySourceException("legacy_database_wal_unavailable");
        }
    }

    private static string NormalizeLocalPath(string path)
    {
        // The downloadable application supports Windows. Reject unsupported path dialects before any I/O.
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || path.Length > 32767
            || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')
            || !Path.IsPathFullyQualified(path) || path.AsSpan(2).Contains(':')
            || path.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or '|' or '*' or '?'))
            throw new LegacySourceException(PathInvalid);
        foreach (var component in path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..") continue;
            if (component.EndsWith(' ') || component.EndsWith('.') || IsDeviceName(component))
                throw new LegacySourceException(PathInvalid);
        }
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new LegacySourceException(PathInvalid); }
    }

    private static bool IsDeviceName(string component)
    {
        var name = component.Split('.')[0].TrimEnd(' ');
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (name[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }

    private static void CheckAncestors(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        CheckAttributes(current, directory: true);
        var components = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            CheckAttributes(current, directory: index < components.Length - 1);
        }
        if (components.Length == 0) throw new LegacySourceException(NotRegular);
    }

    private static void CheckAttributes(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new LegacySourceException(NotRegular);
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    private static void CheckOpenedFile(FileStream file, string expectedPath)
    {
        if (GetFileType(file.SafeFileHandle) != 1) throw new LegacySourceException(NotRegular);
        var finalName = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(file.SafeFileHandle, finalName, (uint)finalName.Capacity, 0);
        if (length == 0 || length >= finalName.Capacity) throw new LegacySourceException(Unavailable);
        var finalPath = finalName.ToString();
        if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal)) finalPath = finalPath[4..];
        if (!string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new LegacySourceException(NotRegular);
        // Recheck the path after opening, while subsequent reads use the already-verified file handle.
        CheckAncestors(expectedPath);
    }

    private static long ExistingSidecarLength(string path)
    {
        string canonical;
        try { canonical = ValidateSelectedFile(path); }
        catch (LegacySourceException error) when (error.Code == Missing) { return 0; }
        using var file = OpenShared(canonical);
        CheckOpenedFile(file, canonical);
        return file.Length;
    }

    private static bool IsFileFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}

/// <summary>A fixed error code only: source content, paths and nested exceptions never escape the reader boundary.</summary>
internal sealed class LegacySourceException : Exception
{
    internal LegacySourceException(string code) : base(ValidateCode(code)) => Code = code;
    public string Code { get; }

    private static string ValidateCode(string code) => code is { Length: > 0 and <= 80 }
        && code.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? code : throw new ArgumentException("A bounded legacy error code is required.", nameof(code));
}
