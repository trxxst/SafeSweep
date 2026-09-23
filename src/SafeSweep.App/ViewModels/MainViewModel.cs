using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Services;
using SafeSweep.Core;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;
using SafeSweep.Core.Platform;
using SafeSweep.Core.Scanning;
using Wpf.Ui.Controls;

namespace SafeSweep.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private NavItem? _selectedNav;

    [ObservableProperty]
    private PageViewModel? _currentPage;

    /// <summary>Narrow window: the sidebar shows icons only (tooltips carry the names).</summary>
    [ObservableProperty]
    private bool _isCompactNav;

    /// <summary>Short window: secondary text is hidden so tables keep their room.</summary>
    [ObservableProperty]
    private bool _isCompactHeight;

    public string VersionText => "SafeSweep " + AppInfo.DisplayVersion;

    public void UpdateLayoutMode(double width, double height)
    {
        IsCompactNav = width < 1180;
        IsCompactHeight = height < 760;
    }

    public MainViewModel(SafeSweepServices services)
    {
        Services = services;
        Dashboard = new DashboardViewModel(this);
        Cleaner = new CleanerViewModel(this);
        LargeFiles = ReviewToolViewModel.CreateLargeFiles(this);
        Duplicates = ReviewToolViewModel.CreateDuplicates(this);
        OldFiles = ReviewToolViewModel.CreateOldFiles(this);
        Leftovers = ReviewToolViewModel.CreateLeftovers(this);
        Quarantine = new QuarantineViewModel(this);
        History = new HistoryViewModel(this);

        NavItems =
        [
            NavItem.Header("CLEAN"),
            new NavItem("Dashboard", SymbolRegular.Home24, Dashboard),
            new NavItem("Scan & Clean", SymbolRegular.Sparkle24, Cleaner),
            NavItem.Header("REVIEW"),
            new NavItem("Large files", SymbolRegular.DocumentOnePage24, LargeFiles),
            new NavItem("Duplicate files", SymbolRegular.DocumentCopy24, Duplicates),
            new NavItem("Old / unused files", SymbolRegular.Clock24, OldFiles),
            new NavItem("App leftovers", SymbolRegular.AppsList24, Leftovers),
            NavItem.Header("MANAGE"),
            new NavItem("Startup apps", SymbolRegular.Rocket24, new StartupViewModel(this)),
            new NavItem("Quarantine", SymbolRegular.ArchiveArrowBack24, Quarantine),
            new NavItem("History", SymbolRegular.History24, History),
            new NavItem("Scheduled cleaning", SymbolRegular.CalendarClock24, new ScheduleViewModel(this)),
            NavItem.Header("SYSTEM"),
            new NavItem("Windows tools", SymbolRegular.Toolbox24, new WindowsToolsViewModel(this)),
            new NavItem("Protection", SymbolRegular.ShieldCheckmark24, new ProtectionViewModel(this)),
            new NavItem("Logs", SymbolRegular.DocumentText24, new LogsViewModel(this)),
            new NavItem("Settings", SymbolRegular.Settings24, new SettingsViewModel(this)),
        ];

        SelectedNav = NavItems[1];
    }

    public SafeSweepServices Services { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    public DashboardViewModel Dashboard { get; }

    public CleanerViewModel Cleaner { get; }

    public ReviewToolViewModel LargeFiles { get; }

    public ReviewToolViewModel Duplicates { get; }

    public ReviewToolViewModel OldFiles { get; }

    public ReviewToolViewModel Leftovers { get; }

    public QuarantineViewModel Quarantine { get; }

    public HistoryViewModel History { get; }

    public bool IsElevated => Services.IsElevated;

    public string ElevationText => IsElevated
        ? "Running as administrator"
        : "Standard rights";

    public bool IsSimulation => Services.Settings.SimulationMode;

    /// <summary>Most recent scan result, shared with the dashboard.</summary>
    public ScanResult? LastScan { get; private set; }

    public event EventHandler? ScanCompleted;

    public void PublishScan(ScanResult result)
    {
        LastScan = result;
        ScanCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void NotifySettingsChanged() => OnPropertyChanged(nameof(IsSimulation));

    public async Task InitializeAsync()
    {
        try
        {
            // Build the installed-program inventory up front so install folders are
            // protected before the first scan, and apply the quarantine retention.
            await Services.GetInventoryAsync();
            int purged = await Task.Run(() => Services.Quarantine.PurgeExpired(Services.Settings.QuarantineRetentionDays));
            if (purged > 0)
            {
                AppLog.Info($"{purged} expired quarantine item(s) purged at start-up.");
            }

            await Dashboard.RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Start-up tasks failed.", ex);
        }
    }

    public void Navigate(PageViewModel page)
    {
        NavItem? item = NavItems.FirstOrDefault(n => n.Page == page);
        if (item is not null)
        {
            SelectedNav = item;
        }
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null || value.IsHeader)
        {
            return;
        }

        CurrentPage = value.Page;
        _ = value.Page!.OnNavigatedToAsync();
    }

    [RelayCommand]
    private async Task RestartAsAdminAsync()
    {
        bool ok = await DialogService.ConfirmAsync(
            "Restart as administrator",
            "SafeSweep will restart with administrator rights so it can include system locations (Windows temporary files, the Windows Update cache, system crash dumps and logs, all-users startup entries). Windows will ask for your approval.",
            "Restart");
        if (!ok)
        {
            return;
        }

        if (Elevation.TryRestartElevated())
        {
            AppLog.Info("Restarting elevated at the user's request.");
            Application.Current.Shutdown();
        }
    }
}
