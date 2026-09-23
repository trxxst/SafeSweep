using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SafeSweep.Core.Platform;

/// <summary>Stable identity of a file on disk: two paths with the same id are hard links.</summary>
public readonly record struct FileIdentity(uint VolumeSerial, ulong FileIndex, uint LinkCount);

/// <summary>Low-level file system queries the .NET BCL does not expose.</summary>
public static class FileSystemInspector
{
    private const int CloudAttributes =
        NativeMethods.FILE_ATTRIBUTE_OFFLINE
        | NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN
        | NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
        | NativeMethods.FILE_ATTRIBUTE_PINNED
        | NativeMethods.FILE_ATTRIBUTE_UNPINNED;

    /// <summary>
    /// True for OneDrive / cloud-sync placeholders. Reading them triggers a
    /// download and deleting them deletes the cloud copy, so SafeSweep never
    /// touches them.
    /// </summary>
    public static bool IsCloudFile(FileAttributes attributes) => ((int)attributes & CloudAttributes) != 0;

    public static bool IsReparsePoint(FileAttributes attributes) => (attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Resolves a path through every junction, symlink and 8.3 short name to the
    /// real location on disk. Returns null when the path cannot be opened.
    /// </summary>
    public static string? TryGetFinalPath(string path)
    {
        using SafeFileHandle handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.FILE_READ_ATTRIBUTES,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        char[] buffer = new char[1024];
        uint length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NativeMethods.FILE_NAME_NORMALIZED);
        if (length > buffer.Length)
        {
            buffer = new char[length + 1];
            length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NativeMethods.FILE_NAME_NORMALIZED);
        }

        if (length == 0 || length > buffer.Length)
        {
            return null;
        }

        string result = new(buffer, 0, (int)length);
        return StripExtendedPrefix(result);
    }

    /// <summary>Returns the file's volume + index identity without following reparse points.</summary>
    public static FileIdentity? TryGetIdentity(string path)
    {
        using SafeFileHandle handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.FILE_READ_ATTRIBUTES,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (handle.IsInvalid || !NativeMethods.GetFileInformationByHandle(handle, out NativeMethods.BY_HANDLE_FILE_INFORMATION info))
        {
            return null;
        }

        ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return new FileIdentity(info.VolumeSerialNumber, index, info.NumberOfLinks);
    }

    /// <summary>Expands 8.3 short names (e.g. PROGRA~1) to their long form.</summary>
    public static string ExpandShortNames(string path)
    {
        if (!path.Contains('~'))
        {
            return path;
        }

        char[] buffer = new char[1024];
        uint length = NativeMethods.GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (length == 0 || length > buffer.Length)
        {
            return path;
        }

        return new string(buffer, 0, (int)length);
    }

    /// <summary>Returns the volume GUID path (\\?\Volume{...}\) for a drive root, or null.</summary>
    public static string? TryGetVolumeGuidPath(string driveRoot)
    {
        char[] buffer = new char[64];
        if (!NativeMethods.GetVolumeNameForVolumeMountPointW(driveRoot, buffer, (uint)buffer.Length))
        {
            return null;
        }

        return new string(buffer).TrimEnd('\0');
    }

    public static bool IsOnBatteryPower()
    {
        if (!NativeMethods.GetSystemPowerStatus(out NativeMethods.SYSTEM_POWER_STATUS status))
        {
            return false;
        }

        // 0 = offline (battery), 1 = online (AC), 255 = unknown.
        return status.ACLineStatus == 0;
    }

    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[4..];
        }

        return path;
    }

    internal static int LastError => Marshal.GetLastWin32Error();
}
