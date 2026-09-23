using SafeSweep.Core.Models;
using SafeSweep.Core.Safety;

namespace SafeSweep.Tests;

public class PathGuardTests : IDisposable
{
    private readonly Sandbox _box = new();
    private readonly PathGuard _guard;

    public PathGuardTests()
    {
        _guard = new PathGuard(new ProtectionPolicy(_box.Paths));
    }

    public void Dispose() => _box.Dispose();

    private SafetyVerdict Static(string path, DeletionIntent intent, string? scope = null, bool checkScope = false)
        => _guard.EvaluateStatic(PathUtil.TryNormalize(path, out _)!, intent, scope, checkScope);

    [Theory]
    [InlineData(DeletionIntent.RuleCleanup)]
    [InlineData(DeletionIntent.Leftover)]
    [InlineData(DeletionIntent.UserReview)]
    [InlineData(DeletionIntent.EmptyFolder)]
    public void System32_is_refused_for_every_intent(DeletionIntent intent)
    {
        Assert.False(Static(Path.Combine(_box.Windows, "System32", "kernel32.dll"), intent).IsAllowed);
        Assert.False(Static(Path.Combine(_box.Windows, "System32"), intent).IsAllowed);
    }

    [Fact]
    public void Program_Files_is_refused()
    {
        Assert.False(Static(Path.Combine(_box.ProgramFiles, "App", "app.exe"), DeletionIntent.RuleCleanup).IsAllowed);
        Assert.False(Static(Path.Combine(_box.ProgramFilesX86, "App", "data.bin"), DeletionIntent.UserReview).IsAllowed);
    }

    [Fact]
    public void Windows_Temp_is_a_hole_for_rules_only()
    {
        string file = Path.Combine(_box.WindowsTemp, "setup.tmp");
        Assert.True(Static(file, DeletionIntent.RuleCleanup).IsAllowed);
        Assert.False(Static(file, DeletionIntent.UserReview).IsAllowed);
        Assert.False(Static(file, DeletionIntent.Leftover).IsAllowed);
    }

    [Fact]
    public void Update_database_stays_protected_even_next_to_the_download_hole()
    {
        Assert.True(Static(Path.Combine(_box.Windows, "SoftwareDistribution", "Download", "a.cab"), DeletionIntent.RuleCleanup).IsAllowed);
        Assert.False(Static(Path.Combine(_box.Windows, "SoftwareDistribution", "DataStore", "Logs", "edb.log"), DeletionIntent.RuleCleanup).IsAllowed);
    }

    [Fact]
    public void Memory_dump_exception_is_exact()
    {
        Assert.True(Static(Path.Combine(_box.Windows, "MEMORY.DMP"), DeletionIntent.RuleCleanup).IsAllowed);
        Assert.False(Static(Path.Combine(_box.Windows, "MEMORY.DMP.bak"), DeletionIntent.RuleCleanup).IsAllowed);
    }

    [Theory]
    [InlineData("pagefile.sys")]
    [InlineData("NTUSER.DAT")]
    [InlineData("ntuser.dat.LOG1")]
    [InlineData("UsrClass.dat")]
    [InlineData("wallet.dat")]
    [InlineData("passwords.kdbx")]
    [InlineData("id_rsa")]
    [InlineData("mail.pst")]
    [InlineData("driver.sys")]
    [InlineData("desktop.ini")]
    public void Critical_names_are_refused_even_in_temp(string name)
    {
        Assert.False(Static(Path.Combine(_box.Temp, name), DeletionIntent.RuleCleanup).IsAllowed);
    }

    [Theory]
    [InlineData(@"Default\Login Data")]
    [InlineData(@"Default\Network\Cookies")]
    [InlineData(@"Local State")]
    [InlineData(@"Default\Local Storage\leveldb\000003.log")]
    [InlineData(@"Default\Extensions\abc\1.0\manifest.json")]
    public void Browser_profile_data_is_refused(string relative)
    {
        string userData = Path.Combine(_box.LocalAppData, "Google", "Chrome", "User Data");
        Assert.False(Static(Path.Combine(userData, relative), DeletionIntent.RuleCleanup).IsAllowed);
    }

