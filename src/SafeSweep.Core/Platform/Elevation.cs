using System.Diagnostics;
using System.Security.Principal;

namespace SafeSweep.Core.Platform;

/// <summary>
/// SafeSweep runs with normal rights and asks for elevation only when the user
/// chooses a feature that needs it (system temp, Windows Update cache, system
/// dumps and logs, all-users startup entries).
/// </summary>
public static class Elevation
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsElevated => Elevated.Value;

    /// <summary>Starts a new elevated instance. Returns false if the user declined the UAC prompt.</summary>
    public static bool TryRestartElevated(string arguments = "")
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED: the user said no.
            return false;
        }
    }
}
