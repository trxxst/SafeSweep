using System.Windows;
using SafeSweep.App;
using SafeSweep.App.Services;
using SafeSweep.App.ViewModels;
using SafeSweep.Core;
using SafeSweep.Core.Models;
using SafeSweep.Core.Scanning;

namespace SafeSweep.Tests.Ui;

/// <summary>
/// Regenerates the screenshots in docs/images from sandbox data. Opt-in, so it
/// is skipped in normal runs. Map an empty folder to a drive letter first, so
/// no real user name appears in the paths shown:
///
///   subst S: C:\some\empty\folder
///   $env:SAFESWEEP_DEMO_ROOT = 'S:\'
///   $env:SAFESWEEP_UI_SNAPSHOTS = "$PWD\docs\images"
///   dotnet test --filter FullyQualifiedName~ReadmeScreenshots
///   subst S: /d
/// </summary>
[Trait("Category", "UI")]
public class ReadmeScreenshots
{
    [SkippableFact]
    public void Capture_readme_screenshots()
    {
        string? root = Environment.GetEnvironmentVariable("SAFESWEEP_DEMO_ROOT");
        Skip.If(string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SAFESWEEP_UI_SNAPSHOTS")),
            "Set SAFESWEEP_DEMO_ROOT and SAFESWEEP_UI_SNAPSHOTS to regenerate the README screenshots.");

        var box = new Sandbox(root!);
        string[] tempNames =
        [
            "setup_2026-08-14.log", "~DF3A7C21B4E9.TMP", "chrome_installer.log", "msedge_update.log",
            "wct5E1F.tmp", "StructuredQuery.log", "dd_vcredist_amd64.log", "tmp9C41.tmp",
            "aria-debug-1180.log", "MpCmdRun.log", "~DF81D0AE5B26.TMP", "vminst.log",
        ];
        for (int i = 0; i < tempNames.Length; i++)
        {
            box.File(Path.Combine(box.Temp, tempNames[i]), new string('x', 40_000 * (i + 1)), ageDays: 3 + i);
        }

        box.File(Path.Combine(box.Temp, "Package Cache Old", "installer-extract", "data1.cab"), new string('x', 900_000), ageDays: 20);
        string explorer = box.Dir(@"Users\me\AppData\Local\Microsoft\Windows\Explorer");
        box.File(Path.Combine(explorer, "thumbcache_256.db"), new string('x', 1_800_000), ageDays: 2);
        box.File(Path.Combine(explorer, "thumbcache_1024.db"), new string('x', 2_600_000), ageDays: 2);
        string shaders = box.Dir(@"Users\me\AppData\Local\D3DSCache\5f2a9c1e0b7d4a36");
        box.File(Path.Combine(shaders, "shader.idx"), new string('x', 300_000), ageDays: 5);
        box.File(Path.Combine(shaders, "shader.val"), new string('x', 1_200_000), ageDays: 5);

        SafeSweepServices services = SafeSweepServices.CreateFor(box.Paths);

        UiTestHost.Run(async () =>
        {
            ThemeService.Apply("Dark");
            var main = new MainViewModel(services);
            var window = new MainWindow
            {
                DataContext = main,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = 0,
                Width = 1280,
                Height = 800,
                ShowInTaskbar = false,
            };
            window.Show();
            window.UpdateLayout();
            main.UpdateLayoutMode(window.ActualWidth, window.ActualHeight);
            try
            {
                main.Navigate(main.Cleaner);
                await main.Cleaner.ScanAsync(new ScanRequest(ScanMode.Custom, ["win-user-temp", "win-thumbnails", "win-d3d-shaders"]));
                main.Cleaner.SelectedCategory = main.Cleaner.CategoriesView!.Cast<CategoryResult>().First(c => c.TotalCount > 0);
                main.Cleaner.Browser.SelectedItem = main.Cleaner.Browser.ItemsView!.Cast<ScanItem>().First();
                await UiTestHost.SettleAsync(400);
                UiTestHost.Snapshot(window, "screenshot-scan-and-clean");

                // No dashboard shot: it shows the real drives of the machine it runs on.
                main.SelectedNav = main.NavItems.First(n => n.Title == "Protection");
                await UiTestHost.SettleAsync(400);
                UiTestHost.Snapshot(window, "screenshot-protection");
            }
            finally
            {
                window.Close();
                ThemeService.Apply("Light");
            }
        });
    }
}
