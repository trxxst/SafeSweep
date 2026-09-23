using Microsoft.Win32;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Platform;

namespace SafeSweep.Core.Cleaning;

public readonly record struct RecycleBinInfo(long SizeBytes, long ItemCount);

/// <summary>
/// Windows Recycle Bin access through the shell API. Sending a file to the bin
/// is only attempted when the bin is guaranteed to keep it: the shell silently
/// deletes permanently when the bin is disabled for a drive or the file is
/// larger than the bin, which would turn a "recoverable" action into data loss.
/// </summary>
public static class RecycleBinService
{
    public static RecycleBinInfo Query(string? driveRoot = null)
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        int hr = NativeMethods.SHQueryRecycleBinW(driveRoot, ref info);
        return hr == 0 ? new RecycleBinInfo(info.i64Size, info.i64NumItems) : new RecycleBinInfo(0, 0);
    }

    /// <summary>Per-drive usage, for display.</summary>
    public static IReadOnlyList<(string Drive, RecycleBinInfo Info)> QueryPerDrive()
    {
        var list = new List<(string, RecycleBinInfo)>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    RecycleBinInfo info = Query(drive.RootDirectory.FullName);
                    if (info.ItemCount > 0)
                    {
                        list.Add((drive.RootDirectory.FullName, info));
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        return list;
    }

    /// <summary>Empties the Recycle Bin on all drives. Permanent.</summary>
    public static bool Empty(out string? error)
    {
        int hr = NativeMethods.SHEmptyRecycleBinW(
            IntPtr.Zero,
            null,
            NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);

        // S_OK, or E_UNEXPECTED when the bin was already empty.
        if (hr == 0 || hr == unchecked((int)0x8000FFFF))
        {
            error = null;
            return true;
        }

        error = $"The shell reported error 0x{hr:X8}.";
        return false;
    }

    /// <summary>True only when the Recycle Bin on the file's drive will really keep it.</summary>
    public static bool CanRecycle(string path, long sizeBytes, out string reason)
    {
        string? root = Path.GetPathRoot(path);
        if (root is null)
        {
            reason = "No drive root.";
            return false;
        }

        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                reason = "Only fixed drives have a Recycle Bin.";
                return false;
            }

            var info = new NativeMethods.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            if (NativeMethods.SHQueryRecycleBinW(root, ref info) != 0)
            {
                reason = "The Recycle Bin is not available on this drive.";
                return false;
            }

            (bool nuke, long? capacityBytes) = ReadBinSettings(root);
            if (nuke)
            {
                reason = "The Recycle Bin is set to delete files immediately on this drive.";
                return false;
            }

            long limit = capacityBytes ?? drive.TotalSize / 20; // Windows' default is ~5% of the drive.
            if (sizeBytes > limit * 8 / 10)
            {
                reason = "The file is too large for the Recycle Bin on this drive; Windows would delete it permanently.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reason = "Recycle Bin state could not be read: " + ex.Message;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Sends one file or folder to the Recycle Bin. Call <see cref="CanRecycle"/> first.</summary>
    public static bool Send(string path, out string? error)
    {
        var op = new NativeMethods.SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = NativeMethods.FO_DELETE,
            pFrom = path + "\0", // the marshaller adds the second terminator
            pTo = null,
            fFlags = (ushort)(NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT
                              | NativeMethods.FOF_NOERRORUI | NativeMethods.FOF_NOCONFIRMMKDIR),
        };

        int result = NativeMethods.SHFileOperationW(ref op);
        if (result != 0 || op.fAnyOperationsAborted)
        {
            error = $"The shell could not move the item to the Recycle Bin (code 0x{result:X}).";
            return false;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            error = "The item is still present after the Recycle Bin operation.";
            return false;
        }

        error = null;
        return true;
    }

    private static (bool Nuke, long? CapacityBytes) ReadBinSettings(string root)
    {
        const string bitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";
        bool nuke = false;
        long? capacity = null;

        try
        {
            using (RegistryKey? global = Registry.CurrentUser.OpenSubKey(bitBucket))
            {
                if (global?.GetValue("NukeOnDelete") is int g && g != 0)
                {
                    nuke = true;
                }
            }

            string? volume = FileSystemInspector.TryGetVolumeGuidPath(root);
            string? guid = volume is null ? null : ExtractGuid(volume);
            if (guid is not null)
            {
                using RegistryKey? perVolume = Registry.CurrentUser.OpenSubKey($@"{bitBucket}\Volume\{guid}");
                if (perVolume is not null)
                {
                    if (perVolume.GetValue("NukeOnDelete") is int n)
                    {
                        nuke = n != 0;
                    }

                    if (perVolume.GetValue("MaxCapacity") is int mb && mb > 0)
                    {
                        capacity = mb * 1024L * 1024L;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Debug("Recycle Bin settings could not be read: " + ex.Message);
        }

        return (nuke, capacity);
    }

    private static string? ExtractGuid(string volumePath)
    {
        int start = volumePath.IndexOf('{');
        int end = volumePath.IndexOf('}');
        return start >= 0 && end > start ? volumePath[start..(end + 1)] : null;
    }
}
