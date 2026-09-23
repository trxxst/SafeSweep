using System.Diagnostics;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;
using SafeSweep.Core.Rules;

namespace SafeSweep.Core.Scanning;

public sealed record ScanRequest(ScanMode Mode, IReadOnlyCollection<string>? CategoryIds = null);

/// <summary>
/// Runs the scanners for a Quick, Full or Custom scan. Scanning is strictly
/// read-only: nothing is created, modified or deleted.
/// </summary>
public sealed class ScanEngine
{
    private readonly SafeSweepServices _services;

    public ScanEngine(SafeSweepServices services)
    {
        _services = services;
    }

    public static IReadOnlyList<CategoryDefinition> CategoriesFor(ScanRequest request) => request.Mode switch
    {
        ScanMode.Quick => RuleCatalog.AllCategories.Where(c => c.InQuickScan).ToList(),
        ScanMode.Full => RuleCatalog.AllCategories.Where(c => c.InFullScan).ToList(),
        _ => RuleCatalog.AllCategories.Where(c => request.CategoryIds?.Contains(c.Id) == true).ToList(),
    };

    public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Scan(request, progress, cancellationToken), cancellationToken);

    public ScanResult Scan(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        DateTime started = DateTime.UtcNow;
        IReadOnlyList<CategoryDefinition> categories = CategoriesFor(request);
        var ids = categories.Select(c => c.Id).ToHashSet();
        AppLog.Info($"{request.Mode} scan started: {string.Join(", ", ids)}");

        // Install folders must be protected before anything is evaluated.
        progress?.Report(new ScanProgress("Checking installed programs", null, 0, 0));
        _services.GetInventory(cancellationToken: cancellationToken);

        var context = new ScanContext(
            _services.Guard,
            _services.Settings,
            _services.IsElevated,
            () => _services.GetInventory(cancellationToken: cancellationToken),
            progress,
            cancellationToken);

        var results = new Dictionary<string, CategoryResult>();
        bool cancelled = false;
        try
        {
            new RuleScanner().Scan(context, RuleCatalog.All.Where(r => ids.Contains(r.Category.Id)), results);

            if (ids.Contains(Categories.RecycleBin.Id))
            {
                results[Categories.RecycleBin.Id] = new RecycleBinScanner().Scan(context);
            }

            if (ids.Contains(Categories.OldInstallers.Id))
            {
                results[Categories.OldInstallers.Id] = new OldInstallerScanner().Scan(context);
            }

            if (ids.Contains(Categories.Leftovers.Id))
            {
                results[Categories.Leftovers.Id] = new LeftoverScanner().Scan(context);
            }

            if (ids.Contains(Categories.EmptyFolders.Id))
            {
                results[Categories.EmptyFolders.Id] = new EmptyFolderScanner().Scan(context);
            }

            if (ids.Contains(Categories.OldFiles.Id))
            {
                results[Categories.OldFiles.Id] = new OldFileScanner().Scan(context);
            }

            if (ids.Contains(Categories.LargeFiles.Id))
            {
                results[Categories.LargeFiles.Id] = new LargeFileScanner().Scan(context, null, _services.Settings.LargeFileThresholdMb * 1024L * 1024L);
            }

            if (ids.Contains(Categories.Duplicates.Id))
            {
                results[Categories.Duplicates.Id] = new DuplicateScanner().Scan(context, null, _services.Settings.DuplicateMinSizeKb * 1024L);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            AppLog.Info("Scan cancelled by the user; partial results are shown.");
        }

        // Keep catalog order, include empty categories so the user sees they were checked.
        List<CategoryResult> ordered = categories
            .Select(c => results.TryGetValue(c.Id, out CategoryResult? r) ? r : new CategoryResult(c))
            .ToList();

        var result = new ScanResult
        {
            Mode = request.Mode,
            StartedUtc = started,
            Duration = watch.Elapsed,
            Categories = ordered,
            Cancelled = cancelled,
            ProtectedSkipped = context.ProtectedSkipped,
        };

        AppLog.Info($"{request.Mode} scan finished in {watch.Elapsed.TotalSeconds:0.0}s: {result.TotalItems} items, {Formatting.Bytes(result.TotalBytes)}; {context.ProtectedSkipped} protected items were not offered.");
        return result;
    }

    /// <summary>Runs one review tool (large files, duplicates, old files) over specific folders.</summary>
    public Task<CategoryResult> RunToolAsync(string categoryId, IReadOnlyList<string>? roots, long parameter, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                _services.GetInventory(cancellationToken: cancellationToken);
                var context = new ScanContext(
                    _services.Guard,
                    _services.Settings,
                    _services.IsElevated,
                    () => _services.GetInventory(cancellationToken: cancellationToken),
                    progress,
                    cancellationToken);

                AppLog.Info($"Review tool {categoryId} started on: {string.Join("; ", roots ?? [])}");
                CategoryResult result = categoryId switch
                {
                    "large-files" => new LargeFileScanner().Scan(context, roots, parameter),
                    "duplicates" => new DuplicateScanner().Scan(context, roots, parameter),
                    "old-files" => new OldFileScanner().Scan(context, roots),
                    "app-leftovers" => new LeftoverScanner().Scan(context),
                    _ => throw new ArgumentOutOfRangeException(nameof(categoryId)),
                };
                AppLog.Info($"Review tool {categoryId} finished: {result.TotalCount} items, {Formatting.Bytes(result.TotalBytes)}.");
                return result;
            },
            cancellationToken);
}
