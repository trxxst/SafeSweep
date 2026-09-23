namespace SafeSweep.Core.Models;

/// <summary>
/// Scan output for one category. Selection totals are maintained
/// incrementally so select-all on tens of thousands of items stays O(n).
/// </summary>
public sealed class CategoryResult : ObservableBase
{
    private readonly List<ScanItem> _items = [];
    private readonly List<string> _notes = [];
    private long _selectedBytes;
    private int _selectedCount;
    private int _selectableCount;
    private bool _bulkUpdate;

    public CategoryResult(CategoryDefinition definition)
    {
        Definition = definition;
    }

    public CategoryDefinition Definition { get; }

    public IReadOnlyList<ScanItem> Items => _items;

    /// <summary>Informational notes: skipped roots, running programs, missing admin rights.</summary>
    public IReadOnlyList<string> Notes => _notes;

    public string NotesDisplay => string.Join(Environment.NewLine, _notes);

    public bool HasNotes => _notes.Count > 0;

    public long TotalBytes { get; private set; }

    public int TotalCount => _items.Count;

    public long SelectedBytes => _selectedBytes;

    public int SelectedCount => _selectedCount;

    public string TotalDisplay => Formatting.Bytes(TotalBytes);

    public string SelectedDisplay => Formatting.Bytes(_selectedBytes);

    /// <summary>Tri-state: true = all selectable selected, false = none, null = some.</summary>
    public bool? IsSelected
    {
        get
        {
            if (_selectedCount == 0)
            {
                return false;
            }

            return _selectedCount >= _selectableCount ? true : null;
        }
        set => SetAllSelected(value == true);
    }

    public void Add(ScanItem item)
    {
        item.Owner = this;
        _items.Add(item);
        TotalBytes += item.SizeBytes;
        if (item.IsSelectable)
        {
            _selectableCount++;
        }

        if (item.IsSelected)
        {
            _selectedCount++;
            _selectedBytes += item.SizeBytes;
        }
    }

    public void AddNote(string note)
    {
        if (!_notes.Contains(note))
        {
            _notes.Add(note);
        }
    }

    public void SetAllSelected(bool selected)
    {
        _bulkUpdate = true;
        try
        {
            foreach (ScanItem item in _items)
            {
                item.IsSelected = selected && item.IsSelectable;
            }
        }
        finally
        {
            _bulkUpdate = false;
        }

        RaiseSelectionChanged();
    }

    /// <summary>Selects the given items only (used by filters and helpers).</summary>
    public void SetSelected(IEnumerable<ScanItem> items, bool selected)
    {
        _bulkUpdate = true;
        try
        {
            foreach (ScanItem item in items)
            {
                item.IsSelected = selected && item.IsSelectable;
            }
        }
        finally
        {
            _bulkUpdate = false;
        }

        RaiseSelectionChanged();
    }

    public event EventHandler? SelectionChanged;

    internal void OnItemSelectionChanged(ScanItem item, bool selected)
    {
        if (selected)
        {
            _selectedCount++;
            _selectedBytes += item.SizeBytes;
        }
        else
        {
            _selectedCount--;
            _selectedBytes -= item.SizeBytes;
        }

        if (!_bulkUpdate)
        {
            RaiseSelectionChanged();
        }
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedDisplay));
        OnPropertyChanged(nameof(IsSelected));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
