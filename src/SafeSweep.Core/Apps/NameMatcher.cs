using System.Text;
using System.Text.RegularExpressions;

namespace SafeSweep.Core.Apps;

/// <summary>
/// Fuzzy name comparison between folder names and installed-program names.
/// Deliberately generous: a false match only means a leftover is NOT reported,
/// while a missed match would mean offering a live program's data for removal.
/// </summary>
public static partial class NameMatcher
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "llc", "ltd", "limited", "corp", "corporation", "co", "company", "gmbh", "ag", "sa", "srl", "bv",
        "software", "softwares", "technologies", "technology", "tech", "the", "and", "for", "of", "app", "apps",
        "application", "applications", "x64", "x86", "amd64", "arm64", "win", "win32", "win64", "windows", "setup",
        "installer", "update", "updater", "version", "edition", "free", "pro", "data", "files", "program", "programs",
        "systems", "system", "studio", "studios", "games", "game", "entertainment", "interactive", "digital",
        "media", "labs", "group", "international", "local", "user", "users", "cache", "config", "settings",
        "client", "desktop", "tools", "tool", "service", "services", "launcher", "helper", "plugin", "plugins",
    };

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])|[^A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenSplit();

    [GeneratedRegex(@"[\d.\s_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?(_is1)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GuidLike();

    /// <summary>Lower-case letters and digits only, with a trailing version stripped.</summary>
    public static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = TrailingVersion().Replace(value.Trim(), string.Empty);
        var sb = new StringBuilder(trimmed.Length);
        foreach (char ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    /// <summary>Significant tokens (length 4+, not stop words), lower-cased.</summary>
    public static IEnumerable<string> Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (string raw in TokenSplit().Split(value))
        {
            string token = raw.Trim().ToLowerInvariant();
            if (token.Length >= 4 && !StopWords.Contains(token) && !token.All(char.IsDigit))
            {
                yield return token;
            }
        }
    }

    [GeneratedRegex(@"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex ContainsGuidRegex();

    [GeneratedRegex(@"^[A-Za-z0-9.\-]+_[a-z0-9]{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageFamilyRegex();

    public static bool IsGuidLike(string value) => GuidLike().IsMatch(value);

    public static bool ContainsGuid(string value) => ContainsGuidRegex().IsMatch(value);

    /// <summary>Name_PublisherId, e.g. Microsoft.WindowsCalculator_8wekyb3d8bbwe.</summary>
    public static bool IsPackageFamilyName(string value) => PackageFamilyRegex().IsMatch(value);

    /// <summary>
    /// Machine-generated names such as "BIJrKtBL": mixed case with almost every
    /// letter upper-case in no word pattern. SafeSweep cannot tell which program
    /// made such a folder, so it never calls it a leftover.
    /// </summary>
    public static bool LooksMachineGenerated(string value)
    {
        if (value.Length is < 6 or > 20 || !value.All(char.IsLetterOrDigit))
        {
            return false;
        }

        int upper = value.Count(char.IsUpper);
        int lower = value.Count(char.IsLower);
        int digits = value.Count(char.IsDigit);
        if (upper == 0 || lower == 0)
        {
            // All one case: random only if it mixes letters and digits heavily ("a8f3k2d9").
            return digits >= 3 && digits < value.Length && (upper + lower) >= 3;
        }

        // Case flips per letter: CamelCase ("DuboxYunKernel" 0.36) and acronyms
        // ("WinRAR" 0.33, "IObit" 0.2) stay low; random strings flip constantly.
        int flips = 0;
        for (int i = 1; i < value.Length; i++)
        {
            if (char.IsLetter(value[i]) && char.IsLetter(value[i - 1]) && char.IsUpper(value[i]) != char.IsUpper(value[i - 1]))
            {
                flips++;
            }
        }

        if ((double)flips / (upper + lower) < 0.5)
        {
            return false;
        }

        // ...and contain a one-letter "word" squeezed between capitals ("JrK", "NeC")
        // or start lower-then-upper with several flips ("lKuwIjuJ").
        return MidCapital().IsMatch(value) || (char.IsLower(value[0]) && char.IsUpper(value[1]) && flips >= 3);
    }

    [GeneratedRegex(@"[A-Z][a-z][A-Z]", RegexOptions.CultureInvariant)]
    private static partial Regex MidCapital();

    /// <summary>
    /// True when <paramref name="folderName"/> plausibly belongs to a program
    /// known by <paramref name="compactKeys"/> / <paramref name="tokens"/>.
    /// </summary>
    public static bool Matches(string folderName, IReadOnlyCollection<string> compactKeys, IReadOnlySet<string> tokens, out string? matchedKey)
    {
        matchedKey = null;
        string folder = Compact(folderName);
        if (folder.Length == 0)
        {
            // Nothing to compare: treat as matched (i.e. do not report it).
            matchedKey = "(unnamed)";
            return true;
        }

        foreach (string key in compactKeys)
        {
            if (key.Length == 0)
            {
                continue;
            }

            if (key.Equals(folder, StringComparison.Ordinal))
            {
                matchedKey = key;
                return true;
            }

            // Containment either way, but only on meaningful lengths so "app" does not
            // match everything.
            if ((folder.Length >= 4 && key.Contains(folder, StringComparison.Ordinal))
                || (key.Length >= 4 && folder.Contains(key, StringComparison.Ordinal)))
            {
                matchedKey = key;
                return true;
            }
        }

        foreach (string token in Tokens(folderName))
        {
            if (tokens.Contains(token))
            {
                matchedKey = token;
                return true;
            }
        }

        return false;
    }
}
