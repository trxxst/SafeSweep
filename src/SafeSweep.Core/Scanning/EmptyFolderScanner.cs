using SafeSweep.Core.Models;
using SafeSweep.Core.Platform;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Safety;

namespace SafeSweep.Core.Scanning;

/// <summary>
/// Finds folders in AppData whose whole subtree contains no files. Only the
/// topmost empty folder of each empty subtree is reported, and only when it
/// has not changed for at least 30 days.
/// </summary>
public sealed class EmptyFolderScanner
{
    private const int MaxDepth = 12;
    private static readonly TimeSpan MinIdle = TimeSpan.FromDays(30);

    public CategoryResult Scan(ScanContext context)
    {
        var result = new CategoryResult(Categories.EmptyFolders);
        SystemPaths p = context.Paths;
        string?[] containers = [p.RoamingAppData, p.LocalAppData, p.LocalAppDataLow];

        foreach (string? container in containers)
        {
            if (container is null || !Directory.Exists(container))
            {
                continue;
            }

            context.SetStage($"Looking for empty folders in {container}");
            foreach (DirectoryInfo top in SafeEnumeration.EnumerateDirectories(container))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (ProtectionPolicy.LeftoverNeverNames.Contains(top.Name))
                {
                    continue;
                }

                if (Visit(context, result, top, 1))
                {
                    Report(context, result, top);
                }
            }
        }

        return result;
    }

    /// <summary>Returns true when the subtree rooted at <paramref name="dir"/> holds no files.</summary>
    private static bool Visit(ScanContext context, CategoryResult result, DirectoryInfo dir, int depth)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        context.ReportPath(dir.FullName);

        if (depth > MaxDepth || FileSystemInspector.IsReparsePoint(dir.Attributes) || (dir.Attributes & FileAttributes.System) != 0)
        {
            return false;
        }

        List<FileSystemInfo> children;
        try
        {
            children = dir.EnumerateFileSystemInfos("*", SafeEnumeration.Strict).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (children.Any(c => c is FileInfo || FileSystemInspector.IsReparsePoint(c.Attributes)))
        {
            // Not empty: report empty children individually.
            foreach (DirectoryInfo sub in children.OfType<DirectoryInfo>())
            {
                if (Visit(context, result, sub, depth + 1))
                {
                    Report(context, result, sub);
                }
            }

            return false;
        }

        var emptyChildren = new List<DirectoryInfo>();
        bool allEmpty = true;
        foreach (DirectoryInfo sub in children.OfType<DirectoryInfo>())
        {
            if (Visit(context, result, sub, depth + 1))
            {
                emptyChildren.Add(sub);
            }
            else
            {
                allEmpty = false;
            }
        }

        if (allEmpty)
        {
            // The caller reports this folder (or an ancestor) as a whole.
            return true;
        }

        foreach (DirectoryInfo sub in emptyChildren)
        {
            Report(context, result, sub);
        }

        return false;
    }

    private static void Report(ScanContext context, CategoryResult result, DirectoryInfo dir)
    {
        DateTime newest = ScanContext.NewestTimestamp(dir);
        if (context.NowUtc - newest < MinIdle)
        {
            return;
        }

        // A tree of empty folders is removed bottom-up, so each folder is its own item.
        var folders = new List<DirectoryInfo>();
        Collect(dir, folders, 0);

        foreach (DirectoryInfo folder in folders)
        {
            if (!context.Guard.EvaluateCandidate(folder.FullName, DeletionIntent.EmptyFolder, null, folder.Attributes).IsAllowed)
            {
                context.CountProtected();
                continue;
            }

            if (!context.TryClaim(folder.FullName, 0))
            {
                continue;
            }

            result.Add(new ScanItem
            {
                Path = folder.FullName,
                Kind = ItemKind.Directory,
                Category = Categories.EmptyFolders,
                SizeBytes = 0,
                FileCount = 0,
                LastWriteUtc = folder.LastWriteTimeUtc,
                CreationUtc = folder.CreationTimeUtc,
                Reason = $"Contains no files; unchanged for {Formatting.Age(newest, context.NowUtc)}.",
                Evidence =
                [
                    "The folder and everything below it contain no files.",
                    $"Unchanged since {newest.ToLocalTime():yyyy-MM-dd}.",
                    "It is re-checked immediately before removal and kept if anything appeared in it.",
                ],
            });
        }
    }

    private static void Collect(DirectoryInfo dir, List<DirectoryInfo> into, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        // Children first so removal order is bottom-up.
        foreach (DirectoryInfo sub in SafeEnumeration.EnumerateDirectories(dir.FullName))
        {
            Collect(sub, into, depth + 1);
        }

        into.Add(dir);
    }
}
