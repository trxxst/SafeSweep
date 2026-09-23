using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SafeSweep.App.Services;

public static class ThemeService
{
    /// <summary>Applies "System", "Light" or "Dark".</summary>
    public static void Apply(string theme)
    {
        ApplicationTheme target = theme switch
        {
            "Light" => ApplicationTheme.Light,
            "Dark" => ApplicationTheme.Dark,
            _ => SystemUsesLightTheme() ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };

        ApplicationThemeManager.Apply(target, WindowBackdropType.Mica, true);

        if (theme == "System" && Application.Current?.MainWindow is Window window)
        {
            if (window.IsLoaded)
            {
                SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
            }
            else
            {
                window.Loaded += (_, _) => SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
            }
        }
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
