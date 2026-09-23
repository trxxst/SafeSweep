using SafeSweep.Core.Apps;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;
using SafeSweep.Core.Platform;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Safety;

namespace SafeSweep.Core.Scanning;

/// <summary>
/// Finds folders left behind by uninstalled programs. A folder is only
/// reported when every available signal agrees nothing installed or running
/// owns it, and it has been idle for a long time. Anything uncertain is left out.
/// </summary>
public sealed class LeftoverScanner
{
    private const int MaxEntriesPerFolder = 200_000;

    public CategoryResult Scan(ScanContext context)
    {
        var result = new CategoryResult(Categories.Leftovers);
        context.SetStage("Building the installed-program inventory");
        InstalledAppInventory inventory = context.InventoryProvider();
        SystemPaths p = context.Paths;
        int sensitiveSkipped = 0;
        int protectedSkipped = 0;
        int unidentifiable = 0;

        var containers = new List<(string Path, string Label, bool Packages)>
        {
            (p.RoamingAppData, "AppData\\Roaming", false),
            (p.LocalAppData, "AppData\\Local", false),
            (p.ProgramData, "ProgramData", false),
        };

        if (p.LocalAppDataLow is not null)
        {
            containers.Add((p.LocalAppDataLow, "AppData\\LocalLow", false));
        }

        string packages = Path.Combine(p.LocalAppData, "Packages");
        if (Directory.Exists(packages))
        {
            if (inventory.PackagesKnown)
            {
                containers.Add((packages, "Store app data", true));
            }
            else
            {
                result.AddNote("The list of installed Store apps could not be read, so Store app data is not checked.");
            }
        }

        TimeSpan minIdle = TimeSpan.FromDays(context.Settings.LeftoverMinIdleDays);

        foreach ((string container, string label, bool isPackages) in containers)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.SetStage($"Looking for leftovers in {label}");

            foreach (DirectoryInfo folder in SafeEnumeration.EnumerateDirectories(container))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.ReportPath(folder.FullName);

                string name = folder.Name;
                if (isPackages)
                {
                    // Only real package family names. AppContainer profiles created by
                    // Windows and browser sandboxes (cr.sb.*, windows_ie_ac_001, ActiveSync)
                    // live here too and belong to no Store package.
                    if (!NameMatcher.IsPackageFamilyName(name))
                    {
                        continue;
                    }
                }
                else if (ProtectionPolicy.LeftoverNeverNames.Contains(name)
                         || name.StartsWith("regid.", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith('.')
                         || NameMatcher.ContainsGuid(name))
                {
                    continue;
                }
                else if (NameMatcher.LooksMachineGenerated(name))
                {
                    unidentifiable++;
                    AppLog.Debug($"Leftover candidate with a machine-generated name not offered: {folder.FullName}");
                    continue;
                }

                string matchEvidence;
                if (isPackages)
                {
                    if (inventory.IsPackageInstalled(name))
                    {
                        continue;
                    }

                    matchEvidence = $"No installed Store app has the package family name \"{name}\" ({inventory.PackageCount} installed packages checked).";
                }
                else
                {
                    if (inventory.MatchesInstalledName(name, out _))
                    {
                        continue;
                    }

                    matchEvidence = $"No installed program matches \"{name}\" ({inventory.SignalSummary}).";
                }

                if (inventory.IsReferenced(folder.FullName, out _))
                {
                    continue;
                }

                SafetyVerdict verdict = context.Guard.EvaluateCandidate(folder.FullName, DeletionIntent.Leftover, null, folder.Attributes);
                if (!verdict.IsAllowed)
                {
                    protectedSkipped++;
                    context.CountProtected();
                    continue;
                }

                FolderStats stats = Measure(folder, context.CancellationToken);
                if (stats.Unreadable)
                {
                    continue;
                }

                if (stats.HasSensitiveContent)
                {
                    sensitiveSkipped++;
                    AppLog.Info($"Leftover candidate not offered (contains protected data): {folder.FullName} - {stats.SensitiveDetail}");
                    continue;
                }

                if (context.NowUtc - stats.NewestUtc < minIdle)
                {
                    continue;
                }

                if (!context.TryClaim(folder.FullName, stats.Bytes))
                {
                    continue;
                }

                string idle = Formatting.Age(stats.NewestUtc, context.NowUtc);
                var evidence = new List<string>
                {
                    matchEvidence,
                    "No service, startup entry, scheduled task, App Path, PATH entry or running process uses anything inside it.",
                    $"Nothing inside has changed for {idle} (newest change {stats.NewestUtc.ToLocalTime():yyyy-MM-dd}).",
                    $"{stats.Files} file(s), {Formatting.Bytes(stats.Bytes)}.",
                    "Checked for wallets, keys, password databases and mail stores: none found.",
                    "It will be quarantined, not deleted, so it can be restored.",
                };

                RiskLevel risk = RiskLevel.Moderate;
                if (stats.LooksLikeUserData)
                {
                    evidence.Add("Contains files that look like saved data or settings (saves, profiles, databases) - review carefully.");
                    risk = RiskLevel.High;
                }

                if (label == "ProgramData")
                {
                    evidence.Add("ProgramData is shared by all users and can hold licence or activation data used by other programs.");
                    risk = RiskLevel.High;
                }
                else if (label == "AppData\\LocalLow")
                {
                    evidence.Add("Many games keep their save files in LocalLow. Keep it if you might reinstall the game.");
                    risk = RiskLevel.High;
                }

                var item = new ScanItem
                {
                    Path = folder.FullName,
                    Kind = ItemKind.Directory,
                    Category = Categories.Leftovers,
                    SizeBytes = stats.Bytes,
                    FileCount = stats.Files,
                    LastWriteUtc = stats.NewestUtc,
                    CreationUtc = folder.CreationTimeUtc,
                    Reason = isPackages
                        ? $"Data of a Store app that is no longer installed; unused for {idle}."
                        : $"No installed program claims this folder; unused for {idle}.",
                    Evidence = evidence,
                    RiskOverride = risk,
                };

                result.Add(item);
            }
        }

