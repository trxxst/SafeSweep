using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;
using SafeSweep.Core.Platform;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Safety;

namespace SafeSweep.Core.Scanning;

/// <summary>Applies the declarative <see cref="CleanupRule"/>s from the catalog.</summary>
public sealed class RuleScanner
{
    public void Scan(ScanContext context, IEnumerable<CleanupRule> rules, IDictionary<string, CategoryResult> results)
    {
        foreach (CleanupRule rule in rules)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            CategoryDefinition category = rule.Category;
            if (!results.TryGetValue(category.Id, out CategoryResult? result))
            {
                result = new CategoryResult(category);
                results[category.Id] = result;
            }

            context.SetStage($"Scanning {category.Name}");
            ScanRule(context, rule, result);
        }
    }

    private static void ScanRule(ScanContext context, CleanupRule rule, CategoryResult result)
    {
        CategoryDefinition category = rule.Category;

        if (category.RequiresAdmin && !context.IsElevated)
        {
            result.AddNote("Requires administrator rights. Use \"Restart as administrator\" to scan and clean this category.");
            return;
        }

        string? precondition = rule.Precondition?.Invoke();
        if (precondition is not null)
        {
            result.AddNote(precondition);
            return;
        }

        string? lockReason = null;
        if (rule.BlockingProcesses.Count > 0 && context.IsAnyRunning(rule.BlockingProcesses))
        {
            lockReason = $"{rule.BlockingProgramName ?? "The program using this data"} is running. Close it completely (including from the system tray) and scan again to clean this.";
            result.AddNote(lockReason);
        }

        TimeSpan minAge = rule.UsesTempAgeSetting ? TimeSpan.FromHours(context.Settings.TempMinAgeHours) : rule.MinAge;
        DateTime cutoff = context.NowUtc - minAge;

        foreach (string rawRoot in ExpandRoots(rule, context.Paths))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (rule.ExactFile ? !File.Exists(rawRoot) : !Directory.Exists(rawRoot))
            {
                continue;
            }

            if (rule.RequiredRootLeafNames.Count > 0)
            {
                string leaf = Path.GetFileName(rawRoot.TrimEnd('\\'));
                if (!rule.RequiredRootLeafNames.Contains(leaf, StringComparer.OrdinalIgnoreCase))
                {
                    string note = $"Skipped {rawRoot}: the folder is not named {string.Join(" or ", rule.RequiredRootLeafNames)}, so it is not treated as a temporary folder.";
                    result.AddNote(note);
                    AppLog.Warn(note);
                    continue;
                }
            }

            if (!context.Guard.TryRegisterRuleRoot(rawRoot, rule.ExactFile, out string root, out string? rejection))
            {
                string note = $"Skipped {rawRoot}: {rejection}";
                result.AddNote(note);
                AppLog.Warn($"[{category.Id}] root refused by protection layer - {note}");
                continue;
            }

            if (rule.ExactFile)
            {
                AddFile(context, rule, result, root, new FileInfo(root), cutoff, minAge, lockReason);
                continue;
            }

            IEnumerable<FileInfo> files = SafeEnumeration.EnumerateFiles(
                root,
                rule.Recursive,
                dir =>
                {
                    if (context.Guard.ShouldSkipDirectory(dir, out string? why))
                    {
                        AppLog.Debug($"[{category.Id}] not entering {dir.FullName}: {why}");
                        return false;
                    }

                    return rule.SkipDirectory is null || !rule.SkipDirectory(dir);
                },
                (path, ex) => AppLog.Debug($"[{category.Id}] cannot read {path}: {ex.Message}"),
                context.CancellationToken);

            foreach (FileInfo file in files)
            {
                context.ReportPath(file.FullName);
                if (!Matches(file.Name, rule))
                {
                    continue;
                }

                AddFile(context, rule, result, root, file, cutoff, minAge, lockReason);
            }
        }
    }

    private static void AddFile(ScanContext context, CleanupRule rule, CategoryResult result, string root, FileInfo file, DateTime cutoff, TimeSpan minAge, string? lockReason)
    {
        FileAttributes attributes;
        DateTime newest;
        long length;
        try
        {
            attributes = file.Attributes;
            length = file.Length;

            // Installers often extract files that keep an old modification date; the
            // creation date shows they arrived a moment ago. Use whichever is newer.
            newest = ScanContext.NewestTimestamp(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (newest > cutoff)
        {
            return;
        }

        SafetyVerdict verdict = context.Guard.EvaluateCandidate(file.FullName, DeletionIntent.RuleCleanup, root, attributes);
        if (!verdict.IsAllowed)
        {
            context.CountProtected();
            AppLog.Debug($"[{rule.Category.Id}] protected, not offered: {file.FullName} ({verdict.Reason})");
            return;
        }

        if (!context.TryClaim(file.FullName, length))
        {
            return;
        }

        DateTime? lastAccess = null;
        try
        {
            lastAccess = file.LastAccessTimeUtc;
        }
        catch (IOException)
        {
        }

        string age = Formatting.Age(newest, context.NowUtc);
        var evidence = new List<string>
        {
            $"Location: {rule.LocationLabel} ({root})",
            $"Last changed {age} ago ({newest.ToLocalTime():yyyy-MM-dd HH:mm})",
        };

        if (minAge > TimeSpan.Zero)
        {
            evidence.Add($"Older than the safety window of {FormatWindow(minAge)}, so no running program is still expected to use it");
        }

        if (rule.Include.Count > 0 && rule.Include[0] != "*")
        {
            evidence.Add($"Matches the known pattern {string.Join(", ", rule.Include)}");
        }

        evidence.Add(rule.Category.WhyRemovable);

        var item = new ScanItem
        {
            Path = file.FullName,
            Kind = ItemKind.File,
            Category = rule.Category,
            SizeBytes = length,
            LastWriteUtc = file.LastWriteTimeUtc,
            LastAccessUtc = lastAccess,
            CreationUtc = file.CreationTimeUtc,
            Reason = $"In {rule.LocationLabel}; unchanged for {age}.",
            Evidence = evidence,
            ScopeRoot = root,
            PruneAgeThreshold = minAge >= TimeSpan.FromDays(1) ? minAge : null,
            LockReason = lockReason,
            BlockingProcesses = rule.BlockingProcesses,
            BlockingProgramName = rule.BlockingProgramName,
        };

        item.IsSelected = rule.Category.PreSelect && item.IsSelectable;
        result.Add(item);
    }

    private static bool Matches(string name, CleanupRule rule)
    {
        bool included = rule.Include.Any(p => PathUtil.WildcardMatch(name, p));
        return included && !rule.Exclude.Any(p => PathUtil.WildcardMatch(name, p));
    }

    private static string FormatWindow(TimeSpan span)
        => span.TotalDays >= 1 ? $"{span.TotalDays:0} day(s)" : $"{span.TotalHours:0} hour(s)";

    /// <summary>Expands tokens and "*" segments of every root of a rule.</summary>
    internal static IEnumerable<string> ExpandRoots(CleanupRule rule, SystemPaths paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pattern in rule.Roots)
        {
            foreach (string root in ExpandPattern(pattern, paths, rule.ExcludedWildcardNames))
            {
                string? normalized = PathUtil.TryNormalize(root, out _);
                if (normalized is not null && seen.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }
    }

    internal static IEnumerable<string> ExpandPattern(string pattern, SystemPaths paths, IReadOnlyList<string> excludedWildcardNames)
    {
        string? expanded = ReplaceTokens(pattern, paths);
        if (expanded is null)
        {
            yield break;
        }

        string[] parts = expanded.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length != 2 || parts[0][1] != ':')
        {
            yield break;
        }

        var current = new List<string> { parts[0] + "\\" };
        bool firstWildcard = true;
        for (int i = 1; i < parts.Length; i++)
        {
            string segment = parts[i];
            var next = new List<string>();
            foreach (string c in current)
            {
                if (segment == "*")
                {
                    foreach (DirectoryInfo d in SafeEnumeration.EnumerateDirectories(c))
                    {
                        if (firstWildcard && excludedWildcardNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        next.Add(d.FullName);
                    }
                }
                else
                {
                    next.Add(Path.Combine(c, segment));
                }
            }

            if (segment == "*")
            {
                firstWildcard = false;
            }

            current = next;
        }

        foreach (string c in current)
        {
            yield return c;
        }
    }

    private static string? ReplaceTokens(string pattern, SystemPaths paths)
    {
        var tokens = new (string Token, string? Value)[]
        {
            ("{LocalAppData}", paths.LocalAppData),
            ("{RoamingAppData}", paths.RoamingAppData),
            ("{LocalLow}", paths.LocalAppDataLow),
            ("{Windows}", paths.WindowsDir),
            ("{ProgramData}", paths.ProgramData),
            ("{Temp}", paths.UserTemp),
            ("{SystemDrive}", paths.SystemDrive.TrimEnd('\\')),
            ("{UserProfile}", paths.UserProfile),
        };

        string result = pattern;
        foreach ((string token, string? value) in tokens)
        {
            if (!result.Contains(token, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            result = result.Replace(token, value.TrimEnd('\\'), StringComparison.Ordinal);
        }

        return result;
    }
}
