using Microsoft.Win32;
using SafeSweep.Core.Models;

namespace SafeSweep.Core.Rules;

/// <summary>
/// Every built-in file-cleanup rule. Adding a location here is the only way
/// SafeSweep can ever clean it automatically; nothing outside these roots is
/// touched by Safe or Deep cleaning.
/// </summary>
public static class RuleCatalog
{
    private const string Windows = "Windows";
    private const string Browsers = "Browsers";
    private const string Applications = "Applications";
    private const string WindowsUpdate = "Windows Update";

    private static readonly string[] ChromiumProfileCaches = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache"];
    private static readonly string[] ChromiumSharedCaches = ["GrShaderCache", "ShaderCache", "GraphiteDawnCache"];

    public static IReadOnlyList<CleanupRule> All { get; } = Build();

    public static IEnumerable<CategoryDefinition> RuleCategories
        => All.Select(r => r.Category).DistinctBy(c => c.Id);

    /// <summary>All categories: rule-based plus the specialised scanners.</summary>
    public static IReadOnlyList<CategoryDefinition> AllCategories { get; } =
        RuleCategories
            .Concat([Categories.RecycleBin, Categories.Leftovers, Categories.EmptyFolders, Categories.OldInstallers, Categories.OldFiles, Categories.LargeFiles, Categories.Duplicates])
            .ToList();

    public static CategoryDefinition? FindCategory(string id) => AllCategories.FirstOrDefault(c => c.Id == id);

    private static CategoryDefinition Safe(string id, string name, string group, string description, string why, string? after = null, bool admin = false, RiskLevel risk = RiskLevel.VeryLow)
        => new()
        {
            Id = id,
            Name = name,
            Group = group,
            Level = CleanLevel.Safe,
            Risk = risk,
            Description = description,
            WhyRemovable = why,
            AfterEffects = after ?? "Nothing noticeable. The data is recreated automatically when needed.",
            DefaultMethod = DeletionMethod.Permanent,
            Intent = DeletionIntent.RuleCleanup,
            RequiresAdmin = admin,
            InQuickScan = true,
        };

    private static CategoryDefinition Deep(string id, string name, string group, string description, string why, string after, bool admin = false, RiskLevel risk = RiskLevel.Low)
        => new()
        {
            Id = id,
            Name = name,
            Group = group,
            Level = CleanLevel.Deep,
            Risk = risk,
            Description = description,
            WhyRemovable = why,
            AfterEffects = after + " Removed items are quarantined first, so they can be restored.",
            DefaultMethod = DeletionMethod.Quarantine,
            Intent = DeletionIntent.RuleCleanup,
            RequiresAdmin = admin,
            InQuickScan = false,
        };

