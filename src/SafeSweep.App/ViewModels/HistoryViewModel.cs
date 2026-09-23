using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Services;
using SafeSweep.Core.History;

namespace SafeSweep.App.ViewModels;

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private HistoryEntry? _selected;

    [ObservableProperty]
    private long _totalFreed;

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
    }

    public override string Title => "Cleaning history";

    public override string Subtitle => "Every clean, manual or scheduled, with what it did and anything it skipped or refused.";

    public ObservableCollection<HistoryEntry> Entries { get; } = [];

    public override Task OnNavigatedToAsync() => RefreshAsync();

    public async Task RefreshAsync()
    {
        IReadOnlyList<HistoryEntry> entries = await Task.Run(_main.Services.History.Load);
        Entries.Clear();
        foreach (HistoryEntry entry in entries)
        {
            Entries.Add(entry);
        }

        TotalFreed = entries.Where(e => !e.Simulation).Sum(e => e.BytesFreed + e.BytesRecycled);
        Selected = Entries.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        bool ok = await DialogService.ConfirmAsync(
            "Clear history",
            "Remove the list of past cleans? This does not affect quarantined items or the detailed log files.",
            "Clear history");
        if (ok)
        {
            _main.Services.History.Clear();
            await RefreshAsync();
        }
    }
}
