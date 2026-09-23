using System.Windows;

namespace SafeSweep.App.Views;

/// <summary>
/// Carries a DataContext into places outside the visual tree (DataGrid columns,
/// context menus), which cannot bind to it directly.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
