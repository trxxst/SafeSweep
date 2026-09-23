using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Services;
using SafeSweep.Core.Settings;

namespace SafeSweep.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private bool _simulationMode;

    [ObservableProperty]
    private bool _useRecycleBinForReview;

    [ObservableProperty]
    private double _quarantineRetentionDays;

    [ObservableProperty]
    private double _tempMinAgeHours;

    [ObservableProperty]
    private double _largeFileThresholdMb;

    [ObservableProperty]
    private double _oldFileDays;

    [ObservableProperty]
    private double _oldInstallerDays;

    [ObservableProperty]
    private double _duplicateMinSizeKb;

    [ObservableProperty]
    private double _leftoverMinIdleDays;

    [ObservableProperty]
    private string _theme = "System";

    [ObservableProperty]
    private string? _savedMessage;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
    }

    public override string Title => "Settings";

    public override string Subtitle => "Defaults are deliberately conservative. Changing them never switches off the protection rules.";

    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];

    public ObservableCollection<string> ReviewRoots { get; } = [];

    public string DataFolder => _main.Services.Paths.AppDataRoot;

    public string VersionText => _main.VersionText;

    [RelayCommand]
    private void OpenLink(string? which)
    {
        string url = which switch
        {
            "releases" => AppInfo.ReleasesUrl,
            "issues" => AppInfo.IssuesUrl,
            _ => AppInfo.RepositoryUrl,
        };

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser registered; nothing else to do.
        }
    }

    public override Task OnNavigatedToAsync()
    {
        Load(_main.Services.Settings);
        SavedMessage = null;
        return Task.CompletedTask;
    }

    private void Load(AppSettings s)
    {
        SimulationMode = s.SimulationMode;
        UseRecycleBinForReview = s.UseRecycleBinForReview;
        QuarantineRetentionDays = s.QuarantineRetentionDays;
        TempMinAgeHours = s.TempMinAgeHours;
        LargeFileThresholdMb = s.LargeFileThresholdMb;
        OldFileDays = s.OldFileDays;
        OldInstallerDays = s.OldInstallerDays;
        DuplicateMinSizeKb = s.DuplicateMinSizeKb;
        LeftoverMinIdleDays = s.LeftoverMinIdleDays;
        Theme = s.Theme;
        ReviewRoots.Clear();
        foreach (string root in s.ReviewRoots.Count > 0 ? s.ReviewRoots : _main.Services.Paths.DefaultReviewRoots)
        {
            ReviewRoots.Add(root);
        }
    }

    [RelayCommand]
    private void AddRoot()
    {
        string? folder = DialogService.PickFolder("Choose a folder for the review tools");
        if (folder is not null && !ReviewRoots.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            ReviewRoots.Add(folder);
        }
    }

    [RelayCommand]
    private void RemoveRoot(string? root)
    {
        if (root is not null)
        {
            ReviewRoots.Remove(root);
        }
    }

    [RelayCommand]
    private void Save()
    {
        AppSettings s = _main.Services.Settings;
        s.SimulationMode = SimulationMode;
        s.UseRecycleBinForReview = UseRecycleBinForReview;
        s.QuarantineRetentionDays = (int)QuarantineRetentionDays;
        s.TempMinAgeHours = (int)TempMinAgeHours;
        s.LargeFileThresholdMb = (int)LargeFileThresholdMb;
        s.OldFileDays = (int)OldFileDays;
        s.OldInstallerDays = (int)OldInstallerDays;
        s.DuplicateMinSizeKb = (int)DuplicateMinSizeKb;
        s.LeftoverMinIdleDays = (int)LeftoverMinIdleDays;
        s.Theme = Theme;

        IReadOnlyList<string> defaults = _main.Services.Paths.DefaultReviewRoots;
        s.ReviewRoots = ReviewRoots.SequenceEqual(defaults, StringComparer.OrdinalIgnoreCase) ? [] : ReviewRoots.ToList();

        _main.Services.SaveSettings(s);
        Load(s); // show the values after clamping
        ThemeService.Apply(s.Theme);
        _main.NotifySettingsChanged();
        SavedMessage = "Settings saved.";
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        var defaults = new AppSettings();
        defaults.Normalize();
        AppSettings current = _main.Services.Settings;

        // Keep the user's own exclusions and schedule choices; reset only the tuning values.
        defaults.Exclusions = current.Exclusions;
        defaults.ScheduledCategoryIds = current.ScheduledCategoryIds;
        Load(defaults);
        SavedMessage = "Defaults loaded. Press Save to apply them.";
    }
}
