using System.Text.RegularExpressions;
using SafeSweep.Core.Platform;

namespace SafeSweep.Core.Safety;

/// <summary>
/// Path normalisation and containment checks. Every comparison in the
/// protection layer goes through here so that "C:\Windows\.\System32",
/// "c:/windows/system32", "C:\WINDOWS\System32\" and "C:\PROGRA~1" all compare
/// as the locations they really are.
/// </summary>
public static partial class PathUtil
{
    private static readonly char[] ForbiddenChars = ['*', '?', '"', '<', '>', '|'];

    [GeneratedRegex(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePathRegex();

    /// <summary>
    /// Normalises a local, drive-letter path. Returns null (with a reason) for
    /// anything SafeSweep refuses to reason about: relative paths, UNC/network
    /// paths, device paths, wildcards and alternate data streams.
    /// </summary>
    public static string? TryNormalize(string? path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Empty path.";
            return null;
        }

        string p = path.Trim();

        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            error = "Device and extended-length paths are not accepted.";
            return null;
        }

        p = p.Replace('/', '\\');

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "Network (UNC) paths are never cleaned.";
            return null;
        }

        if (p.IndexOfAny(ForbiddenChars) >= 0 || p.Any(char.IsControl))
        {
            error = "Path contains wildcard or invalid characters.";
            return null;
        }

        if (!DrivePathRegex().IsMatch(p))
        {
            error = "Only fully qualified local drive paths are accepted.";
            return null;
        }

        if (p.IndexOf(':', 2) >= 0)
        {
            error = "Alternate data streams are not accepted.";
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Path could not be normalised: " + ex.Message;
            return null;
        }

        full = FileSystemInspector.ExpandShortNames(full);
        full = TrimTrailingSeparator(full);

        if (!DrivePathRegex().IsMatch(full + (full.Length == 2 ? "\\" : string.Empty)))
        {
            error = "Path normalised to an unexpected form.";
            return null;
        }

        // Upper-case drive letter for stable display and comparison.
        return char.ToUpperInvariant(full[0]) + full[1..];
    }

    public static string TrimTrailingSeparator(string path)
    {
        // Keep "C:\" intact; strip trailing separators from everything else.
        if (path.Length <= 3)
        {
            return path.Length == 2 ? path + "\\" : path;
        }

        return path.TrimEnd('\\');
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies beneath it.</summary>
    public static bool IsUnderOrEqual(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsStrictlyUnder(path, root);
    }

    /// <summary>True when <paramref name="path"/> lies beneath <paramref name="root"/> (segment-aware).</summary>
    public static bool IsStrictlyUnder(string path, string root)
    {
        string r = root.EndsWith('\\') ? root : root + "\\";
        return path.Length > r.Length && path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Number of segments below the drive root: "C:\" = 0, "C:\A" = 1, "C:\A\B" = 2.</summary>
    public static int Depth(string normalizedPath)
    {
        string rest = normalizedPath.Length > 3 ? normalizedPath[3..] : string.Empty;
        return rest.Length == 0 ? 0 : rest.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    public static string[] Segments(string normalizedPath)
        => normalizedPath.Length > 3
            ? normalizedPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)
            : [];

    public static string DriveRoot(string normalizedPath) => normalizedPath[..3];

    /// <summary>Simple wildcard match (* and ?) on a single file name, case-insensitive.</summary>
    public static bool WildcardMatch(string name, string pattern)
    {
        if (pattern == "*" || pattern == "*.*")
        {
            return true;
        }

        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);
    }
}