    private static List<CleanupRule> Build()
    {
        var rules = new List<CleanupRule>();

        // ---------------------------------------------------------------- Safe: Windows
        CategoryDefinition userTemp = Safe(
            "win-user-temp", "User temporary files", Windows,
            "Files programs and installers left in your personal temporary folder (%TEMP%).",
            "Temporary files are meant to be discarded. Only files untouched for longer than the safety window (24 hours by default) are included, so running installers keep theirs.",
            "Nothing noticeable. Files still in use are skipped automatically.");
        rules.Add(new CleanupRule
        {
            Category = userTemp,
            Roots = ["{Temp}", @"{LocalAppData}\Temp"],
            UsesTempAgeSetting = true,
            RequiredRootLeafNames = ["Temp", "Tmp"],
            LocationLabel = "your temporary folder",
        });

        CategoryDefinition systemTemp = Safe(
            "win-system-temp", "Windows temporary files", Windows,
            "Temporary files written by Windows services and system installers to C:\\Windows\\Temp.",
            "Temporary system files are meant to be discarded. Only files older than 48 hours are included.",
            "Nothing noticeable. Files still in use are skipped automatically.",
            admin: true);
        rules.Add(new CleanupRule
        {
            Category = systemTemp,
            Roots = [@"{Windows}\Temp"],
            MinAge = TimeSpan.FromHours(48),
            RequiredRootLeafNames = ["Temp"],
            LocationLabel = "the Windows temporary folder",
        });

        CategoryDefinition thumbs = Safe(
            "win-thumbnails", "Thumbnail cache", Windows,
            "Preview images File Explorer stores for pictures, videos and documents (thumbcache_*.db).",
            "Explorer rebuilds thumbnails automatically as you browse. Clearing also fixes stale or broken previews.",
            "Folders full of pictures may show blank previews for a moment while thumbnails are rebuilt. Cache files Explorer is using are skipped.");
        rules.Add(new CleanupRule
        {
            Category = thumbs,
            Roots = [@"{LocalAppData}\Microsoft\Windows\Explorer"],
            Include = ["thumbcache_*.db"],
            Recursive = false,
            LocationLabel = "the Explorer thumbnail cache",
        });

        CategoryDefinition d3d = Safe(
            "win-d3d-shaders", "DirectX shader cache", Windows,
            "Compiled graphics shaders stored by DirectX (the same item Windows Disk Cleanup offers).",
            "Shaders are recompiled automatically when a game or app needs them.",
            "Games may stutter briefly the first time a scene is shown again while shaders are rebuilt.");
        rules.Add(new CleanupRule
        {
            Category = d3d,
            Roots = [@"{LocalAppData}\D3DSCache"],
            LocationLabel = "the DirectX shader cache",
        });

        CategoryDefinition werUser = Safe(
            "win-error-reports", "Windows error reports", Windows,
            "Crash and problem reports Windows Error Reporting collected for programs you ran.",
            "Reports that were already sent or archived are only kept for your reference.",
            "The report history in Reliability Monitor may show fewer details.");
        rules.Add(new CleanupRule
        {
            Category = werUser,
            Roots =
            [
                @"{LocalAppData}\Microsoft\Windows\WER\ReportArchive",
                @"{LocalAppData}\Microsoft\Windows\WER\ReportQueue",
                @"{LocalAppData}\Microsoft\Windows\WER\Temp",
            ],
            MinAge = TimeSpan.FromDays(1),
            LocationLabel = "your error report folders",
        });

        CategoryDefinition werSystem = Safe(
            "win-error-reports-system", "System error reports", Windows,
            "Error reports collected for all users and for Windows services.",
            "Reports that were already sent or archived are only kept for reference.",
            "The report history in Reliability Monitor may show fewer details.",
            admin: true);
        rules.Add(new CleanupRule
        {
            Category = werSystem,
            Roots =
            [
                @"{ProgramData}\Microsoft\Windows\WER\ReportArchive",
                @"{ProgramData}\Microsoft\Windows\WER\ReportQueue",
                @"{ProgramData}\Microsoft\Windows\WER\Temp",
            ],
            MinAge = TimeSpan.FromDays(1),
            LocationLabel = "the system error report folders",
        });

        CategoryDefinition inet = Safe(
            "win-inetcache", "Windows internet cache", Windows,
            "Web content cached by Windows components and older apps that use the system web stack (INetCache).",
            "It is a cache: content is downloaded again when needed. Cookies are stored elsewhere and are never touched.",
            "Some web content inside apps may load slightly slower once.");
        rules.Add(new CleanupRule
        {
            Category = inet,
            Roots = [@"{LocalAppData}\Microsoft\Windows\INetCache"],
            MinAge = TimeSpan.FromDays(1),
            LocationLabel = "the Windows internet cache",
        });

        CategoryDefinition crash = Safe(
            "app-crash-dumps", "Application crash dumps", Windows,
            "Memory dumps (.dmp) written when a program crashed.",
            "They are only useful for diagnosing a specific crash and are often hundreds of megabytes. Only dumps older than 7 days are included.",
            "If you are working with a developer or support team on a recent crash, keep the dump they asked for.",
            risk: RiskLevel.Low);
        rules.Add(new CleanupRule
        {
            Category = crash,
            Roots = [@"{LocalAppData}\CrashDumps"],
            Include = ["*.dmp"],
            MinAge = TimeSpan.FromDays(7),
            LocationLabel = "the application crash dump folder",
        });

        CategoryDefinition rdp = Safe(
            "win-rdp-cache", "Remote Desktop bitmap cache", Windows,
            "Screen fragments cached by the Remote Desktop client to speed up sessions.",
            "The client rebuilds the cache during the next session.",
            "The first remote session afterwards may redraw a little slower.");
        rules.Add(new CleanupRule
        {
            Category = rdp,
            Roots = [@"{LocalAppData}\Microsoft\Terminal Server Client\Cache"],
            BlockingProcesses = ["mstsc"],
            BlockingProgramName = "Remote Desktop Connection",
            LocationLabel = "the Remote Desktop cache",
        });

        // ---------------------------------------------------------------- Safe: Browsers
        AddChromium(rules, "chrome", "Google Chrome", @"{LocalAppData}\Google\Chrome\User Data", "chrome");
        AddChromium(rules, "edge", "Microsoft Edge", @"{LocalAppData}\Microsoft\Edge\User Data", "msedge");
        AddChromium(rules, "brave", "Brave", @"{LocalAppData}\BraveSoftware\Brave-Browser\User Data", "brave");
        AddChromium(rules, "vivaldi", "Vivaldi", @"{LocalAppData}\Vivaldi\User Data", "vivaldi");
        AddChromium(rules, "chromium", "Chromium", @"{LocalAppData}\Chromium\User Data", "chrome");

        CategoryDefinition firefox = BrowserCategory("firefox", "Mozilla Firefox");
        rules.Add(new CleanupRule
        {
            Category = firefox,
            Roots =
            [
                @"{LocalAppData}\Mozilla\Firefox\Profiles\*\cache2",
                @"{LocalAppData}\Mozilla\Firefox\Profiles\*\startupCache",
                @"{LocalAppData}\Mozilla\Firefox\Profiles\*\thumbnails",
            ],
            BlockingProcesses = ["firefox"],
            BlockingProgramName = "Firefox",
            LocationLabel = "Firefox's cache",
        });

        CategoryDefinition opera = BrowserCategory("opera", "Opera / Opera GX");
        rules.Add(new CleanupRule
        {
            Category = opera,
            Roots =
            [
                @"{LocalAppData}\Opera Software\Opera Stable\Cache",
                @"{LocalAppData}\Opera Software\Opera GX Stable\Cache",
            ],
            BlockingProcesses = ["opera"],
            BlockingProgramName = "Opera",
            LocationLabel = "Opera's cache",
        });

        // ---------------------------------------------------------------- Deep
        CategoryDefinition iconCache = Deep(
            "win-icon-cache", "Icon cache", Windows,
            "Icons File Explorer caches for programs and file types (iconcache_*.db).",
            "Explorer rebuilds the cache automatically. Clearing it fixes wrong or blank icons.",
            "Icons may appear blank or generic until Explorer or Windows restarts.");
        rules.Add(new CleanupRule
        {
            Category = iconCache,
            Roots = [@"{LocalAppData}\Microsoft\Windows\Explorer"],
            Include = ["iconcache_*.db"],
            Recursive = false,
            LocationLabel = "the Explorer icon cache",
        });

        CategoryDefinition gpu = Deep(
            "gpu-vendor-shaders", "Graphics driver shader caches", Windows,
            "Shader caches kept by NVIDIA, AMD and Intel graphics drivers.",
            "Drivers rebuild these caches automatically. Clearing them can fix graphical glitches after a driver update.",
            "Games may stutter or load slower the first time while shaders are rebuilt.");
        rules.Add(new CleanupRule
        {
            Category = gpu,
            Roots =
            [
                @"{LocalAppData}\NVIDIA\DXCache",
                @"{LocalAppData}\NVIDIA\GLCache",
                @"{LocalAppData}\NVIDIA\OptixCache",
                @"{LocalLow}\NVIDIA\PerDriverVersion\DXCache",
                @"{LocalLow}\NVIDIA\PerDriverVersion\GLCache",
                @"{ProgramData}\NVIDIA Corporation\NV_Cache",
                @"{LocalAppData}\AMD\DxCache",
                @"{LocalAppData}\AMD\DxcCache",
                @"{LocalAppData}\AMD\GLCache",
                @"{LocalAppData}\AMD\VkCache",
                @"{LocalAppData}\Intel\ShaderCache",
            ],
            LocationLabel = "a graphics driver shader cache",
        });

        CategoryDefinition wuDownload = Deep(
            "wu-download-cache", "Windows Update download cache", WindowsUpdate,
            "Update packages Windows Update already downloaded (C:\\Windows\\SoftwareDistribution\\Download).",
            "Microsoft documents this folder as safe to clear; Windows downloads anything it still needs again. Only files older than 10 days are included, and nothing is offered while an update is waiting for a restart.",
            "A pending update may be downloaded again.",
            admin: true);
        rules.Add(new CleanupRule
        {
            Category = wuDownload,
            Roots = [@"{Windows}\SoftwareDistribution\Download"],
            MinAge = TimeSpan.FromDays(10),
            LocationLabel = "the Windows Update download cache",
            Precondition = WindowsUpdatePendingReason,
        });

        CategoryDefinition sysDumps = Deep(
            "win-system-dumps", "System crash dumps", Windows,
            "Memory dumps written by Windows after a blue screen (MEMORY.DMP, minidumps) and live kernel reports.",
            "They are only needed to diagnose a past system crash. A full MEMORY.DMP can be several gigabytes.",
            "You lose the evidence for diagnosing past blue screens. Keep them if you are investigating crashes.",
            admin: true);
        rules.Add(new CleanupRule
        {
            Category = sysDumps,
            Roots = [@"{Windows}\Minidump", @"{Windows}\LiveKernelReports"],
            Include = ["*.dmp"],
            MinAge = TimeSpan.FromDays(3),
            LocationLabel = "the system crash dump folders",
        });
        rules.Add(new CleanupRule
        {
            Category = sysDumps,
            Roots = [@"{Windows}\MEMORY.DMP"],
            ExactFile = true,
            MinAge = TimeSpan.FromDays(3),
            LocationLabel = "the full kernel memory dump",
        });

        CategoryDefinition winLogs = Deep(
            "win-logs", "Windows diagnostic logs", Windows,
            "Logs written by Windows servicing, DISM, Windows Update and setup components.",
            "Logs are only read when troubleshooting. Only logs older than 14 days are included; current logs are never touched.",
            "Older troubleshooting history is gone. Logs Windows uses for System Restore and recovery are protected and never included.",
            admin: true);
        rules.Add(new CleanupRule
        {
            Category = winLogs,
            Roots =
            [
                @"{Windows}\Logs\CBS", @"{Windows}\Logs\DISM", @"{Windows}\Logs\WindowsUpdate", @"{Windows}\Logs\waasmedic",
                @"{Windows}\Logs\SIH", @"{Windows}\Logs\NetSetup", @"{Windows}\Logs\MoSetup", @"{Windows}\Logs\DPX",
                @"{Windows}\Logs\SetupCln",
            ],
            Include = ["*.log", "*.etl", "*.cab", "*.txt", "*.xml"],
            MinAge = TimeSpan.FromDays(14),
            LocationLabel = "Windows diagnostic logs",
        });

        CategoryDefinition appLogs = Deep(
            "app-logs", "Application log files", Applications,
            "Old .log files programs wrote into their \"logs\" folders inside AppData.",
            "Log files are diagnostic records, not program data. Only files older than 30 days in folders named \"logs\" are included, and folders that look like databases are skipped.",
            "Older troubleshooting history for those programs is gone.");
        rules.Add(new CleanupRule
        {
            Category = appLogs,
            Roots = [@"{LocalAppData}\*\logs", @"{LocalAppData}\*\*\logs", @"{RoamingAppData}\*\logs", @"{RoamingAppData}\*\*\logs"],
            Include = ["*.log", "*.log.*", "*.old", "*.log.gz"],
            MinAge = TimeSpan.FromDays(30),
            ExcludedWildcardNames = ["Microsoft", "Packages", "Temp", "SafeSweep", "Programs", "Comms", "ConnectedDevicesPlatform"],
            SkipDirectory = LooksLikeDatabaseFolder,
            LocationLabel = "an application log folder",
        });

        CategoryDefinition driverExtract = Deep(
            "driver-installer-leftovers", "Extracted driver installers", Applications,
            "Installation files NVIDIA and AMD graphics driver installers unpack to C:\\NVIDIA and C:\\AMD and leave behind.",
            "The installed driver does not use these files; the vendors' own guidance is that they can be deleted.",
            "You would need to download the driver again to reinstall the same version.");
        rules.Add(new CleanupRule
        {
            Category = driverExtract,
            Roots = [@"{SystemDrive}\NVIDIA\DisplayDriver", @"{SystemDrive}\AMD\*"],
            MinAge = TimeSpan.FromDays(7),
            LocationLabel = "an extracted driver installer folder",
        });

        CategoryDefinition storeTemp = Deep(
            "store-app-temp", "Store app temporary files", Applications,
            "Temporary files and web caches of Microsoft Store apps (AC\\Temp, AC\\INetCache, TempState).",
            "These are the apps' own temporary locations. Only files older than 7 days are included.",
            "Apps may reload some content once.");
        rules.Add(new CleanupRule
        {
            Category = storeTemp,
            Roots = [@"{LocalAppData}\Packages\*\AC\Temp", @"{LocalAppData}\Packages\*\AC\INetCache", @"{LocalAppData}\Packages\*\TempState"],
            MinAge = TimeSpan.FromDays(7),
            LocationLabel = "a Store app temporary folder",
        });

        CategoryDefinition devCaches = Deep(
            "dev-package-caches", "Developer package caches", Applications,
            "Download caches of npm, NuGet, pip and Yarn.",
            "Package managers download anything missing again on the next restore or install.",
            "The next build or install is slower and needs an internet connection.");
        rules.Add(new CleanupRule
        {
            Category = devCaches,
            Roots = [@"{LocalAppData}\npm-cache\_cacache", @"{LocalAppData}\Yarn\Cache"],
            BlockingProcesses = ["node", "npm", "yarn"],
            BlockingProgramName = "Node.js (npm/yarn may be installing packages)",
            LocationLabel = "the npm/Yarn package cache",
        });
        rules.Add(new CleanupRule
        {
            Category = devCaches,
            Roots = [@"{LocalAppData}\NuGet\v3-cache", @"{LocalAppData}\NuGet\plugins-cache"],
            BlockingProcesses = ["dotnet", "msbuild", "devenv", "nuget"],
            BlockingProgramName = ".NET build tools (a restore may be running)",
            LocationLabel = "the NuGet HTTP cache",
        });
        rules.Add(new CleanupRule
        {
            Category = devCaches,
            Roots = [@"{LocalAppData}\pip\cache"],
            BlockingProcesses = ["pip", "pip3"],
            BlockingProgramName = "pip",
            LocationLabel = "the pip download cache",
        });

        CategoryDefinition appCaches = Deep(
            "app-caches", "Application caches", Applications,
            "Web and GPU caches of desktop apps built on web technology (Discord, Slack, Visual Studio Code, Teams classic) and Steam's web cache.",
            "These are caches; the apps download or rebuild them automatically. Logins and settings are stored elsewhere and are not touched.",
            "The app may start a little slower once.");
        AddAppCache(rules, appCaches, "Discord", "discord", @"{RoamingAppData}\discord");
        AddAppCache(rules, appCaches, "Slack", "slack", @"{RoamingAppData}\Slack");
        AddAppCache(rules, appCaches, "Visual Studio Code", "Code", @"{RoamingAppData}\Code", extra: ["CachedData", "CachedExtensionVSIXs"]);
        AddAppCache(rules, appCaches, "Microsoft Teams (classic)", "Teams", @"{RoamingAppData}\Microsoft\Teams");
        rules.Add(new CleanupRule
        {
            Category = appCaches,
            Roots = [@"{LocalAppData}\Steam\htmlcache"],
            BlockingProcesses = ["steam", "steamwebhelper"],
            BlockingProgramName = "Steam",
            LocationLabel = "Steam's web cache",
        });

        CategoryDefinition swCache = Deep(
            "browser-sw-cache", "Browser offline web-app caches", Browsers,
            "Service-worker caches websites store in Chromium browsers so they load faster or work offline.",
            "It is cache data; sites download it again when you visit them.",
            "Web apps you use offline may need an internet connection once before working offline again.",
            risk: RiskLevel.Moderate);
        foreach ((string name, string root, string process) in ChromiumBrowsers())
        {
            rules.Add(new CleanupRule
            {
                Category = swCache,
                Roots = [$@"{root}\*\Service Worker\CacheStorage", $@"{root}\*\Service Worker\ScriptCache"],
                BlockingProcesses = [process],
                BlockingProgramName = name,
                LocationLabel = $"{name}'s service-worker cache",
            });
        }

        return rules;
    }

