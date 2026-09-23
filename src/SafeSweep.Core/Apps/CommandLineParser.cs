namespace SafeSweep.Core.Apps;

/// <summary>Extracts the executable (or DLL) path from registry command strings.</summary>
public static class CommandLineParser
{
    private static readonly string[] Extensions = [".exe", ".dll", ".bat", ".cmd", ".com", ".msc", ".vbs", ".ps1", ".lnk"];

    public static string? ExtractPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string c = Environment.ExpandEnvironmentVariables(command.Trim());

        // Icon references look like "C:\app\app.exe,0".
        string? candidate;
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            candidate = end > 1 ? c[1..end] : c.Trim('"');
        }
        else
        {
            candidate = null;
            foreach (string ext in Extensions)
            {
                int index = c.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (index > 0)
                {
                    int stop = index + ext.Length;
                    if (stop == c.Length || c[stop] is ' ' or ',' or '"' or '/')
                    {
                        candidate = c[..stop];
                        break;
                    }
                }
            }

            candidate ??= c.Split(' ', 2)[0];
        }

        candidate = candidate.Split(',')[0].Trim();

        // rundll32 / msiexec: the interesting path is the argument.
        string leaf = Path.GetFileName(candidate);
        if (leaf.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            string rest = c[(c.IndexOf(leaf, StringComparison.OrdinalIgnoreCase) + leaf.Length)..].Trim().Trim('"');
            return ExtractPath(rest.Split(',')[0]);
        }

        if (candidate.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            candidate = candidate[4..];
        }

        if (candidate.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), candidate[12..]);
        }

        return candidate.Length >= 3 && candidate[1] == ':' ? candidate : null;
    }
}
