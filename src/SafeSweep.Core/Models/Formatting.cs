using System.Globalization;

namespace SafeSweep.Core.Models;

public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Bytes(-bytes);
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes} B")
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.##} {Units[unit]}");
    }

    public static string Age(DateTime utc, DateTime nowUtc)
    {
        TimeSpan span = nowUtc - utc;
        if (span.TotalDays >= 730)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalDays / 365.25:0.#} years");
        }

        if (span.TotalDays >= 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalDays / 30.44:0} months");
        }

        if (span.TotalDays >= 2)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.TotalDays:0} days");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:0} hours");
    }
}
