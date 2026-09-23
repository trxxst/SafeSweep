using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.Core.Models;

namespace SafeSweep.App.ViewModels;

/// <summary>
/// Searchable, filterable, sortable view over a list of scan items, shared by
/// the Scan &amp; Clean page and every review tool.
/// </summary>
public sealed partial class ItemBrowser : ObservableObject
{
    private readonly DispatcherTimer _debounce;
    private List<ScanItem> _source = [];
    private int _wantedRisk = -1;

    [ObservableProperty]
    private ICollectionView? _itemsView;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _riskFilter = AllRisks;

    [ObservableProperty]
    private bool _onlySelected;

    [ObservableProperty]
    private ScanItem? _selectedItem;

    [ObservableProperty]
    private int _visibleCount;

    [ObservableProperty]
    private bool _showCategoryColumn;

    [ObservableProperty]
    private bool _showGroupColumn;

    private const string AllRisks = "All risk levels";

    public ItemBrowser()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };
    }

    public IReadOnlyList<string> RiskOptions { get; } = [AllRisks, "Very low risk", "Low risk", "Moderate risk", "High risk"];

    public int TotalCount => _source.Count;

    public void SetSource(IEnumerable<ScanItem> items)
    {
        foreach (CategoryResult owner in _owners)
        {
            owner.SelectionChanged -= OnOwnerSelectionChanged;
        }

        _source = items.ToList();
        _owners.Clear();
        foreach (CategoryResult owner in _source.Select(i => i.Owner).OfType<CategoryResult>().Distinct())
        {
            _owners.Add(owner);
            owner.SelectionChanged += OnOwnerSelectionChanged;
        }

        var view = new ListCollectionView(_source) { Filter = Matches };
        ItemsView = view;
        SelectedItem = null;
        OnPropertyChanged(nameof(TotalCount));
        VisibleCount = view.Count;
        RefreshSelectionState();
    }

    private readonly HashSet<CategoryResult> _owners = [];
    private bool? _allShownSelected = false;
    private bool _selectionRefreshQueued;

    /// <summary>
    /// Header "select all" for the rows currently shown (search and filters
    /// apply): true = all selectable shown rows selected, false = none, null = some.
    /// Setting it selects or clears exactly the shown rows.
    /// </summary>
    public bool? AllShownSelected
    {
        get => _allShownSelected;
        set
        {
            SetShown(value == true);
            RefreshSelectionState();
        }
    }

    [ObservableProperty]
    private int _selectedShownCount;

    public bool HasSelectableRows => _source.Any(i => i.IsSelectable);

    /// <summary>Recomputes the header state now (also used by tests).</summary>
    public void RefreshSelectionState()
    {
        _selectionRefreshQueued = false;
        int selectable = 0;
        int selected = 0;
        if (ItemsView is not null)
        {
            foreach (ScanItem item in ItemsView)
            {
                if (!item.IsSelectable)
                {
                    continue;
                }

                selectable++;
                if (item.IsSelected)
                {
                    selected++;
                }
            }
        }

        bool? state = selected == 0 ? false : selected >= selectable ? true : null;
        SelectedShownCount = selected;
        if (_allShownSelected != state)
        {
            _allShownSelected = state;
        }

        OnPropertyChanged(nameof(AllShownSelected));
        OnPropertyChanged(nameof(HasSelectableRows));
    }

    private void OnOwnerSelectionChanged(object? sender, EventArgs e)
    {
        // Bulk changes raise many events; recompute once when the dispatcher is idle.
        if (_selectionRefreshQueued)
        {
            return;
        }

        _selectionRefreshQueued = true;
        System.Windows.Threading.Dispatcher dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(RefreshSelectionState, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Space bar on the highlighted row.</summary>
    [RelayCommand]
    private void ToggleSelectedItem()
    {
        if (SelectedItem is { IsSelectable: true } item)
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    public void Clear() => SetSource([]);

    partial void OnSearchTextChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    partial void OnRiskFilterChanged(string value)
    {
        _wantedRisk = RiskOptions.ToList().IndexOf(value) - 1;
        Refresh();
    }

    partial void OnOnlySelectedChanged(bool value) => Refresh();

    private void Refresh()
    {
        if (ItemsView is null)
        {
            return;
        }

        ItemsView.Refresh();
        VisibleCount = ItemsView is ListCollectionView l ? l.Count : 0;
        RefreshSelectionState();
    }

    private bool Matches(object obj)
    {
        if (obj is not ScanItem item)
        {
            return false;
        }

        if (OnlySelected && !item.IsSelected)
        {
            return false;
        }

        if (_wantedRisk >= 0 && (int)item.Risk != _wantedRisk)
        {
            return false;
        }

        string search = SearchText.Trim();
        return search.Length == 0
            || item.Path.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.Reason.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.Category.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<ScanItem> Shown()
        => ItemsView is null ? [] : ItemsView.Cast<ScanItem>().ToList();

    [RelayCommand]
    private void SelectShown() => SetShown(true);

    [RelayCommand]
    private void DeselectShown() => SetShown(false);

    private void SetShown(bool selected)
    {
        foreach (IGrouping<CategoryResult?, ScanItem> group in Shown().GroupBy(i => i.Owner))
        {
            if (group.Key is null)
            {
                foreach (ScanItem item in group)
                {
                    item.IsSelected = selected;
                }
            }
            else
            {
                group.Key.SetSelected(group, selected);
            }
        }
    }

    [RelayCommand]
    private void OpenLocation(ScanItem? item)
    {
        if (item is null || item.Kind == ItemKind.RecycleBin)
        {
            return;
        }

        try
        {
            string argument = System.IO.File.Exists(item.Path) || System.IO.Directory.Exists(item.Path)
                ? $"/select,\"{item.Path}\""
                : $"\"{item.Directory}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Explorer unavailable; nothing to do.
        }
    }

    [RelayCommand]
    private void CopyPath(ScanItem? item)
    {
        if (item is not null)
        {
            try
            {
                Clipboard.SetText(item.Path);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Clipboard busy; ignore.
            }
        }
    }
}
