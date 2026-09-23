using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Services;
using SafeSweep.Core.Models;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Scheduling;
using SafeSweep.Core.Settings;

namespace SafeSweep.App.ViewModels;

public sealed partial class ScheduleViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    [ObservableProperty]
    private bool _exists;

    [ObservableProperty]
    private string _status = "Checking...";

    [ObservableProperty]
    private ScheduleFrequency _frequency = ScheduleFrequency.Weekly;

    [ObservableProperty]
    private DayOfWeek _day = DayOfWeek.Sunday;

    [ObservableProperty]
    private string _time = "10:00";

    [ObservableProperty]
    private bool _skipOnBattery;

    public ScheduleViewModel(MainViewModel main)
    {
        _main = main;
    }

    public override string Title => "Scheduled cleaning";

    public override string Subtitle => "Let Windows Task Scheduler run a Safe Clean automatically. Scheduled runs only ever clean Safe categories: never Deep Clean, never Manual Review, never anything needing administrator rights.";

    public IReadOnlyList<ScheduleFrequency> Frequencies { get; } = Enum.GetValues<ScheduleFrequency>();

    public IReadOnlyList<DayOfWeek> Days { get; } = Enum.GetValues<DayOfWeek>();

    public ObservableCollection<CategoryChoice> Choices { get; } = [];

    public override async Task OnNavigatedToAsync()
    {
        AppSettings settings = _main.Services.Settings;
        SkipOnBattery = settings.ScheduledSkipOnBattery;
        Choices.Clear();
        foreach (CategoryDefinition category in RuleCatalog.AllCategories.Where(c => c.AllowScheduled && c.Intent == DeletionIntent.RuleCleanup))
        {
            bool isChecked = settings.ScheduledCategoryIds.Count == 0 ? !category.RequiresAdmin : settings.ScheduledCategoryIds.Contains(category.Id);
            Choices.Add(new CategoryChoice(category, isChecked));
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        ScheduleInfo info = await Task.Run(ScheduleService.Query);
        Exists = info.Exists;
        Status = info.Exists
            ? $"Active. Next run: {info.NextRun ?? "unknown"}. Last run: {info.LastRun ?? "never"}. State: {info.Status ?? "unknown"}."
            : "No scheduled cleaning is set up.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TimeOnly.TryParseExact(Time.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time))
        {
            await DialogService.ShowInfoAsync("Invalid time", "Enter the time as HH:mm, for example 10:00 or 18:30.");
            return;
        }

        AppSettings settings = _main.Services.Settings;
        settings.ScheduledCategoryIds = Choices.Where(c => c.IsChecked).Select(c => c.Definition.Id).ToList();
        settings.ScheduledSkipOnBattery = SkipOnBattery;
        if (settings.ScheduledCategoryIds.Count == 0)
        {
            await DialogService.ShowInfoAsync("No categories", "Choose at least one Safe category for the scheduled clean.");
            return;
        }

        _main.Services.SaveSettings(settings);

        ScheduleFrequency frequency = Frequency;
        DayOfWeek day = Day;
        (bool created, string message) = await Task.Run(() =>
        {
            bool ok = ScheduleService.Create(frequency, day, time, out string text);
            return (ok, text);
        });

        await DialogService.ShowInfoAsync(created ? "Scheduled cleaning saved" : "Could not create the schedule", message);

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        bool confirmed = await DialogService.ConfirmAsync("Remove scheduled cleaning", "Stop cleaning automatically? You can set it up again at any time.", "Remove");
        if (!confirmed)
        {
            return;
        }

        await Task.Run(() => ScheduleService.Delete(out _));
        await RefreshAsync();
    }
}
