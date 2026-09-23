using SafeSweep.Core.Models;

namespace SafeSweep.Core.Rules;

/// <summary>
/// A declarative file-cleanup rule: where to look, what matches, how old it
/// must be, and which running programs make it unsafe right now. Rules never
/// contain code that decides to delete; the scanner applies them and the
/// protection layer has the final word.
/// </summary>
public sealed class CleanupRule
{
    public required CategoryDefinition Category { get; init; }

    /// <summary>
    /// Root locations with tokens ({LocalAppData}, {RoamingAppData}, {LocalLow},
    /// {Windows}, {ProgramData}, {Temp}, {SystemDrive}, {UserProfile}). A segment
    /// that is exactly "*" matches every sub-folder at that level (profiles).
    /// </summary>
    public required IReadOnlyList<string> Roots { get; init; }

    public IReadOnlyList<string> Include { get; init; } = ["*"];

    public IReadOnlyList<string> Exclude { get; init; } = [];

    public bool Recursive { get; init; } = true;

    /// <summary>Files modified more recently than this are left alone.</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;

    /// <summary>When set, <see cref="MinAge"/> comes from the user's temp-age setting.</summary>
    public bool UsesTempAgeSetting { get; init; }

    /// <summary>The root is a single file rather than a folder.</summary>
    public bool ExactFile { get; init; }

    /// <summary>Process names (without .exe). When any is running, the items are shown but locked.</summary>
    public IReadOnlyList<string> BlockingProcesses { get; init; } = [];

    /// <summary>Friendly name of the program behind <see cref="BlockingProcesses"/>.</summary>
    public string? BlockingProgramName { get; init; }

    /// <summary>Root folder name must be one of these (defence against a mis-set %TEMP%).</summary>
    public IReadOnlyList<string> RequiredRootLeafNames { get; init; } = [];

    /// <summary>First-level folder names under a token root that are never expanded by "*".</summary>
    public IReadOnlyList<string> ExcludedWildcardNames { get; init; } = [];

    /// <summary>Skip a directory (and its subtree) when this returns true, e.g. database folders.</summary>
    public Func<DirectoryInfo, bool>? SkipDirectory { get; init; }

    /// <summary>Human-readable description of the location, used in each item's reason.</summary>
    public required string LocationLabel { get; init; }

    /// <summary>Optional precondition; returns a reason string when the rule must not run now.</summary>
    public Func<string?>? Precondition { get; init; }
}
