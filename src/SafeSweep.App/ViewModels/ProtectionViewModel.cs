using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Services;
using SafeSweep.Core.Safety;
using SafeSweep.Core.Settings;

namespace SafeSweep.App.ViewModels;

/// <summary>Shows every protection rule in force and lets the user add their own exclusions.</summary>
public sealed partial class ProtectionViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private ICollectionView? _locationsView;

    [ObservableProperty]
    private string _search = string.Empty;

    public ProtectionViewModel(MainViewModel main)
    {
        _main = main;
    }

    public override string Title => "Protection";

    public override string Subtitle => "Everything SafeSweep refuses to delete, even if a scan somehow offered it. These checks run again immediately before every single deletion.";

    public ObservableCollection<ProtectedLocation> Locations { get; } = [];

    public ObservableCollection<CleanupException> Exceptions { get; } = [];

    public ObservableCollection<string> Exclusions { get; } = [];

    public string ProtectedNames => string.Join(", ", ProtectionPolicy.CriticalFileNames.Concat(ProtectionPolicy.CriticalNamePatterns));

    public string ProtectedFolderNames => string.Join(", ", ProtectionPolicy.ProtectedSegmentNames);

    public string BrowserNames => string.Join(", ", ProtectionPolicy.BrowserProtectedNames);

    public string GeneralRules =>
        "- Only local fixed drives; never network, removable or optical drives.\n" +
        "- Never a drive root or a top-level folder.\n" +
        "- Never follows or removes symbolic links or junctions; a path that passes through one is re-checked at its real location.\n" +
        "- Never touches files with several hard links, files marked System, or OneDrive/cloud placeholder files.\n" +
        "- Automatic cleaning only inside registered cleaning locations, and those locations may never be a drive root, your profile, AppData itself or a personal folder.\n" +
        "- Items are re-checked right before removal; anything that changed since the scan is skipped.\n" +
        "- Only Safe Clean items and empty folders are deleted outright, and emptying the Recycle Bin is permanent by nature. Everything else is quarantined (or sent to the Recycle Bin when you choose that and it is guaranteed to keep the file).";

    public override Task OnNavigatedToAsync()
    {
        ProtectionPolicy policy = _main.Services.Policy;
        Locations.Clear();
        foreach (ProtectedLocation location in policy.ProtectedLocations.OrderBy(l => l.Group).ThenBy(l => l.Path))
        {
            Locations.Add(location);
        }

        var view = new ListCollectionView(Locations) { Filter = Filter };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProtectedLocation.Group)));
        LocationsView = view;

        Exceptions.Clear();
        foreach (CleanupException exception in policy.CleanupExceptions)
        {
            Exceptions.Add(exception);
        }

        Exclusions.Clear();
        foreach (string exclusion in _main.Services.Settings.Exclusions)
        {
            Exclusions.Add(exclusion);
        }

        return Task.CompletedTask;
    }

    partial void OnSearchChanged(string value) => LocationsView?.Refresh();

    private bool Filter(object obj)
        => obj is ProtectedLocation l
           && (Search.Length == 0
               || l.Path.Contains(Search, StringComparison.OrdinalIgnoreCase)
               || l.Reason.Contains(Search, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void AddExclusion()
    {
        string? folder = DialogService.PickFolder("Choose a folder SafeSweep must never touch");
        if (folder is null || Exclusions.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Exclusions.Add(folder);
        SaveExclusions();
    }

    [RelayCommand]
    private async Task RemoveExclusionAsync(string? exclusion)
    {
        if (exclusion is null)
        {
            return;
        }

        bool ok = await DialogService.ConfirmAsync(
            "Remove exclusion",
            $"SafeSweep will no longer skip this location:\n{exclusion}\n\nThe normal protection rules still apply to it.",
            "Remove");
        if (ok)
        {
            Exclusions.Remove(exclusion);
            SaveExclusions();
        }
    }

    private void SaveExclusions()
    {
        AppSettings settings = _main.Services.Settings;
        settings.Exclusions = Exclusions.ToList();
        _main.Services.SaveSettings(settings);
    }
}
