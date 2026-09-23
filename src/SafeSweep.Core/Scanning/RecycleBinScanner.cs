using SafeSweep.Core.Cleaning;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;
using SafeSweep.Core.Rules;

namespace SafeSweep.Core.Scanning;

public sealed class RecycleBinScanner
{
    public CategoryResult Scan(ScanContext context)
    {
        context.SetStage("Checking the Recycle Bin");
        var result = new CategoryResult(Categories.RecycleBin);

        RecycleBinInfo total = RecycleBinService.Query();
        if (total.ItemCount <= 0)
        {
            return result;
        }

        var evidence = new List<string>
        {
            $"{total.ItemCount} item(s) you deleted earlier are still kept by Windows.",
        };

        foreach ((string drive, RecycleBinInfo info) in RecycleBinService.QueryPerDrive())
        {
            evidence.Add($"{drive}  {info.ItemCount} item(s), {Formatting.Bytes(info.SizeBytes)}");
        }

        evidence.AddRange(SampleNames(15));
        evidence.Add("Emptying the Recycle Bin is permanent.");

        result.Add(new ScanItem
        {
            Path = "Recycle Bin (all drives)",
            Kind = ItemKind.RecycleBin,
            Category = Categories.RecycleBin,
            SizeBytes = total.SizeBytes,
            FileCount = (int)Math.Min(total.ItemCount, int.MaxValue),
            Reason = "Files you already deleted; emptying the bin frees their space permanently.",
            Evidence = evidence,
        });

        return result;
    }

    /// <summary>A few item names from the bin through the shell, so the user sees what is in it.</summary>
    private static IEnumerable<string> SampleNames(int max)
    {
        var names = new List<string>();
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return names;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic bin = shell.NameSpace(10); // ssfBITBUCKET
            int count = 0;
            foreach (dynamic item in bin.Items())
            {
                if (count++ >= max)
                {
                    names.Add("...");
                    break;
                }

                names.Add("  - " + (string)item.Name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Recycle Bin contents could not be listed: " + ex.Message);
        }

        return names.Count > 0 ? new[] { "Contains, for example:" }.Concat(names) : names;
    }
}
