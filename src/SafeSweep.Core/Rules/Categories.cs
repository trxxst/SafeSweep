using SafeSweep.Core.Models;

namespace SafeSweep.Core.Rules;

/// <summary>Categories produced by specialised scanners rather than by file rules.</summary>
public static class Categories
{
    public static readonly CategoryDefinition RecycleBin = new()
    {
        Id = "recycle-bin",
        Name = "Recycle Bin",
        Group = "Windows",
        Level = CleanLevel.Deep,
        Risk = RiskLevel.Moderate,
        Description = "Files and folders you deleted earlier and that Windows is still keeping in the Recycle Bin on every drive.",
        WhyRemovable = "You already chose to delete these files. Emptying the bin reclaims the space they still occupy.",
        AfterEffects = "Emptying is permanent: neither Windows nor SafeSweep can restore these files afterwards. Open the Recycle Bin first if you are unsure.",
        DefaultMethod = DeletionMethod.Permanent,
        Intent = DeletionIntent.RecycleBin,
        InQuickScan = true,
    };

    public static readonly CategoryDefinition Leftovers = new()
    {
        Id = "app-leftovers",
        Name = "Uninstalled application leftovers",
        Group = "Review",
        Level = CleanLevel.ManualReview,
        Risk = RiskLevel.Moderate,
        Description = "Folders in AppData or ProgramData that no installed program claims: no uninstall entry, Store package, Program Files folder, shortcut, service, startup entry, scheduled task or running process matches them.",
        WhyRemovable = "The program that created the folder appears to be uninstalled and the folder has not been used for a long time.",
        AfterEffects = "If you reinstall the program later, its old settings will be gone. Some folders may hold settings or saved data (for example game saves) you still want: review each one. Items are quarantined, so you can restore them.",
        DefaultMethod = DeletionMethod.Quarantine,
        Intent = DeletionIntent.Leftover,
    };

    public static readonly CategoryDefinition EmptyFolders = new()
    {
        Id = "empty-folders",
        Name = "Empty folders",
        Group = "Review",
        Level = CleanLevel.ManualReview,
        Risk = RiskLevel.Low,
        Description = "Folders in AppData that contain no files at all and have not changed for at least a month.",
        WhyRemovable = "An empty folder holds no data. Programs normally recreate their folders when they need them.",
        AfterEffects = "Usually nothing. A small number of programs expect a folder to exist; that is why these are only removed after your review.",
        DefaultMethod = DeletionMethod.Permanent,
        Intent = DeletionIntent.EmptyFolder,
    };

    public static readonly CategoryDefinition OldInstallers = new()
    {
        Id = "old-installers",
        Name = "Old installers in Downloads",
        Group = "Review",
        Level = CleanLevel.ManualReview,
        Risk = RiskLevel.Moderate,
        Description = "Setup programs and packages (.exe, .msi, .msix, .appx) in your Downloads folder that were downloaded a long time ago.",
        WhyRemovable = "Once a program is installed its setup file is rarely needed again, and a newer version can be downloaded.",
        AfterEffects = "You would need to download the installer again to reinstall. Keep installers for software you cannot download again.",
        DefaultMethod = DeletionMethod.Quarantine,
        Intent = DeletionIntent.UserReview,
    };

    public static readonly CategoryDefinition OldFiles = new()
    {
        Id = "old-files",
        Name = "Old / unused files",
        Group = "Review",
        Level = CleanLevel.ManualReview,
        Risk = RiskLevel.High,
        Description = "Your own files that have not been modified (and, where Windows records it, not opened) for a long time.",
        WhyRemovable = "Nothing marks these as unneeded; they are shown only so you can decide. Age alone never makes a file removable.",
        AfterEffects = "These are your files. Only remove the ones you are sure you no longer want. They are quarantined (or sent to the Recycle Bin) so you can restore them.",
        DefaultMethod = DeletionMethod.Quarantine,
        Intent = DeletionIntent.UserReview,
        InFullScan = true,
    };

    public static readonly CategoryDefinition LargeFiles = new()
    {
        Id = "large-files",
        Name = "Large files",
        Group = "Review",
        Level = CleanLevel.ManualReview,
        Risk = RiskLevel.High,
        Description = "The largest files in the folders you chose.",
        WhyRemovable = "Nothing marks these as unneeded; size only tells you where space is going.",
        AfterEffects = "These are your files. They are quarantined (or sent to the Recycle Bin) so you can restore them.",
        DefaultMethod = DeletionMethod.Quarantine,
        Intent = DeletionIntent.UserReview,
        InFullScan = false,
    };

    public static readonly CategoryDefinition Duplicates = new()
    {
        Id = "duplicates",
        Name = "Duplicate files",
        Group = "Review",
        Level = CleanLevel.ManualReview,
        Risk = RiskLevel.Moderate,
        Description = "Files whose content is byte-for-byte identical, verified with a SHA-256 hash of the whole file (never by name).",
        WhyRemovable = "Extra copies of identical content use space without adding anything. At least one copy of every group is always kept.",
        AfterEffects = "Shortcuts or programs that point at a removed copy will no longer find it there. Removed copies are quarantined so you can restore them.",
        DefaultMethod = DeletionMethod.Quarantine,
        Intent = DeletionIntent.UserReview,
        InFullScan = false,
    };
}
