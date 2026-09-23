using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.Core.Cleaning;
using SafeSweep.Core.Disk;
using SafeSweep.Core.History;
using SafeSweep.Core.Models;
using SafeSweep.Core.Scanning;

namespace SafeSweep.App.ViewModels;

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private long _safeBytes;

    [ObservableProperty]
    private long _deepBytes;

    [ObservableProperty]
    private long _reviewBytes;

    [ObservableProperty]
    private bool _hasScan;

    [ObservableProperty]
    private string _scanInfo = "No scan yet. Run a Quick Scan to see how much space can be recovered safely.";

    [ObservableProperty]
    private long _quarantineBytes;

    [ObservableProperty]
    private int _quarantineCount;

    [ObservableProperty]
    private long _recycleBinBytes;

    [ObservableProperty]
    private long _recycleBinCount;

    [ObservableProperty]
    private string _lastClean = "No cleaning yet.";

    [ObservableProperty]
    private long _totalCleanedBytes;

    [ObservableProperty]
    private string _protectionSummary = string.Empty;

    public DashboardViewModel(MainViewModel main)
    {
        _main = main;
        _main.ScanCompleted += (_, _) => UpdateFromScan();
    }

    public override string Title => "Dashboard";

    public override string Subtitle => "Disk usage, recoverable space and the state of your quarantine at a glance.";

    public ObservableCollection<DriveUsage> Drives { get; } = [];

    public MainViewModel Main => _main;

    public override Task OnNavigatedToAsync() => RefreshAsync();

    private bool _refreshing;

    public async Task RefreshAsync()
    {
        // Navigation, start-up and a finished clean can all ask at once.
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            await RefreshCoreAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var drives = await Task.Run(DiskInfoService.GetFixedDrives);
        Drives.Clear();
        foreach (DriveUsage drive in drives)
        {
            Drives.Add(drive);
        }

        (IReadOnlyList<QuarantineEntry> quarantine, RecycleBinInfo bin, IReadOnlyList<HistoryEntry> history) = await Task.Run(() =>
            (_main.Services.Quarantine.LoadAll(), RecycleBinService.Query(), _main.Services.History.Load()));

        QuarantineBytes = quarantine.Sum(q => q.SizeBytes);
        QuarantineCount = quarantine.Count;
        RecycleBinBytes = bin.SizeBytes;
        RecycleBinCount = bin.ItemCount;

        HistoryEntry? last = history.FirstOrDefault(h => !h.Simulation);
        LastClean = last is null
            ? "No cleaning yet."
            : $"{last.StartedLocal:g} ({last.Trigger}): {Formatting.Bytes(last.BytesFreed)} freed, {Formatting.Bytes(last.BytesQuarantined)} quarantined.";
        TotalCleanedBytes = history.Where(h => !h.Simulation).Sum(h => h.BytesFreed + h.BytesRecycled);

        var policy = _main.Services.Policy;
        ProtectionSummary = $"{policy.StaticLocations.Count} protected system locations, {policy.InstalledApplicationFolders.Count} installed-program folders, "
                            + $"{policy.UserExclusions.Count} exclusion(s) of yours, and {Core.Safety.ProtectionPolicy.CriticalNamePatterns.Count + Core.Safety.ProtectionPolicy.CriticalFileNames.Count} protected file types are enforced on every deletion.";

        UpdateFromScan();
    }

    private void UpdateFromScan()
    {
        ScanResult? scan = _main.LastScan;
        if (scan is null)
        {
            return;
        }

        HasScan = true;
        SafeBytes = scan.BytesAt(CleanLevel.Safe);
        DeepBytes = scan.BytesAt(CleanLevel.Deep);
        ReviewBytes = scan.BytesAt(CleanLevel.ManualReview);
        ScanInfo = $"From the {scan.Mode} scan at {scan.StartedUtc.ToLocalTime():t}. Safe Clean is what SafeSweep recommends; the rest needs your review.";
    }

    [RelayCommand]
    private async Task QuickScanAsync()
    {
        _main.Navigate(_main.Cleaner);
        await _main.Cleaner.ScanAsync(new ScanRequest(ScanMode.Quick));
    }

    [RelayCommand]
    private async Task FullScanAsync()
    {
        _main.Navigate(_main.Cleaner);
        await _main.Cleaner.ScanAsync(new ScanRequest(ScanMode.Full));
    }

    [RelayCommand]
    private void OpenQuarantine() => _main.Navigate(_main.Quarantine);
}
