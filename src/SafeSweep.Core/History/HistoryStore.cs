using System.Text.Json;
using SafeSweep.Core.Logging;

namespace SafeSweep.Core.History;

public sealed record CategoryCleanSummary(string CategoryId, string CategoryName, int Items, long Bytes);

public sealed record CleanFailure(string Path, string Reason);

/// <summary>One cleaning session, as shown in the History page.</summary>
public sealed record HistoryEntry
{
    public required Guid Id { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required DateTime FinishedUtc { get; init; }

    /// <summary>"Manual" or "Scheduled".</summary>
    public required string Trigger { get; init; }

    public bool Simulation { get; init; }

    public bool Cancelled { get; init; }

    public int ItemsRequested { get; init; }

    public int ItemsDeleted { get; init; }

    public int ItemsQuarantined { get; init; }

    public int ItemsRecycled { get; init; }

    public int ItemsSkipped { get; init; }

    public int ItemsBlocked { get; init; }

    public long BytesFreed { get; init; }

    public long BytesQuarantined { get; init; }

    public long BytesRecycled { get; init; }

    /// <summary>Preview runs only: what would have been removed. Never counted as freed.</summary>
    public int ItemsSimulated { get; init; }

    public long BytesSimulated { get; init; }

    public Guid? QuarantineSessionId { get; init; }

    public List<CategoryCleanSummary> Categories { get; init; } = [];

    /// <summary>First failures and blocks (capped), for display.</summary>
    public List<CleanFailure> Problems { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime StartedLocal => StartedUtc.ToLocalTime();

    [System.Text.Json.Serialization.JsonIgnore]
    public long TotalBytes => BytesFreed + BytesQuarantined + BytesRecycled;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Outcome => Simulation
        ? $"Preview: {ItemsSimulated} item(s), {Models.Formatting.Bytes(BytesSimulated)} would be removed"
        : $"{Models.Formatting.Bytes(BytesFreed)} freed, {Models.Formatting.Bytes(BytesQuarantined)} quarantined";
}

/// <summary>Append-only JSON-lines history file.</summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;
    private readonly object _gate = new();

    public HistoryStore(string path)
    {
        _path = path;
    }

    public void Append(HistoryEntry entry)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("History could not be written.", ex);
            }
        }
    }

    public IReadOnlyList<HistoryEntry> Load()
    {
        lock (_gate)
        {
            var list = new List<HistoryEntry>();
            if (!File.Exists(_path))
            {
                return list;
            }

            try
            {
                foreach (string line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        HistoryEntry? entry = JsonSerializer.Deserialize<HistoryEntry>(line, Json);
                        if (entry is not null)
                        {
                            list.Add(entry);
                        }
                    }
                    catch (JsonException)
                    {
                        // A damaged line never hides the rest of the history.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("History could not be read.", ex);
            }

            list.Sort((a, b) => b.StartedUtc.CompareTo(a.StartedUtc));
            return list;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("History could not be cleared.", ex);
            }
        }
    }
}
