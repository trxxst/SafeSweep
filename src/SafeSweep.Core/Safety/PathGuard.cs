using System.Collections.Concurrent;
using SafeSweep.Core.Models;
using SafeSweep.Core.Platform;

namespace SafeSweep.Core.Safety;

/// <summary>Outcome of a protection check. A denial always carries a human-readable reason.</summary>
public sealed record SafetyVerdict(bool IsAllowed, string Reason)
{
    public static SafetyVerdict Allow(string reason = "Allowed.") => new(true, reason);

    public static SafetyVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// The single choke point every deletion passes through. It is consulted twice:
/// by scanners (so protected items never appear as removable) and again by the
/// cleaner immediately before each deletion (so a scanner bug, a stale result or
/// a path that changed into a link since the scan still cannot reach a
/// protected location).
///
/// The model is default-deny for automatic cleaning: a built-in rule may only
/// delete inside a root that was registered and sanity-checked at start-up, and
/// the protected-location list is applied on top of that.
/// </summary>
public sealed class PathGuard
{
    private const int MaxDirectoryVerifyEntries = 250_000;

    private readonly ProtectionPolicy _policy;
    private readonly ConcurrentDictionary<string, bool> _ruleRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _finalPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DriveType Type, string Label)> _driveTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hostDrives = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _appDataContainers;
    private readonly string[] _leftoverContainers;
    private readonly string[] _emptyFolderContainers;
    private readonly string[] _cloudRoots;

    public PathGuard(ProtectionPolicy policy)
    {
        _policy = policy;
        SystemPaths p = policy.Paths;

        _appDataContainers = NormalizeAll(
            p.WindowsDir, p.ProgramFiles, p.ProgramFilesX86, p.ProgramData, p.LocalAppData, p.RoamingAppData,
            p.LocalAppDataLow, Path.Combine(p.UserProfile, "AppData"), p.AppDataRoot);

        _leftoverContainers = NormalizeAll(
            p.RoamingAppData, p.LocalAppData, p.LocalAppDataLow, p.ProgramData, Path.Combine(p.LocalAppData, "Packages"));

        _emptyFolderContainers = NormalizeAll(p.RoamingAppData, p.LocalAppData, p.LocalAppDataLow);

        _cloudRoots = NormalizeAll([.. p.CloudRoots]);

        // The drives holding Windows and the user profile have their own detailed
        // rules; the generic top-level check applies to every other drive.
        foreach (string? hostPath in new[] { p.SystemDrive, p.WindowsDir, p.UserProfile })
        {
            string? n = hostPath is null ? null : PathUtil.TryNormalize(hostPath, out _);
            if (n is not null)
            {
                _hostDrives.Add(PathUtil.DriveRoot(n));
            }
        }
    }

