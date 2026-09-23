using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SafeSweep.App;
using SafeSweep.App.ViewModels;
using SafeSweep.App.Views;
using SafeSweep.Core;
using SafeSweep.Core.Models;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Scanning;
using CheckBox = System.Windows.Controls.CheckBox;
using WpfUiButton = Wpf.Ui.Controls.Button;

namespace SafeSweep.Tests.Ui;

/// <summary>
/// The real window and styles, driven with sandbox data at the screen sizes
/// SafeSweep supports. Set SAFESWEEP_UI_SNAPSHOTS to a folder to also get PNGs.
/// </summary>
[Trait("Category", "UI")]
public class UiTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    /// <summary>Logical window sizes: screen resolution divided by Windows display scaling, minus the taskbar.</summary>
    public static IEnumerable<object[]> Sizes()
    {
        yield return ["minimum", 940, 540, 1.0];
        yield return ["1366x768-100", 1366, 728, 1.0];
        yield return ["1366x768-125", 1093, 582, 1.25];
        yield return ["1920x1080-100", 1920, 1040, 1.0];
        yield return ["1920x1080-125", 1536, 824, 1.25];
        yield return ["1920x1080-150", 1280, 693, 1.5];
        yield return ["2560x1440-100", 2560, 1400, 1.0];
        yield return ["2560x1440-150", 1707, 933, 1.5];
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Every_main_action_stays_reachable_at_supported_window_sizes(string label, int width, int height, double scale)
    {
        for (int i = 0; i < 40; i++)
        {
            _box.File(Path.Combine(_box.Temp, $"cache-file-with-a-long-name-{i:000}.tmp"), new string('x', 100 + i), ageDays: 10);
        }

        _box.File(Path.Combine(_box.Temp, "deep", "nested", "folder", "structure", "with", "a", "very", "long", "path", "leaf.tmp"), ageDays: 10);
        SafeSweepServices services = SafeSweepServices.CreateFor(_box.Paths);

        UiTestHost.Run(async () =>
        {
            var main = new MainViewModel(services);
            MainWindow window = OpenWindow(main, width, height);
            try
            {
                Assert.Equal(width < 1180, main.IsCompactNav);
                Assert.Equal(height < 760, main.IsCompactHeight);

                // Scan & Clean with results and a highlighted row: the densest page.
                main.Navigate(main.Cleaner);
                await main.Cleaner.ScanAsync(new ScanRequest(ScanMode.Custom, ["win-user-temp"]));
                main.Cleaner.SelectedCategory = main.Cleaner.CategoriesView!.Cast<CategoryResult>().First(c => c.TotalCount > 0);
                main.Cleaner.Browser.SelectedItem = main.Cleaner.Browser.ItemsView!.Cast<ScanItem>().First();
                await UiTestHost.SettleAsync(250);
                UiTestHost.Snapshot(window, $"cleaner-{label}", scale);

                var root = (FrameworkElement)window.Content;
                AssertFullyInside(Button(window, "Clean selected..."), root, label);
                DataGrid grid = VisualTree.Descendants<DataGrid>(window).First(VisualTree.IsShown);
                Assert.True(grid.ActualHeight >= 100, $"{label}: results table is only {grid.ActualHeight:0}px tall");

                // Row check boxes are whole and sit inside their cells.
                CheckBox rowBox = VisualTree.Descendants<DataGridCell>(grid)
                    .SelectMany(VisualTree.Descendants<CheckBox>)
                    .First(VisualTree.IsShown);
                Assert.True(rowBox.ActualWidth >= 26 && rowBox.ActualHeight >= 26, $"{label}: check box is {rowBox.ActualWidth}x{rowBox.ActualHeight}");
                DataGridCell cell = VisualTree.Descendants<DataGridCell>(grid).First(c => VisualTree.Descendants<CheckBox>(c).Contains(rowBox));
                Rect boxBounds = VisualTree.BoundsIn(rowBox, cell);
                Assert.True(boxBounds.Left >= -0.5 && boxBounds.Right <= cell.ActualWidth + 0.5, $"{label}: check box clipped by its cell");

                // Quarantine and a review tool keep their main buttons on screen.
                main.Navigate(main.Quarantine);
                await UiTestHost.SettleAsync(250);
                UiTestHost.Snapshot(window, $"quarantine-{label}", scale);
                AssertFullyInside(Button(window, "Restore selected"), (FrameworkElement)window.Content, label);

                main.Navigate(main.LargeFiles);
                await UiTestHost.SettleAsync(250);
                UiTestHost.Snapshot(window, $"largefiles-{label}", scale);
                AssertFullyInside(Button(window, "Find large files"), (FrameworkElement)window.Content, label);

                // The sidebar keeps every destination reachable (it scrolls when short).
                ListBox nav = VisualTree.Descendants<ListBox>(window).First(l => l.ItemsSource == main.NavItems);
                Assert.True(VisualTree.BoundsIn(nav, root).Right <= (width < 1180 ? 90 : 270), $"{label}: sidebar too wide");

                // No page is wider than the space it gets (nothing cut off on the right).
                ContentControl host = VisualTree.Descendants<ContentControl>(window).First(c => c.Content == main.CurrentPage && c is not ListBoxItem);
                foreach (NavItem item in main.NavItems.Where(n => !n.IsHeader))
                {
                    main.SelectedNav = item;
                    await UiTestHost.SettleAsync(150);
                    FrameworkElement? overflow = FirstOverflowing(host);
                    Assert.True(
                        overflow is null,
                        $"{label}: on '{item.Title}' {overflow?.GetType().Name} '{(overflow as TextBlock)?.Text ?? (overflow as ContentControl)?.Content}' ends at {(overflow is null ? 0 : VisualTree.BoundsIn(overflow, host).Right):0}px of {host.ActualWidth:0}px");
                    UiTestHost.Snapshot(window, $"page-{label}-{item.Title.Replace(' ', '-').Replace('/', '-')}", scale);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Test_windows_get_their_full_size_even_when_larger_than_the_screen()
    {
        UiTestHost.Run(() =>
        {
            double width = SystemParameters.VirtualScreenWidth + 800;
            double height = SystemParameters.VirtualScreenHeight + 600;

            // Control: Windows caps an ordinary window at the screen size. This is
            // what made the size tests run shrunk on a build server's small screen.
            // An explicit empty style keeps the app's implicit window style (which
            // changes AllowsTransparency after showing) out of a bare Window.
            var plain = new Window
            {
                Style = new Style(typeof(Window)),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = 0,
                Width = width,
                Height = height,
                ShowInTaskbar = false,
            };
            plain.Show();
            plain.UpdateLayout();
            double plainWidth = plain.ActualWidth;
            plain.Close();
            Assert.True(plainWidth < width - 1, $"control: an ordinary window got {plainWidth}px of {width}px");

            var sized = new Window { Style = new Style(typeof(Window)) };
            UiTestHost.ShowAtSize(sized, width, height);
            try
            {
                Assert.Equal(width, sized.ActualWidth, 0.5);
                Assert.Equal(height, sized.ActualHeight, 0.5);
            }
            finally
            {
                sized.Close();
            }
        });
    }

    [Fact]
    public void Header_select_all_tracks_partial_selection_and_respects_filters()
    {
        UiTestHost.Run(async () =>
        {
            CategoryDefinition category = RuleCatalog.FindCategory("win-user-temp")!;
            var result = new CategoryResult(category);
            for (int i = 0; i < 10; i++)
            {
                result.Add(new ScanItem { Path = $@"C:\x\{(i < 5 ? "alpha" : "beta")}{i}.tmp", Kind = ItemKind.File, Category = category, SizeBytes = 1, Reason = "t" });
            }

            result.Add(new ScanItem { Path = @"C:\x\locked.tmp", Kind = ItemKind.File, Category = category, SizeBytes = 1, Reason = "t", LockReason = "busy" });

            var browser = new ItemBrowser();
            browser.SetSource(result.Items);
            Assert.False(browser.AllShownSelected);

            browser.AllShownSelected = true;
            Assert.True(browser.AllShownSelected); // the locked row does not keep it partial
            Assert.Equal(10, result.SelectedCount);

            result.Items[0].IsSelected = false;
            browser.RefreshSelectionState();
            Assert.Null(browser.AllShownSelected);

            browser.AllShownSelected = false;
            Assert.Equal(0, result.SelectedCount);

            // With a search active, select-all only touches the rows shown.
            browser.SearchText = "beta";
            await UiTestHost.SettleAsync(400);
            browser.AllShownSelected = true;
            Assert.Equal(5, result.SelectedCount);
            Assert.All(result.Items.Where(i => i.IsSelected), i => Assert.Contains("beta", i.Path));

            // Space-bar toggle on the highlighted row.
            browser.SelectedItem = result.Items[5];
            browser.ToggleSelectedItemCommand.Execute(null);
            Assert.False(result.Items[5].IsSelected);
        });
    }

    [Fact]
    public void The_scroll_bar_gutter_is_reserved_so_content_never_shifts()
    {
        UiTestHost.Run(async () =>
        {
            double WidthWith(int items)
            {
                var list = new ListBox { ItemsSource = Enumerable.Range(0, items).Select(i => $"Item {i}").ToList() };
                var window = new Window { Content = list, Width = 400, Height = 300, Left = -10000, Top = 0, ShowInTaskbar = false };
                window.Show();
                window.UpdateLayout();
                ScrollContentPresenter presenter = VisualTree.Descendants<ScrollContentPresenter>(list).First();
                double w = presenter.ActualWidth;
                ScrollBar bar = VisualTree.Descendants<ScrollBar>(list).First(b => b.Orientation == Orientation.Vertical);
                if (items > 100)
                {
                    Assert.Equal(Visibility.Visible, bar.Visibility);
                }
                else
                {
                    Assert.Equal(Visibility.Hidden, bar.Visibility);
                }

                window.Close();
                return w;
            }

            await UiTestHost.SettleAsync();
            Assert.Equal(WidthWith(3), WidthWith(300), 1);
        });
    }

    [Fact]
    public void Scroll_bars_use_the_slim_SafeSweep_style()
    {
        UiTestHost.Run(() =>
        {
            var list = new ListBox { ItemsSource = Enumerable.Range(0, 300).ToList() };
            var window = new Window { Content = list, Width = 300, Height = 200, Left = -10000, Top = 0, ShowInTaskbar = false };
            window.Show();
            window.UpdateLayout();
            ScrollBar bar = VisualTree.Descendants<ScrollBar>(list).First(b => b.Orientation == Orientation.Vertical);
            Assert.Equal(12, bar.ActualWidth, 1);

            // No arrow buttons: only the track's page areas exist.
            Assert.DoesNotContain(VisualTree.Descendants<RepeatButton>(bar), b => b.Command == ScrollBar.LineUpCommand || b.Command == ScrollBar.LineDownCommand);
            Thumb thumb = VisualTree.Descendants<Thumb>(bar).Single();
            Assert.True(thumb.ActualWidth is >= 5 and <= 8, $"thumb is {thumb.ActualWidth}px wide");
            window.Close();
        });
    }

    [Fact]
    public void Long_paths_are_shortened_in_the_middle_keeping_drive_and_file_name()
    {
        const string path = @"C:\Users\someone\AppData\Local\Temp\a\very\deep\folder\structure\report-final.pdf";
        string shortened = PathTrimming.Shorten(path, candidate => candidate.Length <= 40);
        Assert.StartsWith(@"C:\...\", shortened);
        Assert.EndsWith("report-final.pdf", shortened);
        Assert.True(shortened.Length <= 40);

        // Whole folders are dropped before characters are: with room to spare only
        // the folders nearest the drive go.
        Assert.Equal(@"C:\...\someone\AppData\Local\Temp\a\very\deep\folder\structure\report-final.pdf", PathTrimming.Shorten(path, c => c.Length <= 80));

        // A single enormous file name still keeps its ending.
        string huge = @"C:\" + new string('n', 300) + ".log";
        Assert.EndsWith(".log", PathTrimming.Shorten(huge, c => c.Length <= 30));
    }

    private static MainWindow OpenWindow(MainViewModel main, int width, int height)
    {
        var window = new MainWindow { DataContext = main };
        UiTestHost.ShowAtSize(window, width, height);

        // Every size assertion below is meaningless if Windows shrank the window.
        Assert.Equal(width, window.ActualWidth, 0.5);
        Assert.Equal(height, window.ActualHeight, 0.5);
        main.UpdateLayoutMode(window.ActualWidth, window.ActualHeight);
        return window;
    }

    /// <summary>
    /// The first visible element whose right edge lies beyond the page area,
    /// i.e. something cut off. Content inside an area that scrolls sideways
    /// (tables) is allowed to extend; everything else must fit.
    /// </summary>
    internal static FrameworkElement? FirstOverflowing(FrameworkElement host)
    {
        return Walk(host);

        FrameworkElement? Walk(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is UIElement { Visibility: not Visibility.Visible })
                {
                    continue;
                }

                if (child is ScrollViewer { HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled })
                {
                    continue;
                }

                if (child is FrameworkElement element && element.ActualWidth > 0
                    && (element is TextBlock or ButtonBase or Border or ContentPresenter)
                    && VisualTree.BoundsIn(element, host).Right > host.ActualWidth + 1)
                {
                    return element;
                }

                FrameworkElement? nested = Walk(child);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }
    }

    [Fact]
    public void The_overflow_probe_detects_a_cut_off_element()
    {
        UiTestHost.Run(() =>
        {
            var host = new Border { Width = 300, Height = 100 };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = "fits", Width = 100 });
            host.Child = panel;
            var window = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight, Left = -10000, Top = 0, ShowInTaskbar = false };
            window.Show();
            window.UpdateLayout();
            Assert.Null(FirstOverflowing(host)); // negative control

            panel.Children.Add(new TextBlock { Text = "cut off", Width = 400 });
            window.UpdateLayout();
            Assert.NotNull(FirstOverflowing(host)); // positive control
            window.Close();
        });
    }

    private static WpfUiButton Button(DependencyObject root, string text)
        => VisualTree.Descendants<WpfUiButton>(root).First(b => b.Content as string == text && VisualTree.IsShown(b));

    private static void AssertFullyInside(FrameworkElement element, FrameworkElement container, string label)
    {
        Rect bounds = VisualTree.BoundsIn(element, container);
        Assert.True(
            bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= container.ActualWidth + 0.5 && bounds.Bottom <= container.ActualHeight + 0.5,
            $"{label}: '{(element as ContentControl)?.Content}' at {bounds} is outside {container.ActualWidth:0}x{container.ActualHeight:0}");
    }
}
