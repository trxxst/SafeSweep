using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Converters;
using SafeSweep.Core.Cleaning;
using SafeSweep.Core.Models;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Scanning;
using Wpf.Ui.Controls;

namespace SafeSweep.App.ViewModels;

public sealed partial class CategoryChoice : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public CategoryChoice(CategoryDefinition definition, bool isChecked)
    {
        Definition = definition;
        _isChecked = isChecked;
    }

    public CategoryDefinition Definition { get; }

    public string LevelText => LevelToTextConverter.Text(Definition.Level);
}

/// <summary>Quick / Full / Custom scan, category review and cleaning.</summary>
public sealed partial class CleanerViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private List<CategoryResult> _results = [];

    [ObservableProperty]
    private ICollectionView? _categoriesView;

    [ObservableProperty]
    private CategoryResult? _selectedCategory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResults), nameof(ShowEmptyState))]
    private bool _hasResult;

    [ObservableProperty]
    private string _progressStage = string.Empty;

    [ObservableProperty]
    private string _progressDetail = string.Empty;

    [ObservableProperty]
    private string _progressCounts = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIndeterminate = true;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private long _selectedBytes;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private long _safeBytes;

    [ObservableProperty]
    private long _deepBytes;

    [ObservableProperty]
    private long _reviewBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResults), nameof(ShowEmptyState))]
    private bool _isCustomOpen;

    [ObservableProperty]
    private string? _cleanMessage;

    [ObservableProperty]
    private InfoBarSeverity _cleanSeverity = InfoBarSeverity.Success;

    [ObservableProperty]
    private bool _showCleanMessage;

    public CleanerViewModel(MainViewModel main)
    {
        _main = main;
        CustomChoices = new ObservableCollection<CategoryChoice>(
            RuleCatalog.AllCategories
                .Where(c => c.Id is not ("large-files" or "duplicates"))
                .Select(c => new CategoryChoice(c, c.InQuickScan)));
        CustomChoicesView = new ListCollectionView(CustomChoices);
        CustomChoicesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CategoryChoice.LevelText)));
    }

    public override string Title => "Scan & Clean";

    public override string Subtitle => "Scan first, review what was found and why, then clean only what you select.";

    public ItemBrowser Browser { get; } = new() { ShowCategoryColumn = true };

    public ObservableCollection<CategoryChoice> CustomChoices { get; }

    public ListCollectionView CustomChoicesView { get; }

    public ObservableCollection<CleanItemResult> Problems { get; } = [];

    public bool IsIdle => !IsBusy;

    public bool ShowResults => HasResult && !IsCustomOpen;

    public bool ShowEmptyState => !HasResult && !IsCustomOpen;

    public bool IsAllSelected => SelectedCategory is null;

    public string DetailTitle => SelectedCategory?.Definition.Name ?? "All items found";

    partial void OnSelectedCategoryChanged(CategoryResult? value)
    {
        Browser.ShowCategoryColumn = value is null;
        Browser.SetSource(value?.Items ?? _results.SelectMany(r => r.Items));
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(DetailTitle));
    }

    [RelayCommand]
    private void ShowAll() => SelectedCategory = null;

    [RelayCommand]
    private Task QuickScanAsync() => ScanAsync(new ScanRequest(ScanMode.Quick));

    [RelayCommand]
    private Task FullScanAsync() => ScanAsync(new ScanRequest(ScanMode.Full));

    [RelayCommand]
    private void ToggleCustom() => IsCustomOpen = !IsCustomOpen;

    [RelayCommand]
    private Task CustomScanAsync()
    {
        IsCustomOpen = false;
        var ids = CustomChoices.Where(c => c.IsChecked).Select(c => c.Definition.Id).ToList();
        return ids.Count == 0 ? Task.CompletedTask : ScanAsync(new ScanRequest(ScanMode.Custom, ids));
    }

    [RelayCommand]
    private void CustomSelectAll(string level)
    {
        foreach (CategoryChoice choice in CustomChoices.Where(c => level == "all" || c.LevelText == level))
        {
            choice.IsChecked = true;
        }
    }

    [RelayCommand]
    private void CustomClear()
    {
        foreach (CategoryChoice choice in CustomChoices)
        {
            choice.IsChecked = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    public async Task ScanAsync(ScanRequest request)
    {
        if (IsBusy)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ShowCleanMessage = false;
        Problems.Clear();
        ProgressIndeterminate = true;
        ProgressStage = "Starting scan";
        ProgressDetail = string.Empty;
        ProgressCounts = string.Empty;

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressStage = p.Stage;
            if (p.CurrentPath is not null)
            {
                ProgressDetail = p.CurrentPath;
            }

            ProgressCounts = $"{p.ItemsFound:N0} items, {Formatting.Bytes(p.BytesFound)} found so far";
            ProgressIndeterminate = p.Fraction is null;
            ProgressValue = (p.Fraction ?? 0) * 100;
        });

        try
        {
            ScanResult result = await new ScanEngine(_main.Services).ScanAsync(request, progress, _cts.Token);
            ApplyResults(result.Categories.ToList());
            ResultSummary = $"{request.Mode} scan{(result.Cancelled ? " (cancelled, partial)" : string.Empty)} finished in {result.Duration.TotalSeconds:0.0} s: "
                            + $"{result.TotalItems:N0} items, {Formatting.Bytes(result.TotalBytes)}. "
                            + $"{result.ProtectedSkipped:N0} protected item(s) were found and deliberately not offered.";
            _main.PublishScan(result);
        }
        catch (OperationCanceledException)
        {
            ResultSummary = "Scan cancelled.";
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ApplyResults(List<CategoryResult> results)
    {
        foreach (CategoryResult old in _results)
        {
            old.SelectionChanged -= OnSelectionChanged;
        }

        _results = results;
        foreach (CategoryResult r in _results)
        {
            r.SelectionChanged += OnSelectionChanged;
        }

        var view = new ListCollectionView(_results);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Definition.Level"));
        CategoriesView = view;
        HasResult = true;
        SelectedCategory = null;
        OnSelectedCategoryChanged(null);
        UpdateTotals();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateTotals();

    private void UpdateTotals()
    {
        SelectedBytes = _results.Sum(r => r.SelectedBytes);
        SelectedCount = _results.Sum(r => r.SelectedCount);
        SafeBytes = _results.Where(r => r.Definition.Level == CleanLevel.Safe).Sum(r => r.TotalBytes);
        DeepBytes = _results.Where(r => r.Definition.Level == CleanLevel.Deep).Sum(r => r.TotalBytes);
        ReviewBytes = _results.Where(r => r.Definition.Level == CleanLevel.ManualReview).Sum(r => r.TotalBytes);
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (CategoryResult r in _results)
        {
            r.SetAllSelected(r.Definition.PreSelect);
        }
    }

    [RelayCommand]
    private void SelectSafeAndDeep()
    {
        foreach (CategoryResult r in _results)
        {
            // The Recycle Bin is never bulk-selected: emptying it is permanent.
            bool select = r.Definition.Level != CleanLevel.ManualReview && r.Definition.Intent != DeletionIntent.RecycleBin;
            r.SetAllSelected(select);
        }
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (CategoryResult r in _results)
        {
            r.SetAllSelected(false);
        }
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        List<ScanItem> selected = _results.SelectMany(r => r.Items).Where(i => i.IsSelected).ToList();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressStage = "Cleaning";
        ProgressDetail = string.Empty;
        ProgressIndeterminate = false;
        ProgressValue = 0;

        var progress = new Progress<CleanProgress>(p =>
        {
            ProgressValue = p.Total == 0 ? 100 : 100.0 * p.Processed / p.Total;
            ProgressDetail = p.CurrentPath ?? string.Empty;
            ProgressCounts = $"{p.Processed:N0} of {p.Total:N0} items, {Formatting.Bytes(p.Bytes)}";
        });

        try
        {
            CleanReport? report = await CleanFlow.ConfirmAndRunAsync(_main, selected, progress, _cts.Token);
            if (report is null)
            {
                return;
            }

            CleanMessage = CleanFlow.Summarize(report);
            CleanSeverity = report.Problems.Any() ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            ShowCleanMessage = true;
            Problems.Clear();
            foreach (CleanItemResult problem in report.Problems.Take(500))
            {
                Problems.Add(problem);
            }

            if (!report.Simulation)
            {
                ApplyResults(CleanFlow.Prune(_results, report));
            }

            await _main.Dashboard.RefreshAsync();
            await _main.Quarantine.RefreshAsync();
            await _main.History.RefreshAsync();
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }
}