        if (sensitiveSkipped > 0)
        {
            result.AddNote($"{sensitiveSkipped} unclaimed folder(s) were not offered because they contain protected data (wallets, keys, certificates, password databases, mail, source-code projects) or links.");
        }

        if (unidentifiable > 0)
        {
            result.AddNote($"{unidentifiable} unclaimed folder(s) have machine-generated names, so the program that made them cannot be identified; they are not offered.");
        }

        if (protectedSkipped > 0)
        {
            result.AddNote($"{protectedSkipped} unclaimed folder(s) sit in protected locations and were not offered.");
        }

        return result;
    }

    private static FolderStats Measure(DirectoryInfo folder, CancellationToken cancellationToken)
    {
        var stats = new FolderStats { NewestUtc = ScanContext.NewestTimestamp(folder) };
        int entries = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryInfo current = pending.Pop();
            List<FileSystemInfo> children;
            try
            {
                children = current.EnumerateFileSystemInfos("*", SafeEnumeration.Strict).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stats.Unreadable = true;
                return stats;
            }

            foreach (FileSystemInfo child in children)
            {
                if (++entries > MaxEntriesPerFolder)
                {
                    stats.Unreadable = true;
                    return stats;
                }

                if (FileSystemInspector.IsReparsePoint(child.Attributes) || FileSystemInspector.IsCloudFile(child.Attributes))
                {
                    stats.HasSensitiveContent = true;
                    stats.SensitiveDetail = "contains a link or cloud file: " + child.FullName;
                    return stats;
                }

                if (ProtectionPolicy.ProtectedSegmentNames.Contains(child.Name) || IsProtectedName(child.Name))
                {
                    stats.HasSensitiveContent = true;
                    stats.SensitiveDetail = child.FullName;
                    return stats;
                }

                if (ProtectionPolicy.ProjectMarkers.Any(marker => PathUtil.WildcardMatch(child.Name, marker)))
                {
                    stats.HasSensitiveContent = true;
                    stats.SensitiveDetail = "contains a source-code project: " + child.FullName;
                    return stats;
                }

                DateTime newest = ScanContext.NewestTimestamp(child);
                if (newest > stats.NewestUtc)
                {
                    stats.NewestUtc = newest;
                }

                if (child is DirectoryInfo dir)
                {
                    if (dir.Name.Contains("save", StringComparison.OrdinalIgnoreCase) || dir.Name.Equals("Profiles", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.LooksLikeUserData = true;
                    }

                    pending.Push(dir);
                }
                else if (child is FileInfo file)
                {
                    stats.Files++;
                    stats.Bytes += file.Length;
                    string ext = file.Extension;
                    if (ext.Equals(".sav", StringComparison.OrdinalIgnoreCase) || ext.Equals(".save", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".db", StringComparison.OrdinalIgnoreCase) || ext.Equals(".sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.LooksLikeUserData = true;
                    }
                }
            }
        }

        return stats;
    }

    private static bool IsProtectedName(string name)
    {
        if (ProtectionPolicy.CriticalFileNames.Contains(name))
        {
            return true;
        }

        foreach (string pattern in ProtectionPolicy.CriticalNamePatterns)
        {
            if (PathUtil.WildcardMatch(name, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FolderStats
    {
        public long Bytes;
        public int Files;
        public DateTime NewestUtc;
        public bool Unreadable;
        public bool HasSensitiveContent;
        public string? SensitiveDetail;
        public bool LooksLikeUserData;
    }
}
