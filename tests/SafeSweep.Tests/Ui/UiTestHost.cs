using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SafeSweep.Tests.Ui;

/// <summary>
/// One STA thread with the real SafeSweep application resources (App.xaml:
/// theme, WPF-UI controls, SafeSweep styles). App.OnStartup is never run, so
/// no real window opens and no real settings are applied.
/// </summary>
internal static class UiTestHost
{
    private static readonly Lazy<Dispatcher> Host = new(Start, isThreadSafe: true);

    public static Dispatcher Dispatcher => Host.Value;

    public static void Run(Func<Task> test)
        => Dispatcher.InvokeAsync(test).Task.Unwrap().GetAwaiter().GetResult();

    public static void Run(Action test) => Dispatcher.Invoke(test);

    /// <summary>Lets layout, bindings and deferred work settle.</summary>
    public static async Task SettleAsync(int milliseconds = 0)
    {
        if (milliseconds > 0)
        {
            await Task.Delay(milliseconds);
        }

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Writes a PNG when SAFESWEEP_UI_SNAPSHOTS names a folder (used for manual review).</summary>
    public static void Snapshot(FrameworkElement element, string name, double scale = 1.0)
    {
        string? folder = Environment.GetEnvironmentVariable("SAFESWEEP_UI_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);

        // Mica windows are transparent; paint the theme background so the PNG shows what users see.
        if (element is Window window && Application.Current.TryFindResource("ApplicationBackgroundBrush") is Brush background)
        {
            window.Background = background;
        }

        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }

    /// <summary>
    /// Shows <paramref name="window"/> off screen at exactly the requested size.
    /// Windows normally caps a window at the size of the screen
    /// (WM_GETMINMAXINFO), and build servers have small screens, so without this
    /// a 2560x1400 layout test would quietly run at 1024x768.
    /// </summary>
    public static void ShowAtSize(Window window, double width, double height)
    {
        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = 0;
        window.ShowInTaskbar = false;
        window.SourceInitialized += (_, _) =>
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(AllowAnyTrackSize);

        // The window is created before the hook exists, so size it only once it is shown.
        window.Width = 1000;
        window.Height = 600;
        window.Show();
        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
    }

    private static IntPtr AllowAnyTrackSize(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (msg == WmGetMinMaxInfo)
        {
            MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaxTrackSize = new Point32 { X = 16000, Y = 16000 };
            Marshal.StructureToPtr(info, lParam, false);
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point32 Reserved;
        public Point32 MaxSize;
        public Point32 MaxPosition;
        public Point32 MinTrackSize;
        public Point32 MaxTrackSize;
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var app = new SafeSweep.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            ready.Set();
            if (failure is null)
            {
                Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
            Name = "SafeSweep UI tests",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return failure is null ? dispatcher! : throw new InvalidOperationException("The UI host could not start.", failure);
    }
}

internal static class VisualTree
{
    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Bounds of <paramref name="element"/> in the coordinates of <paramref name="ancestor"/>.</summary>
    public static Rect BoundsIn(FrameworkElement element, Visual ancestor)
        => element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    public static bool IsShown(UIElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }
}
