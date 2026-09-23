namespace SafeSweep.Core.Models;

/// <summary>
/// The cleaning level a category belongs to. Levels are ordered from most to
/// least certain; the UI never pre-selects anything above <see cref="Safe"/>.
/// </summary>
public enum CleanLevel
{
    /// <summary>Extremely safe: regenerated automatically, never user data.</summary>
    Safe = 0,

    /// <summary>Caches, logs and leftovers that are safe in general but deserve a look.</summary>
    Deep = 1,

    /// <summary>Uncertain items. Only removed after explicit, per-item user confirmation.</summary>
    ManualReview = 2,
}

/// <summary>Risk that removing an item has an effect the user would not want.</summary>
public enum RiskLevel
{
    VeryLow = 0,
    Low = 1,
    Moderate = 2,
    High = 3,
}

/// <summary>How a selected item is removed.</summary>
public enum DeletionMethod
{
    /// <summary>Deleted outright. Only ever used for Safe-level, regenerable data.</summary>
    Permanent = 0,

    /// <summary>Moved into SafeSweep's own restorable quarantine.</summary>
    Quarantine = 1,

    /// <summary>Sent to the Windows Recycle Bin (only when the bin is guaranteed to keep it).</summary>
    RecycleBin = 2,
}

public enum ItemKind
{
    File = 0,
    Directory = 1,

    /// <summary>A virtual item that stands for the whole Recycle Bin.</summary>
    RecycleBin = 2,
}

/// <summary>
/// Why a path is being removed. The protection layer applies different rules
/// to each intent, so the same path can be refused for one intent and allowed
/// for another (e.g. a file in AppData may be a cache entry but is never a
/// "large file" candidate).
/// </summary>
public enum DeletionIntent
{
    /// <summary>A built-in cleanup rule (temp, cache, logs, dumps...).</summary>
    RuleCleanup = 0,

    /// <summary>A folder left behind by an application that is verifiably uninstalled.</summary>
    Leftover = 1,

    /// <summary>A user-reviewed file: large file, duplicate, old/unused file, old installer.</summary>
    UserReview = 2,

    /// <summary>An empty directory.</summary>
    EmptyFolder = 3,

    /// <summary>The Windows Recycle Bin (emptied only through the shell API).</summary>
    RecycleBin = 4,
}

public enum ScanMode
{
    Quick = 0,
    Full = 1,
    Custom = 2,
}
