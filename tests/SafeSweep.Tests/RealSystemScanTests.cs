using SafeSweep.Core;
using SafeSweep.Core.Models;
using SafeSweep.Core.Safety;
using SafeSweep.Core.Scanning;
using Xunit.Abstractions;

namespace SafeSweep.Tests;

/// <summary>
/// Read-only scans of the real machine. Scanning never modifies anything;
/// these tests assert that nothing a scan offers lies in a location the
/// protection policy guards, whatever this particular PC has installed.
/// </summary>
[Trait("Category", "Integration")]
public class RealSystemScanTests
{
    private readonly ITestOutputHelper _output;

    public RealSystemScanTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Full_scan_offers_nothing_from_protected_locations()
    {
        SafeSweepServices services = SafeSweepServices.Create();
        ScanResult result = new ScanEngine(services).Scan(new ScanRequest(ScanMode.Full), null, CancellationToken.None);

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] forbidden =
        [
            Path.Combine(windows, "System32"),
            Path.Combine(windows, "SysWOW64"),
            Path.Combine(windows, "WinSxS"),
            Path.Combine(windows, "Installer"),
            Path.Combine(windows, "Prefetch"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Protect"),
        ];

        var violations = new List<string>();
        int vanished = 0;
        foreach (CategoryResult category in result.Categories)
        {
            _output.WriteLine($"{category.Definition.Level,-12} {category.Definition.Name,-45} {category.TotalCount,7} items {category.TotalDisplay,12}  {(category.HasNotes ? "| " + category.NotesDisplay.Replace(Environment.NewLine, " | ") : string.Empty)}");

            foreach (ScanItem item in category.Items)
            {
                if (item.Kind == ItemKind.RecycleBin)
                {
                    continue;
                }

                foreach (string root in forbidden.Where(f => !string.IsNullOrEmpty(f)))
                {
                    if (PathUtil.IsUnderOrEqual(item.Path, root))
                    {
                        violations.Add($"{item.Path} is inside protected {root}");
                    }
                }

                // Only Safe items may start selected.
                if (item.IsSelected && item.Level != CleanLevel.Safe)
                {
                    violations.Add($"{item.Path} is pre-selected at level {item.Level}");
                }

                // Every offered item passes the protection check again right now.
                // Temp and cache files legitimately disappear between scan and check.
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(item.Path);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    vanished++;
                    continue;
                }

                SafetyVerdict verdict = services.Guard.EvaluateCandidate(item.Path, item.Intent, item.ScopeRoot, attributes);
                if (!verdict.IsAllowed)
                {
                    violations.Add($"{item.Path}: {verdict.Reason}");
                }
            }
        }

        _output.WriteLine($"Items that disappeared between scan and re-check (normal for temp/cache churn): {vanished}");
        foreach (string violation in violations)
        {
            _output.WriteLine("VIOLATION " + violation);
        }

        Assert.Empty(violations);

        foreach (ScanItem leftover in result.Categories.Single(c => c.Definition.Id == "app-leftovers").Items)
        {
            _output.WriteLine($"LEFTOVER {leftover.Path} ({leftover.SizeDisplay}) - {leftover.Reason}");
        }

        _output.WriteLine($"Total: {result.TotalItems} items, {Formatting.Bytes(result.TotalBytes)}; protected candidates not offered: {result.ProtectedSkipped}; {result.Duration.TotalSeconds:0.0}s");
    }
}
