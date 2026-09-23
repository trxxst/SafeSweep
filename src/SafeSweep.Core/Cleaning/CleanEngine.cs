using System.Diagnostics;
using SafeSweep.Core.Apps;
using SafeSweep.Core.History;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;
using SafeSweep.Core.Platform;
using SafeSweep.Core.Safety;
using SafeSweep.Core.Scanning;

namespace SafeSweep.Core.Cleaning;

public enum CleanOutcome
{
    Deleted,
    Quarantined,
    Recycled,
    Simulated,
    Skipped,
    Blocked,
    Failed,
}

public sealed record CleanItemResult(ScanItem Item, CleanOutcome Outcome, string Detail, DeletionMethod? MethodUsed);

public sealed record CleanProgress(int Processed, int Total, long Bytes, string? CurrentPath);

public sealed class CleanOptions
{
    public bool Simulation { get; init; }

    public bool UseRecycleBinForReview { get; init; }

    /// <summary>"Manual" or "Scheduled".</summary>
    public string Trigger { get; init; } = "Manual";
}

public sealed class CleanReport
{
    public required Guid Id { get; init; }

    public required DateTime StartedUtc { get; init; }

    public DateTime FinishedUtc { get; set; }

    public required bool Simulation { get; init; }

    public bool Cancelled { get; set; }

    public List<CleanItemResult> Results { get; } = [];

    public Guid QuarantineSessionId { get; init; }

    public int Count(CleanOutcome outcome) => Results.Count(r => r.Outcome == outcome);

    public long Bytes(CleanOutcome outcome) => Results.Where(r => r.Outcome == outcome).Sum(r => r.Item.SizeBytes);

    public long BytesFreed => Bytes(CleanOutcome.Deleted);

    public long BytesQuarantined => Bytes(CleanOutcome.Quarantined);

    public long BytesRecycled => Bytes(CleanOutcome.Recycled);

    public long BytesSimulated => Bytes(CleanOutcome.Simulated);

    public IEnumerable<CleanItemResult> Problems => Results.Where(r => r.Outcome is CleanOutcome.Skipped or CleanOutcome.Blocked or CleanOutcome.Failed);

    public HistoryEntry ToHistory(string trigger) => new()
    {
        Id = Id,
        StartedUtc = StartedUtc,
        FinishedUtc = FinishedUtc,
        Trigger = trigger,
        Simulation = Simulation,
        Cancelled = Cancelled,
        ItemsRequested = Results.Count,
        ItemsDeleted = Count(CleanOutcome.Deleted),
        ItemsQuarantined = Count(CleanOutcome.Quarantined),
        ItemsRecycled = Count(CleanOutcome.Recycled),
        ItemsSkipped = Count(CleanOutcome.Skipped) + Count(CleanOutcome.Failed),
        ItemsBlocked = Count(CleanOutcome.Blocked),
        ItemsSimulated = Count(CleanOutcome.Simulated),
        BytesFreed = BytesFreed,
        BytesQuarantined = BytesQuarantined,
        BytesRecycled = BytesRecycled,
        BytesSimulated = BytesSimulated,
        QuarantineSessionId = Count(CleanOutcome.Quarantined) > 0 ? QuarantineSessionId : null,
        Categories = Results
            .Where(r => r.Outcome is CleanOutcome.Deleted or CleanOutcome.Quarantined or CleanOutcome.Recycled or CleanOutcome.Simulated)
            .GroupBy(r => r.Item.Category.Id)
            .Select(g => new CategoryCleanSummary(g.Key, g.First().Item.Category.Name, g.Count(), g.Sum(r => r.Item.SizeBytes)))
            .ToList(),
        Problems = Problems.Take(200).Select(p => new CleanFailure(p.Item.Path, $"{p.Outcome}: {p.Detail}")).ToList(),
    };
}

/// <summary>
/// Executes a clean. Every item is re-verified immediately before it is
/// touched: the protection check runs again (including link resolution and
/// hard-link detection), the item must be unchanged since the scan, and the
/// removal method is forced to a restorable one unless the item is Safe-level
/// regenerable data.
/// </summary>
public sealed class CleanEngine
{
    private const string MutexName = @"Local\SafeSweep.CleanLock";
    private readonly SafeSweepServices _services;

    public CleanEngine(SafeSweepServices services)
    {
        _services = services;
    }

