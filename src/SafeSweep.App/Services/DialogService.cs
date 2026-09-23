using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SafeSweep.App.Services;

/// <summary>Fluent-styled dialogs. Every destructive action goes through one of these.</summary>
public static class DialogService
{
    public static async Task<bool> ConfirmAsync(string title, string message, string primaryText, bool danger = false, string? acknowledgement = null, IEnumerable<string>? details = null)
    {
        var panel = new StackPanel { MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        if (details is not null)
        {
            foreach (string line in details)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = line,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 2, 0, 2),
                    Opacity = 0.85,
                });
            }
        }

        var box = new MessageBox
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            MinWidth = 420,
        };

        if (acknowledgement is not null)
        {
            var check = new System.Windows.Controls.CheckBox
            {
                Content = new TextBlock { Text = acknowledgement, TextWrapping = TextWrapping.Wrap },
                Margin = new Thickness(0, 12, 0, 0),
            };
            box.IsPrimaryButtonEnabled = false;
            check.Checked += (_, _) => box.IsPrimaryButtonEnabled = true;
            check.Unchecked += (_, _) => box.IsPrimaryButtonEnabled = false;
            panel.Children.Add(check);
        }

        MessageBoxResult result = await box.ShowDialogAsync();
        return result == MessageBoxResult.Primary;
    }

    public static Task ShowInfoAsync(string title, string message) => ShowAsync(title, message);

    public static void ShowError(string title, string message) => _ = ShowAsync(title, message);

    private static async Task ShowAsync(string title, string message)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
            CloseButtonText = "OK",
            MinWidth = 380,
        };

        await box.ShowDialogAsync();
    }

    /// <summary>Modern folder picker (built into WPF on .NET 8+).</summary>
    public static string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