    [Fact]
    public void Browser_cache_is_allowed()
    {
        string userData = Path.Combine(_box.LocalAppData, "Google", "Chrome", "User Data");
        Assert.True(Static(Path.Combine(userData, "Default", "Cache", "Cache_Data", "f_000001"), DeletionIntent.RuleCleanup).IsAllowed);
    }

    [Theory]
    [InlineData(@"repo\.git\objects\ab\cdef")]
    [InlineData(@"SteamLibrary\steamapps\common\Game\data.pak")]
    [InlineData(@"Exodus\exodus.wallet\seed")]
    public void Protected_folder_names_are_refused_anywhere(string relative)
    {
        Assert.False(Static(Path.Combine(_box.DataFolder, relative), DeletionIntent.UserReview).IsAllowed);
    }

    [Fact]
    public void Personal_folders_allow_only_user_picked_files()
    {
        string doc = Path.Combine(_box.Documents, "report.docx");
        Assert.True(Static(doc, DeletionIntent.UserReview).IsAllowed);
        Assert.False(Static(doc, DeletionIntent.RuleCleanup).IsAllowed);
        Assert.False(Static(_box.Documents, DeletionIntent.UserReview).IsAllowed);
    }

    [Fact]
    public void OneDrive_files_are_refused_for_review()
    {
        Assert.False(Static(Path.Combine(_box.OneDrive, "big.iso"), DeletionIntent.UserReview).IsAllowed);
    }

    [Fact]
    public void AppData_is_never_a_review_target()
    {
        Assert.False(Static(Path.Combine(_box.LocalAppData, "SomeApp", "cache.bin"), DeletionIntent.UserReview).IsAllowed);
    }

    [Fact]
    public void Another_users_profile_is_refused()
    {
        Assert.False(Static(Path.Combine(_box.ProfilesRoot, "other", "Documents", "x.docx"), DeletionIntent.UserReview).IsAllowed);
    }

    [Fact]
    public void Leftovers_only_at_the_top_of_AppData_and_never_shared_vendors()
    {
        Assert.True(Static(Path.Combine(_box.RoamingAppData, "OldTool"), DeletionIntent.Leftover).IsAllowed);
        Assert.False(Static(Path.Combine(_box.RoamingAppData, "Microsoft"), DeletionIntent.Leftover).IsAllowed);
        Assert.False(Static(Path.Combine(_box.RoamingAppData, "OldTool", "a", "b"), DeletionIntent.Leftover).IsAllowed);
        Assert.False(Static(Path.Combine(_box.Documents, "OldTool"), DeletionIntent.Leftover).IsAllowed);
    }

    [Fact]
    public void SafeSweeps_own_data_is_refused()
    {
        Assert.False(Static(Path.Combine(_box.Paths.AppDataRoot, "Quarantine", "x"), DeletionIntent.RuleCleanup).IsAllowed);
    }

    [Fact]
    public void Rule_items_must_be_inside_a_registered_root()
    {
        string file = _box.File(Path.Combine(_box.Temp, "a.tmp"));
        Assert.False(_guard.EvaluateCandidate(file, DeletionIntent.RuleCleanup, _box.Temp, FileAttributes.Normal).IsAllowed);

        Assert.True(_guard.TryRegisterRuleRoot(_box.Temp, false, out string root, out _));
        Assert.True(_guard.EvaluateCandidate(file, DeletionIntent.RuleCleanup, root, FileAttributes.Normal).IsAllowed);

        string outside = _box.File(Path.Combine(_box.LocalAppData, "Other", "b.tmp"));
        Assert.False(_guard.EvaluateCandidate(outside, DeletionIntent.RuleCleanup, root, FileAttributes.Normal).IsAllowed);
    }

