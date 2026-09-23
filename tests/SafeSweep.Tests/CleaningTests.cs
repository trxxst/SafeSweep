using SafeSweep.Core;
using SafeSweep.Core.Cleaning;
using SafeSweep.Core.Models;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Scanning;

namespace SafeSweep.Tests;

/// <summary>End-to-end scan + clean, entirely inside a sandbox.</summary>
public class CleaningTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly SafeSweepServices _services;

    public CleaningTests()
    {
        _services = SafeSweepServices.CreateFor(_box.Paths);
    }

    public void Dispose() => _box.Dispose();

    private ScanContext Context() => new(
        _services.Guard,
        _services.Settings,
        isElevated: false,
        () => _services.GetInventory(),
        null,
        CancellationToken.None);

    private CategoryResult ScanUserTemp()
    {
        CleanupRule rule = RuleCatalog.All.Single(r => r.Category.Id == "win-user-temp");
        var results = new Dictionary<string, CategoryResult>();
        new RuleScanner().Scan(Context(), [rule], results);
        return results["win-user-temp"];
    }

    [Fact]
    public void Temp_scan_respects_the_safety_window_and_never_follows_links()
    {
        string old = _box.File(Path.Combine(_box.Temp, "old.tmp"), ageDays: 10);
        string fresh = _box.File(Path.Combine(_box.Temp, "fresh.tmp"));
        string nestedOld = _box.File(Path.Combine(_box.Temp, "sub", "nested.log"), ageDays: 5);

        // An installer that extracted an old-dated file a moment ago: old write time, new creation time.
        string extracted = _box.File(Path.Combine(_box.Temp, "extracted.dll"));
        File.SetLastWriteTimeUtc(extracted, DateTime.UtcNow.AddYears(-3));

        string precious = _box.File(Path.Combine(_box.Documents, "precious.docx"), ageDays: 400);
        Sandbox.Junction(Path.Combine(_box.Temp, "docs-link"), _box.Documents);

        CategoryResult result = ScanUserTemp();
        var paths = result.Items.Select(i => i.Path).ToList();

        Assert.Contains(old, paths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(nestedOld, paths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(fresh, paths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(extracted, paths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(paths, p => p.Contains("precious", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Items, i => Assert.True(i.IsSelected, "Safe items are pre-selected"));

        CleanReport report = new CleanEngine(_services).Clean(result.Items.ToList(), new CleanOptions(), null, CancellationToken.None);

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(nestedOld));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(extracted));
        Assert.True(File.Exists(precious));
        Assert.Equal(2, report.Count(CleanOutcome.Deleted));
    }

    [Fact]
    public void Simulation_changes_nothing()
    {
        string old = _box.File(Path.Combine(_box.Temp, "old.tmp"), ageDays: 10);
        CategoryResult result = ScanUserTemp();

        CleanReport report = new CleanEngine(_services).Clean(result.Items.ToList(), new CleanOptions { Simulation = true }, null, CancellationToken.None);

        Assert.True(File.Exists(old));
        Assert.Equal(1, report.Count(CleanOutcome.Simulated));
    }

    [Fact]
    public void A_preview_is_never_recorded_as_space_freed()
    {
        _box.File(Path.Combine(_box.Temp, "old.tmp"), "0123456789", ageDays: 10);
        CategoryResult result = ScanUserTemp();
        new CleanEngine(_services).Clean(result.Items.ToList(), new CleanOptions { Simulation = true }, null, CancellationToken.None);

        var entry = Assert.Single(_services.History.Load());
        Assert.True(entry.Simulation);
        Assert.Equal(0, entry.ItemsDeleted);
        Assert.Equal(0, entry.BytesFreed);
        Assert.Equal(1, entry.ItemsSimulated);
        Assert.Equal(10, entry.BytesSimulated);
    }

    [Fact]
    public void Items_changed_after_the_scan_are_skipped()
    {
        string old = _box.File(Path.Combine(_box.Temp, "old.tmp"), ageDays: 10);
        CategoryResult result = ScanUserTemp();
        File.AppendAllText(old, "more data written after the scan");

        CleanReport report = new CleanEngine(_services).Clean(result.Items.ToList(), new CleanOptions(), null, CancellationToken.None);

        Assert.True(File.Exists(old));
        Assert.Equal(1, report.Count(CleanOutcome.Skipped));
    }

    [Fact]
    public void A_forged_item_outside_any_rule_root_is_blocked_at_deletion()
    {
        string precious = _box.File(Path.Combine(_box.Documents, "precious.docx"), ageDays: 400);
        var info = new FileInfo(precious);
        CategoryDefinition temp = RuleCatalog.FindCategory("win-user-temp")!;

        // A scanner bug that emits a personal file as a temp item must not reach the disk.
        var forged = new ScanItem
        {
            Path = precious,
            Kind = ItemKind.File,
            Category = temp,
            SizeBytes = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Reason = "forged",
            ScopeRoot = _box.Documents,
        };

        CleanReport report = new CleanEngine(_services).Clean([forged], new CleanOptions(), null, CancellationToken.None);

        Assert.True(File.Exists(precious));
        Assert.Equal(CleanOutcome.Blocked, report.Results.Single().Outcome);
    }

    [Fact]
    public void Review_items_are_quarantined_and_restorable()
    {
        string file = _box.File(Path.Combine(_box.Downloads, "old-setup.exe"), "installer bytes", ageDays: 400);
        var info = new FileInfo(file);
        var item = new ScanItem
        {
            Path = file,
            Kind = ItemKind.File,
            Category = Categories.OldInstallers,
            SizeBytes = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Reason = "test",
        };

        CleanReport report = new CleanEngine(_services).Clean([item], new CleanOptions(), null, CancellationToken.None);
        Assert.Equal(CleanOutcome.Quarantined, report.Results.Single().Outcome);
        Assert.False(File.Exists(file));

        QuarantineEntry entry = Assert.Single(_services.Quarantine.LoadAll());
        Assert.Equal(file, entry.OriginalPath, ignoreCase: true);

        RestoreResult restored = _services.Quarantine.Restore(entry);
        Assert.True(restored.Success);
        Assert.Equal("installer bytes", File.ReadAllText(file));
        Assert.Empty(_services.Quarantine.LoadAll());
    }

    [Fact]
    public void Restore_never_overwrites_a_newer_file()
    {
        string file = _box.File(Path.Combine(_box.Downloads, "notes.txt"), "old version", ageDays: 400);
        var info = new FileInfo(file);
        var item = new ScanItem { Path = file, Kind = ItemKind.File, Category = Categories.OldFiles, SizeBytes = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Reason = "t" };
        new CleanEngine(_services).Clean([item], new CleanOptions(), null, CancellationToken.None);

        File.WriteAllText(file, "new version");
        RestoreResult restored = _services.Quarantine.Restore(_services.Quarantine.LoadAll().Single());

        Assert.True(restored.Success);
        Assert.Equal("new version", File.ReadAllText(file));
        Assert.Equal("old version", File.ReadAllText(restored.RestoredPath));
    }

    [Fact]
    public void Deep_and_review_items_are_never_deleted_permanently()
    {
        CategoryDefinition deep = RuleCatalog.FindCategory("app-logs")!;
        var deepItem = new ScanItem { Path = @"C:\x\y.log", Kind = ItemKind.File, Category = deep, Reason = "t" };
        var leftover = new ScanItem { Path = @"C:\x\y", Kind = ItemKind.Directory, Category = Categories.Leftovers, Reason = "t", MethodOverride = DeletionMethod.Permanent };
        var review = new ScanItem { Path = @"C:\x\z.iso", Kind = ItemKind.File, Category = Categories.LargeFiles, Reason = "t", MethodOverride = DeletionMethod.Permanent };
        var safe = new ScanItem { Path = @"C:\x\t.tmp", Kind = ItemKind.File, Category = RuleCatalog.FindCategory("win-user-temp")!, Reason = "t" };

        Assert.Equal(DeletionMethod.Quarantine, CleanEngine.ResolveMethod(deepItem, new CleanOptions()));
        Assert.Equal(DeletionMethod.Quarantine, CleanEngine.ResolveMethod(leftover, new CleanOptions()));
        Assert.Equal(DeletionMethod.Quarantine, CleanEngine.ResolveMethod(review, new CleanOptions()));
        Assert.Equal(DeletionMethod.Permanent, CleanEngine.ResolveMethod(safe, new CleanOptions()));
    }

    [Fact]
    public void Duplicates_always_keep_one_verified_copy()
    {
        string a = _box.File(Path.Combine(_box.DataFolder, "a", "photo.jpg"), new string('x', 5000));
        string b = _box.File(Path.Combine(_box.DataFolder, "b", "photo.jpg"), new string('x', 5000));
        string c = _box.File(Path.Combine(_box.DataFolder, "c", "photo-copy.jpg"), new string('x', 5000));
        _box.File(Path.Combine(_box.DataFolder, "d", "different.jpg"), new string('y', 5000));

        CategoryResult result = new DuplicateScanner().Scan(Context(), [_box.DataFolder], 1);
        Assert.Equal(3, result.TotalCount);
        Assert.All(result.Items, i => Assert.False(i.IsSelected, "Duplicates are never pre-selected"));

        // Select every copy: all must be refused.
        result.SetAllSelected(true);
        CleanReport all = new CleanEngine(_services).Clean(result.Items.Where(i => i.IsSelected).ToList(), new CleanOptions(), null, CancellationToken.None);
        Assert.Equal(3, all.Count(CleanOutcome.Blocked));
        Assert.True(File.Exists(a) && File.Exists(b) && File.Exists(c));

        // Keep one: the other two go to quarantine.
        result.SetAllSelected(false);
        result.SetSelected(result.Items.Where(i => !i.Path.Equals(a, StringComparison.OrdinalIgnoreCase)), true);
        CleanReport two = new CleanEngine(_services).Clean(result.Items.Where(i => i.IsSelected).ToList(), new CleanOptions(), null, CancellationToken.None);
        Assert.Equal(2, two.Count(CleanOutcome.Quarantined));
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.False(File.Exists(c));
    }

    [Fact]
    public void Duplicate_whose_kept_copy_changed_is_not_removed()
    {
        string a = _box.File(Path.Combine(_box.DataFolder, "a", "doc.pdf"), new string('x', 5000));
        string b = _box.File(Path.Combine(_box.DataFolder, "b", "doc.pdf"), new string('x', 5000));
        CategoryResult result = new DuplicateScanner().Scan(Context(), [_box.DataFolder], 1);

        ScanItem toRemove = result.Items.Single(i => i.Path.Equals(b, StringComparison.OrdinalIgnoreCase));
        toRemove.IsSelected = true;
        File.WriteAllText(a, new string('z', 5000)); // the kept copy is edited after the scan

        CleanReport report = new CleanEngine(_services).Clean([toRemove], new CleanOptions(), null, CancellationToken.None);
        Assert.Equal(CleanOutcome.Blocked, report.Results.Single().Outcome);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Empty_folder_that_gained_a_file_is_kept()
    {
        string folder = _box.Dir(@"Users\me\AppData\Roaming\OldApp\EmptyCache");
        var item = new ScanItem { Path = folder, Kind = ItemKind.Directory, Category = Categories.EmptyFolders, Reason = "t" };
        _box.File(Path.Combine(folder, "new.txt"));

        CleanReport report = new CleanEngine(_services).Clean([item], new CleanOptions(), null, CancellationToken.None);
        Assert.Equal(CleanOutcome.Skipped, report.Results.Single().Outcome);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Locked_items_are_never_cleaned()
    {
        string old = _box.File(Path.Combine(_box.Temp, "old.tmp"), ageDays: 10);
        var info = new FileInfo(old);
        var item = new ScanItem
        {
            Path = old,
            Kind = ItemKind.File,
            Category = RuleCatalog.FindCategory("win-user-temp")!,
            SizeBytes = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Reason = "t",
            LockReason = "Program running",
        };

        CleanReport report = new CleanEngine(_services).Clean([item], new CleanOptions(), null, CancellationToken.None);
        Assert.Equal(CleanOutcome.Skipped, report.Results.Single().Outcome);
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void Every_clean_is_recorded_in_history()
    {
        _box.File(Path.Combine(_box.Temp, "old.tmp"), ageDays: 10);
        CategoryResult result = ScanUserTemp();
        new CleanEngine(_services).Clean(result.Items.ToList(), new CleanOptions(), null, CancellationToken.None);

        var history = _services.History.Load();
        Assert.Single(history);
        Assert.Equal(1, history[0].ItemsDeleted);
    }
}
