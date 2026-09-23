using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace SafeSweep.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    public abstract string Subtitle { get; }

    /// <summary>Called every time the page is shown.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
}

public sealed class NavItem
{
    public NavItem(string title, SymbolRegular symbol, PageViewModel page)
    {
        Title = title;
        Symbol = symbol;
        Page = page;
    }

    private NavItem(string header)
    {
        Title = header;
        IsHeader = true;
    }

    public string Title { get; }

    public SymbolRegular Symbol { get; }

    public PageViewModel? Page { get; }

    public bool IsHeader { get; }

    public static NavItem Header(string title) => new(title);
}
