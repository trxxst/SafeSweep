using SafeSweep.Core.Platform;

namespace SafeSweep.Core.Safety;

/// <summary>
/// Directory enumeration that never follows junctions or symbolic links.
/// Recursion is done by hand so a link inside a cleanable folder can never lead
/// the scanner (or the cleaner) into another part of the disk.
/// </summary>
public static class SafeEnumeration
{
    /// <summary>One level, include hidden/system entries, skip unreadable folders.</summary>
    public static readonly EnumerationOptions Lenient = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>One level, include everything, throw on unreadable folders (used for verification).</summary>
    public static readonly EnumerationOptions Strict = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Recursively yields files below <paramref name="root"/> without entering
    /// reparse points. Unreadable folders are reported through <paramref name="onError"/>.
    /// </summary>
    public static IEnumerable<FileInfo> EnumerateFiles(
        string root,
        bool recursive,
        Func<DirectoryInfo, bool>? enterDirectory,
        Action<string, Exception>? onError,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo current = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = current.EnumerateFileSystemInfos("*", Strict).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                onError?.Invoke(current.FullName, ex);
                continue;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if (entry is DirectoryInfo dir)
                {
                    if (!recursive || FileSystemInspector.IsReparsePoint(dir.Attributes))
                    {
                        continue;
                    }

                    if (enterDirectory is null || enterDirectory(dir))
                    {
                        pending.Push(dir);
                    }
                }
                else if (entry is FileInfo file)
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>Immediate subdirectories, excluding reparse points.</summary>
    public static IEnumerable<DirectoryInfo> EnumerateDirectories(string root, Action<string, Exception>? onError = null)
    {
        List<DirectoryInfo> result;
        try
        {
            result = new DirectoryInfo(root)
                .EnumerateDirectories("*", Lenient)
                .Where(d => !FileSystemInspector.IsReparsePoint(d.Attributes))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            onError?.Invoke(root, ex);
            return [];
        }

        return result;
    }
}
