using System.Security.Cryptography;
using SafeSweep.Core.Models;
using SafeSweep.Core.Platform;
using SafeSweep.Core.Rules;
using SafeSweep.Core.Safety;

namespace SafeSweep.Core.Scanning;

public static class FileHasher
{
    private const int PartialBlock = 64 * 1024;

    public static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using FileStream stream = Open(path);
        using var sha = SHA256.Create();
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>Hash of the first and last 64 KB: a cheap pre-filter before full hashing.</summary>
    public static string ComputePartial(string path, long length)
    {
        using FileStream stream = Open(path);
        using var sha = SHA256.Create();
        byte[] buffer = new byte[PartialBlock];
        int read = stream.Read(buffer, 0, buffer.Length);
        sha.TransformBlock(buffer, 0, read, null, 0);
        if (length > PartialBlock * 2)
        {
            stream.Seek(-PartialBlock, SeekOrigin.End);
            read = stream.Read(buffer, 0, buffer.Length);
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
}

/// <summary>
/// Content-verified duplicate finder: size → partial hash → full SHA-256.
/// Hard links to the same file are recognised and never reported as
/// duplicates (removing one would free nothing).
/// </summary>
public sealed class DuplicateScanner
{
    public CategoryResult Scan(ScanContext context, IReadOnlyList<string>? roots, long minSizeBytes)
    {
        var result = new CategoryResult(Categories.Duplicates);
        context.SetStage("Listing files");

        var bySize = new Dictionary<long, List<FileInfo>>();
        foreach ((FileInfo file, FileAttributes attributes) in ReviewWalker.Walk(context, ReviewWalker.Roots(context, roots)))
        {
            long length;
            try
            {
                length = file.Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (length < minSizeBytes || length == 0)
            {
                continue;
            }

            if (!context.Guard.EvaluateCandidate(file.FullName, DeletionIntent.UserReview, null, attributes).IsAllowed)
            {
                continue;
            }

            if (!bySize.TryGetValue(length, out List<FileInfo>? list))
            {
                list = [];
                bySize[length] = list;
            }

            list.Add(file);
        }

        List<List<FileInfo>> sizeGroups = bySize.Values.Where(g => g.Count > 1).Select(RemoveHardLinks).Where(g => g.Count > 1).ToList();

        // Partial hash.
        var partialGroups = new List<List<(FileInfo File, string Partial)>>();
        int done = 0;
        int total = sizeGroups.Sum(g => g.Count);
        foreach (List<FileInfo> group in sizeGroups)
        {
            var hashed = new List<(FileInfo, string)>();
            foreach (FileInfo file in group)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.SetStage("Comparing file beginnings and endings", total == 0 ? null : (double)done++ / total);
                try
                {
                    hashed.Add((file, FileHasher.ComputePartial(file.FullName, file.Length)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            partialGroups.AddRange(hashed.GroupBy(h => h.Item2).Where(g => g.Count() > 1).Select(g => g.ToList()));
        }

        // Full hash.
        long bytesToHash = partialGroups.Sum(g => g.Sum(f => f.File.Length));
        long hashedBytes = 0;
        int groupId = 0;
        var groups = new List<(string Hash, List<FileInfo> Files)>();
        foreach (List<(FileInfo File, string Partial)> group in partialGroups)
        {
            var full = new List<(FileInfo File, string Hash)>();
            foreach ((FileInfo file, _) in group)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.SetStage("Verifying content with SHA-256", bytesToHash == 0 ? null : (double)hashedBytes / bytesToHash);
                context.ReportPath(file.FullName);
                try
                {
                    full.Add((file, FileHasher.ComputeSha256(file.FullName, context.CancellationToken)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                hashedBytes += file.Length;
            }

            foreach (IGrouping<string, (FileInfo File, string Hash)> same in full.GroupBy(f => f.Hash).Where(g => g.Count() > 1))
            {
                groups.Add((same.Key, same.Select(s => s.File).ToList()));
            }
        }

        foreach ((string hash, List<FileInfo> files) in groups.OrderByDescending(g => g.Files[0].Length * (g.Files.Count - 1)))
        {
            groupId++;
            List<FileInfo> ordered = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
            foreach (FileInfo file in ordered)
            {
                if (!context.TryClaim(file.FullName, file.Length))
                {
                    continue;
                }

                string others = string.Join(Environment.NewLine, ordered.Where(o => o != file).Select(o => "  = " + o.FullName));
                result.Add(new ScanItem
                {
                    Path = file.FullName,
                    Kind = ItemKind.File,
                    Category = Categories.Duplicates,
                    SizeBytes = file.Length,
                    LastWriteUtc = file.LastWriteTimeUtc,
                    CreationUtc = file.CreationTimeUtc,
                    LastAccessUtc = LastAccessPolicy.IsTracked == true ? file.LastAccessTimeUtc : null,
                    Reason = $"Identical to {ordered.Count - 1} other file(s) (group {groupId}).",
                    Evidence =
                    [
                        $"SHA-256: {hash}",
                        $"Same size ({Formatting.Bytes(file.Length)}) and identical content as:",
                        others,
                        "At least one copy in every group is always kept; both copies are re-verified right before removal.",
                    ],
                    DuplicateGroupId = groupId,
                    ContentHash = hash,
                });
            }
        }

        if (groups.Count > 0)
        {
            long wasted = groups.Sum(g => g.Files[0].Length * (g.Files.Count - 1));
            result.AddNote($"{groups.Count} group(s) of identical files; {Formatting.Bytes(wasted)} could be reclaimed by keeping one copy of each.");
        }

        return result;
    }

    private static List<FileInfo> RemoveHardLinks(List<FileInfo> files)
    {
        var seen = new HashSet<(uint, ulong)>();
        var unique = new List<FileInfo>();
        foreach (FileInfo file in files)
        {
            FileIdentity? id = FileSystemInspector.TryGetIdentity(file.FullName);
            if (id is null)
            {
                continue;
            }

            if (seen.Add((id.Value.VolumeSerial, id.Value.FileIndex)))
            {
                unique.Add(file);
            }
        }

        return unique;
    }
}