    [Fact]
    public void Broad_containers_can_never_be_rule_roots()
    {
        Assert.False(_guard.TryRegisterRuleRoot(_box.Profile, false, out _, out _));
        Assert.False(_guard.TryRegisterRuleRoot(_box.LocalAppData, false, out _, out _));
        Assert.False(_guard.TryRegisterRuleRoot(_box.Documents, false, out _, out _));
        Assert.False(_guard.TryRegisterRuleRoot(Path.GetPathRoot(_box.Root)!, false, out _, out _));
        Assert.False(_guard.TryRegisterRuleRoot(_box.ProfilesRoot, false, out _, out _));
    }

    [Fact]
    public void Links_are_never_followed_at_deletion_time()
    {
        Assert.True(_guard.TryRegisterRuleRoot(_box.Temp, false, out string root, out _));
        string secret = _box.File(Path.Combine(_box.Documents, "thesis.docx"), "precious");
        string link = Path.Combine(_box.Temp, "linked");
        Sandbox.Junction(link, _box.Documents);

        // The junction itself is refused...
        Assert.False(_guard.EvaluateForDeletion(link, DeletionIntent.RuleCleanup, root, ItemKind.Directory).IsAllowed);

        // ...and a path that passes through it resolves to Documents and is refused.
        SafetyVerdict through = _guard.EvaluateForDeletion(Path.Combine(link, "thesis.docx"), DeletionIntent.RuleCleanup, root, ItemKind.File);
        Assert.False(through.IsAllowed);
        Assert.True(File.Exists(secret));
    }

    [Fact]
    public void Hard_linked_files_are_refused()
    {
        Assert.True(_guard.TryRegisterRuleRoot(_box.Temp, false, out string root, out _));
        string original = _box.File(Path.Combine(_box.DataFolder, "original.bin"));
        string link = Path.Combine(_box.Temp, "hardlink.bin");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{original}\"") { UseShellExecute = false, CreateNoWindow = true };
        System.Diagnostics.Process.Start(psi)!.WaitForExit();
        Assert.True(File.Exists(link));

        SafetyVerdict verdict = _guard.EvaluateForDeletion(link, DeletionIntent.RuleCleanup, root, ItemKind.File);
        Assert.False(verdict.IsAllowed);
        Assert.Contains("hard link", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Folders_holding_secrets_fail_content_verification()
    {
        string folder = _box.Dir(@"Users\me\AppData\Roaming\OldWalletApp");
        _box.File(Path.Combine(folder, "config.json"));
        Assert.True(_guard.EvaluateDirectoryContents(folder, DeletionIntent.Leftover).IsAllowed);

        _box.File(Path.Combine(folder, "backup", "wallet.dat"));
        Assert.False(_guard.EvaluateDirectoryContents(folder, DeletionIntent.Leftover).IsAllowed);
    }

    [Fact]
    public void User_exclusions_win()
    {
        var policy = new ProtectionPolicy(_box.Paths);
        policy.SetUserExclusions([Path.Combine(_box.Temp, "keep")]);
        var guard = new PathGuard(policy);
        Assert.False(guard.EvaluateStatic(PathUtil.TryNormalize(Path.Combine(_box.Temp, "keep", "x.tmp"), out _)!, DeletionIntent.RuleCleanup, null, false).IsAllowed);
    }

    [Fact]
    public void Installed_application_folders_are_protected()
    {
        var policy = new ProtectionPolicy(_box.Paths);
        string install = _box.Dir(@"Users\me\AppData\Local\Discord");
        policy.SetInstalledApplicationFolders([(install, "Discord"), (Path.GetPathRoot(_box.Root)!, "Bogus entry")]);
        var guard = new PathGuard(policy);

        Assert.False(guard.EvaluateStatic(PathUtil.TryNormalize(Path.Combine(install, "app.exe"), out _)!, DeletionIntent.Leftover, null, false).IsAllowed);

        // The bogus drive-root entry must not protect (and so silently disable) everything.
        Assert.True(guard.EvaluateStatic(PathUtil.TryNormalize(Path.Combine(_box.Documents, "a.docx"), out _)!, DeletionIntent.UserReview, null, false).IsAllowed);
    }
}
