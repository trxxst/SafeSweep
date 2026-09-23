using System.Reflection;

namespace SafeSweep.App.Services;

/// <summary>Version and project links shown in the UI (one source: the assembly metadata).</summary>
public static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/trxxst/SafeSweep";

    public const string IssuesUrl = RepositoryUrl + "/issues";

    public const string ReleasesUrl = RepositoryUrl + "/releases";

    /// <summary>Semantic version, e.g. "1.0.0" (build metadata after '+' removed).</summary>
    public static string Version { get; } = ReadVersion();

    public static string DisplayVersion => "v" + Version;

    private static string ReadVersion()
    {
        string? informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        int plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