    private static IEnumerable<(string Name, string Root, string Process)> ChromiumBrowsers()
    {
        yield return ("Google Chrome", @"{LocalAppData}\Google\Chrome\User Data", "chrome");
        yield return ("Microsoft Edge", @"{LocalAppData}\Microsoft\Edge\User Data", "msedge");
        yield return ("Brave", @"{LocalAppData}\BraveSoftware\Brave-Browser\User Data", "brave");
        yield return ("Vivaldi", @"{LocalAppData}\Vivaldi\User Data", "vivaldi");
    }

    private static CategoryDefinition BrowserCategory(string id, string name)
        => Safe(
            "browser-" + id, name + " cache", Browsers,
            $"Web pages, images, scripts and GPU shaders {name} cached to load sites faster.",
            "It is a cache: the browser downloads anything it needs again. History, cookies, passwords, bookmarks, sessions and extensions are never touched.",
            "Websites load a little slower the first time you visit them again. The browser must be closed; items are locked while it runs.");

    private static void AddChromium(List<CleanupRule> rules, string id, string name, string userData, string process)
    {
        CategoryDefinition category = BrowserCategory(id, name);
        var roots = new List<string>();
        roots.AddRange(ChromiumProfileCaches.Select(c => $@"{userData}\*\{c}"));
        roots.AddRange(ChromiumSharedCaches.Select(c => $@"{userData}\{c}"));

        rules.Add(new CleanupRule
        {
            Category = category,
            Roots = roots,
            BlockingProcesses = [process],
            BlockingProgramName = name,
            LocationLabel = $"{name}'s cache",
        });
    }

