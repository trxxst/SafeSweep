using System.Globalization;
using System.Text;

namespace SafeSweep.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,

    /// <summary>Every file-system change SafeSweep makes (delete, quarantine, restore, block).</summary>
    Audit = 4,
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    public string LevelText => Level.ToString().ToUpperInvariant();

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelText,-7}] {Message}");
}

/// <summary>
/// Thread-safe daily-rolling file log plus an in-memory ring buffer for the
/// live log view. Every deletion, quarantine, restore and protection block is
/// written as an AUDIT line, so the log is a complete record of what changed.
/// </summary>
public static class AppLog
{
    private const int RingSize = 3000;
    private static readonly object Gate = new();
    private static readonly LinkedList<LogEntry> Ring = new();
    private static string? _directory;
    private static StreamWriter? _writer;
    private static DateTime _writerDate;

    public static event Action<LogEntry>? EntryWritten;

    public static string? LogDirectory => _directory;

    public static string? CurrentLogFile => _directory is null ? null : Path.Combine(_directory, FileName(DateTime.Now));

    public static void Initialize(string directory, int retentionDays = 30)
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
            _directory = directory;
            Directory.CreateDirectory(directory);
            PurgeOldLogs(directory, retentionDays);
        }
    }

    /// <summary>Closes the log file (the in-memory buffer keeps working).</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
            _directory = null;
        }
    }

    public static IReadOnlyList<LogEntry> Recent()
    {
        lock (Gate)
        {
            return Ring.ToList();
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message, Exception? ex = null)
        => Write(LogLevel.Error, ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    public static void Audit(string action, string target, string? detail = null)
        => Write(LogLevel.Audit, detail is null ? $"{action}: {target}" : $"{action}: {target} | {detail}");

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (Gate)
        {
            Ring.AddLast(entry);
            if (Ring.Count > RingSize)
            {
                Ring.RemoveFirst();
            }

            try
            {
                StreamWriter? writer = EnsureWriter(entry.Timestamp);
                writer?.WriteLine(entry.ToString());
            }
            catch (IOException)
            {
                // Logging must never break a scan or a clean.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        EntryWritten?.Invoke(entry);
    }

    public static void Flush()
    {
        lock (Gate)
        {
            _writer?.Flush();
        }
    }

    private static StreamWriter? EnsureWriter(DateTime now)
    {
        if (_directory is null)
        {
            return null;
        }

        if (_writer is not null && _writerDate == now.Date)
        {
            return _writer;
        }

        _writer?.Dispose();
        var stream = new FileStream(Path.Combine(_directory, FileName(now)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _writerDate = now.Date;
        return _writer;
    }

    private static string FileName(DateTime date) => $"safesweep-{date:yyyy-MM-dd}.log";

    private static void PurgeOldLogs(string directory, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
        foreach (string file in Directory.EnumerateFiles(directory, "safesweep-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
