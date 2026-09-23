using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.App.Services;
using SafeSweep.Core.Cleaning;
using SafeSweep.Core.Models;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Scanning;
using Wpf.Ui.Controls;

namespace SafeSweep.App.ViewModels;

/// <summary>
/// One review tool: large files, duplicates, old files or application
/// leftovers. Nothing is ever pre-selected here.
/// </summary>
public sealed partial class ReviewToolViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private CategoryResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _progressStage = string.Empty;

    [ObservableProperty]
    private string _progressDetail = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIndeterminate = true;

    [ObservableProperty]
    private double _parameter;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    private string _notes = string.Empty;

    public bool HasNotes => !string.IsNullOrEmpty(Notes);

    [ObservableProperty]
    private long _selectedBytes;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private string? _cleanMessage;

    [ObservableProperty]
    private InfoBarSeverity _cleanSeverity = InfoBarSeverity.Success;

    [ObservableProperty]
    private bool _showCleanMessage;

    private ReviewToolViewModel(MainViewModel main, CategoryDefinition category, string title, string subtitle)
    {
        _main = main;
        Category = category;
        _title = title;
        _subtitle = subtitle;
    }

    private readonly string _title;
    private readonly string _subtitle;

    public override string Title => _title;

    public override string Subtitle => _subtitle;

    public CategoryDefinition Category { get; }

    public ItemBrowser Browser { get; } = new();

    public ObservableCollection<string> Roots { get; } = [];

    public ObservableCollection<CleanItemResult> Problems { get; } = [];

    public bool UsesRoots { get; private init; } = true;

    public bool UsesParameter { get; private init; }

    public string ParameterLabel { get; private init; } = string.Empty;

    public double ParameterMinimum { get; private init; }

    public bool IsDuplicates => Category.Id == Categories.Duplicates.Id;

    public bool IsIdle => !IsBusy;

    public string ScanButtonText { get; private init; } = "Scan";

    public static ReviewToolViewModel CreateLargeFiles(MainViewModel main) => new(main, Categories.LargeFiles, "Large files",
        "Find the biggest files in the folders you choose. Size alone never makes a file removable; you decide.")
    {
        UsesParameter = true,
        ParameterLabel = "Minimum size (MB)",
        ParameterMinimum = 10,
        Parameter = main.Services.Settings.LargeFileThresholdMb,
        ScanButtonText = "Find large files",
    };

    public static ReviewToolViewModel CreateDuplicates(MainViewModel main) => new(main, Categories.Duplicates, "Duplicate files",
        "Byte-for-byte identical files, verified with SHA-256 (never by name). At least one copy of every group is always kept.")
    {
        UsesParameter = true,
        ParameterLabel = "Minimum size (KB)",
        ParameterMinimum = 1,
        Parameter = main.Services.Settings.DuplicateMinSizeKb,
        ScanButtonText = "Find duplicates",
    };

    public static ReviewToolViewModel CreateOldFiles(MainViewModel main) => new(main, Categories.OldFiles, "Old / unused files",
        $"Your files that have not changed for a long time ({main.Services.Settings.OldFileDays} days, adjustable in Settings). Shown for review only; nothing is selected for you.")
    {
        ScanButtonText = "Find old files",
    };

    public static ReviewToolViewModel CreateLeftovers(MainViewModel main) => new(main, Categories.Leftovers, "Uninstalled application leftovers",
        "Folders in AppData and ProgramData that no installed program, service, startup entry, scheduled task or running process claims.")
    {
        UsesRoots = false,
        ScanButtonText = "Find leftovers",
    };

    public override Task OnNavigatedToAsync()
    {
        if (UsesRoots && Roots.Count == 0)
        {
            IEnumerable<string> roots = _main.Services.Settings.ReviewRoots.Count > 0
                ? _main.Services.Settings.ReviewRoots
                : _main.Services.Paths.DefaultReviewRoots;
            foreach (string root in roots)
            {
                Roots.Add(root);
            }
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void AddRoot()
    {
        string? folder = DialogService.PickFolder("Choose a folder to scan");
        if (folder is not null && !Roots.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            Roots.Add(folder);
        }
    }

    [RelayCommand]
    private void RemoveRoot(string? root)
    {
        if (root is not null)
        {
            Roots.Remove(root);
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ShowCleanMessage = false;
        Problems.Clear();
        ProgressStage = "Starting";
        ProgressDetail = string.Empty;
        ProgressIndeterminate = true;

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressStage = p.Stage;
            if (p.CurrentPath is not null)
            {
                ProgressDetail = p.CurrentPath;
            }

            ProgressIndeterminate = p.Fraction is null;
            ProgressValue = (p.Fraction ?? 0) * 100;
        });

        long parameter = Category.Id switch
        {
            "large-files" => (long)Math.Max(ParameterMinimum, Parameter) * 1024L * 1024L,
            "duplicates" => (long)Math.Max(ParameterMinimum, Parameter) * 1024L,
            _ => 0,
        };

        try
        {
            CategoryResult result = await new ScanEngine(_main.Services).RunToolAsync(
                Category.Id,
                UsesRoots ? Roots.ToList() : null,
                parameter,
                progress,
                _cts.Token);
            SetResult(result);
            ResultSummary = $"{result.TotalCount:N0} item(s), {Formatting.Bytes(result.TotalBytes)}.";
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

    private void SetResult(CategoryResult result)
    {
        if (_result is not null)
        {
            _result.SelectionChanged -= OnSelectionChanged;
        }

        _result = result;
        _result.SelectionChanged += OnSelectionChanged;
        Browser.ShowGroupColumn = IsDuplicates;
        Browser.SetSource(result.Items);
        Notes = result.NotesDisplay;
        HasResult = true;
        UpdateTotals();
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateTotals();

    private void UpdateTotals()
    {
        SelectedBytes = _result?.SelectedBytes ?? 0;
        SelectedCount = _result?.SelectedCount ?? 0;
    }

    /// <summary>Duplicates: select every copy except the newest (or oldest) in each group.</summary>
    [RelayCommand]
    private void SelectAllButOne(string keep)
    {
        if (_result is null)
        {
            return;
        }

        var toSelect = new List<ScanItem>();
        foreach (IGrouping<int?, ScanItem> group in _result.Items.Where(i => i.DuplicateGroupId is not null).GroupBy(i => i.DuplicateGroupId))
        {
            List<ScanItem> ordered = keep == "oldest"
                ? group.OrderBy(i => i.LastWriteUtc).ToList()
                : group.OrderByDescending(i => i.LastWriteUtc).ToList();
            toSelect.AddRange(ordered.Skip(1));
        }

        _result.SetAllSelected(false);
        _result.SetSelected(toSelect, true);
    }

    [RelayCommand]
    private void DeselectAll() => _result?.SetAllSelected(false);

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (_result is null || IsBusy)
        {
            return;
        }

        List<ScanItem> selected = _result.Items.Where(i => i.IsSelected).ToList();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressStage = "Removing selected items";
        ProgressIndeterminate = false;

        var progress = new Progress<CleanProgress>(p =>
        {
            ProgressValue = p.Total == 0 ? 100 : 100.0 * p.Processed / p.Total;
            ProgressDetail = p.CurrentPath ?? string.Empty;
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
                SetResult(CleanFlow.Prune([_result], report)[0]);
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
