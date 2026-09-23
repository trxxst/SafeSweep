using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Services;
using SafeSweep.Core.Tools;

namespace SafeSweep.App.ViewModels;

public sealed partial class WindowsToolsViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private bool _isMeasuring;

    [ObservableProperty]
    private string _leftoverSummary = "Not checked yet.";

    public WindowsToolsViewModel(MainViewModel main)
    {
        _main = main;
    }

    public override string Title => "Windows tools";

    public override string Subtitle => "Some of the biggest Windows leftovers may only be removed by Windows itself. SafeSweep shows them and opens the official tool; it never deletes them directly.";

    public ObservableCollection<SystemLeftover> UpgradeLeftovers { get; } = [];

    public override async Task OnNavigatedToAsync()
    {
        if (UpgradeLeftovers.Count == 0 && !IsMeasuring)
        {
            await CheckAsync();
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        IsMeasuring = true;
        try
        {
            IReadOnlyList<SystemLeftover> found = await Task.Run(() => WindowsTools.FindUpgradeLeftovers(_main.Services.Paths, CancellationToken.None));
            UpgradeLeftovers.Clear();
            foreach (SystemLeftover leftover in found)
            {
                UpgradeLeftovers.Add(leftover);
            }

            LeftoverSummary = found.Count == 0
                ? "No previous Windows installation or upgrade files were found."
                : $"{found.Count} Windows upgrade location(s) found. Sizes count only the files your account can read.";
        }
        finally
        {
            IsMeasuring = false;
        }
    }

    [RelayCommand]
    private Task OpenStorageSettingsAsync() => Run(WindowsTools.OpenStorageSettings);

    [RelayCommand]
    private Task OpenDiskCleanupAsync() => Run(WindowsTools.OpenDiskCleanup);

    [RelayCommand]
    private async Task AnalyzeComponentStoreAsync()
    {
        bool ok = await DialogService.ConfirmAsync(
            "Analyse the component store",
            "This opens an administrator command window running DISM /AnalyzeComponentStore. It only reports; it changes nothing.",
            "Open");
        if (ok)
        {
            await Run(WindowsTools.AnalyzeComponentStore);
        }
    }

    [RelayCommand]
    private async Task ComponentCleanupAsync()
    {
        bool ok = await DialogService.ConfirmAsync(
            "Clean up the component store",
            "This runs Microsoft's own DISM /StartComponentCleanup in an administrator window. It removes superseded versions of Windows components that updates replaced. Already-installed updates can no longer be uninstalled afterwards. It can take several minutes.",
            "Run DISM");
        if (ok)
        {
            await Run(WindowsTools.RunComponentCleanup);
        }
    }

    [RelayCommand]
    private async Task DeliveryOptimizationAsync()
    {
        bool ok = await DialogService.ConfirmAsync(
            "Clear the Delivery Optimization cache",
            "This runs Windows' own Delete-DeliveryOptimizationCache command in an administrator window. The cache holds update files shared with other PCs; Windows downloads again whatever it still needs.",
            "Run");
        if (ok)
        {
            await Run(WindowsTools.ClearDeliveryOptimizationCache);
        }
    }

    private static async Task Run(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException ex)
        {
            await DialogService.ShowInfoAsync("Not started", ex.Message);
        }
    }
}
