using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafeSweep.Core.Logging;

namespace SafeSweep.App.ViewModels;

public sealed partial class LogsViewModel : PageViewModel
{
    private const int MaxEntries = 3000;
    private const string All = "Everything";
    private const string Changes = "Changes only (audit)";
    private const string Problems = "Warnings and errors";
    private const string NoDebug = "Hide debug details";

    [ObservableProperty]
    private string _filter = NoDebug;

    [ObservableProperty]
    private string _search = string.Empty;

    public LogsViewModel(MainViewModel main)
    {
        foreach (LogEntry entry in AppLog.Recent().Reverse())
        {
            Entries.Add(entry);
        }

        EntriesView = new ListCollectionView(Entries) { Filter = Matches };
        AppLog.EntryWritten += OnEntryWritten;
    }

    public override string Title => "Logs";

    public override string Subtitle => "A detailed record of every scan and every change SafeSweep made (AUDIT lines), newest first. Full daily log files are kept for 30 days.";

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public ICollectionView EntriesView { get; }

    public IReadOnlyList<string> Filters { get; } = [NoDebug, All, Changes, Problems];

    public string LogFolder => AppLog.LogDirectory ?? string.Empty;

    partial void OnFilterChanged(string value) => EntriesView.Refresh();

    partial void OnSearchChanged(string value) => EntriesView.Refresh();

    private bool Matches(object obj)
    {
        if (obj is not LogEntry entry)
        {
            return false;
        }

        bool levelOk = Filter switch
        {
            Changes => entry.Level == LogLevel.Audit,
            Problems => entry.Level is LogLevel.Warning or LogLevel.Error,
            NoDebug => entry.Level != LogLevel.Debug,
            _ => true,
        };

        return levelOk && (Search.Length == 0 || entry.Message.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    private void OnEntryWritten(LogEntry entry)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Entries.Insert(0, entry);
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!string.IsNullOrEmpty(AppLog.LogDirectory))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.LogDirectory}\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void OpenToday()
    {
        string? file = AppLog.CurrentLogFile;
        if (file is not null && System.IO.File.Exists(file))
        {
            AppLog.Flush();
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
    }
}
