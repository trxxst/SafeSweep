using SafeSweep.Core.Cleaning;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;
using SafeSweep.Core.Platform;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Scanning;

namespace SafeSweep.Core.Scheduling;

/// <summary>
/// The unattended run started by Task Scheduler (--scheduled-clean). It can
/// only ever clean Safe categories, never Deep or Manual Review items, and it
/// skips anything that needs administrator rights or is locked.
/// </summary>
public static class ScheduledCleaner
{
    public static int Run(SafeSweepServices services)
    {
        try
        {
            AppLog.Info("Scheduled Safe Clean started.");
            if (services.Settings.ScheduledSkipOnBattery && FileSystemInspector.IsOnBatteryPower())
            {
                AppLog.Info("Scheduled Safe Clean skipped: the PC is running on battery.");
                return 0;
            }

            services.Quarantine.PurgeExpired(services.Settings.QuarantineRetentionDays);

            IReadOnlyList<string> ids = SelectCategories(services);
            if (ids.Count == 0)
            {
                AppLog.Info("Scheduled Safe Clean: no categories selected.");
                return 0;
            }

            var engine = new ScanEngine(services);
            ScanResult scan = engine.Scan(new ScanRequest(ScanMode.Custom, ids), null, CancellationToken.None);

            // Belt and braces: only Safe, selectable, rule-based items - whatever the scan returned.
            List<ScanItem> items = scan.Categories
                .Where(c => c.Definition.AllowScheduled)
                .SelectMany(c => c.Items)
                .Where(i => i.IsSelectable && i.Level == CleanLevel.Safe && i.Intent == DeletionIntent.RuleCleanup)
                .ToList();

            if (items.Count == 0)
            {
                AppLog.Info("Scheduled Safe Clean: nothing to clean.");
                return 0;
            }

            CleanReport report = new CleanEngine(services).Clean(
                items,
                new CleanOptions { Trigger = "Scheduled", Simulation = services.Settings.SimulationMode },
                null,
                CancellationToken.None);

            AppLog.Info($"Scheduled Safe Clean done: {Formatting.Bytes(report.BytesFreed)} freed.");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Scheduled Safe Clean failed.", ex);
            return 1;
        }
        finally
        {
            AppLog.Flush();
        }
    }

    public static IReadOnlyList<string> SelectCategories(SafeSweepServices services)
    {
        IEnumerable<CategoryDefinition> allowed = RuleCatalog.AllCategories
            .Where(c => c.AllowScheduled && c.Intent == DeletionIntent.RuleCleanup && (!c.RequiresAdmin || services.IsElevated));

        List<string> configured = services.Settings.ScheduledCategoryIds;
        if (configured.Count > 0)
        {
            allowed = allowed.Where(c => configured.Contains(c.Id));
        }

        return allowed.Select(c => c.Id).ToList();
    }
}
