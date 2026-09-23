using System.Text.Json;
using System.Text.Json.Serialization;
using SafeSweep.Core.Logging;

namespace SafeSweep.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Days a quarantined item is kept before it is purged automatically. 0 = never purge.</summary>
    public int QuarantineRetentionDays { get; set; } = 30;

    /// <summary>Send review items to the Recycle Bin instead of SafeSweep's quarantine (when the bin can hold them).</summary>
    public bool UseRecycleBinForReview { get; set; }

    /// <summary>Preview mode: cleaning is simulated and nothing is touched.</summary>
    public bool SimulationMode { get; set; }

    /// <summary>Temporary files younger than this are left alone (installers may still be using them).</summary>
    public int TempMinAgeHours { get; set; } = 24;

    public int LargeFileThresholdMb { get; set; } = 500;

    public int OldFileDays { get; set; } = 365;

    public int OldInstallerDays { get; set; } = 90;

    public int DuplicateMinSizeKb { get; set; } = 1024;

    /// <summary>Leftover folders modified more recently than this are not reported.</summary>
    public int LeftoverMinIdleDays { get; set; } = 90;

    /// <summary>Folders and files the user never wants touched. Environment variables are expanded.</summary>
    public List<string> Exclusions { get; set; } =
    [
        // Working files of AI coding-agent sessions: a %TEMP% wipe would delete the
        // scratch space of a session that is still running.
        @"%LOCALAPPDATA%\Temp\claude",
    ];

    /// <summary>Folders scanned by the review tools. Empty = Downloads, Documents, Desktop, Videos, Pictures, Music.</summary>
    public List<string> ReviewRoots { get; set; } = [];

    /// <summary>Category ids cleaned by the scheduled task. Empty = every Safe category that needs no admin rights.</summary>
    public List<string> ScheduledCategoryIds { get; set; } = [];

    public bool ScheduledSkipOnBattery { get; set; } = true;

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    public void Normalize()
    {
        QuarantineRetentionDays = Math.Clamp(QuarantineRetentionDays, 0, 3650);
        TempMinAgeHours = Math.Clamp(TempMinAgeHours, 1, 24 * 90);
        LargeFileThresholdMb = Math.Clamp(LargeFileThresholdMb, 10, 1024 * 1024);
        OldFileDays = Math.Clamp(OldFileDays, 30, 3650 * 3);
        OldInstallerDays = Math.Clamp(OldInstallerDays, 7, 3650);
        DuplicateMinSizeKb = Math.Clamp(DuplicateMinSizeKb, 1, 1024 * 1024);
        LeftoverMinIdleDays = Math.Clamp(LeftoverMinIdleDays, 7, 3650);
        Exclusions ??= [];
        ReviewRoots ??= [];
        ScheduledCategoryIds ??= [];
        Theme = Theme is "Light" or "Dark" ? Theme : "System";
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Settings could not be read ({ex.Message}); defaults are used.");
        }

        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(settings, Json));
    }
}

/// <summary>Write-to-temp-then-rename so a crash never leaves a half-written file.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
