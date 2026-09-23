using SafeSweep.App.Services;
using SafeSweep.Core.Cleaning;
using SafeSweep.Core.Models;

namespace SafeSweep.App.ViewModels;

/// <summary>
/// The one path every clean in the UI goes through: an explicit confirmation
/// that states exactly what will happen to what, then the engine, then a
/// plain-language summary.
/// </summary>
public static class CleanFlow
{
    public static async Task<CleanReport?> ConfirmAndRunAsync(
        MainViewModel main,
        IReadOnlyList<ScanItem> selected,
        IProgress<CleanProgress> progress,
        CancellationToken cancellationToken)
    {
        if (selected.Count == 0)
        {
            await DialogService.ShowInfoAsync("Nothing selected", "Select the items you want to remove first.");
            return null;
        }

        var options = new CleanOptions
        {
            Simulation = main.Services.Settings.SimulationMode,
            UseRecycleBinForReview = main.Services.Settings.UseRecycleBinForReview,
        };

        var lines = new List<string>();
        var byMethod = selected
            .Where(i => i.Kind != ItemKind.RecycleBin)
            .GroupBy(i => CleanEngine.ResolveMethod(i, options))
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Bytes: g.Sum(i => i.SizeBytes)));

        if (byMethod.TryGetValue(DeletionMethod.Permanent, out var permanent))
        {
            lines.Add($"- {permanent.Count} item(s), {Formatting.Bytes(permanent.Bytes)}: deleted (Safe Clean temporary and cache files, empty folders).");
        }

        if (byMethod.TryGetValue(DeletionMethod.Quarantine, out var quarantine))
        {
            int days = main.Services.Settings.QuarantineRetentionDays;
            string keep = days > 0 ? $"kept for {days} days" : "kept until you purge them";
            lines.Add($"- {quarantine.Count} item(s), {Formatting.Bytes(quarantine.Bytes)}: moved to quarantine, restorable from the Quarantine page ({keep}).");
        }

        if (byMethod.TryGetValue(DeletionMethod.RecycleBin, out var recycle))
        {
            lines.Add($"- {recycle.Count} item(s), {Formatting.Bytes(recycle.Bytes)}: sent to the Windows Recycle Bin.");
        }

        ScanItem? bin = selected.FirstOrDefault(i => i.Kind == ItemKind.RecycleBin);
        if (bin is not null)
        {
            lines.Add($"- The Recycle Bin ({Formatting.Bytes(bin.SizeBytes)}) will be EMPTIED. This is permanent and cannot be undone.");
        }

        int review = selected.Count(i => i.Level == CleanLevel.ManualReview);
        int highRisk = selected.Count(i => i.Risk == RiskLevel.High);
        if (highRisk > 0)
        {
            lines.Add($"- {highRisk} selected item(s) are marked HIGH risk (personal content or possible saved data).");
        }

        lines.Add("Every item is re-checked against the protection rules right before it is touched; anything that changed since the scan is skipped.");

        string title = options.Simulation ? "Preview clean (simulation)" : "Confirm clean";
        string message = options.Simulation
            ? $"Simulation mode is on: nothing will be changed. SafeSweep will report what would happen to these {selected.Count} item(s):"
            : $"You are about to process {selected.Count} item(s), {Formatting.Bytes(selected.Sum(i => i.SizeBytes))}:";

        string? acknowledgement = null;
        if (!options.Simulation && review > 0)
        {
            acknowledgement = $"I have reviewed the {review} Manual Review item(s) I selected and want them removed.";
        }
        else if (!options.Simulation && bin is not null)
        {
            acknowledgement = "I understand emptying the Recycle Bin is permanent.";
        }

        bool danger = !options.Simulation && (bin is not null || highRisk > 0);
        bool ok = await DialogService.ConfirmAsync(title, message, options.Simulation ? "Run preview" : "Clean", danger, acknowledgement, lines);
        if (!ok)
        {
            return null;
        }

        var engine = new CleanEngine(main.Services);
        try
        {
            return await engine.CleanAsync(selected, options, progress, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            await DialogService.ShowInfoAsync("Cleaning not started", ex.Message);
            return null;
        }
    }

    public static string Summarize(CleanReport report)
    {
        if (report.Simulation)
        {
            return $"Preview finished: {report.Count(CleanOutcome.Simulated)} item(s), {Formatting.Bytes(report.BytesSimulated)} would be removed. Nothing was changed. "
                   + Problems(report);
        }

        var parts = new List<string>();
        if (report.Count(CleanOutcome.Deleted) > 0)
        {
            parts.Add($"{Formatting.Bytes(report.BytesFreed)} freed ({report.Count(CleanOutcome.Deleted)} deleted)");
        }

        if (report.Count(CleanOutcome.Quarantined) > 0)
        {
            parts.Add($"{Formatting.Bytes(report.BytesQuarantined)} quarantined ({report.Count(CleanOutcome.Quarantined)} items, restorable)");
        }

        if (report.Count(CleanOutcome.Recycled) > 0)
        {
            parts.Add($"{Formatting.Bytes(report.BytesRecycled)} sent to the Recycle Bin");
        }

        string done = parts.Count > 0 ? string.Join(", ", parts) + "." : "Nothing was removed.";
        if (report.Cancelled)
        {
            done = "Cancelled. " + done;
        }

        return done + " " + Problems(report);
    }

    private static string Problems(CleanReport report)
    {
        int skipped = report.Count(CleanOutcome.Skipped);
        int blocked = report.Count(CleanOutcome.Blocked);
        int failed = report.Count(CleanOutcome.Failed);
        if (skipped + blocked + failed == 0)
        {
            return string.Empty;
        }

        return $"{skipped} skipped (in use or changed), {blocked} blocked by protection, {failed} failed - see the list below and the log.";
    }

    /// <summary>Returns new category results without the items that were removed.</summary>
    public static List<CategoryResult> Prune(IEnumerable<CategoryResult> categories, CleanReport report)
    {
        var removed = new HashSet<ScanItem>(report.Results
            .Where(r => r.Outcome is CleanOutcome.Deleted or CleanOutcome.Quarantined or CleanOutcome.Recycled)
            .Select(r => r.Item));

        var list = new List<CategoryResult>();
        foreach (CategoryResult category in categories)
        {
            category.SetAllSelected(false);
            var fresh = new CategoryResult(category.Definition);
            foreach (string note in category.Notes)
            {
                fresh.AddNote(note);
            }

            foreach (ScanItem item in category.Items)
            {
                if (!removed.Contains(item))
                {
                    fresh.Add(item);
                }
            }

            list.Add(fresh);
        }

        return list;
    }
}
