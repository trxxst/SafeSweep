using SafeSweep.Core.Apps;
using SafeSweep.Core.Models;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Safety;

namespace SafeSweep.Core.Scanning;

/// <summary>Shared walk over the user's review roots with the protection check applied.</summary>
internal static class ReviewWalker
{
    public static IEnumerable<(FileInfo File, FileAttributes Attributes)> Walk(ScanContext context, IEnumerable<string> roots, bool recursive = true)
    {
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in roots)
        {
            string? root = PathUtil.TryNormalize(Environment.ExpandEnvironmentVariables(raw), out _);
            if (root is null || !Directory.Exists(root) || !seenRoots.Add(root))
            {
                continue;
            }

            // Skip a root nested in another root already walked.
            if (seenRoots.Any(r => !r.Equals(root, StringComparison.OrdinalIgnoreCase) && PathUtil.IsStrictlyUnder(root, r)))
            {
                continue;
            }

            IEnumerable<FileInfo> files = SafeEnumeration.EnumerateFiles(
                root,
                recursive,
                dir => !ProtectionPolicy.ProtectedSegmentNames.Contains(dir.Name)
                       && !dir.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                       && (dir.Attributes & FileAttributes.System) == 0,
                null,
                context.CancellationToken);

            foreach (FileInfo file in files)
            {
                context.ReportPath(file.FullName);
                FileAttributes attributes;
                try
                {
                    attributes = file.Attributes;
                }
                catch (IOException)
                {
                    continue;
                }

                yield return (file, attributes);
            }
        }
    }

    public static IReadOnlyList<string> Roots(ScanContext context, IReadOnlyList<string>? overrideRoots = null)
    {
        if (overrideRoots is { Count: > 0 })
        {
            return overrideRoots;
        }

        return context.Settings.ReviewRoots.Count > 0 ? context.Settings.ReviewRoots : context.Paths.DefaultReviewRoots;
    }
}

/// <summary>Setup files in Downloads that are old enough to be probably unneeded.</summary>
public sealed class OldInstallerScanner
{
    private static readonly string[] Patterns = ["*.exe", "*.msi", "*.msix", "*.msixbundle", "*.appx", "*.appxbundle"];

    public CategoryResult Scan(ScanContext context)
    {
        var result = new CategoryResult(Categories.OldInstallers);
        string? downloads = context.Paths.Downloads;
        if (downloads is null || !Directory.Exists(downloads))
        {
            return result;
        }

        context.SetStage("Looking for old installers in Downloads");
        InstalledAppInventory inventory = context.InventoryProvider();
        TimeSpan minAge = TimeSpan.FromDays(context.Settings.OldInstallerDays);

        foreach ((FileInfo file, FileAttributes attributes) in ReviewWalker.Walk(context, [downloads]))
        {
            if (!Patterns.Any(p => PathUtil.WildcardMatch(file.Name, p)))
            {
                continue;
            }

            DateTime newest = ScanContext.NewestTimestamp(file);
            if (context.NowUtc - newest < minAge)
            {
                continue;
            }

            if (!context.Guard.EvaluateCandidate(file.FullName, DeletionIntent.UserReview, null, attributes).IsAllowed)
            {
                context.CountProtected();
                continue;
            }

            if (!context.TryClaim(file.FullName, file.Length))
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(file.Name);
            bool installed = inventory.MatchesInstalledName(stem, out string? matchedBy);
            string age = Formatting.Age(newest, context.NowUtc);

            result.Add(new ScanItem
            {
                Path = file.FullName,
                Kind = ItemKind.File,
                Category = Categories.OldInstallers,
                SizeBytes = file.Length,
                LastWriteUtc = file.LastWriteTimeUtc,
                LastAccessUtc = file.LastAccessTimeUtc,
                CreationUtc = file.CreationTimeUtc,
                Reason = installed
                    ? $"Installer downloaded {age} ago; the program appears to be installed."
                    : $"Installer downloaded {age} ago.",
                Evidence =
                [
                    $"Setup file type: {file.Extension}",
                    $"In Downloads for {age}",
                    installed
                        ? $"An installed program matches its name (\"{matchedBy}\"), so the setup file is probably no longer needed."
                        : "No installed program matches its name; keep it if you still intend to install it.",
                    "It will be quarantined (or sent to the Recycle Bin), not deleted outright.",
                ],
                RiskOverride = installed ? RiskLevel.Low : RiskLevel.Moderate,
            });
        }

        return result;
    }
}

/// <summary>
/// Files the user has not modified for a long time. Never pre-selected. Age
/// is never the only signal shown: file type, location and (where Windows
/// records it) last access are all reported, and personal content is marked
/// high risk.
/// </summary>
public sealed class OldFileScanner
{
    private const long MinSize = 1024 * 1024;
    private const int MaxResults = 20_000;

