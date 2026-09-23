namespace SafeSweep.Core.Safety;

/// <summary>A location that must never be deleted from (the entry itself and everything below it).</summary>
public sealed record ProtectedLocation(string Path, string Reason, string Group);

/// <summary>
/// A hole punched in a protected tree for built-in cleanup rules only
/// (e.g. C:\Windows\Temp inside C:\Windows). Holes never apply to user-review,
/// leftover or empty-folder deletions.
/// </summary>
public sealed record CleanupException(string Path, string Reason, bool ExactFileOnly = false);

/// <summary>
/// The full protection rule set. Built from <see cref="SystemPaths"/> plus the
/// installed-application inventory and the user's own exclusions. Pure data:
/// all decisions are made by <see cref="PathGuard"/>.
/// </summary>
public sealed class ProtectionPolicy
{
    private readonly List<ProtectedLocation> _protected = [];
    private readonly List<CleanupException> _exceptions = [];

    // Swapped atomically so a scan running on another thread always sees a
    // complete list, never one that is half-rebuilt.
    private volatile IReadOnlyList<ProtectedLocation> _combined = [];
    private volatile IReadOnlyList<ProtectedLocation> _installFolders = [];
    private volatile IReadOnlyList<string> _userExclusions = [];

    public ProtectionPolicy(SystemPaths paths)
    {
        Paths = paths;
        BuildStaticRules();
        _combined = _protected.ToList();
    }

    public SystemPaths Paths { get; }

    public IReadOnlyList<ProtectedLocation> ProtectedLocations => _combined;

    public IReadOnlyList<ProtectedLocation> StaticLocations => _protected;

    public IReadOnlyList<ProtectedLocation> InstalledApplicationFolders => _installFolders;

    public IReadOnlyList<CleanupException> CleanupExceptions => _exceptions;

    public IReadOnlyList<string> UserExclusions => _userExclusions;

