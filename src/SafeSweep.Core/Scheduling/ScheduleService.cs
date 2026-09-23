using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SafeSweep.Core.Logging;

namespace SafeSweep.Core.Scheduling;

public enum ScheduleFrequency
{
    Daily,
    Weekly,
    Monthly,
}

public sealed record ScheduleInfo(bool Exists, string? NextRun, string? LastRun, string? Status);

/// <summary>
/// Registers a per-user Windows Task Scheduler task that runs SafeSweep in
/// headless mode. The task runs with normal (non-elevated) rights and only ever
/// cleans Safe categories.
/// </summary>
public static partial class ScheduleService
{
    public const string TaskName = @"SafeSweep\Scheduled Safe Clean";

    [GeneratedRegex("\"((?:[^\"]|\"\")*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex CsvFields();

    public static bool Create(ScheduleFrequency frequency, DayOfWeek day, TimeOnly time, out string message)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            message = "The SafeSweep executable path could not be determined.";
            return false;
        }

        string schedule = frequency switch
        {
            ScheduleFrequency.Daily => "/SC DAILY",
            ScheduleFrequency.Weekly => $"/SC WEEKLY /D {day.ToString()[..3].ToUpperInvariant()}",
            _ => "/SC MONTHLY /D 1",
        };

        string arguments = string.Create(
            CultureInfo.InvariantCulture,
            $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --scheduled-clean\" {schedule} /ST {time:HH\\:mm} /RL LIMITED");

        (int code, string output) = Run(arguments);
        if (code == 0)
        {
            message = $"Scheduled Safe Clean created ({frequency}, {time:HH\\:mm}).";
            AppLog.Audit("SCHEDULE-CREATED", TaskName, $"{frequency} {day} {time:HH\\:mm}");
            return true;
        }

        message = "Task Scheduler reported an error: " + output.Trim();
        AppLog.Warn(message);
        return false;
    }

    public static bool Delete(out string message)
    {
        (int code, string output) = Run($"/Delete /F /TN \"{TaskName}\"");
        if (code == 0)
        {
            message = "Scheduled cleaning removed.";
            AppLog.Audit("SCHEDULE-DELETED", TaskName);
            return true;
        }

        message = "Task Scheduler reported an error: " + output.Trim();
        return false;
    }

    public static ScheduleInfo Query()
    {
        // CSV column order is fixed even though the labels are localised:
        // 0 HostName, 1 TaskName, 2 Next Run Time, 3 Status, 4 Logon Mode, 5 Last Run Time.
        (int code, string output) = Run($"/Query /TN \"{TaskName}\" /FO CSV /V /NH");
        if (code != 0)
        {
            return new ScheduleInfo(false, null, null, null);
        }

        string? line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith('"'));
        if (line is null)
        {
            return new ScheduleInfo(true, null, null, null);
        }

        List<string> fields = CsvFields().Matches(line).Select(m => m.Groups[1].Value).ToList();
        string? Field(int i) => i < fields.Count ? fields[i] : null;
        return new ScheduleInfo(true, Field(2), Field(5), Field(3));
    }

    private static (int Code, string Output) Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);
            return (process.ExitCode, stdout + stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
