using System.Diagnostics;

namespace SafeSweep.Core.Platform;

public static class ProcessSnapshot
{
    /// <summary>Lower-case names of every running process (without .exe).</summary>
    public static IReadOnlySet<string> RunningNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                set.Add(process.ProcessName.ToLowerInvariant());
            }
            catch (InvalidOperationException)
            {
                // Exited while enumerating.
            }
            finally
            {
                process.Dispose();
            }
        }

        return set;
    }
}
