using System.Diagnostics;
using Microsoft.Win32;
using SafeSweep.Core.Apps;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;

namespace SafeSweep.Core.Startup;

public enum StartupSource
{
    RegistryUser,
    RegistryMachine,
    RegistryMachine32,
    StartupFolderUser,
    StartupFolderCommon,
}

/// <summary>One program that starts with Windows.</summary>
public sealed class StartupEntry : ObservableBase
{
    private bool _isEnabled;

    public required string Name { get; init; }

    public required string Command { get; init; }

    public required StartupSource Source { get; init; }

    public string? ExecutablePath { get; init; }

    public string? Publisher { get; init; }

    public bool FileExists { get; init; }

    public bool RequiresAdmin => Source is StartupSource.RegistryMachine or StartupSource.RegistryMachine32 or StartupSource.StartupFolderCommon;

    public string SourceDisplay => Source switch
    {
        StartupSource.RegistryUser => "Registry (current user)",
        StartupSource.RegistryMachine => "Registry (all users)",
        StartupSource.RegistryMachine32 => "Registry (all users, 32-bit)",
        StartupSource.StartupFolderUser => "Startup folder (current user)",
        _ => "Startup folder (all users)",
    };

    public string StatusDisplay => !FileExists ? "Program missing" : IsEnabled ? "Enabled" : "Disabled";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    /// <summary>Re-announces <see cref="IsEnabled"/> so a toggle the user flipped snaps back after a refused change.</summary>
    public void RefreshState() => OnPropertyChanged(nameof(IsEnabled));

    /// <summary>Registry value name or startup-folder file name used in StartupApproved.</summary>
    public required string ApprovalName { get; init; }
}

/// <summary>
/// Lists and enables/disables startup programs using the same mechanism as
/// Task Manager (the StartupApproved registry keys). Nothing is ever deleted:
/// disabling is fully reversible.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static IReadOnlyList<StartupEntry> Load()
    {
        var entries = new List<StartupEntry>();
        ReadRegistry(entries, Registry.CurrentUser, RunKey, StartupSource.RegistryUser, "Run");
        ReadRegistry(entries, Registry.LocalMachine, RunKey, StartupSource.RegistryMachine, "Run");
        ReadRegistry(entries, Registry.LocalMachine, Run32Key, StartupSource.RegistryMachine32, "Run32");
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.StartupFolderUser);
        ReadFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.StartupFolderCommon);
        return entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool SetEnabled(StartupEntry entry, bool enabled, out string? error)
    {
        (RegistryKey hive, string subKey) = ApprovalLocation(entry.Source);
        try
        {
            using RegistryKey key = hive.CreateSubKey(subKey, writable: true);
            byte[] value = new byte[12];
            if (enabled)
            {
                value[0] = 0x02;
            }
            else
            {
                value[0] = 0x03;
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
            }

            key.SetValue(entry.ApprovalName, value, RegistryValueKind.Binary);
            entry.IsEnabled = enabled;
            AppLog.Audit(enabled ? "STARTUP-ENABLED" : "STARTUP-DISABLED", entry.Name, entry.Command);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = entry.RequiresAdmin
                ? "This entry applies to all users; changing it requires administrator rights."
                : ex.Message;
            AppLog.Warn($"Startup entry {entry.Name} could not be changed: {ex.Message}");
            return false;
        }
    }

    public static void OpenLocation(StartupEntry entry)
    {
        if (entry.ExecutablePath is not null && File.Exists(entry.ExecutablePath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.ExecutablePath}\"") { UseShellExecute = true });
        }
    }

    private static (RegistryKey Hive, string SubKey) ApprovalLocation(StartupSource source) => source switch
    {
        StartupSource.RegistryUser => (Registry.CurrentUser, ApprovedRoot + @"\Run"),
        StartupSource.RegistryMachine => (Registry.LocalMachine, ApprovedRoot + @"\Run"),
        StartupSource.RegistryMachine32 => (Registry.LocalMachine, ApprovedRoot + @"\Run32"),
        StartupSource.StartupFolderUser => (Registry.CurrentUser, ApprovedRoot + @"\StartupFolder"),
        _ => (Registry.LocalMachine, ApprovedRoot + @"\StartupFolder"),
    };

    private static void ReadRegistry(List<StartupEntry> entries, RegistryKey hive, string path, StartupSource source, string approvedSub)
    {
        try
        {
            using RegistryKey? run = hive.OpenSubKey(path);
            if (run is null)
            {
                return;
            }

            using RegistryKey? approved = hive.OpenSubKey($@"{ApprovedRoot}\{approvedSub}");
            foreach (string name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name) || run.GetValue(name) is not string command)
                {
                    continue;
                }

                string? exe = CommandLineParser.ExtractPath(command);
                entries.Add(new StartupEntry
                {
                    Name = name,
                    Command = command,
                    Source = source,
                    ExecutablePath = exe,
                    Publisher = Publisher(exe),
                    FileExists = exe is not null && File.Exists(exe),
                    IsEnabled = IsApproved(approved, name),
                    ApprovalName = name,
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            AppLog.Warn($"Startup entries in {path} could not be read: {ex.Message}");
        }
    }

    private static void ReadFolder(List<StartupEntry> entries, string folder, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        RegistryKey hive = source == StartupSource.StartupFolderUser ? Registry.CurrentUser : Registry.LocalMachine;
        using RegistryKey? approved = hive.OpenSubKey($@"{ApprovedRoot}\StartupFolder");

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(new StartupEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Command = file,
                Source = source,
                ExecutablePath = file,
                Publisher = Publisher(file),
                FileExists = true,
                IsEnabled = IsApproved(approved, fileName),
                ApprovalName = fileName,
            });
        }
    }

    /// <summary>Absent or even first byte = enabled; odd first byte = disabled.</summary>
    private static bool IsApproved(RegistryKey? approved, string name)
    {
        if (approved?.GetValue(name) is byte[] { Length: > 0 } data)
        {
            return (data[0] & 1) == 0;
        }

        return true;
    }

    private static string? Publisher(string? exe)
    {
        if (exe is null || !File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(exe).CompanyName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }
}