    private static bool IsCloudDriveLabel(string label)
        => label.Contains("Google Drive", StringComparison.OrdinalIgnoreCase)
           || label.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)
           || label.Contains("Dropbox", StringComparison.OrdinalIgnoreCase)
           || label.Contains("iCloud", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a directory is a source-code project or repository (a .git
    /// folder, a solution, a package manifest...). Automatic cleaning never
    /// enters one, even inside a temp folder.
    /// </summary>
    public static bool LooksLikeProject(DirectoryInfo directory)
    {
        try
        {
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", SafeEnumeration.Lenient))
            {
                foreach (string marker in ProtectionPolicy.ProjectMarkers)
                {
                    if (PathUtil.WildcardMatch(entry.Name, marker))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: the caller skips it anyway.
            return true;
        }

        return false;
    }

    public ProtectionPolicy Policy => _policy;

    public IReadOnlyCollection<string> RegisteredRuleRoots => _ruleRoots.Keys.ToList();

    /// <summary>
    /// Registers a root a built-in rule may clean inside. Refuses roots that
    /// are drive roots, broad containers (the profile, AppData, Documents...),
    /// protected, or not what they claim to be. A misconfigured %TEMP% that
    /// points at C:\ or at the profile is rejected here rather than cleaned.
    /// </summary>
    public bool TryRegisterRuleRoot(string root, bool exactFile, out string normalizedRoot, out string? rejection)
    {
        normalizedRoot = string.Empty;
        string? n = PathUtil.TryNormalize(root, out string? error);
        if (n is null)
        {
            rejection = error;
            return false;
        }

        normalizedRoot = n;

        if (PathUtil.Depth(n) < 2)
        {
            rejection = "A drive root or top-level folder can never be a cleaning root.";
            return false;
        }

        if (IsBroadContainer(n))
        {
            rejection = "This location is a broad container (profile, AppData, personal folder...) and can never be a cleaning root.";
            return false;
        }

        SafetyVerdict staticVerdict = EvaluateStatic(n, DeletionIntent.RuleCleanup, scopeRoot: null, checkScope: false);
        if (!staticVerdict.IsAllowed)
        {
            rejection = staticVerdict.Reason;
            return false;
        }

        if (exactFile ? !File.Exists(n) : !Directory.Exists(n))
        {
            rejection = "Location does not exist.";
            return false;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(n);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rejection = "Location cannot be read: " + ex.Message;
            return false;
        }

        if (FileSystemInspector.IsReparsePoint(attributes))
        {
            // Follow the link once and register the real location instead, but only
            // if the real location passes the same checks.
            string? final = FileSystemInspector.TryGetFinalPath(n);
            string? nf = final is null ? null : PathUtil.TryNormalize(final, out _);
            if (nf is null || nf.Equals(n, StringComparison.OrdinalIgnoreCase))
            {
                rejection = "Location is a link whose target could not be verified.";
                return false;
            }

            return TryRegisterRuleRoot(nf, exactFile, out normalizedRoot, out rejection);
        }

        _ruleRoots[n] = exactFile;
        rejection = null;
        return true;
    }

    /// <summary>
    /// True when a scanner should not even descend into <paramref name="directory"/>:
    /// a protected folder name or a user exclusion. Everything below would be
    /// refused anyway; skipping the walk keeps scans fast and logs short.
    /// </summary>
    public bool ShouldSkipDirectory(DirectoryInfo directory, out string? reason)
    {
        if (ProtectionPolicy.ProtectedSegmentNames.Contains(directory.Name))
        {
            reason = $"protected folder name \"{directory.Name}\"";
            return true;
        }

        if (LooksLikeProject(directory))
        {
            reason = "looks like a source-code project or repository";
            return true;
        }

        string? n = PathUtil.TryNormalize(directory.FullName, out _);
        if (n is not null)
        {
            foreach (string exclusion in _policy.UserExclusions)
            {
                if (PathUtil.IsUnderOrEqual(n, exclusion))
                {
                    reason = "excluded in Settings";
                    return true;
                }
            }
        }

        reason = null;
        return false;
    }

    /// <summary>Quick check used while scanning: static rules plus already-known attributes.</summary>
    public SafetyVerdict EvaluateCandidate(string path, DeletionIntent intent, string? scopeRoot, FileAttributes attributes)
    {
        string? n = PathUtil.TryNormalize(path, out string? error);
        if (n is null)
        {
            return SafetyVerdict.Deny(error ?? "Invalid path.");
        }

        SafetyVerdict verdict = EvaluateStatic(n, intent, scopeRoot, checkScope: true);
        if (!verdict.IsAllowed)
        {
            return verdict;
        }

        return EvaluateAttributes(attributes, intent);
    }

    /// <summary>
    /// Full check performed immediately before a deletion: static rules,
    /// current attributes, link resolution of the real on-disk location and
    /// hard-link detection.
    /// </summary>
    public SafetyVerdict EvaluateForDeletion(string path, DeletionIntent intent, string? scopeRoot, ItemKind kind)
    {
        string? n = PathUtil.TryNormalize(path, out string? error);
        if (n is null)
        {
            return SafetyVerdict.Deny(error ?? "Invalid path.");
        }

        SafetyVerdict verdict = EvaluateStatic(n, intent, scopeRoot, checkScope: true);
        if (!verdict.IsAllowed)
        {
            return verdict;
        }

        FileAttributes attributes;
        try
        {
            if (!File.Exists(n) && !Directory.Exists(n))
            {
                return SafetyVerdict.Deny("The item no longer exists.");
            }

            attributes = File.GetAttributes(n);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SafetyVerdict.Deny("Attributes could not be read: " + ex.Message);
        }

        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (kind == ItemKind.File && isDirectory)
        {
            return SafetyVerdict.Deny("Expected a file but found a folder.");
        }

        if (kind == ItemKind.Directory && !isDirectory)
        {
            return SafetyVerdict.Deny("Expected a folder but found a file.");
        }

        verdict = EvaluateAttributes(attributes, intent);
        if (!verdict.IsAllowed)
        {
            return verdict;
        }

        // Resolve the real location of the parent: if any ancestor is a junction or
        // symlink, the path we compared is not where the delete would land.
        string? parent = Path.GetDirectoryName(n);
        if (parent is null)
        {
            return SafetyVerdict.Deny("Items without a parent folder are never removed.");
        }

        string? realParent = ResolveFinalPath(parent);
        if (realParent is null)
        {
            return SafetyVerdict.Deny("The real location of the containing folder could not be verified.");
        }

        if (!realParent.Equals(parent, StringComparison.OrdinalIgnoreCase))
        {
            string realPath = Path.Combine(realParent, Path.GetFileName(n));
            string? realScope = scopeRoot is null ? null : ResolveFinalPath(PathUtil.TryNormalize(scopeRoot, out _) ?? scopeRoot);
            SafetyVerdict real = EvaluateStatic(realPath, intent, realScope ?? scopeRoot, checkScope: true);
            if (!real.IsAllowed)
            {
                return SafetyVerdict.Deny("The path passes through a link to a location that is not removable: " + real.Reason);
            }
        }

        if (!isDirectory)
        {
            FileIdentity? identity = FileSystemInspector.TryGetIdentity(n);
            if (identity is null)
            {
                return SafetyVerdict.Deny("The file could not be opened to verify its identity.");
            }

            if (identity.Value.LinkCount > 1)
            {
                return SafetyVerdict.Deny("The file has multiple hard links (it is shared with another location), so removing it would not free space and may affect the other location.");
            }
        }

        return SafetyVerdict.Allow();
    }

    /// <summary>
    /// Walks a folder that is about to be removed as a whole and refuses it if
    /// anything inside is protected: secrets, wallets, registry hives, links.
    /// </summary>
    public SafetyVerdict EvaluateDirectoryContents(string directory, DeletionIntent intent, CancellationToken cancellationToken = default)
    {
        int count = 0;
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(current).EnumerateFileSystemInfos("*", SafeEnumeration.Strict).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SafetyVerdict.Deny($"Part of the folder cannot be read ({current}), so its contents cannot be verified.");
            }

            foreach (FileSystemInfo entry in entries)
            {
                if (++count > MaxDirectoryVerifyEntries)
                {
                    return SafetyVerdict.Deny("The folder is too large to verify completely.");
                }

                if (FileSystemInspector.IsReparsePoint(entry.Attributes))
                {
                    return SafetyVerdict.Deny($"Contains a link ({entry.FullName}); folders with links are never removed as a whole.");
                }

                if (FileSystemInspector.IsCloudFile(entry.Attributes))
                {
                    return SafetyVerdict.Deny($"Contains a cloud file ({entry.FullName}).");
                }

                string? nameVerdict = CheckName(entry.Name);
                if (nameVerdict is not null)
                {
                    return SafetyVerdict.Deny($"Contains a protected item: {entry.FullName} ({nameVerdict})");
                }

                if (ProtectionPolicy.ProtectedSegmentNames.Contains(entry.Name))
                {
                    return SafetyVerdict.Deny($"Contains a protected folder: {entry.FullName}");
                }

                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                }
            }
        }

        return SafetyVerdict.Allow();
    }

    /// <summary>
    /// Pure rule evaluation on a normalised path. No file system access except
    /// the cached drive-type lookup.
    /// </summary>
    internal SafetyVerdict EvaluateStatic(string n, DeletionIntent intent, string? scopeRoot, bool checkScope)
    {
        int depth = PathUtil.Depth(n);
        bool reviewFileOnDataDrive = intent == DeletionIntent.UserReview
            && depth == 1
            && !PathUtil.DriveRoot(n).Equals(PathUtil.TrimTrailingSeparator(_policy.Paths.SystemDrive), StringComparison.OrdinalIgnoreCase);
        if (depth < 2 && !reviewFileOnDataDrive)
        {
            return SafetyVerdict.Deny("Drive roots and top-level folders are never removed.");
        }

        (DriveType driveType, string label) = _driveTypes.GetOrAdd(PathUtil.DriveRoot(n), static root =>
        {
            try
            {
                var drive = new DriveInfo(root);
                string volumeLabel = string.Empty;
                try
                {
                    volumeLabel = drive.IsReady ? drive.VolumeLabel : string.Empty;
                }
                catch (IOException)
                {
                }

                return (drive.DriveType, volumeLabel);
            }
            catch (Exception)
            {
                return (DriveType.Unknown, string.Empty);
            }
        });

        if (driveType != DriveType.Fixed)
        {
            return SafetyVerdict.Deny("Only local fixed drives are cleaned (not network, removable or optical drives).");
        }

        if (IsCloudDriveLabel(label))
        {
            return SafetyVerdict.Deny($"The drive \"{label}\" is a cloud-storage drive: deleting here would delete the cloud copy.");
        }

        string[] segments = PathUtil.Segments(n);
        string leaf = segments[^1];

        // A second Windows installation, a lettered recovery partition or Program Files
        // on a data drive: system folders are refused on every drive, not just C:.
        if (ProtectionPolicy.SystemRootFolderNames.Contains(segments[0])
            && !_hostDrives.Contains(PathUtil.DriveRoot(n)))
        {
            return SafetyVerdict.Deny($"\"{segments[0]}\" at the top of a drive is a system or program location.");
        }

        string? nameProblem = CheckName(leaf);
        if (nameProblem is not null)
        {
            return SafetyVerdict.Deny(nameProblem);
        }

        foreach (string segment in segments)
        {
            if (ProtectionPolicy.ProtectedSegmentNames.Contains(segment))
            {
                return SafetyVerdict.Deny($"Inside a protected folder (\"{segment}\"): version control data, game libraries, wallets and key stores are never touched.");
            }
        }

        string? browserProblem = CheckBrowserProfile(segments);
        if (browserProblem is not null)
        {
            return SafetyVerdict.Deny(browserProblem);
        }

        foreach (string exclusion in _policy.UserExclusions)
        {
            if (PathUtil.IsUnderOrEqual(n, exclusion))
            {
                return SafetyVerdict.Deny($"Excluded by you in Settings ({exclusion}).");
            }
        }

        SystemPaths p = _policy.Paths;
        string? profilesRoot = PathUtil.TryNormalize(p.ProfilesRoot, out _);
        string? profile = PathUtil.TryNormalize(p.UserProfile, out _);
        if (profilesRoot is not null && profile is not null
            && PathUtil.IsStrictlyUnder(n, profilesRoot) && !PathUtil.IsUnderOrEqual(n, profile))
        {
            return SafetyVerdict.Deny("Belongs to another user account.");
        }

        SafetyVerdict protectedVerdict = CheckProtectedLocations(n, intent);
        if (!protectedVerdict.IsAllowed)
        {
            return protectedVerdict;
        }

        return EvaluateIntent(n, intent, scopeRoot, checkScope);
    }

    private SafetyVerdict CheckProtectedLocations(string n, DeletionIntent intent)
    {
        foreach (ProtectedLocation location in _policy.ProtectedLocations)
        {
            if (!PathUtil.IsUnderOrEqual(n, location.Path))
            {
                continue;
            }

            // Personal folders: the user may pick individual files inside them in a
            // review tool, but never the folder itself and never automatically.
            if (intent == DeletionIntent.UserReview
                && location.Group == "Personal files"
                && PathUtil.IsStrictlyUnder(n, location.Path))
            {
                continue;
            }

            if (intent == DeletionIntent.RuleCleanup && IsNeutralizedByException(n, location))
            {
                continue;
            }

            return SafetyVerdict.Deny($"Protected location: {location.Path}. {location.Reason}");
        }

        return SafetyVerdict.Allow();
    }

    private bool IsNeutralizedByException(string n, ProtectedLocation location)
    {
        foreach (CleanupException exception in _policy.CleanupExceptions)
        {
            bool contains = exception.ExactFileOnly
                ? n.Equals(exception.Path, StringComparison.OrdinalIgnoreCase)
                : PathUtil.IsUnderOrEqual(n, exception.Path);

            // The exception must sit inside the protected location and be more
            // specific than it; a deeper protected location is never neutralised by
            // a shallower exception.
            if (contains
                && PathUtil.IsStrictlyUnder(exception.Path, location.Path)
                && exception.Path.Length > location.Path.Length)
            {
                return true;
            }
        }

        return false;
    }

    private SafetyVerdict EvaluateIntent(string n, DeletionIntent intent, string? scopeRoot, bool checkScope)
    {
        switch (intent)
        {
            case DeletionIntent.RuleCleanup:
                {
                    if (!checkScope)
                    {
                        return SafetyVerdict.Allow();
                    }

                    string? scope = scopeRoot is null ? null : PathUtil.TryNormalize(scopeRoot, out _);
                    if (scope is null || !_ruleRoots.TryGetValue(scope, out bool exactFile))
                    {
                        return SafetyVerdict.Deny("Not inside an approved cleaning location.");
                    }

                    bool inside = exactFile
                        ? n.Equals(scope, StringComparison.OrdinalIgnoreCase)
                        : PathUtil.IsStrictlyUnder(n, scope);
                    return inside
                        ? SafetyVerdict.Allow()
                        : SafetyVerdict.Deny("Outside the approved cleaning location for this category.");
                }

            case DeletionIntent.Leftover:
                {
                    // Use the deepest container (AppData\Local\Packages beats AppData\Local).
                    string? container = _leftoverContainers
                        .Where(c => PathUtil.IsStrictlyUnder(n, c))
                        .OrderByDescending(c => c.Length)
                        .FirstOrDefault();
                    if (container is null)
                    {
                        return SafetyVerdict.Deny("Leftover folders are only removed from the top of AppData or ProgramData.");
                    }

                    int relativeDepth = PathUtil.Depth(n) - PathUtil.Depth(container);
                    if (relativeDepth is not (1 or 2))
                    {
                        return SafetyVerdict.Deny("Leftover folders are only removed from the top of AppData or ProgramData.");
                    }

                    string firstSegment = PathUtil.Segments(n)[PathUtil.Depth(container)];
                    if (ProtectionPolicy.LeftoverNeverNames.Contains(firstSegment))
                    {
                        return SafetyVerdict.Deny($"\"{firstSegment}\" is shared by Windows or by many programs and is never treated as a leftover.");
                    }

                    return SafetyVerdict.Allow();
                }

            case DeletionIntent.UserReview:
                {
                    foreach (string container in _appDataContainers)
                    {
                        if (PathUtil.IsUnderOrEqual(n, container))
                        {
                            return SafetyVerdict.Deny("Program and application-data folders are excluded from review tools: files there belong to installed programs.");
                        }
                    }

                    foreach (string cloud in _cloudRoots)
                    {
                        if (PathUtil.IsUnderOrEqual(n, cloud))
                        {
                            return SafetyVerdict.Deny("Synced by OneDrive: removing it here would also remove the cloud copy. Manage it through OneDrive instead.");
                        }
                    }

                    return SafetyVerdict.Allow();
                }

            case DeletionIntent.EmptyFolder:
                {
                    foreach (string container in _emptyFolderContainers)
                    {
                        if (!PathUtil.IsStrictlyUnder(n, container))
                        {
                            continue;
                        }

                        string firstSegment = PathUtil.Segments(n)[PathUtil.Depth(container)];
                        if (ProtectionPolicy.LeftoverNeverNames.Contains(firstSegment))
                        {
                            return SafetyVerdict.Deny($"Folders under \"{firstSegment}\" are managed by Windows or shared programs; empty ones are expected there.");
                        }

                        return SafetyVerdict.Allow();
                    }

                    return SafetyVerdict.Deny("Empty folders are only removed inside AppData.");
                }

            default:
                return SafetyVerdict.Deny("This kind of item is not removed by path.");
        }
    }

    private static SafetyVerdict EvaluateAttributes(FileAttributes attributes, DeletionIntent intent)
    {
        if (FileSystemInspector.IsReparsePoint(attributes))
        {
            return SafetyVerdict.Deny("Symbolic links and junctions are never followed or removed.");
        }

        if (FileSystemInspector.IsCloudFile(attributes))
        {
            return SafetyVerdict.Deny("Cloud placeholder file: removing it would delete the cloud copy.");
        }

        if ((attributes & FileAttributes.System) != 0)
        {
            return SafetyVerdict.Deny("Marked as a system file by Windows.");
        }

        if (intent is DeletionIntent.UserReview && (attributes & FileAttributes.Hidden) != 0)
        {
            return SafetyVerdict.Deny("Hidden file: hidden files in personal folders are usually application or sync metadata.");
        }

        return SafetyVerdict.Allow();
    }

    private static string? CheckName(string name)
    {
        if (ProtectionPolicy.CriticalFileNames.Contains(name))
        {
            return $"\"{name}\" is a critical Windows file.";
        }

        foreach (string pattern in ProtectionPolicy.CriticalNamePatterns)
        {
            if (PathUtil.WildcardMatch(name, pattern))
            {
                return $"\"{name}\" matches a protected file type ({pattern}): registry data, keys, wallets, mail stores and drivers are never removed.";
            }
        }

        return null;
    }

    private static string? CheckBrowserProfile(string[] segments)
    {
        int start = -1;
        for (int i = 0; i < segments.Length; i++)
        {
            string s = segments[i];
            if (s.Equals("User Data", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Opera Stable", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Opera GX Stable", StringComparison.OrdinalIgnoreCase)
                || (s.Equals("Profiles", StringComparison.OrdinalIgnoreCase) && i > 0 && segments[i - 1].Equals("Firefox", StringComparison.OrdinalIgnoreCase)))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        for (int i = start + 1; i < segments.Length; i++)
        {
            if (ProtectionPolicy.BrowserProtectedNames.Contains(segments[i]))
            {
                return $"Browser profile data (\"{segments[i]}\"): logins, passwords, history, extensions and site data are never touched; only caches are cleaned.";
            }
        }

        return null;
    }

    private bool IsBroadContainer(string n)
    {
        SystemPaths p = _policy.Paths;
        string?[] broad =
        [
            p.UserProfile, p.LocalAppData, p.RoamingAppData, p.LocalAppDataLow, p.ProgramData, p.ProfilesRoot,
            p.ProgramFiles, p.ProgramFilesX86, p.WindowsDir, Path.Combine(p.UserProfile, "AppData"),
        ];

        foreach (string? b in broad.Concat(p.PersonalFolders).Concat(p.CloudRoots))
        {
            string? nb = b is null ? null : PathUtil.TryNormalize(b, out _);
            if (nb is not null && PathUtil.IsUnderOrEqual(nb, n))
            {
                // n is the container itself or an ancestor of it.
                return true;
            }
        }

        return false;
    }

    private string? ResolveFinalPath(string directory)
        => _finalPathCache.GetOrAdd(directory, static d =>
        {
            string? final = FileSystemInspector.TryGetFinalPath(d);
            return final is null ? null : PathUtil.TryNormalize(final, out _);
        });

    private static string[] NormalizeAll(params string?[] paths)
        => paths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => PathUtil.TryNormalize(x, out _))
            .Where(x => x is not null)
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
