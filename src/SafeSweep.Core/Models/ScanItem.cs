namespace SafeSweep.Core.Models;

/// <summary>
/// One removable candidate found by a scanner. Carries everything the UI shows
/// (path, size, dates, reason, risk) plus the fingerprint the cleaner uses to
/// refuse the deletion if the item changed between scan and clean.
/// </summary>
public sealed class ScanItem : ObservableBase
{
    private bool _isSelected;

    public required string Path { get; init; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;

    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    public required ItemKind Kind { get; init; }

    public required CategoryDefinition Category { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Number of files represented (1 for a file, n for a folder).</summary>
    public int FileCount { get; init; } = 1;

    public DateTime? LastWriteUtc { get; init; }

    public DateTime? LastAccessUtc { get; init; }

    public DateTime? CreationUtc { get; init; }

    /// <summary>Plain-language reason this item is considered removable.</summary>
    public required string Reason { get; init; }

    /// <summary>The individual signals that led to the decision.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>Per-item risk; defaults to the category risk.</summary>
    public RiskLevel? RiskOverride { get; init; }

    public RiskLevel Risk => RiskOverride ?? Category.Risk;

    public DeletionMethod? MethodOverride { get; init; }

    public DeletionMethod Method => MethodOverride ?? Category.DefaultMethod;

    public DeletionIntent Intent => Category.Intent;

    public CleanLevel Level => Category.Level;

    /// <summary>
    /// For rule cleanup: the approved root the rule scanned. The cleaner
    /// re-verifies at deletion time that the item still lies inside it.
    /// </summary>
    public string? ScopeRoot { get; init; }

    /// <summary>
    /// For rule cleanup: after removal, sub-folders of <see cref="ScopeRoot"/>
    /// left empty are pruned if they were created longer ago than this. Null
    /// means no pruning (caches keep their folder structure).
    /// </summary>
    public TimeSpan? PruneAgeThreshold { get; init; }

    /// <summary>
    /// Processes that make this item unsafe to touch while they run (a browser
    /// for its cache). Checked at scan time and again when cleaning starts.
    /// </summary>
    public IReadOnlyList<string> BlockingProcesses { get; init; } = [];

    public string? BlockingProgramName { get; init; }

    /// <summary>Duplicate group (duplicate finder only).</summary>
    public int? DuplicateGroupId { get; init; }

    /// <summary>SHA-256 content hash (duplicate finder only).</summary>
    public string? ContentHash { get; init; }

    /// <summary>When set, the item is shown but cannot be selected.</summary>
    public string? LockReason { get; init; }

    public bool IsSelectable => LockReason is null;

    /// <summary>Owning category result; wired up by <see cref="CategoryResult"/>.</summary>
    public CategoryResult? Owner { get; internal set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !IsSelectable)
            {
                value = false;
            }

            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            Owner?.OnItemSelectionChanged(this, value);
        }
    }

    public string LastWriteDisplay => LastWriteUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    public string LastAccessDisplay => LastAccessUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    public string SizeDisplay => Formatting.Bytes(SizeBytes);

    public string EvidenceDisplay => Evidence.Count == 0 ? Reason : string.Join(Environment.NewLine, Evidence);
}