    private static void AddAppCache(List<CleanupRule> rules, CategoryDefinition category, string name, string process, string root, string[]? extra = null)
    {
        var folders = new List<string> { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache" };
        if (extra is not null)
        {
            folders.AddRange(extra);
        }

        rules.Add(new CleanupRule
        {
            Category = category,
            Roots = folders.Select(f => $@"{root}\{f}").ToList(),
            BlockingProcesses = [process],
            BlockingProgramName = name,
            LocationLabel = $"{name}'s cache",
        });
    }

    /// <summary>LevelDB / ESE / SQLite folders keep write-ahead logs named *.log that are NOT logs.</summary>
    internal static bool LooksLikeDatabaseFolder(DirectoryInfo directory)
    {
        if (directory.Name.Equals("leveldb", StringComparison.OrdinalIgnoreCase)
            || directory.Name.Equals("IndexedDB", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            foreach (FileInfo file in directory.EnumerateFiles("*", Safety.SafeEnumeration.Lenient))
            {
                string n = file.Name;
                if (n.Equals("CURRENT", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("MANIFEST-", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".chk", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".jrs", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("edb.log", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".sqlite-wal", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return false;
    }

    /// <summary>Returns a reason when Windows Update has work pending and its cache must be left alone.</summary>
    internal static string? WindowsUpdatePendingReason()
    {
        try
        {
            using RegistryKey? wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu is not null)
            {
                return "Windows Update is waiting for a restart. Restart the PC first, then scan again.";
            }

            using RegistryKey? cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null)
            {
                return "Windows servicing is waiting for a restart. Restart the PC first, then scan again.";
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "The Windows Update state could not be verified, so its cache is left alone.";
        }

        return null;
    }
}