    public CategoryResult Scan(ScanContext context, IReadOnlyList<string>? roots = null)
    {
        var result = new CategoryResult(Categories.OldFiles);
        context.SetStage("Looking for old, unused files");
        TimeSpan minAge = TimeSpan.FromDays(context.Settings.OldFileDays);
        bool? accessTracked = LastAccessPolicy.IsTracked;
        result.AddNote(LastAccessPolicy.Description);
        result.AddNote($"Only files of 1 MB or more, unchanged for at least {context.Settings.OldFileDays} days, are listed. Nothing here is selected for you.");

        foreach ((FileInfo file, FileAttributes attributes) in ReviewWalker.Walk(context, ReviewWalker.Roots(context, roots)))
        {
            if (result.TotalCount >= MaxResults)
            {
                result.AddNote($"Stopped after {MaxResults} files; narrow the folders in Settings to see the rest.");
                break;
            }

            long length;
            try
            {
                length = file.Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (length < MinSize)
            {
                continue;
            }

            // Both modification and creation must be old: a file copied here
            // recently keeps an old modification date but a new creation date.
            DateTime newest = ScanContext.NewestTimestamp(file);
            if (context.NowUtc - newest < minAge)
            {
                continue;
            }

            DateTime lastAccess = file.LastAccessTimeUtc;
            if (accessTracked == true && context.NowUtc - lastAccess < TimeSpan.FromDays(90))
            {
                continue; // opened recently
            }

            if (!context.Guard.EvaluateCandidate(file.FullName, DeletionIntent.UserReview, null, attributes).IsAllowed)
            {
                context.CountProtected();
                continue;
            }

            if (!context.TryClaim(file.FullName, length))
            {
                continue;
            }

            FileKind kind = FileClassifier.Classify(file.FullName);
            bool personal = FileClassifier.IsPersonalContent(kind);
            string age = Formatting.Age(newest, context.NowUtc);
            var evidence = new List<string>
            {
                $"Not modified for {age} (last change {newest.ToLocalTime():yyyy-MM-dd})",
                accessTracked == true
                    ? $"Last opened: {lastAccess.ToLocalTime():yyyy-MM-dd} (approximate)"
                    : "Last opened: unknown (Windows does not record it here)",
                $"Type: {FileClassifier.Describe(kind)}",
                $"Folder: {file.DirectoryName}",
            };

            if (personal)
            {
                evidence.Add("This looks like personal content. Age alone does not mean you no longer need it.");
            }

            result.Add(new ScanItem
            {
                Path = file.FullName,
                Kind = ItemKind.File,
                Category = Categories.OldFiles,
                SizeBytes = length,
                LastWriteUtc = file.LastWriteTimeUtc,
                LastAccessUtc = accessTracked == true ? lastAccess : null,
                CreationUtc = file.CreationTimeUtc,
                Reason = $"{FileClassifier.Describe(kind)} not modified for {age}.",
                Evidence = evidence,
                RiskOverride = personal ? RiskLevel.High : RiskLevel.Moderate,
            });
        }

        return result;
    }
}

/// <summary>The largest files in the chosen folders.</summary>
public sealed class LargeFileScanner
{
    private const int MaxResults = 5_000;

    public CategoryResult Scan(ScanContext context, IReadOnlyList<string>? roots, long thresholdBytes)
    {
        var result = new CategoryResult(Categories.LargeFiles);
        context.SetStage("Looking for large files");
        var found = new List<ScanItem>();
        int protectedCount = 0;

        foreach ((FileInfo file, FileAttributes attributes) in ReviewWalker.Walk(context, ReviewWalker.Roots(context, roots)))
        {
            long length;
            try
            {
                length = file.Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (length < thresholdBytes)
            {
                continue;
            }

            if (!context.Guard.EvaluateCandidate(file.FullName, DeletionIntent.UserReview, null, attributes).IsAllowed)
            {
                protectedCount++;
                context.CountProtected();
                continue;
            }

            if (!context.TryClaim(file.FullName, length))
            {
                continue;
            }

            FileKind kind = FileClassifier.Classify(file.FullName);
            DateTime newest = ScanContext.NewestTimestamp(file);
            found.Add(new ScanItem
            {
                Path = file.FullName,
                Kind = ItemKind.File,
                Category = Categories.LargeFiles,
                SizeBytes = length,
                LastWriteUtc = file.LastWriteTimeUtc,
                LastAccessUtc = LastAccessPolicy.IsTracked == true ? file.LastAccessTimeUtc : null,
                CreationUtc = file.CreationTimeUtc,
                Reason = $"{FileClassifier.Describe(kind)}, {Formatting.Bytes(length)}.",
                Evidence =
                [
                    $"Size: {Formatting.Bytes(length)}",
                    $"Type: {FileClassifier.Describe(kind)}",
                    $"Last changed {Formatting.Age(newest, context.NowUtc)} ago",
                    "Size alone does not make a file removable; decide for yourself.",
                ],
                RiskOverride = kind is FileKind.VirtualDisk || FileClassifier.IsPersonalContent(kind) ? RiskLevel.High : RiskLevel.Moderate,
            });
        }

        foreach (ScanItem item in found.OrderByDescending(i => i.SizeBytes).Take(MaxResults))
        {
            result.Add(item);
        }

        if (protectedCount > 0)
        {
            result.AddNote($"{protectedCount} large file(s) in protected locations (program folders, OneDrive, game libraries, system files) are not listed.");
        }

        return result;
    }
}
