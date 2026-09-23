using System.Diagnostics;
using SafeSweep.Core.Apps;
using SafeSweep.Core.Models;
using SafeSweep.Core.Safety;
using SafeSweep.Core.Settings;

namespace SafeSweep.Core.Scanning;

public sealed record ScanProgress(string Stage, string? CurrentPath, int ItemsFound, long BytesFound, double? Fraction = null);

/// <summary>Shared state and services for one scan run.</summary>
public sealed class ScanContext
{
    private readonly IProgress<ScanProgress>? _progress;
    private readonly Stopwatch _throttle = Stopwatch.StartNew();
    private readonly HashSet<string> _seenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _items;
    private long _bytes;
    private string _stage = "Preparing";

    public ScanContext(
        PathGuard guard,
        AppSettings settings,
        bool isElevated,
        Func<InstalledAppInventory> inventory,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        Guard = guard;
        Settings = settings;
        IsElevated = isElevated;
        InventoryProvider = inventory;
        _progress = progress;
        CancellationToken = cancellationToken;
        NowUtc = DateTime.UtcNow;
        RunningProcesses = Platform.ProcessSnapshot.RunningNames();
    }

    public PathGuard Guard { get; }

    public SystemPaths Paths => Guard.Policy.Paths;

    public AppSettings Settings { get; }

    public bool IsElevated { get; }

    public Func<InstalledAppInventory> InventoryProvider { get; }

    public CancellationToken CancellationToken { get; }

    public DateTime NowUtc { get; }

    /// <summary>Lower-case process names running when the scan started.</summary>
    public IReadOnlySet<string> RunningProcesses { get; }

    /// <summary>Number of candidates dropped because the protection layer refused them.</summary>
    public int ProtectedSkipped { get; private set; }

    public void SetStage(string stage, double? fraction = null)
    {
        _stage = stage;
        _progress?.Report(new ScanProgress(stage, null, _items, _bytes, fraction));
    }

    public void ReportPath(string path)
    {
        if (_throttle.ElapsedMilliseconds < 80)
        {
            return;
        }

        _throttle.Restart();
        _progress?.Report(new ScanProgress(_stage, path, _items, _bytes));
    }

    /// <summary>Records an item; returns false if another rule already produced the same path.</summary>
    public bool TryClaim(string path, long bytes)
    {
        lock (_gate)
        {
            if (!_seenPaths.Add(path))
            {
                return false;
            }

            _items++;
            _bytes += bytes;
            return true;
        }
    }

    public void CountProtected()
    {
        lock (_gate)
        {
            ProtectedSkipped++;
        }
    }

    public bool IsAnyRunning(IEnumerable<string> processNames)
        => processNames.Any(p => RunningProcesses.Contains(p.ToLowerInvariant()));

    public static DateTime NewestTimestamp(FileSystemInfo info)
        => info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
}

/// <summary>The complete result of a scan: per-category results plus totals.</summary>
public sealed class ScanResult
{
    public required ScanMode Mode { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    public required IReadOnlyList<CategoryResult> Categories { get; init; }

    public bool Cancelled { get; init; }

    public int ProtectedSkipped { get; init; }

    public long TotalBytes => Categories.Sum(c => c.TotalBytes);

    public int TotalItems => Categories.Sum(c => c.TotalCount);

    public long BytesAt(CleanLevel level) => Categories.Where(c => c.Definition.Level == level).Sum(c => c.TotalBytes);

    public IEnumerable<ScanItem> SelectedItems => Categories.SelectMany(c => c.Items).Where(i => i.IsSelected);
}
