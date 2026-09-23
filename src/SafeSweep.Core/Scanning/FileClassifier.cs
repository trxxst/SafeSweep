using Microsoft.Win32;

namespace SafeSweep.Core.Scanning;

public enum FileKind
{
    Other,
    Document,
    Image,
    Video,
    Audio,
    Archive,
    DiskImage,
    VirtualDisk,
    Installer,
    Code,
}

public static class FileClassifier
{
    private static readonly Dictionary<string, FileKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = FileKind.Document,
        [".docx"] = FileKind.Document,
        [".pdf"] = FileKind.Document,
        [".xls"] = FileKind.Document,
        [".xlsx"] = FileKind.Document,
        [".ppt"] = FileKind.Document,
        [".pptx"] = FileKind.Document,
        [".odt"] = FileKind.Document,
        [".ods"] = FileKind.Document,
        [".txt"] = FileKind.Document,
        [".rtf"] = FileKind.Document,
        [".md"] = FileKind.Document,
        [".csv"] = FileKind.Document,
        [".epub"] = FileKind.Document,
        [".psd"] = FileKind.Document,
        [".ai"] = FileKind.Document,
        [".jpg"] = FileKind.Image,
        [".jpeg"] = FileKind.Image,
        [".png"] = FileKind.Image,
        [".gif"] = FileKind.Image,
        [".bmp"] = FileKind.Image,
        [".heic"] = FileKind.Image,
        [".webp"] = FileKind.Image,
        [".tif"] = FileKind.Image,
        [".tiff"] = FileKind.Image,
        [".raw"] = FileKind.Image,
        [".cr2"] = FileKind.Image,
        [".nef"] = FileKind.Image,
        [".arw"] = FileKind.Image,
        [".mp4"] = FileKind.Video,
        [".mkv"] = FileKind.Video,
        [".avi"] = FileKind.Video,
        [".mov"] = FileKind.Video,
        [".wmv"] = FileKind.Video,
        [".webm"] = FileKind.Video,
        [".m4v"] = FileKind.Video,
        [".flv"] = FileKind.Video,
        [".ts"] = FileKind.Video,
        [".mp3"] = FileKind.Audio,
        [".flac"] = FileKind.Audio,
        [".wav"] = FileKind.Audio,
        [".m4a"] = FileKind.Audio,
        [".aac"] = FileKind.Audio,
        [".ogg"] = FileKind.Audio,
        [".wma"] = FileKind.Audio,
        [".zip"] = FileKind.Archive,
        [".rar"] = FileKind.Archive,
        [".7z"] = FileKind.Archive,
        [".tar"] = FileKind.Archive,
        [".gz"] = FileKind.Archive,
        [".bz2"] = FileKind.Archive,
        [".xz"] = FileKind.Archive,
        [".iso"] = FileKind.DiskImage,
        [".img"] = FileKind.DiskImage,
        [".dmg"] = FileKind.DiskImage,
        [".vhd"] = FileKind.VirtualDisk,
        [".vhdx"] = FileKind.VirtualDisk,
        [".vmdk"] = FileKind.VirtualDisk,
        [".vdi"] = FileKind.VirtualDisk,
        [".qcow2"] = FileKind.VirtualDisk,
        [".exe"] = FileKind.Installer,
        [".msi"] = FileKind.Installer,
        [".msix"] = FileKind.Installer,
        [".msixbundle"] = FileKind.Installer,
        [".appx"] = FileKind.Installer,
        [".appxbundle"] = FileKind.Installer,
        [".cs"] = FileKind.Code,
        [".js"] = FileKind.Code,
        [".py"] = FileKind.Code,
        [".java"] = FileKind.Code,
        [".cpp"] = FileKind.Code,
        [".h"] = FileKind.Code,
        [".json"] = FileKind.Code,
        [".sln"] = FileKind.Code,
    };

    public static FileKind Classify(string path) => Map.TryGetValue(Path.GetExtension(path), out FileKind kind) ? kind : FileKind.Other;

    public static bool IsPersonalContent(FileKind kind)
        => kind is FileKind.Document or FileKind.Image or FileKind.Video or FileKind.Audio or FileKind.Code;

    public static string Describe(FileKind kind) => kind switch
    {
        FileKind.Document => "Document",
        FileKind.Image => "Photo / image",
        FileKind.Video => "Video",
        FileKind.Audio => "Audio",
        FileKind.Archive => "Archive",
        FileKind.DiskImage => "Disk image",
        FileKind.VirtualDisk => "Virtual machine disk (may contain a whole operating system and its files)",
        FileKind.Installer => "Installer / program",
        FileKind.Code => "Source code / project file",
        _ => "File",
    };
}

/// <summary>
/// Whether NTFS last-access timestamps can be trusted on this PC. They are
/// disabled on many systems and updated lazily everywhere, so they are only
/// ever used as a supporting signal, never as the reason to remove a file.
/// </summary>
public static class LastAccessPolicy
{
    private static readonly Lazy<bool?> Enabled = new(Read);

    /// <summary>True = updates enabled, false = disabled, null = unknown.</summary>
    public static bool? IsTracked => Enabled.Value;

    public static string Description => IsTracked switch
    {
        true => "Windows records last-access times on this PC (updated lazily, so treat them as approximate).",
        false => "Windows does not record last-access times on this PC, so \"last opened\" is unknown.",
        _ => "Whether Windows records last-access times could not be determined, so \"last opened\" is treated as unknown.",
    };

    private static bool? Read()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            object? value = key?.GetValue("NtfsDisableLastAccessUpdate");
            if (value is int raw)
            {
                // Low bit set = updates disabled (legacy 1, 0x80000001, 0x80000003).
                return (raw & 1) == 0;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return null;
    }
}
