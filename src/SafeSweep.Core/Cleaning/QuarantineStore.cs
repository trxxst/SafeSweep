using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Models;
using SafeSweep.Core.Safety;
using SafeSweep.Core.Scanning;

namespace SafeSweep.Core.Cleaning;

public sealed record QuarantineEntry
{
    public required Guid Id { get; init; }

    public required Guid SessionId { get; init; }

    public required string OriginalPath { get; init; }

    /// <summary>Absolute path of the stored copy.</summary>
    public required string StoredPath { get; init; }

    public required ItemKind Kind { get; init; }

    public long SizeBytes { get; init; }

    public int FileCount { get; init; } = 1;

    public required DateTime QuarantinedUtc { get; init; }

    public required string CategoryId { get; init; }

    public required string CategoryName { get; init; }

    public string Reason { get; init; } = string.Empty;

    public DateTime? LastWriteUtc { get; init; }

    public DateTime QuarantinedLocal => QuarantinedUtc.ToLocalTime();

    public string SizeDisplay => Formatting.Bytes(SizeBytes);

    public string Name => Path.GetFileName(OriginalPath.TrimEnd('\\'));
}

public sealed record RestoreResult(bool Success, string RestoredPath, string Message);

/// <summary>
/// SafeSweep's own restorable holding area. Items are MOVED (a rename on the
/// same drive, so it is instant and nothing is re-written). Each drive gets its
/// own store so moving never copies gigabytes across drives. A per-session
/// append-only manifest records every add / restore / purge; the manifest line
/// is written BEFORE the move, so a crash can never leave an item in the store
/// that the manifest does not know about.
/// </summary>
public sealed class QuarantineStore
{
    private const string ManifestName = "manifest.jsonl";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _primaryRoot;
    private readonly SystemPaths _paths;
    private readonly object _gate = new();

    public QuarantineStore(string primaryRoot, SystemPaths paths)
    {
        _primaryRoot = primaryRoot;
        _paths = paths;
    }

    public string PrimaryRoot => _primaryRoot;

    private sealed record ManifestRecord(string Op, QuarantineEntry? Entry, Guid? EntryId, DateTime AtUtc);

    /// <summary>All store roots that currently exist (system drive + other drives).</summary>
    public IEnumerable<string> StoreRoots()
    {
        if (Directory.Exists(_primaryRoot))
        {
            yield return _primaryRoot;
        }

        string sid = CurrentSid();
        foreach (DriveInfo drive in SafeDrives())
        {
            string root = Path.Combine(drive.RootDirectory.FullName, ".SafeSweep-Quarantine", sid);
            if (!IsSystemDrive(drive.RootDirectory.FullName) && Directory.Exists(root))
            {
                yield return root;
            }
        }
    }

