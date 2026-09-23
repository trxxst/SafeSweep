using System.Windows;
using System.Windows.Threading;
using SafeSweep.App.Services;
using SafeSweep.App.ViewModels;
using SafeSweep.Core;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Scheduling;

namespace SafeSweep.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless mode used by the Task Scheduler entry: Safe categories only, no window.
        if (e.Args.Any(a => a.Equals("--scheduled-clean", StringComparison.OrdinalIgnoreCase)))
        {
            int code;
            try
            {
                code = ScheduledCleaner.Run(SafeSweepServices.Create());
            }
            catch (Exception ex)
            {
                code = 2;
                AppLog.Error("The scheduled clean could not start.", ex);
            }

            Shutdown(code);
            return;
        }

        // Used by the uninstaller: remove the scheduled-clean task, then exit. No window.
        if (e.Args.Any(a => a.Equals("--remove-schedule", StringComparison.OrdinalIgnoreCase)))
        {
            ScheduleService.Delete(out _);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        SafeSweepServices services;
        try
        {
            services = SafeSweepServices.Create();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "SafeSweep could not start: " + ex.Message,
                "SafeSweep",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var main = new MainViewModel(services);
        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        ThemeService.Apply(services.Settings.Theme);
        window.Show();
        _ = main.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("SafeSweep closed.");
        AppLog.Flush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unexpected error in the user interface.", e.Exception);
        DialogService.ShowError("Something went wrong", e.Exception.Message + "\n\nThe details were written to the log. No files were changed by this error.");
        e.Handled = true;
    }
}
