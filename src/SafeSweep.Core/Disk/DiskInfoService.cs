using SafeSweep.Core.Models;

namespace SafeSweep.Core.Disk;

public sealed record DriveUsage(string Name, string Label, long TotalBytes, long FreeBytes, bool IsSystem)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedFraction => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;

    public double UsedPercent => UsedFraction * 100;

    public string Title => string.IsNullOrWhiteSpace(Label) ? Name : $"{Label} ({Name.TrimEnd('\\')})";

    public string Summary => $"{Formatting.Bytes(FreeBytes)} free of {Formatting.Bytes(TotalBytes)}";

    public bool IsLow => FreeBytes < TotalBytes * 0.1;
}

public static class DiskInfoService
{
    public static IReadOnlyList<DriveUsage> GetFixedDrives()
    {
        string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        var list = new List<DriveUsage>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                list.Add(new DriveUsage(
                    drive.Name,
                    drive.VolumeLabel,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    drive.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase)));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return list.OrderByDescending(d => d.IsSystem).ThenBy(d => d.Name).ToList();
    }
}
