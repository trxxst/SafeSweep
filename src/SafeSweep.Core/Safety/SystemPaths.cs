using SafeSweep.Core.Platform;

namespace SafeSweep.Core.Safety;

/// <summary>
/// The machine-specific locations the protection policy is built from.
/// Resolved once from the running system; tests construct their own instance
/// pointing into a sandbox.
/// </summary>
public sealed record SystemPaths
{
    public required string SystemDrive { get; init; }

    public required string WindowsDir { get; init; }

    public required string ProgramFiles { get; init; }

    public string? ProgramFilesX86 { get; init; }

    public required string ProgramData { get; init; }

    public required string UserProfile { get; init; }

    public required string ProfilesRoot { get; init; }

    public required string LocalAppData { get; init; }

    public required string RoamingAppData { get; init; }

    public string? LocalAppDataLow { get; init; }

    public required string UserTemp { get; init; }

    /// <summary>SafeSweep's own data directory (settings, logs, quarantine).</summary>
    public required string AppDataRoot { get; init; }

    /// <summary>Desktop, Documents, Pictures, Downloads... (including OneDrive redirections).</summary>
    public IReadOnlyList<string> PersonalFolders { get; init; } = [];

    /// <summary>The Downloads folder, where the old-installer scan looks.</summary>
    public string? Downloads { get; init; }

    /// <summary>Folders the review tools (large, duplicate and old files) scan when the user has not chosen their own.</summary>
    public IReadOnlyList<string> DefaultReviewRoots { get; init; } = [];

    /// <summary>Cloud-synced roots (OneDrive, Dropbox, Google Drive, iCloud, Box). Removing a synced file removes the cloud copy too.</summary>
    public IReadOnlyList<string> CloudRoots { get; init; } = [];

    /// <summary>Folder SafeSweep itself runs from (never cleaned).</summary>
    public string? InstallDirectory { get; init; }

    /// <summary>The running SafeSweep executable (never cleaned, even when it sits in Downloads).</summary>
    public string? ExecutablePath { get; init; }

    public static SystemPaths FromEnvironment()
    {
        string windows = KnownFolders.Get(Environment.SpecialFolder.Windows) ?? @"C:\Windows";
        string systemDrive = Path.GetPathRoot(windows) ?? @"C:\";
        string profile = KnownFolders.Get(Environment.SpecialFolder.UserProfile)
            ?? throw new InvalidOperationException("The user profile folder could not be resolved.");
        string local = KnownFolders.Get(Environment.SpecialFolder.LocalApplicationData)
            ?? Path.Combine(profile, "AppData", "Local");

        return new SystemPaths
        {
            SystemDrive = systemDrive,
            WindowsDir = windows,
            ProgramFiles = KnownFolders.Get(Environment.SpecialFolder.ProgramFiles) ?? Path.Combine(systemDrive, "Program Files"),
            ProgramFilesX86 = KnownFolders.Get(Environment.SpecialFolder.ProgramFilesX86),
            ProgramData = KnownFolders.Get(Environment.SpecialFolder.CommonApplicationData) ?? Path.Combine(systemDrive, "ProgramData"),
            UserProfile = profile,
            ProfilesRoot = Path.GetDirectoryName(profile) ?? Path.Combine(systemDrive, "Users"),
            LocalAppData = local,
            RoamingAppData = KnownFolders.Get(Environment.SpecialFolder.ApplicationData) ?? Path.Combine(profile, "AppData", "Roaming"),
            LocalAppDataLow = KnownFolders.Get(KnownFolders.LocalAppDataLow),
            UserTemp = Path.GetTempPath(),
            AppDataRoot = Path.Combine(local, "SafeSweep"),
            PersonalFolders = KnownFolders.PersonalContentFolders(),
            Downloads = KnownFolders.Get(KnownFolders.Downloads),
            DefaultReviewRoots = KnownFolders.DefaultReviewRoots(),
            CloudRoots = DetectCloudRoots(profile, local, KnownFolders.Get(Environment.SpecialFolder.ApplicationData)),
            InstallDirectory = AppContext.BaseDirectory,
            ExecutablePath = Environment.ProcessPath,
        };
    }

    /// <summary>
    /// Folders kept in sync with cloud storage. Deleting a file there deletes it
    /// from the cloud as well, so SafeSweep treats them as off limits.
    /// </summary>
    internal static IReadOnlyList<string> DetectCloudRoots(string profile, string localAppData, string? roamingAppData)
    {
        var roots = new List<string?>
        {
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
            KnownFolders.Get(KnownFolders.OneDrive),
            Path.Combine(profile, "OneDrive"),
            Path.Combine(profile, "Dropbox"),
            Path.Combine(profile, "Google Drive"),
            Path.Combine(profile, "My Drive"),
            Path.Combine(profile, "iCloudDrive"),
            Path.Combine(profile, "iCloud Drive"),
            Path.Combine(profile, "Box"),
            Path.Combine(profile, "pCloud Drive"),
            Path.Combine(profile, "MEGA"),
        };

        // Dropbox records its real folder (it can live on any drive) in info.json.
        foreach (string? baseDir in new[] { localAppData, roamingAppData })
        {
            if (baseDir is null)
            {
                continue;
            }

            string info = Path.Combine(baseDir, "Dropbox", "info.json");
            try
            {
                if (File.Exists(info))
                {
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(info), "\"path\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                    {
                        roots.Add(System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unreadable: the default location above still applies.
            }
        }

        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
