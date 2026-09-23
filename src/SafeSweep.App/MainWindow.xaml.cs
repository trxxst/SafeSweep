using System.Windows;
using SafeSweep.App.ViewModels;
using Wpf.Ui.Controls;

namespace SafeSweep.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // On short screens (laptops at 125-150% scaling) start maximised so the
        // results grid has room.
        if (SystemParameters.WorkArea.Height < Height + 40)
        {
            WindowState = WindowState.Maximized;
        }

        SizeChanged += (_, e) => (DataContext as MainViewModel)?.UpdateLayoutMode(e.NewSize.Width, e.NewSize.Height);
        DataContextChanged += (_, _) => (DataContext as MainViewModel)?.UpdateLayoutMode(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
    }
}
