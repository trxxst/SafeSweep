namespace SafeSweep.Core.Models;

/// <summary>
/// Static description of a cleaning category. Every category carries a plain
/// language explanation of what it is, why it is removable and what happens
/// afterwards, so the user never has to trust a bare label.
/// </summary>
public sealed record CategoryDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>UI grouping, e.g. "Windows", "Browsers", "Applications", "Review".</summary>
    public required string Group { get; init; }

    public required CleanLevel Level { get; init; }

    public required RiskLevel Risk { get; init; }

    /// <summary>What the data is.</summary>
    public required string Description { get; init; }

    /// <summary>Why it is considered removable.</summary>
    public required string WhyRemovable { get; init; }

    /// <summary>What the user may notice after removal (slower first launch, logged out, etc.).</summary>
    public string AfterEffects { get; init; } = "Nothing noticeable. The data is recreated automatically when needed.";

    public required DeletionMethod DefaultMethod { get; init; }

    public required DeletionIntent Intent { get; init; }

    public bool RequiresAdmin { get; init; }

    /// <summary>Included in a Quick Scan.</summary>
    public bool InQuickScan { get; init; }

    /// <summary>Included in a Full Scan.</summary>
    public bool InFullScan { get; init; } = true;

    /// <summary>Allowed in unattended scheduled cleaning. Only ever true for Safe categories.</summary>
    public bool AllowScheduled => Level == CleanLevel.Safe && DefaultMethod != DeletionMethod.RecycleBin;

    /// <summary>Items are pre-selected after a scan. Only Safe categories may be pre-selected.</summary>
    public bool PreSelect => Level == CleanLevel.Safe;
}