    /// <summary>File names that are refused anywhere on disk, whatever the intent.</summary>
    public static readonly IReadOnlySet<string> CriticalFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "bootnxt", "ntldr", "boot.ini",
        "bootsect.bak", "ntdetect.com", "desktop.ini", "ntuser.ini", "ntuser.pol", "autoexec.bat", "config.sys",
        "iconcache.db", // the legacy root icon cache is in use by Explorer; the per-size caches are handled by a rule
    };

    /// <summary>Name patterns that are refused anywhere on disk (registry hives, transaction logs, secrets).</summary>
    public static readonly IReadOnlyList<string> CriticalNamePatterns =
    [
        "ntuser.dat*", "usrclass.dat*", "*.regtrans-ms", "*.blf",
        // Secrets and irreplaceable personal stores. Deleting any of these can mean
        // permanent loss of money, access or mail, so they are refused even when
        // they sit inside an otherwise removable folder.
        "wallet.dat", "*.wallet", "*.kdbx", "*.kdb", "*.keyx", "id_rsa*", "id_ed25519*", "id_ecdsa*", "id_dsa*",
        "*.pem", "*.pfx", "*.p12", "*.ppk", "*.ovpn", "*.gpg", "*.pgp", "*.pst", "*.ost", "*.mbox",
        "*.cer", "*.crt", "*.der", "*.key", "*.jks", "*.keystore", "*.p7b", "*.p7c", "*.snk", "*.1pux", "*.opvault",
        "known_hosts", "authorized_keys", "*.sys", "*.efi",
    ];

    /// <summary>
    /// First-level folders that are system or program locations on ANY drive:
    /// a second Windows installation, a recovery partition that has a letter,
    /// a Program Files on a data drive. Refused whatever the intent.
    /// </summary>
    public static readonly IReadOnlySet<string> SystemRootFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "Program Files (Arm)", "ProgramData", "Recovery", "Boot",
        "EFI", "System Volume Information", "$Recycle.Bin", "$WinREAgent", "$SysReset", "Config.Msi", "Windows.old",
        "$Windows.~BT", "$Windows.~WS", "$WINDOWS.~Q", "$GetCurrent", "WindowsApps", "XboxGames", "MSOCache",
        "PerfLogs", "Users", "Documents and Settings",
    };

    /// <summary>
    /// Folder names refused anywhere in a path: version-control metadata, game
    /// libraries whose files are managed by a launcher, and wallets/key stores.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedSegmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker",
        "steamapps", "SteamLibrary", "XboxGames", "WindowsApps", "ModifiableWindowsApps",
        "Epic Games", "EA Games", "Origin Games", "GOG Games", "Riot Games", "Ubisoft Game Launcher",
        "wallets", "keystore", "Bitcoin", "Electrum", "Exodus", "Ethereum", "Monero", "Litecoin", "Dogecoin",
        "atomic", "Ledger Live", "Armory", "System Volume Information", "$Recycle.Bin",
        ".SafeSweep-Quarantine", ".vs", ".idea", "Saved Games", "My Games", "Dropbox", ".dropbox.cache",
        "Google Drive", "My Drive", "iCloudDrive", "iCloud Drive",
    };

    /// <summary>
    /// Files and folders that mark a directory as a source-code project or repository.
    /// Such directories are never cleaned automatically, even inside a temp folder.
    /// </summary>
    public static readonly IReadOnlyList<string> ProjectMarkers =
    [
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode", "*.sln", "*.slnx", "*.csproj", "*.vbproj", "*.fsproj",
        "*.vcxproj", "package.json", "Cargo.toml", "go.mod", "pom.xml", "build.gradle", "build.gradle.kts",
        "pyproject.toml", "setup.py", "CMakeLists.txt", "composer.json", "Gemfile", "Directory.Build.props",
    ];

    /// <summary>
    /// Top-level AppData/ProgramData folder names that are shared by Windows or by
    /// many programs. They are never reported as leftovers of a single program.
    /// </summary>
    public static readonly IReadOnlySet<string> LeftoverNeverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Packages", "Programs", "Temp", "Comms", "ConnectedDevicesPlatform", "CrashDumps",
        "D3DSCache", "VirtualStore", "Publishers", "PeerDistRepub", "History", "Microsoft Help", "Application Data",
        "Temporary Internet Files", "Package Cache", "USOPrivate", "USOShared", "SoftwareDistribution", "ssh",
        "WindowsHolographicDevices", "Desktop", "Documents", "Start Menu", "Templates", "Favorites", "dbg",
        "SafeSweep", "regid.1991-06.com.microsoft", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "ATI",
        "Realtek", "Dell", "HP", "Hewlett-Packard", "Lenovo", "ASUS", "MSI", "Acer", "Samsung", "Apple",
        "Apple Computer", "Google", "Mozilla", "Adobe", "Oracle", "Sun", "Java", "Package Management",
        "Local", "LocalLow", "Roaming", "fontconfig", "Squirrel", "SquirrelTemp", "IsolatedStorage",
        "npm", "npm-cache", "pip", "NuGet", "Yarn", "Docker", "Docker Desktop", "Thunderbird", "Sticky Notes",
        // Developer tool chains keep runtimes and SDKs here without an uninstall entry.
        "uv", "pypoetry", "pnpm", "pnpm-cache", "pnpm-state", "Pub", "go-build", "Android", "JetBrains", "Python",
        "ms-playwright", "Cypress", "electron", "electron-builder", "node-gyp", "deno", "bun", "Volta", "nvm",
        "chocolatey", "scoop", "WinGet", "vcpkg", "Conda", "Anaconda", "miniconda3", ".NET", "dotnet", "Unity",
        // Created by Windows itself.
        "PlaceholderTileLogoFolder", "ElevatedDiagnostics", "Diagnostics", "TileDataLayer", "IconCache",
        "Microsoft_Corporation", "WindowsPowerShell", "PowerShell", "OneDrive", "Edge", "WinSAT",
    };

    /// <summary>
    /// Chromium/Edge profile entries that hold logins, passwords, history,
    /// extensions or site data. Only the cache folders next to them are cleaned.
    /// "Local State" holds the key that decrypts saved passwords and cookies.
    /// </summary>
    public static readonly IReadOnlySet<string> BrowserProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Local State", "Cookies", "Network", "Login Data", "Login Data For Account", "History", "Bookmarks",
        "Favicons", "Top Sites", "Web Data", "Preferences", "Secure Preferences", "Local Storage", "Session Storage",
        "IndexedDB", "WebStorage", "Sessions", "Sync Data", "Sync Extension Settings", "Local Extension Settings",
        "Extensions", "Extension State", "Extension Rules", "Extension Scripts", "Affiliation Database",
        "Web Applications", "Safe Browsing", "shared_proto_db", "Storage", "EdgeWallet", "EdgeSessions",
        "Workspaces", "Continuous Migration", "databases", "File System", "blob_storage", "leveldb",
        // Firefox profile files.
        "places.sqlite", "cookies.sqlite", "logins.json", "key4.db", "cert9.db", "formhistory.sqlite",
        "sessionstore.jsonlz4", "sessionstore-backups", "storage", "prefs.js", "extensions",
    };

    private void AddProtected(string path, string reason, string group)
    {
        string? normalized = PathUtil.TryNormalize(path, out _);
        if (normalized is null)
        {
            return;
        }

        if (_protected.Any(p => p.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _protected.Add(new ProtectedLocation(normalized, reason, group));
    }

    public void AddCleanupException(string path, string reason, bool exactFileOnly = false)
    {
        string? normalized = PathUtil.TryNormalize(path, out _);
        if (normalized is null)
        {
            return;
        }

        _exceptions.Add(new CleanupException(normalized, reason, exactFileOnly));
    }

    public void SetUserExclusions(IEnumerable<string> paths)
    {
        var list = new List<string>();
        foreach (string path in paths)
        {
            string? normalized = PathUtil.TryNormalize(Environment.ExpandEnvironmentVariables(path), out _);
            if (normalized is not null && !list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(normalized);
            }
        }

        _userExclusions = list;
    }

    /// <summary>
    /// Protects the install folder of every installed program (replacing the
    /// previous set). Bogus registry values (a drive root, the profile itself)
    /// are ignored rather than protecting the whole disk, which would silently
    /// disable user review.
    /// </summary>
    public void SetInstalledApplicationFolders(IEnumerable<(string Path, string Name)> installs)
    {
        var list = new List<ProtectedLocation>();
        foreach ((string path, string name) in installs)
        {
            string? normalized = PathUtil.TryNormalize(path, out _);
            if (normalized is null || PathUtil.Depth(normalized) < 2 || IsBroadContainer(normalized))
            {
                continue;
            }

            if (list.Any(l => l.Path.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            list.Add(new ProtectedLocation(normalized, $"Installation folder of \"{name}\", which is still installed.", "Installed applications"));
        }

        _installFolders = list;
        _combined = _protected.Concat(list).ToList();
    }

    private bool IsBroadContainer(string normalized)
    {
        string?[] broad =
        [
            Paths.UserProfile, Paths.LocalAppData, Paths.RoamingAppData, Paths.LocalAppDataLow, Paths.ProgramData,
            Paths.ProfilesRoot, Paths.ProgramFiles, Paths.ProgramFilesX86, Paths.WindowsDir,
            Path.Combine(Paths.LocalAppData, "Programs"), Path.Combine(Paths.UserProfile, "AppData"),
        ];

        foreach (string? b in broad.Concat(Paths.PersonalFolders))
        {
            if (b is null)
            {
                continue;
            }

            string? nb = PathUtil.TryNormalize(b, out _);
            if (nb is not null && nb.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void BuildStaticRules()
    {
        SystemPaths p = Paths;
        string win = p.WindowsDir;
        string sys = p.SystemDrive;

        const string gWindows = "Windows system";
        const string gBoot = "Boot and recovery";
        const string gApps = "Installed applications";
        const string gProfile = "User profile and settings";
        const string gData = "Personal files";
        const string gSelf = "SafeSweep";

        // --- Windows ------------------------------------------------------------
        AddProtected(win, "Windows system directory. Only the specific temporary and log folders listed as exceptions may be cleaned.", gWindows);
        AddProtected(Path.Combine(win, "System32"), "Core Windows binaries, drivers and the registry.", gWindows);
        AddProtected(Path.Combine(win, "SysWOW64"), "32-bit Windows system binaries.", gWindows);
        AddProtected(Path.Combine(win, "WinSxS"), "Component store. Mostly hard links into System32; only DISM may modify it.", gWindows);
        AddProtected(Path.Combine(win, "Installer"), "Windows Installer cache. Needed to uninstall, repair and patch every MSI-installed program. Nothing regenerates it.", gWindows);
        AddProtected(Path.Combine(win, "Prefetch"), "Prefetch speeds up application launch; clearing it makes the PC feel slower for days.", gWindows);
        AddProtected(Path.Combine(win, "System32", "config"), "Registry hives.", gWindows);
        AddProtected(Path.Combine(win, "System32", "drivers"), "Device drivers.", gWindows);
        AddProtected(Path.Combine(win, "System32", "DriverStore"), "Driver store used to install and repair drivers.", gWindows);
        AddProtected(Path.Combine(win, "System32", "LogFiles"), "System log files used by Windows components.", gWindows);
        AddProtected(Path.Combine(win, "System32", "Tasks"), "Scheduled task definitions.", gWindows);
        AddProtected(Path.Combine(win, "Boot"), "Boot environment files.", gBoot);
        AddProtected(Path.Combine(win, "Fonts"), "Installed fonts.", gWindows);
        AddProtected(Path.Combine(win, "servicing"), "Windows servicing stack.", gWindows);
        AddProtected(Path.Combine(win, "assembly"), "Global assembly cache.", gWindows);
        AddProtected(Path.Combine(win, "Microsoft.NET"), ".NET Framework runtime.", gWindows);
        AddProtected(Path.Combine(win, "INF"), "Driver installation information.", gWindows);
        AddProtected(Path.Combine(win, "SystemApps"), "Built-in Windows apps.", gWindows);
        AddProtected(Path.Combine(win, "Panther"), "Windows setup and upgrade rollback data.", gBoot);
        AddProtected(Path.Combine(win, "SoftwareDistribution", "DataStore"), "Windows Update database. Deleting its transaction logs corrupts it.", gWindows);
        AddProtected(Path.Combine(win, "SoftwareDistribution", "SLS"), "Windows Update service data.", gWindows);
        AddProtected(Path.Combine(win, "Logs", "PITR"), "Point-in-time restore logs.", gBoot);
        AddProtected(Path.Combine(win, "Logs", "SystemRestore"), "System Restore logs.", gBoot);
        AddProtected(Path.Combine(win, "Logs", "WinREAgent"), "Recovery environment update logs.", gBoot);

        // Holes in the Windows tree that built-in rules may clean.
        AddCleanupException(Path.Combine(win, "Temp"), "System temporary files.");
        AddCleanupException(Path.Combine(win, "SoftwareDistribution", "Download"), "Windows Update download cache (re-downloaded if needed).");
        AddCleanupException(Path.Combine(win, "Minidump"), "Blue-screen minidumps.");
        AddCleanupException(Path.Combine(win, "LiveKernelReports"), "Live kernel diagnostic dumps.");
        AddCleanupException(Path.Combine(win, "MEMORY.DMP"), "Full kernel memory dump.", exactFileOnly: true);
        foreach (string log in new[] { "CBS", "DISM", "WindowsUpdate", "waasmedic", "SIH", "NetSetup", "MoSetup", "DPX", "SetupCln" })
        {
            AddCleanupException(Path.Combine(win, "Logs", log), "Windows diagnostic logs.");
        }

        // --- Boot / recovery / system drive root ---------------------------------
        foreach (string name in new[] { "Boot", "EFI", "Recovery", "System Volume Information", "$Recycle.Bin", "$WinREAgent", "$SysReset", "Config.Msi", "MSOCache", "PerfLogs" })
        {
            AddProtected(Path.Combine(sys, name), "Boot, recovery or system-managed data on the system drive.", gBoot);
        }

        foreach (string name in new[] { "Windows.old", "$Windows.~BT", "$Windows.~WS", "$WINDOWS.~Q", "$GetCurrent", "ESD" })
        {
            AddProtected(Path.Combine(sys, name), "Previous Windows installation / upgrade files. Remove them only with Windows' own Disk Cleanup or Storage settings (see Windows Tools).", gBoot);
        }

        // --- Applications ----------------------------------------------------------
        AddProtected(p.ProgramFiles, "Installed applications.", gApps);
        if (p.ProgramFilesX86 is not null)
        {
            AddProtected(p.ProgramFilesX86, "Installed 32-bit applications.", gApps);
        }

        AddProtected(Path.Combine(sys, "Program Files (Arm)"), "Installed ARM applications.", gApps);
        AddProtected(Path.Combine(p.ProgramFiles, "WindowsApps"), "Microsoft Store application packages.", gApps);
        AddProtected(Path.Combine(p.ProgramData, "Package Cache"), "Installer bootstrapper cache (Visual Studio, .NET, VC++ runtimes). Needed for repair and uninstall.", gApps);
        AddProtected(Path.Combine(p.ProgramData, "Packages"), "Microsoft Store package data for all users.", gApps);
        AddProtected(Path.Combine(p.ProgramData, "Microsoft"), "Windows and Microsoft component data (Defender, Crypto, Start menu, Search...).", gWindows);
        AddProtected(Path.Combine(p.LocalAppData, "Programs"), "Applications installed for the current user.", gApps);
        AddProtected(Path.Combine(p.LocalAppData, "Microsoft", "WindowsApps"), "App execution aliases (python.exe, winget.exe...).", gApps);
        AddProtected(Path.Combine(p.LocalAppData, "Microsoft", "OneDrive"), "OneDrive application.", gApps);

        AddCleanupException(Path.Combine(p.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"), "Archived error reports.");
        AddCleanupException(Path.Combine(p.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"), "Queued error reports.");
        AddCleanupException(Path.Combine(p.ProgramData, "Microsoft", "Windows", "WER", "Temp"), "Error reporting temporary files.");

        // --- Other accounts ----------------------------------------------------------
        foreach (string name in new[] { "Default", "Default User", "Public", "All Users" })
        {
            AddProtected(Path.Combine(p.ProfilesRoot, name), "Shared profile or the template for new accounts.", gProfile);
        }

        // --- Current profile: settings, credentials, keys ------------------------------
        AddProtected(Path.Combine(p.RoamingAppData, "Microsoft", "Protect"), "DPAPI master keys. Without them saved passwords and certificates become unreadable.", gProfile);
        AddProtected(Path.Combine(p.RoamingAppData, "Microsoft", "Crypto"), "Cryptographic keys.", gProfile);
        AddProtected(Path.Combine(p.RoamingAppData, "Microsoft", "SystemCertificates"), "Personal certificates.", gProfile);
        AddProtected(Path.Combine(p.RoamingAppData, "Microsoft", "Credentials"), "Saved Windows credentials.", gProfile);
        AddProtected(Path.Combine(p.LocalAppData, "Microsoft", "Credentials"), "Saved Windows credentials.", gProfile);
        AddProtected(Path.Combine(p.LocalAppData, "Microsoft", "Vault"), "Windows credential vault.", gProfile);
        AddProtected(Path.Combine(p.RoamingAppData, "Microsoft", "Windows", "Start Menu"), "Start menu shortcuts.", gProfile);
        AddProtected(Path.Combine(p.LocalAppData, "Microsoft", "Windows", "INetCookies"), "Cookies (logged-in sessions). Its sibling INetCache is a cache; this is not.", gProfile);
        AddProtected(Path.Combine(p.LocalAppData, "Microsoft", "Windows", "WebCache"), "Database holding history for Windows web components.", gProfile);
        AddProtected(Path.Combine(p.LocalAppData, "Microsoft", "Windows", "UsrClass.dat"), "Per-user registry hive.", gProfile);

        // --- Personal content ------------------------------------------------------------
        foreach (string folder in p.PersonalFolders)
        {
            // Registered as protected for automatic cleaning; the guard lets the
            // user-review intent through here because the user picks each file.
            AddProtected(folder, "Personal files. Never cleaned automatically; only files you pick yourself in a review tool.", gData);
        }

        // --- Cloud-synced folders ------------------------------------------------------------
        foreach (string cloud in p.CloudRoots)
        {
            AddProtected(cloud, "Synced with cloud storage: deleting here would also delete the cloud copy.", "Cloud storage");
        }

        // --- SafeSweep's own data and program ----------------------------------------------
        AddProtected(p.AppDataRoot, "SafeSweep settings, logs, history and quarantine.", gSelf);
        AddApplicationLocation(p.InstallDirectory, p.ExecutablePath, gSelf);
    }

    /// <summary>
    /// Protects SafeSweep's own program. A portable copy is often run straight
    /// from Downloads or the Desktop; protecting that whole folder would hide the
    /// user's other files from review, so in that case only the executable itself
    /// is protected.
    /// </summary>
    private void AddApplicationLocation(string? installDirectory, string? executablePath, string group)
    {
        string? dir = installDirectory is null ? null : PathUtil.TryNormalize(installDirectory, out _);
        if (dir is not null && PathUtil.Depth(dir) >= 2 && !IsBroadContainer(dir))
        {
            AddProtected(dir, "SafeSweep's own program files.", group);
        }

        if (executablePath is not null)
        {
            AddProtected(executablePath, "The running SafeSweep program.", group);
        }
    }
}
