using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using SafeSweep.Core;
using SafeSweep.Core.Cleaning;
using SafeSweep.Core.Models;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Safety;
using SafeSweep.Core.Scanning;

namespace SafeSweep.Tests;

/// <summary>
/// Release safety audit: every way a path could sneak past the protection
/// layer, and every failure mode of the cleaner, exercised end to end in a
/// sandbox. A forged scan item stands in for a scanner bug: the cleaner must
/// refuse it on its own.
/// </summary>
public class SafetyAuditTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly SafeSweepServices _services;
    private readonly string _system32File;

    public SafetyAuditTests()
    {
        _services = SafeSweepServices.CreateFor(_box.Paths);
        _system32File = _box.File(Path.Combine(_box.Windows, "System32", "kernel32.dll"), "system", ageDays: 400);
        Assert.True(_services.Guard.TryRegisterRuleRoot(_box.Temp, false, out _, out _));
    }

    public void Dispose() => _box.Dispose();

    private ScanItem ForgedTempItem(string path, long size = 6, DateTime? written = null) => new()
    {
        Path = path,
        Kind = ItemKind.File,
        Category = RuleCatalog.FindCategory("win-user-temp")!,
        SizeBytes = size,
        LastWriteUtc = written,
        Reason = "forged by the test",
        ScopeRoot = _box.Temp,
    };

    private CleanReport Clean(params ScanItem[] items)
        => new CleanEngine(_services).Clean(items, new CleanOptions(), null, CancellationToken.None);

    private CategoryResult ScanTemp()
    {
        var context = new ScanContext(_services.Guard, _services.Settings, false, () => _services.GetInventory(), null, CancellationToken.None);
        var results = new Dictionary<string, CategoryResult>();
        new RuleScanner().Scan(context, [RuleCatalog.All.Single(r => r.Category.Id == "win-user-temp")], results);
        return results["win-user-temp"];
    }

    // ------------------------------------------------------------------ path tricks

    public static IEnumerable<object[]> BypassSpellings()
    {
        yield return [@"{temp}\..\..\..\..\Windows\System32\kernel32.dll"];
        yield return [@"{temp}\..\..\..\..\WINDOWS\system32\KERNEL32.DLL"];
        yield return [@"{temp}/../../../../Windows/System32/kernel32.dll"];
        yield return [@"{temp}\..\..\..\..\Windows\System32\kernel32.dll."];
        yield return [@"{temp}\..\..\..\..\Windows\System32\kernel32.dll   "];
        yield return [@"{temp}\.\..\..\..\..\Windows\.\System32\\kernel32.dll"];
        yield return [@"\\?\{sys32}"];
        yield return [@"\\localhost\C$\{sys32nodrive}"];
    }

    [Theory]
    [MemberData(nameof(BypassSpellings))]
    public void Traversal_case_separator_and_device_spellings_cannot_reach_a_protected_file(string spelling)
    {
        string path = spelling
            .Replace("{temp}", _box.Temp)
            .Replace("{sys32nodrive}", _system32File[3..])
            .Replace("{sys32}", _system32File);

        CleanReport report = Clean(ForgedTempItem(path));

        Assert.NotEqual(CleanOutcome.Deleted, report.Results.Single().Outcome);
        Assert.NotEqual(CleanOutcome.Quarantined, report.Results.Single().Outcome);
        Assert.True(File.Exists(_system32File));
    }

    [SkippableFact]
    public void Short_8dot3_names_are_expanded_before_comparison()
    {
        string longDir = _box.Dir(@"Users\me\Documents\A Very Long Folder Name");
        string file = _box.File(Path.Combine(longDir, "secret.txt"));
        string shortPath = ShortPath(file);
        Skip.If(shortPath.Equals(file, StringComparison.OrdinalIgnoreCase), "8.3 short names are disabled on this volume.");

        string? normalized = PathUtil.TryNormalize(shortPath, out _);
        Assert.Equal(file, normalized, ignoreCase: true);
        Assert.False(_services.Guard.EvaluateCandidate(shortPath, DeletionIntent.RuleCleanup, _box.Temp, FileAttributes.Normal).IsAllowed);
    }

    // ------------------------------------------------------------------ links

    [Fact]
    public void A_junction_into_System32_is_never_followed()
    {
        string link = Path.Combine(_box.Temp, "innocent");
        Sandbox.Junction(link, Path.Combine(_box.Windows, "System32"));
        File.SetLastWriteTimeUtc(_system32File, DateTime.UtcNow.AddDays(-400));

        // The scanner never enters it...
        Assert.DoesNotContain(ScanTemp().Items, i => i.Path.Contains("kernel32", StringComparison.OrdinalIgnoreCase));

        // ...and a forged item through it is refused at deletion time.
        var info = new FileInfo(_system32File);
        CleanReport report = Clean(ForgedTempItem(Path.Combine(link, "kernel32.dll"), info.Length, info.LastWriteTimeUtc));
        Assert.Equal(CleanOutcome.Blocked, report.Results.Single().Outcome);
        Assert.True(File.Exists(_system32File));
    }

    [SkippableFact]
    public void A_directory_symlink_into_a_protected_folder_is_never_followed()
    {
        string link = Path.Combine(_box.Temp, "dirlink");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_box.Windows, "System32"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "Creating symbolic links needs Developer Mode or administrator rights here.");
        }

        var info = new FileInfo(_system32File);
        Assert.DoesNotContain(ScanTemp().Items, i => i.Path.Contains("kernel32", StringComparison.OrdinalIgnoreCase));
        CleanReport report = Clean(ForgedTempItem(Path.Combine(link, "kernel32.dll"), info.Length, info.LastWriteTimeUtc));
        Assert.Equal(CleanOutcome.Blocked, report.Results.Single().Outcome);
        Assert.True(File.Exists(_system32File));
    }

    [SkippableFact]
    public void A_file_symlink_in_temp_is_never_removed_or_followed()
    {
        string target = _box.File(Path.Combine(_box.Documents, "thesis.docx"), "precious", ageDays: 400);
        string link = Path.Combine(_box.Temp, "old.tmp");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "Creating symbolic links needs Developer Mode or administrator rights here.");
        }

        Assert.DoesNotContain(ScanTemp().Items, i => i.Path.Equals(link, StringComparison.OrdinalIgnoreCase));
        CleanReport report = Clean(ForgedTempItem(link));
        Assert.Equal(CleanOutcome.Blocked, report.Results.Single().Outcome);
        Assert.Equal("precious", File.ReadAllText(target));
    }

    // ------------------------------------------------------------------ what automatic cleaning refuses

    [Theory]
    [InlineData("server.cer")]
    [InlineData("site.crt")]
    [InlineData("private.key")]
    [InlineData("known_hosts")]
    [InlineData("store.jks")]
    public void Certificates_and_keys_are_refused_even_in_temp(string name)
    {
        string file = _box.File(Path.Combine(_box.Temp, name), ageDays: 30);
        Assert.DoesNotContain(ScanTemp().Items, i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("App.sln")]
    [InlineData("Cargo.toml")]
    [InlineData(".git")]
    public void Source_projects_inside_temp_are_left_alone(string marker)
    {
        string project = _box.Dir(@"Users\me\AppData\Local\Temp\my-project");
        if (marker == ".git")
        {
            _box.Dir(@"Users\me\AppData\Local\Temp\my-project\.git");
        }
        else
        {
            _box.File(Path.Combine(project, marker), ageDays: 30);
        }

        string source = _box.File(Path.Combine(project, "src", "main.cs"), ageDays: 30);
        string loose = _box.File(Path.Combine(_box.Temp, "loose.tmp"), ageDays: 30);

        var paths = ScanTemp().Items.Select(i => i.Path).ToList();
        Assert.DoesNotContain(source, paths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(loose, paths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cloud_sync_folders_are_refused()
    {
        string dropbox = _box.Dir(@"Users\me\Dropbox");
        var paths = _box.Paths with { CloudRoots = [.. _box.Paths.CloudRoots, dropbox] };
        var guard = new PathGuard(new ProtectionPolicy(paths));
        string file = _box.File(Path.Combine(dropbox, "holiday.mp4"));

        Assert.False(guard.EvaluateCandidate(file, DeletionIntent.UserReview, null, FileAttributes.Normal).IsAllowed);
        Assert.False(guard.EvaluateCandidate(file, DeletionIntent.RuleCleanup, null, FileAttributes.Normal).IsAllowed);
    }

    [Fact]
    public void SafeSweeps_own_program_folder_is_protected()
    {
        string install = _box.Dir(@"Users\me\Projects\Tools\SafeSweep");
        var guard = new PathGuard(new ProtectionPolicy(_box.Paths with { InstallDirectory = install }));
        string dll = PathUtil.TryNormalize(Path.Combine(install, "SafeSweep.dll"), out _)!;
        Assert.True(new PathGuard(new ProtectionPolicy(_box.Paths)).EvaluateStatic(dll, DeletionIntent.UserReview, null, false).IsAllowed, "control: allowed without the install protection");
        Assert.False(guard.EvaluateStatic(dll, DeletionIntent.UserReview, null, false).IsAllowed);
    }

    [Fact]
    public void A_portable_copy_in_Downloads_protects_only_itself()
    {
        string exe = _box.File(Path.Combine(_box.Downloads, "SafeSweep.exe"));
        string other = _box.File(Path.Combine(_box.Downloads, "old-setup.exe"));
        var guard = new PathGuard(new ProtectionPolicy(_box.Paths with { InstallDirectory = _box.Downloads, ExecutablePath = exe }));

        Assert.False(guard.EvaluateCandidate(exe, DeletionIntent.UserReview, null, FileAttributes.Normal).IsAllowed);
        Assert.True(guard.EvaluateCandidate(other, DeletionIntent.UserReview, null, FileAttributes.Normal).IsAllowed);
    }

    [SkippableFact]
    public void System_folders_on_other_drives_are_refused()
    {
        string? other = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .FirstOrDefault(r => !r.Equals(Path.GetPathRoot(_box.Root), StringComparison.OrdinalIgnoreCase));
        Skip.If(other is null, "No second fixed drive on this machine.");

        var guard = new PathGuard(new ProtectionPolicy(_box.Paths));
        foreach (string top in new[] { "Windows", "Program Files", "Recovery", "Boot", "EFI", "Users" })
        {
            string path = Path.Combine(other!, top, "file.bin");
            Assert.False(guard.EvaluateStatic(path, DeletionIntent.UserReview, null, false).IsAllowed, path);
        }

        Assert.True(guard.EvaluateStatic(Path.Combine(other!, "Data", "big.iso"), DeletionIntent.UserReview, null, false).IsAllowed);
    }

    // ------------------------------------------------------------------ failures while cleaning

    [Fact]
    public void A_file_in_use_is_skipped_not_crashed_and_left_in_place()
    {
        string file = _box.File(Path.Combine(_box.Temp, "locked.tmp"), "busy", ageDays: 10);
        CategoryResult scan = ScanTemp();
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            CleanReport report = Clean([.. scan.Items]);
            CleanItemResult result = report.Results.Single(r => r.Item.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(CleanOutcome.Skipped, result.Outcome);
            Assert.Contains("in use", result.Detail, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Access_denied_is_skipped_not_crashed()
    {
        string folder = _box.Dir(@"Users\me\AppData\Local\Temp\guarded");
        string file = _box.File(Path.Combine(folder, "old.tmp"), "x", ageDays: 10);
        CategoryResult scan = ScanTemp();
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        var fileRule = new FileSystemAccessRule(me, FileSystemRights.Delete, AccessControlType.Deny);
        var dirRule = new FileSystemAccessRule(me, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
        AddRule(new FileInfo(file), fileRule);
        AddRule(new DirectoryInfo(folder), dirRule);
        try
        {
            CleanReport report = Clean([.. scan.Items]);
            CleanItemResult result = report.Results.Single(r => r.Item.Path.Equals(file, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(CleanOutcome.Skipped, result.Outcome);
            Assert.True(File.Exists(file));
        }
        finally
        {
            RemoveRule(new FileInfo(file), fileRule);
            RemoveRule(new DirectoryInfo(folder), dirRule);
        }
    }

    [Fact]
    public void An_unreadable_folder_does_not_stop_the_scan()
    {
        string folder = _box.Dir(@"Users\me\AppData\Local\Temp\sealed");
        _box.File(Path.Combine(folder, "hidden.tmp"), ageDays: 10);
        string visible = _box.File(Path.Combine(_box.Temp, "visible.tmp"), ageDays: 10);
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny);
        AddRule(new DirectoryInfo(folder), rule);
        try
        {
            Assert.Contains(ScanTemp().Items, i => i.Path.Equals(visible, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RemoveRule(new DirectoryInfo(folder), rule);
        }
    }

    [Fact]
    public void A_file_removed_since_the_scan_is_reported_as_skipped()
    {
        string file = _box.File(Path.Combine(_box.Temp, "gone.tmp"), ageDays: 10);
        CategoryResult scan = ScanTemp();
        File.Delete(file);

        CleanItemResult result = Clean([.. scan.Items]).Results.Single();
        Assert.Equal(CleanOutcome.Skipped, result.Outcome);
        Assert.Contains("no longer exists", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_program_that_started_after_the_scan_locks_its_data_again()
    {
        string file = _box.File(Path.Combine(_box.Temp, "cache.bin"), "cache", ageDays: 10);
        var info = new FileInfo(file);
        var item = new ScanItem
        {
            Path = file,
            Kind = ItemKind.File,
            Category = RuleCatalog.FindCategory("win-user-temp")!,
            SizeBytes = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
            Reason = "t",
            ScopeRoot = _box.Temp,
            BlockingProcesses = [Process.GetCurrentProcess().ProcessName],
            BlockingProgramName = "The test host",
        };

        CleanItemResult result = Clean(item).Results.Single();
        Assert.Equal(CleanOutcome.Skipped, result.Outcome);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Cancelling_before_cleaning_touches_nothing_and_is_recorded()
    {
        for (int i = 0; i < 20; i++)
        {
            _box.File(Path.Combine(_box.Temp, $"old{i}.tmp"), ageDays: 10);
        }

        CategoryResult scan = ScanTemp();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        CleanReport report = new CleanEngine(_services).Clean([.. scan.Items], new CleanOptions(), null, cts.Token);

        Assert.True(report.Cancelled);
        Assert.Empty(report.Results);
        Assert.Equal(20, Directory.GetFiles(_box.Temp).Length);
        Assert.True(_services.History.Load().Single().Cancelled);
    }

    [Fact]
    public void Cancelling_a_scan_returns_a_partial_result_instead_of_throwing()
    {
        for (int i = 0; i < 200; i++)
        {
            _box.File(Path.Combine(_box.Temp, $"old{i}.tmp"), ageDays: 10);
        }

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ScanProgress>(p =>
        {
            if (p.Stage.StartsWith("Scanning", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        });

        ScanResult result = new ScanEngine(_services).Scan(new ScanRequest(ScanMode.Quick), progress, cts.Token);
        Assert.True(result.Cancelled);
    }

    // ------------------------------------------------------------------ quarantine

    [Fact]
    public void A_leftover_folder_round_trips_through_quarantine_intact()
    {
        string folder = _box.Dir(@"Users\me\AppData\Roaming\Zqxvwplm");
        _box.File(Path.Combine(folder, "settings.ini"), "a=1", ageDays: 200);
        _box.File(Path.Combine(folder, "cache", "blob.bin"), "blob", ageDays: 200);
        foreach (string dir in new[] { Path.Combine(folder, "cache"), folder })
        {
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-200));
            Directory.SetCreationTimeUtc(dir, DateTime.UtcNow.AddDays(-200));
        }

        var item = new ScanItem
        {
            Path = folder,
            Kind = ItemKind.Directory,
            Category = Categories.Leftovers,
            SizeBytes = 7,
            FileCount = 2,
            LastWriteUtc = DateTime.UtcNow.AddDays(-199),
            Reason = "t",
        };

        CleanReport report = Clean(item);
        Assert.Equal(CleanOutcome.Quarantined, report.Results.Single().Outcome);
        Assert.False(Directory.Exists(folder));

        RestoreResult restored = _services.Quarantine.Restore(_services.Quarantine.LoadAll().Single());
        Assert.True(restored.Success);
        Assert.Equal("a=1", File.ReadAllText(Path.Combine(folder, "settings.ini")));
        Assert.Equal("blob", File.ReadAllText(Path.Combine(folder, "cache", "blob.bin")));
    }

    [Fact]
    public void Purge_refuses_anything_outside_the_quarantine_store()
    {
        string outside = _box.File(Path.Combine(_box.Documents, "keep.docx"), "keep");
        var forged = new QuarantineEntry
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            OriginalPath = outside,
            StoredPath = outside,
            Kind = ItemKind.File,
            QuarantinedUtc = DateTime.UtcNow,
            CategoryId = "x",
            CategoryName = "x",
        };

        Assert.False(_services.Quarantine.Purge(forged, out _));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Restore_recreates_a_missing_parent_folder()
    {
        string folder = _box.Dir(@"Users\me\Downloads\old");
        string file = _box.File(Path.Combine(folder, "setup.exe"), "setup", ageDays: 400);
        var info = new FileInfo(file);
        Clean(new ScanItem { Path = file, Kind = ItemKind.File, Category = Categories.OldInstallers, SizeBytes = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Reason = "t" });
        Directory.Delete(folder);

        Assert.True(_services.Quarantine.Restore(_services.Quarantine.LoadAll().Single()).Success);
        Assert.Equal("setup", File.ReadAllText(file));
    }

    // ------------------------------------------------------------------ scanners

    [Fact]
    public void Empty_folder_trees_are_found_and_removed_bottom_up_but_shared_folders_are_not()
    {
        string top = _box.Dir(@"Users\me\AppData\Roaming\OldGame\a\b");
        _box.Dir(@"Users\me\AppData\Roaming\Microsoft\EmptyThing");
        string oldGame = Path.Combine(_box.RoamingAppData, "OldGame");
        foreach (string dir in new[] { top, Path.GetDirectoryName(top)!, oldGame, Path.Combine(_box.RoamingAppData, "Microsoft", "EmptyThing") })
        {
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-90));
            Directory.SetCreationTimeUtc(dir, DateTime.UtcNow.AddDays(-90));
        }

        var context = new ScanContext(_services.Guard, _services.Settings, false, () => _services.GetInventory(), null, CancellationToken.None);
        CategoryResult result = new EmptyFolderScanner().Scan(context);

        Assert.Equal(3, result.Items.Count(i => i.Path.StartsWith(oldGame, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(result.Items, i => i.Path.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

        CleanReport report = new CleanEngine(_services).Clean([.. result.Items], new CleanOptions(), null, CancellationToken.None);
        Assert.Equal(3, report.Count(CleanOutcome.Deleted));
        Assert.False(Directory.Exists(oldGame));
    }

    [Fact]
    public void Hard_links_are_never_reported_as_duplicates()
    {
        string original = _box.File(Path.Combine(_box.DataFolder, "a", "movie.mkv"), new string('m', 4096));
        string link = Path.Combine(_box.DataFolder, "b", "movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{original}\"") { UseShellExecute = false, CreateNoWindow = true })!.WaitForExit();
        Assert.True(File.Exists(link));

        var context = new ScanContext(_services.Guard, _services.Settings, false, () => _services.GetInventory(), null, CancellationToken.None);
        Assert.Equal(0, new DuplicateScanner().Scan(context, [_box.DataFolder], 1).TotalCount);
    }

    // ------------------------------------------------------------------ selection at scale

    [Fact]
    public void Selecting_a_hundred_thousand_items_is_linear_and_exact()
    {
        CategoryDefinition category = RuleCatalog.FindCategory("win-user-temp")!;
        var result = new CategoryResult(category);
        for (int i = 0; i < 100_000; i++)
        {
            result.Add(new ScanItem { Path = $@"C:\x\{i}.tmp", Kind = ItemKind.File, Category = category, SizeBytes = 10, Reason = "t" });
        }

        var watch = Stopwatch.StartNew();
        result.SetAllSelected(true);
        Assert.Equal(100_000, result.SelectedCount);
        Assert.Equal(1_000_000, result.SelectedBytes);
        Assert.True(result.IsSelected);

        result.Items[5].IsSelected = false;
        Assert.Null(result.IsSelected); // partial

        result.SetAllSelected(false);
        Assert.Equal(0, result.SelectedCount);
        Assert.False(result.IsSelected);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Locked_items_are_never_counted_by_select_all()
    {
        CategoryDefinition category = RuleCatalog.FindCategory("browser-chrome")!;
        var result = new CategoryResult(category);
        result.Add(new ScanItem { Path = @"C:\x\a", Kind = ItemKind.File, Category = category, SizeBytes = 1, Reason = "t" });
        result.Add(new ScanItem { Path = @"C:\x\b", Kind = ItemKind.File, Category = category, SizeBytes = 1, Reason = "t", LockReason = "Chrome is running" });

        result.SetAllSelected(true);
        Assert.Equal(1, result.SelectedCount);
        Assert.True(result.IsSelected); // every selectable item is selected
    }

    private static void AddRule(FileSystemInfo target, FileSystemAccessRule rule) => ChangeRule(target, rule, add: true);

    private static void RemoveRule(FileSystemInfo target, FileSystemAccessRule rule) => ChangeRule(target, rule, add: false);

    private static void ChangeRule(FileSystemInfo target, FileSystemAccessRule rule, bool add)
    {
        if (target is FileInfo file)
        {
            FileSecurity security = file.GetAccessControl();
            if (add)
            {
                security.AddAccessRule(rule);
            }
            else
            {
                security.RemoveAccessRule(rule);
            }

            file.SetAccessControl(security);
        }
        else if (target is DirectoryInfo directory)
        {
            DirectorySecurity security = directory.GetAccessControl();
            if (add)
            {
                security.AddAccessRule(rule);
            }
            else
            {
                security.RemoveAccessRule(rule);
            }

            directory.SetAccessControl(security);
        }
    }

    private static string ShortPath(string path)
    {
        var buffer = new char[1024];
        uint length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return length == 0 ? path : new string(buffer, 0, (int)length);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, [System.Runtime.InteropServices.Out] char[] shortPath, uint bufferLength);

    // ------------------------------------------------------------------ review tools

    [Fact]
    public void Review_tools_default_to_the_configured_folders_only()
    {
        Assert.Empty(_services.Settings.ReviewRoots); // the defaults are in use
        string big = _box.File(Path.Combine(_box.Downloads, "movie.mkv"), new string('x', 11 * 1024 * 1024));
        string installer = _box.File(Path.Combine(_box.Downloads, "tool-setup.exe"), "MZ", ageDays: 400);
        ScanContext NewContext() => new(_services.Guard, _services.Settings, false, () => _services.GetInventory(), null, CancellationToken.None);

        CategoryResult large = new LargeFileScanner().Scan(NewContext(), null, 10L * 1024 * 1024);
        Assert.Contains(large.Items, i => i.Path.Equals(big, StringComparison.OrdinalIgnoreCase));
        Assert.All(large.Items, i => Assert.True(PathUtil.IsUnderOrEqual(i.Path, _box.Root), i.Path + " is outside the configured folders"));

        CategoryResult installers = new OldInstallerScanner().Scan(NewContext());
        Assert.Equal(installer, Assert.Single(installers.Items).Path, ignoreCase: true);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
