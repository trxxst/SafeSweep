using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Services;
using SafeSweep.Core.Cleaning;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;

namespace SafeSweep.App.ViewModels;

public sealed partial class QuarantineRow : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public QuarantineRow(QuarantineEntry entry)
    {
        Entry = entry;
    }

    public QuarantineEntry Entry { get; }
}

public sealed partial class QuarantineViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private string _retentionText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public QuarantineViewModel(MainViewModel main)
    {
        _main = main;
    }

    public override string Title => "Quarantine";

    public override string Subtitle => "Items SafeSweep moved aside instead of deleting. Restore them to where they were, or purge them to free the space for good.";

    public ObservableCollection<QuarantineRow> Rows { get; } = [];

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private long _selectedBytes;

    /// <summary>Header select-all: true = all, false = none, null = some.</summary>
    public bool? AllSelected
    {
        get => SelectedCount == 0 ? false : SelectedCount == Rows.Count ? true : null;
        set
        {
            bool select = value == true;
            foreach (QuarantineRow row in Rows)
            {
                row.IsSelected = select;
            }
        }
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuarantineRow.IsSelected))
        {
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        SelectedCount = Rows.Count(r => r.IsSelected);
        SelectedBytes = Rows.Where(r => r.IsSelected).Sum(r => r.Entry.SizeBytes);
        OnPropertyChanged(nameof(AllSelected));
    }

    public override Task OnNavigatedToAsync() => RefreshAsync();

    public async Task RefreshAsync()
    {
        IReadOnlyList<QuarantineEntry> entries = await Task.Run(_main.Services.Quarantine.LoadAll);
        foreach (QuarantineRow old in Rows)
        {
            old.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        foreach (QuarantineEntry entry in entries)
        {
            var row = new QuarantineRow(entry);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        UpdateSelection();

        TotalBytes = entries.Sum(e => e.SizeBytes);
        int days = _main.Services.Settings.QuarantineRetentionDays;
        RetentionText = days > 0
            ? $"Items are kept for {days} days, then purged automatically (change this in Settings). Quarantined items still use disk space until they are purged."
            : "Items are kept until you purge them (automatic purging is off in Settings). Quarantined items still use disk space until they are purged.";
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (QuarantineRow row in Rows)
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (QuarantineRow row in Rows)
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        List<QuarantineRow> rows = Rows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
        {
            await DialogService.ShowInfoAsync("Nothing selected", "Select the items to restore first.");
            return;
        }

        IsBusy = true;
        try
        {
            List<RestoreResult> results = await Task.Run(() => rows.Select(r => _main.Services.Quarantine.Restore(r.Entry)).ToList());
            int ok = results.Count(r => r.Success);
            var failures = results.Where(r => !r.Success).Select(r => $"{r.RestoredPath}: {r.Message}").ToList();
            int beside = results.Count(r => r.Success && r.Message.Contains("beside", StringComparison.Ordinal));
            string message = $"{ok} item(s) restored.";
            if (beside > 0)
            {
                message += $" {beside} were restored beside a newer file with the same name (nothing was overwritten).";
            }

            if (failures.Count > 0)
            {
                message += "\n\nCould not restore:\n" + string.Join("\n", failures.Take(20));
            }

            await DialogService.ShowInfoAsync("Restore finished", message);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
            await _main.Dashboard.RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task PurgeSelectedAsync()
    {
        List<QuarantineRow> rows = Rows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
        {
            await DialogService.ShowInfoAsync("Nothing selected", "Select the items to purge first.");
            return;
        }

        long bytes = rows.Sum(r => r.Entry.SizeBytes);
        bool ok = await DialogService.ConfirmAsync(
            "Purge permanently",
            $"Permanently delete {rows.Count} quarantined item(s), {Formatting.Bytes(bytes)}? They can no longer be restored afterwards.",
            "Purge",
            danger: true,
            acknowledgement: "I understand purged items cannot be restored.");
        if (!ok)
        {
            return;
        }

        IsBusy = true;
        try
        {
            int purged = await Task.Run(() => rows.Count(r => _main.Services.Quarantine.Purge(r.Entry, out _)));
            AppLog.Info($"User purged {purged} quarantined item(s).");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
            await _main.Dashboard.RefreshAsync();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        string root = _main.Services.Quarantine.PrimaryRoot;
        if (System.IO.Directory.Exists(root))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
    }
}