    public Task<CleanReport> CleanAsync(IReadOnlyList<ScanItem> items, CleanOptions options, IProgress<CleanProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Clean(items, options, progress, cancellationToken), CancellationToken.None);

    public CleanReport Clean(IReadOnlyList<ScanItem> items, CleanOptions options, IProgress<CleanProgress>? progress, CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            throw new InvalidOperationException("Another SafeSweep clean is already running (possibly the scheduled clean). Try again when it has finished.");
        }

        try
        {
            return CleanCore(items, options, progress, cancellationToken);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private CleanReport CleanCore(IReadOnlyList<ScanItem> items, CleanOptions options, IProgress<CleanProgress>? progress, CancellationToken cancellationToken)
    {
        var report = new CleanReport
        {
            Id = Guid.NewGuid(),
            StartedUtc = DateTime.UtcNow,
            Simulation = options.Simulation,
            QuarantineSessionId = Guid.NewGuid(),
        };

        AppLog.Info($"Clean started ({options.Trigger}{(options.Simulation ? ", SIMULATION - nothing will be changed" : string.Empty)}): {items.Count} item(s).");

        // Protected install folders must be current before any deletion.
        InstalledAppInventory inventory = _services.GetInventory(cancellationToken: CancellationToken.None);

        var duplicateGuard = new DuplicateGuard(items);
        IReadOnlySet<string> running = ProcessSnapshot.RunningNames();
        var pruneRoots = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        var throttle = Stopwatch.StartNew();

        // Folders go last and deepest first so empty-folder trees are removed bottom-up.
        List<ScanItem> ordered = items
            .OrderBy(i => i.Kind == ItemKind.Directory ? 1 : 0)
            .ThenByDescending(i => i.Path.Length)
            .ToList();

        for (int index = 0; index < ordered.Count; index++)
        {
            ScanItem item = ordered[index];
            if (cancellationToken.IsCancellationRequested)
            {
                report.Cancelled = true;
                AppLog.Info("Clean cancelled by the user; remaining items were not touched.");
                break;
            }

            if (throttle.ElapsedMilliseconds > 60 || index == ordered.Count - 1)
            {
                throttle.Restart();
                progress?.Report(new CleanProgress(index, ordered.Count, bytes, item.Path));
            }

            CleanItemResult result = Process(item, options, report.QuarantineSessionId, inventory, duplicateGuard, running);
            report.Results.Add(result);

            if (result.Outcome is CleanOutcome.Deleted or CleanOutcome.Quarantined or CleanOutcome.Recycled or CleanOutcome.Simulated)
            {
                bytes += item.SizeBytes;
                if (!options.Simulation && item.ScopeRoot is not null && item.PruneAgeThreshold is TimeSpan age)
                {
                    pruneRoots[item.ScopeRoot] = age;
                }
            }
        }

        foreach ((string root, TimeSpan age) in pruneRoots)
        {
            PruneEmptyDirectories(root, age);
        }

        progress?.Report(new CleanProgress(ordered.Count, ordered.Count, bytes, null));
        report.FinishedUtc = DateTime.UtcNow;

        _services.History.Append(report.ToHistory(options.Trigger));
        AppLog.Info(
            $"Clean finished: deleted {report.Count(CleanOutcome.Deleted)} ({Formatting.Bytes(report.BytesFreed)}), " +
            $"quarantined {report.Count(CleanOutcome.Quarantined)} ({Formatting.Bytes(report.BytesQuarantined)}), " +
            $"recycled {report.Count(CleanOutcome.Recycled)}, simulated {report.Count(CleanOutcome.Simulated)}, " +
            $"skipped {report.Count(CleanOutcome.Skipped)}, blocked {report.Count(CleanOutcome.Blocked)}, failed {report.Count(CleanOutcome.Failed)}.");
        AppLog.Flush();
        return report;
    }

    private CleanItemResult Process(ScanItem item, CleanOptions options, Guid sessionId, InstalledAppInventory inventory, DuplicateGuard duplicates, IReadOnlySet<string> running)
    {
        if (!item.IsSelectable)
        {
            return new CleanItemResult(item, CleanOutcome.Skipped, item.LockReason ?? "Locked.", null);
        }

        if (item.Kind == ItemKind.RecycleBin)
        {
            return EmptyRecycleBin(item, options);
        }

        // The program owning this data may have started since the scan.
        string? runningNow = item.BlockingProcesses.FirstOrDefault(p => running.Contains(p));
        if (runningNow is not null)
        {
            string detail = $"{item.BlockingProgramName ?? runningNow} is running now; its data is left alone until it is closed.";
            AppLog.Audit("SKIPPED", item.Path, detail);
            return new CleanItemResult(item, CleanOutcome.Skipped, detail, null);
        }

        if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            AppLog.Audit("SKIPPED", item.Path, "no longer exists");
            return new CleanItemResult(item, CleanOutcome.Skipped, "No longer exists (removed by something else since the scan).", null);
        }

        DeletionMethod method = ResolveMethod(item, options);

        SafetyVerdict verdict = _services.Guard.EvaluateForDeletion(item.Path, item.Intent, item.ScopeRoot, item.Kind);
        if (!verdict.IsAllowed)
        {
            AppLog.Audit("BLOCKED", item.Path, verdict.Reason);
            return new CleanItemResult(item, CleanOutcome.Blocked, verdict.Reason, null);
        }

        string? changed = CheckUnchanged(item, inventory);
        if (changed is not null)
        {
            AppLog.Audit("SKIPPED", item.Path, changed);
            return new CleanItemResult(item, CleanOutcome.Skipped, changed, null);
        }

        if (item.DuplicateGroupId is not null)
        {
            string? duplicateProblem = duplicates.Verify(item);
            if (duplicateProblem is not null)
            {
                AppLog.Audit("BLOCKED", item.Path, duplicateProblem);
                return new CleanItemResult(item, CleanOutcome.Blocked, duplicateProblem, null);
            }
        }

        if (options.Simulation)
        {
            AppLog.Audit("SIMULATED", item.Path, $"would be {Describe(method)}");
            return new CleanItemResult(item, CleanOutcome.Simulated, $"Would be {Describe(method)}.", method);
        }

        try
        {
            switch (method)
            {
                case DeletionMethod.Permanent when item.Kind == ItemKind.Directory:
                    // Only empty folders are ever removed permanently, and Directory.Delete
                    // without recursion refuses a folder that is not empty.
                    Directory.Delete(item.Path, recursive: false);
                    AppLog.Audit("DELETED", item.Path, "empty folder");
                    return new CleanItemResult(item, CleanOutcome.Deleted, "Empty folder removed.", method);

                case DeletionMethod.Permanent:
                    FileAttributes attributes = File.GetAttributes(item.Path);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(item.Path, attributes & ~FileAttributes.ReadOnly);
                    }

                    File.Delete(item.Path);
                    AppLog.Audit("DELETED", item.Path, $"{item.Category.Id}, {Formatting.Bytes(item.SizeBytes)}");
                    return new CleanItemResult(item, CleanOutcome.Deleted, "Deleted.", method);

                case DeletionMethod.RecycleBin:
                    if (RecycleBinService.Send(item.Path, out string? recycleError))
                    {
                        AppLog.Audit("RECYCLED", item.Path, item.Category.Id);
                        return new CleanItemResult(item, CleanOutcome.Recycled, "Moved to the Recycle Bin.", method);
                    }

                    AppLog.Warn($"Recycle Bin refused {item.Path} ({recycleError}); using quarantine instead.");
                    goto case DeletionMethod.Quarantine;

                case DeletionMethod.Quarantine:
                    QuarantineEntry entry = _services.Quarantine.Add(sessionId, item);
                    return new CleanItemResult(item, CleanOutcome.Quarantined, "Moved to quarantine (restorable).", DeletionMethod.Quarantine);

                default:
                    return new CleanItemResult(item, CleanOutcome.Failed, "Unknown method.", null);
            }
        }
        catch (IOException ex) when (IsInUse(ex))
        {
            AppLog.Audit("SKIPPED", item.Path, "in use");
            return new CleanItemResult(item, CleanOutcome.Skipped, "In use by a running program; left in place.", null);
        }
        catch (UnauthorizedAccessException)
        {
            AppLog.Audit("SKIPPED", item.Path, "access denied");
            return new CleanItemResult(item, CleanOutcome.Skipped, "Access denied (it may need administrator rights, or it is in use).", null);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            AppLog.Error($"Could not remove {item.Path}", ex);
            return new CleanItemResult(item, CleanOutcome.Failed, ex.Message, null);
        }
    }

    /// <summary>
    /// Picks the removal method. Permanent deletion is only allowed for Safe
    /// rule items and for empty folders; everything else is forced into the
    /// quarantine (or the Recycle Bin, when chosen and guaranteed to keep it).
    /// </summary>
    public static DeletionMethod ResolveMethod(ScanItem item, CleanOptions options)
    {
        if (item.Intent == DeletionIntent.EmptyFolder)
        {
            return DeletionMethod.Permanent;
        }

        bool permanentAllowed = item.Intent == DeletionIntent.RuleCleanup && item.Level == CleanLevel.Safe;
        DeletionMethod method = item.Method;
        if (method == DeletionMethod.Permanent && !permanentAllowed)
        {
            method = DeletionMethod.Quarantine;
        }

        if (item.Intent == DeletionIntent.UserReview && options.UseRecycleBinForReview && item.Kind == ItemKind.File)
        {
            method = DeletionMethod.RecycleBin;
        }

        if (item.Intent == DeletionIntent.Leftover)
        {
            method = DeletionMethod.Quarantine;
        }

        if (method == DeletionMethod.RecycleBin && !RecycleBinService.CanRecycle(item.Path, item.SizeBytes, out string reason))
        {
            AppLog.Info($"Recycle Bin not used for {item.Path}: {reason} Quarantine is used instead.");
            method = DeletionMethod.Quarantine;
        }

        return method;
    }

    private string? CheckUnchanged(ScanItem item, InstalledAppInventory inventory)
    {
        try
        {
            switch (item.Kind)
            {
                case ItemKind.File:
                    {
                        var info = new FileInfo(item.Path);
                        if (!info.Exists)
                        {
                            return "The file no longer exists.";
                        }

                        if (info.Length != item.SizeBytes || (item.LastWriteUtc is DateTime written && info.LastWriteTimeUtc != written))
                        {
                            return "The file changed since the scan; scan again to include it.";
                        }

                        return null;
                    }

                case ItemKind.Directory when item.Intent == DeletionIntent.EmptyFolder:
                    {
                        if (Directory.EnumerateFileSystemEntries(item.Path).Any())
                        {
                            return "The folder is no longer empty.";
                        }

                        return null;
                    }

                case ItemKind.Directory when item.Intent == DeletionIntent.Leftover:
                    {
                        string name = Path.GetFileName(item.Path);
                        bool isPackage = PathUtil.IsStrictlyUnder(item.Path, Path.Combine(_services.Paths.LocalAppData, "Packages"));
                        if (isPackage ? inventory.IsPackageInstalled(name) : inventory.MatchesInstalledName(name, out _))
                        {
                            return "A program matching this folder is now installed.";
                        }

                        if (inventory.IsReferenced(item.Path, out string? reference))
                        {
                            return $"Something now uses this folder ({reference}).";
                        }

                        SafetyVerdict contents = _services.Guard.EvaluateDirectoryContents(item.Path, item.Intent);
                        if (!contents.IsAllowed)
                        {
                            return contents.Reason;
                        }

                        DateTime newest = NewestWrite(item.Path);
                        if (item.LastWriteUtc is DateTime scanned && newest > scanned.AddSeconds(2))
                        {
                            return "Something inside the folder changed since the scan.";
                        }

                        return null;
                    }

                default:
                    return "Unsupported item type.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Could not be re-checked: " + ex.Message;
        }
    }

    private static DateTime NewestWrite(string directory)
    {
        DateTime newest = ScanContext.NewestTimestamp(new DirectoryInfo(directory));
        foreach (FileInfo file in SafeEnumeration.EnumerateFiles(directory, true, null, null, CancellationToken.None))
        {
            DateTime t = ScanContext.NewestTimestamp(file);
            if (t > newest)
            {
                newest = t;
            }
        }

        return newest;
    }

    private static CleanItemResult EmptyRecycleBin(ScanItem item, CleanOptions options)
    {
        if (options.Simulation)
        {
            AppLog.Audit("SIMULATED", "Recycle Bin", "would be emptied");
            return new CleanItemResult(item, CleanOutcome.Simulated, "The Recycle Bin would be emptied.", DeletionMethod.Permanent);
        }

        if (RecycleBinService.Empty(out string? error))
        {
            AppLog.Audit("EMPTIED", "Recycle Bin", Formatting.Bytes(item.SizeBytes));
            return new CleanItemResult(item, CleanOutcome.Deleted, "Recycle Bin emptied.", DeletionMethod.Permanent);
        }

        AppLog.Error("Recycle Bin could not be emptied: " + error);
        return new CleanItemResult(item, CleanOutcome.Failed, error ?? "The Recycle Bin could not be emptied.", null);
    }

    /// <summary>
    /// Removes sub-folders of a rule root that are now empty and were created
    /// long ago. The root itself is never removed.
    /// </summary>
    private void PruneEmptyDirectories(string root, TimeSpan minAge)
    {
        DateTime cutoff = DateTime.UtcNow - minAge;
        var directories = new List<DirectoryInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            DirectoryInfo current = pending.Pop();
            foreach (DirectoryInfo sub in SafeEnumeration.EnumerateDirectories(current.FullName))
            {
                directories.Add(sub);
                pending.Push(sub);
            }
        }

        int removed = 0;
        foreach (DirectoryInfo dir in directories.OrderByDescending(d => d.FullName.Length))
        {
            try
            {
                dir.Refresh();
                if (!dir.Exists || dir.CreationTimeUtc > cutoff || FileSystemInspector.IsReparsePoint(dir.Attributes)
                    || (dir.Attributes & FileAttributes.System) != 0 || dir.EnumerateFileSystemInfos().Any())
                {
                    continue;
                }

                string? n = PathUtil.TryNormalize(dir.FullName, out _);
                if (n is null || !PathUtil.IsStrictlyUnder(n, root))
                {
                    continue;
                }

                if (!_services.Guard.EvaluateStatic(n, DeletionIntent.RuleCleanup, root, checkScope: true).IsAllowed)
                {
                    continue;
                }

                dir.Delete(recursive: false);
                removed++;
                AppLog.Audit("DELETED", dir.FullName, "empty folder left after cleaning");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or not empty: left in place.
            }
        }

        if (removed > 0)
        {
            AppLog.Info($"Removed {removed} empty folder(s) left in {root}.");
        }
    }

    private static bool IsInUse(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }

    private static string Describe(DeletionMethod method) => method switch
    {
        DeletionMethod.Permanent => "deleted",
        DeletionMethod.Quarantine => "moved to quarantine",
        DeletionMethod.RecycleBin => "sent to the Recycle Bin",
        _ => "removed",
    };

    /// <summary>
    /// Guarantees at least one copy of every duplicate group survives, and
    /// re-verifies content right before a copy is removed.
    /// </summary>
    private sealed class DuplicateGuard
    {
        private readonly Dictionary<int, List<ScanItem>> _keepers = [];
        private readonly Dictionary<string, string> _hashCache = new(StringComparer.OrdinalIgnoreCase);

        public DuplicateGuard(IReadOnlyList<ScanItem> selected)
        {
            var selectedSet = new HashSet<ScanItem>(selected);
            foreach (IGrouping<int, ScanItem> group in selected.Where(i => i.DuplicateGroupId is not null).GroupBy(i => i.DuplicateGroupId!.Value))
            {
                ScanItem sample = group.First();
                IEnumerable<ScanItem> members = sample.Owner?.Items.Where(i => i.DuplicateGroupId == group.Key) ?? group;
                _keepers[group.Key] = members.Where(m => !selectedSet.Contains(m)).ToList();
            }
        }

        public string? Verify(ScanItem item)
        {
            if (!_keepers.TryGetValue(item.DuplicateGroupId!.Value, out List<ScanItem>? keepers) || keepers.Count == 0)
            {
                return "Every copy in this duplicate group was selected. At least one copy is always kept; deselect the one you want to keep.";
            }

            string? current = HashOf(item.Path);
            if (current is null || !current.Equals(item.ContentHash, StringComparison.Ordinal))
            {
                return "The file's content changed since the scan; it is no longer a verified duplicate.";
            }

            foreach (ScanItem keeper in keepers)
            {
                string? keeperHash = HashOf(keeper.Path);
                if (keeperHash is not null && keeperHash.Equals(item.ContentHash, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            return "No kept copy with identical content still exists, so this copy is kept.";
        }

        private string? HashOf(string path)
        {
            if (_hashCache.TryGetValue(path, out string? cached))
            {
                return cached;
            }

            try
            {
                string hash = FileHasher.ComputeSha256(path, CancellationToken.None);
                _hashCache[path] = hash;
                return hash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
