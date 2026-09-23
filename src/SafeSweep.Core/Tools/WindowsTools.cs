using System.Diagnostics;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;
using SafeSweep.Core.Safety;

namespace SafeSweep.Core.Tools;

public sealed record SystemLeftover(string Path, string Name, string Description, long? SizeBytes)
{
    public string SizeDisplay => SizeBytes is long s ? Formatting.Bytes(s) : "not measured";
}

/// <summary>
/// Some of the largest Windows leftovers (previous installations, the
/// component store, Delivery Optimization) must only be cleaned by Windows'
/// own tools. SafeSweep reports them and launches the official tool; it never
/// deletes them itself.
/// </summary>
public static class WindowsTools
{
    public static IReadOnlyList<SystemLeftover> FindUpgradeLeftovers(SystemPaths paths, CancellationToken cancellationToken)
    {
        var list = new List<SystemLeftover>();
        var candidates = new (string Name, string Description)[]
        {
            ("Windows.old", "Your previous Windows installation, kept for rollback after an upgrade (normally removed automatically after about 10 days)."),
            ("$Windows.~BT", "Temporary Windows upgrade files."),
            ("$Windows.~WS", "Temporary Windows upgrade files (media creation)."),
            ("$WINDOWS.~Q", "Temporary Windows upgrade files."),
            ("$GetCurrent", "Temporary files from a Windows feature update."),
            ("ESD", "Windows installation files downloaded by Windows Update or the upgrade assistant."),
        };

        foreach ((string name, string description) in candidates)
        {
            string path = System.IO.Path.Combine(paths.SystemDrive, name);
            if (!Directory.Exists(path))
            {
                continue;
            }

            list.Add(new SystemLeftover(path, name, description, MeasureReadable(path, cancellationToken)));
        }

        return list;
    }

    /// <summary>Opens Windows Settings > System > Storage (Temporary files, Cleanup recommendations).</summary>
    public static void OpenStorageSettings() => Launch("ms-settings:storagesense", useShell: true, "Storage settings");

    /// <summary>Classic Disk Cleanup in system-files mode (it asks for elevation itself).</summary>
    public static void OpenDiskCleanup() => Launch("cleanmgr.exe", useShell: true, "Disk Cleanup", "/d " + (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:"));

    /// <summary>
    /// Component store cleanup: DISM /Online /Cleanup-Image /StartComponentCleanup.
    /// Removes superseded component versions the Microsoft-supported way.
    /// </summary>
    public static void RunComponentCleanup()
        => LaunchElevatedConsole("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", "Component store cleanup (DISM)");

    /// <summary>Delivery Optimization cache through its official PowerShell cmdlet.</summary>
    public static void ClearDeliveryOptimizationCache()
        => LaunchElevatedConsole("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"Delete-DeliveryOptimizationCache -Force; Write-Host 'Done. Press Enter to close.'; Read-Host\"", "Delivery Optimization cache cleanup");

    /// <summary>Analyses the component store without changing anything.</summary>
    public static void AnalyzeComponentStore()
        => LaunchElevatedConsole("cmd.exe", "/k dism.exe /Online /Cleanup-Image /AnalyzeComponentStore", "Component store analysis (DISM)");

    private static void LaunchElevatedConsole(string file, string arguments, string label)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true, Verb = "runas" });
            AppLog.Audit("TOOL-LAUNCHED", label, $"{file} {arguments}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppLog.Info($"{label} was not started: {ex.Message}");
            throw new InvalidOperationException($"{label} was not started (administrator approval is required).", ex);
        }
    }

    private static void Launch(string file, bool useShell, string label, string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = useShell });
            AppLog.Info($"Opened {label}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{label} could not be opened: {ex.Message}", ex);
        }
    }

    private static long? MeasureReadable(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        bool any = false;
        foreach (FileInfo file in SafeEnumeration.EnumerateFiles(path, true, null, null, cancellationToken))
        {
            try
            {
                total += file.Length;
                any = true;
            }
            catch (IOException)
            {
            }
        }

        return any ? total : null;
    }
}
