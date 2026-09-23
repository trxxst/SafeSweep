using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Services;
using SafeSweep.Core.Startup;

namespace SafeSweep.App.ViewModels;

public sealed partial class StartupViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private StartupEntry? _selected;

    [ObservableProperty]
    private string _summary = string.Empty;

    public StartupViewModel(MainViewModel main)
    {
        _main = main;
    }

    public override string Title => "Startup apps";

    public override string Subtitle => "Programs that start with Windows. Turning one off uses the same switch as Task Manager: nothing is deleted and you can turn it back on at any time.";

    public ObservableCollection<StartupEntry> Entries { get; } = [];

    public bool IsElevated => _main.IsElevated;

    public override Task OnNavigatedToAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IReadOnlyList<StartupEntry> entries = await Task.Run(StartupManager.Load);
        Entries.Clear();
        foreach (StartupEntry entry in entries)
        {
            Entries.Add(entry);
        }

        Summary = $"{entries.Count} startup entries, {entries.Count(e => e.IsEnabled)} enabled, {entries.Count(e => !e.FileExists)} pointing at a missing program.";
    }

    [RelayCommand]
    private async Task ToggleAsync(StartupEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        // The switch has already flipped visually; apply the opposite of the stored state.
        bool target = !entry.IsEnabled;
        if (!StartupManager.SetEnabled(entry, target, out string? error))
        {
            entry.RefreshState();
            await DialogService.ShowInfoAsync("Could not change this entry", error ?? "The change was refused.");
            return;
        }

        Summary = $"{Entries.Count} startup entries, {Entries.Count(e => e.IsEnabled)} enabled, {Entries.Count(e => !e.FileExists)} pointing at a missing program.";
    }

    [RelayCommand]
    private void OpenLocation(StartupEntry? entry)
    {
        if (entry is not null)
        {
            StartupManager.OpenLocation(entry);
        }
    }
}