    /// <summary>
    /// Moves an item into quarantine. The caller has already run the protection
    /// check. Returns the manifest entry, or throws on failure (the original is
    /// untouched when this throws).
    /// </summary>
    public QuarantineEntry Add(Guid sessionId, ScanItem item)
    {
        lock (_gate)
        {
            string source = item.Path;
            string storeRoot = GetOrCreateStoreRoot(source);
            string sessionDir = Path.Combine(storeRoot, sessionId.ToString("N"));
            string itemsDir = Path.Combine(sessionDir, "items");
            Directory.CreateDirectory(itemsDir);

            var entryId = Guid.NewGuid();
            string stored = Path.Combine(itemsDir, entryId.ToString("N"));

            var entry = new QuarantineEntry
            {
                Id = entryId,
                SessionId = sessionId,
                OriginalPath = source,
                StoredPath = stored,
                Kind = item.Kind,
                SizeBytes = item.SizeBytes,
                FileCount = item.FileCount,
                QuarantinedUtc = DateTime.UtcNow,
                CategoryId = item.Category.Id,
                CategoryName = item.Category.Name,
                Reason = item.Reason,
                LastWriteUtc = item.LastWriteUtc,
            };

            string manifest = Path.Combine(sessionDir, ManifestName);
            Append(manifest, new ManifestRecord("add", entry, entryId, DateTime.UtcNow));

            bool sameVolume = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(stored), StringComparison.OrdinalIgnoreCase);
            if (item.Kind == ItemKind.Directory)
            {
                if (!sameVolume)
                {
                    throw new IOException("Folders are only quarantined on their own drive, and a quarantine store could not be created there.");
                }

                Directory.Move(source, stored);
            }
            else if (sameVolume)
            {
                File.Move(source, stored);
            }
            else
            {
                CopyVerifiedThenDelete(source, stored);
            }

            AppLog.Audit("QUARANTINED", source, $"stored as {stored}");
            return entry;
        }
    }

    /// <summary>Every item currently held (not restored, not purged, and physically present).</summary>
    public IReadOnlyList<QuarantineEntry> LoadAll()
    {
        lock (_gate)
        {
            var list = new List<QuarantineEntry>();
            foreach (string root in StoreRoots())
            {
                foreach (string sessionDir in SafeDirectories(root))
                {
                    list.AddRange(LoadSession(sessionDir));
                }
            }

            list.Sort((a, b) => b.QuarantinedUtc.CompareTo(a.QuarantinedUtc));
            return list;
        }
    }

    public long TotalBytes() => LoadAll().Sum(e => e.SizeBytes);

    /// <summary>Moves an item back. If something now exists at the original path, restores beside it.</summary>
    public RestoreResult Restore(QuarantineEntry entry)
    {
        lock (_gate)
        {
            if (!ExistsOnDisk(entry))
            {
                return new RestoreResult(false, entry.OriginalPath, "The quarantined copy is missing.");
            }

            string? target = PathUtil.TryNormalize(entry.OriginalPath, out string? error);
            if (target is null)
            {
                return new RestoreResult(false, entry.OriginalPath, "The original path is not valid: " + error);
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                target = AlternativeName(target);
            }

            try
            {
                string? parent = Path.GetDirectoryName(target);
                if (parent is not null)
                {
                    Directory.CreateDirectory(parent);
                }

                bool sameVolume = string.Equals(Path.GetPathRoot(entry.StoredPath), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
                if (entry.Kind == ItemKind.Directory)
                {
                    Directory.Move(entry.StoredPath, target);
                }
                else if (sameVolume)
                {
                    File.Move(entry.StoredPath, target);
                }
                else
                {
                    CopyVerifiedThenDelete(entry.StoredPath, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"Restore failed for {entry.OriginalPath}", ex);
                return new RestoreResult(false, target, ex.Message);
            }

            Append(ManifestPath(entry), new ManifestRecord("restored", null, entry.Id, DateTime.UtcNow));
            AppLog.Audit("RESTORED", target, $"from quarantine entry {entry.Id}");
            CleanupSessionIfEmpty(entry);
            string message = target.Equals(entry.OriginalPath, StringComparison.OrdinalIgnoreCase)
                ? "Restored to its original location."
                : "Something already existed at the original location, so it was restored beside it.";
            return new RestoreResult(true, target, message);
        }
    }

    /// <summary>Permanently deletes a quarantined item.</summary>
    public bool Purge(QuarantineEntry entry, out string? error)
    {
        lock (_gate)
        {
            error = null;
            if (!IsInsideStore(entry.StoredPath))
            {
                error = "Refused: the stored path is not inside a SafeSweep quarantine store.";
                AppLog.Audit("BLOCKED", entry.StoredPath, error);
                return false;
            }

            try
            {
                if (entry.Kind == ItemKind.Directory && Directory.Exists(entry.StoredPath))
                {
                    // Directory.Delete removes links without following them.
                    ClearReadOnly(entry.StoredPath);
                    Directory.Delete(entry.StoredPath, recursive: true);
                }
                else if (File.Exists(entry.StoredPath))
                {
                    File.SetAttributes(entry.StoredPath, FileAttributes.Normal);
                    File.Delete(entry.StoredPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                AppLog.Error($"Purge failed for {entry.StoredPath}", ex);
                return false;
            }

            Append(ManifestPath(entry), new ManifestRecord("purged", null, entry.Id, DateTime.UtcNow));
            AppLog.Audit("PURGED", entry.OriginalPath, $"quarantine entry {entry.Id}");
            CleanupSessionIfEmpty(entry);
            return true;
        }
    }

    /// <summary>Purges items older than the retention period. 0 days = keep forever.</summary>
    public int PurgeExpired(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        int purged = 0;
        foreach (QuarantineEntry entry in LoadAll().Where(e => e.QuarantinedUtc < cutoff))
        {
            if (Purge(entry, out _))
            {
                purged++;
            }
        }

        if (purged > 0)
        {
            AppLog.Info($"Purged {purged} quarantined item(s) older than {retentionDays} days (retention setting).");
        }

        return purged;
    }

    private IEnumerable<QuarantineEntry> LoadSession(string sessionDir)
    {
        string manifest = Path.Combine(sessionDir, ManifestName);
        if (!File.Exists(manifest))
        {
            yield break;
        }

        var entries = new Dictionary<Guid, QuarantineEntry>();
        List<string> lines;
        try
        {
            lines = File.ReadAllLines(manifest).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Quarantine manifest could not be read: {manifest}", ex);
            yield break;
        }

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ManifestRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<ManifestRecord>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (record is null)
            {
                continue;
            }

            if (record.Op == "add" && record.Entry is not null)
            {
                entries[record.Entry.Id] = record.Entry;
            }
            else if (record.EntryId is Guid id)
            {
                entries.Remove(id);
            }
        }

        foreach (QuarantineEntry entry in entries.Values)
        {
            // An "add" whose move never happened (crash between the two) is not held.
            if (ExistsOnDisk(entry))
            {
                yield return entry;
            }
        }
    }

    private static bool ExistsOnDisk(QuarantineEntry entry)
        => entry.Kind == ItemKind.Directory ? Directory.Exists(entry.StoredPath) : File.Exists(entry.StoredPath);

    private static string ManifestPath(QuarantineEntry entry)
        => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(entry.StoredPath)!)!, ManifestName);

    private void CleanupSessionIfEmpty(QuarantineEntry entry)
    {
        string sessionDir = Path.GetDirectoryName(Path.GetDirectoryName(entry.StoredPath)!)!;
        if (!IsInsideStore(sessionDir))
        {
            return;
        }

        try
        {
            if (!LoadSession(sessionDir).Any())
            {
                string items = Path.Combine(sessionDir, "items");
                if (!Directory.Exists(items) || !Directory.EnumerateFileSystemEntries(items).Any())
                {
                    Directory.Delete(sessionDir, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Quarantine session folder not removed: " + ex.Message);
        }
    }

    private bool IsInsideStore(string path)
    {
        string? n = PathUtil.TryNormalize(path, out _);
        return n is not null && StoreRoots().Any(r => PathUtil.IsStrictlyUnder(n, PathUtil.TryNormalize(r, out _) ?? r));
    }

    private string GetOrCreateStoreRoot(string source)
    {
        string? sourceRoot = Path.GetPathRoot(source);
        if (sourceRoot is null || IsSystemDrive(sourceRoot) || string.Equals(sourceRoot, Path.GetPathRoot(_primaryRoot), StringComparison.OrdinalIgnoreCase))
        {
            CreateSecured(_primaryRoot);
            return _primaryRoot;
        }

        string root = Path.Combine(sourceRoot, ".SafeSweep-Quarantine", CurrentSid());
        try
        {
            string container = Path.Combine(sourceRoot, ".SafeSweep-Quarantine");
            if (!Directory.Exists(container))
            {
                DirectoryInfo created = Directory.CreateDirectory(container);
                created.Attributes |= FileAttributes.Hidden;
            }

            CreateSecured(root);
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"No quarantine store could be created on {sourceRoot} ({ex.Message}); files from that drive are copied to the system drive store instead.");
            CreateSecured(_primaryRoot);
            return _primaryRoot;
        }
    }

    /// <summary>Creates a folder only the current user, SYSTEM and Administrators can open.</summary>
    private static void CreateSecured(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        string? parent = Path.GetDirectoryName(path);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        new DirectoryInfo(path).Create(security);
    }

    private static void CopyVerifiedThenDelete(string source, string destination)
    {
        var sourceInfo = new FileInfo(source);
        string? destinationRoot = Path.GetPathRoot(destination);
        if (destinationRoot is not null)
        {
            var drive = new DriveInfo(destinationRoot);
            if (drive.AvailableFreeSpace < sourceInfo.Length + (2L * 1024 * 1024 * 1024))
            {
                throw new IOException("Not enough free space to hold a quarantined copy; the file was left in place.");
            }
        }

        File.Copy(source, destination, overwrite: false);
        string a = FileHasher.ComputeSha256(source, CancellationToken.None);
        string b = FileHasher.ComputeSha256(destination, CancellationToken.None);
        if (!a.Equals(b, StringComparison.Ordinal))
        {
            File.Delete(destination);
            throw new IOException("The quarantined copy did not verify; the original was left in place.");
        }

        File.SetAttributes(source, File.GetAttributes(source) & ~FileAttributes.ReadOnly);
        File.Delete(source);
    }

    private static string AlternativeName(string target)
    {
        string directory = Path.GetDirectoryName(target) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(target);
        string extension = Path.GetExtension(target);
        for (int i = 1; i < 1000; i++)
        {
            string candidate = Path.Combine(directory, $"{stem} (restored {i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} (restored {Guid.NewGuid():N}){extension}");
    }

    private static void Append(string manifest, ManifestRecord record)
    {
        using var stream = new FileStream(manifest, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(JsonSerializer.Serialize(record, Json));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void ClearReadOnly(string directory)
    {
        foreach (FileInfo file in SafeEnumeration.EnumerateFiles(directory, true, null, null, CancellationToken.None))
        {
            if ((file.Attributes & FileAttributes.ReadOnly) != 0)
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            bool ok;
            try
            {
                ok = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (IOException)
            {
                ok = false;
            }

            if (ok)
            {
                yield return drive;
            }
        }
    }

    private bool IsSystemDrive(string root) => string.Equals(root, _paths.SystemDrive, StringComparison.OrdinalIgnoreCase);

    private static string CurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? "user";
    }
}
