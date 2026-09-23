using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Safety;

namespace SafeSweep.Core.Apps;

public sealed record InstalledApp(
    string DisplayName,
    string? Publisher,
    string? InstallLocation,
    string? ExecutablePath,
    string KeyName,
    string Source);

/// <summary>
/// Everything SafeSweep knows about what is installed and running. Leftover
/// detection only reports a folder when NONE of these signals point at it:
/// uninstall entries (per-machine, per-user, 32 and 64 bit), Store packages,
/// Program Files folders, Start-menu shortcuts, services, startup entries,
/// scheduled tasks, App Paths and running processes.
/// </summary>
public sealed partial class InstalledAppInventory
{
    private readonly List<InstalledApp> _apps = [];
    private readonly HashSet<string> _packageFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _packageNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _referencedPaths = [];
    private readonly HashSet<string> _compactKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);

    private InstalledAppInventory()
    {
    }

    public IReadOnlyList<InstalledApp> Apps => _apps;

    /// <summary>False when the Store package list could not be read; Store leftovers are then never reported.</summary>
    public bool PackagesKnown { get; private set; }

    public int PackageCount => _packageFamilies.Count;

    public int ReferencedPathCount => _referencedPaths.Count;

    public DateTime BuiltUtc { get; private set; }

    public string SignalSummary =>
        $"{_apps.Count} installed programs, {_packageFamilies.Count} Store packages, {_referencedPaths.Count} referenced program paths (services, startup, tasks, running processes)";

    [GeneratedRegex(@"<Command>\s*(.*?)\s*</Command>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TaskCommand();

    public static InstalledAppInventory Build(SystemPaths paths, CancellationToken cancellationToken = default)
    {
        var inventory = new InstalledAppInventory();
        var watch = Stopwatch.StartNew();

        inventory.ReadUninstallEntries();
        cancellationToken.ThrowIfCancellationRequested();
        inventory.ReadStorePackages();
        cancellationToken.ThrowIfCancellationRequested();
        inventory.ReadFolderNames(paths);
        cancellationToken.ThrowIfCancellationRequested();
        inventory.ReadServices();
        inventory.ReadRunKeys();
        inventory.ReadAppPaths();
        cancellationToken.ThrowIfCancellationRequested();
        inventory.ReadScheduledTasks();
        inventory.ReadRunningProcesses();
        inventory.ReadEnvironmentPaths();

        inventory.BuiltUtc = DateTime.UtcNow;
        AppLog.Info($"Installed-application inventory built in {watch.ElapsedMilliseconds} ms: {inventory.SignalSummary}.");
        return inventory;
    }

    /// <summary>Install folders for the protection policy.</summary>
    public IEnumerable<(string Path, string Name)> InstallLocations()
    {
        foreach (InstalledApp app in _apps)
        {
            if (!string.IsNullOrWhiteSpace(app.InstallLocation))
            {
                yield return (app.InstallLocation!, app.DisplayName);
            }
        }
    }

    public bool MatchesInstalledName(string folderName, out string? matchedBy)
        => NameMatcher.Matches(folderName, _compactKeys, _tokens, out matchedBy);

    public bool IsPackageInstalled(string packageFolderName)
    {
        if (_packageFamilies.Contains(packageFolderName))
        {
            return true;
        }

        // Folder name is a package family name: Name_PublisherId.
        int underscore = packageFolderName.LastIndexOf('_');
        string name = underscore > 0 ? packageFolderName[..underscore] : packageFolderName;
        return _packageNames.Contains(name);
    }

    /// <summary>True when a service, startup entry, task, App Path or running process lives inside <paramref name="folder"/>.</summary>
    public bool IsReferenced(string folder, out string? reference)
    {
        foreach (string path in _referencedPaths)
        {
            if (PathUtil.IsUnderOrEqual(path, folder))
            {
                reference = path;
                return true;
            }
        }

        reference = null;
        return false;
    }

    private void AddKey(string? value)
    {
        string compact = NameMatcher.Compact(value);
        if (compact.Length >= 2)
        {
            _compactKeys.Add(compact);
        }

        foreach (string token in NameMatcher.Tokens(value))
        {
            _tokens.Add(token);
        }
    }

    private void AddReferencedPath(string? command)
    {
        string? path = CommandLineParser.ExtractPath(command);
        string? normalized = path is null ? null : PathUtil.TryNormalize(path, out _);
        if (normalized is not null)
        {
            _referencedPaths.Add(normalized);
            AddKey(Path.GetFileNameWithoutExtension(normalized));
            string? parent = Path.GetFileName(Path.GetDirectoryName(normalized));
            AddKey(parent);
        }
    }

    private void ReadUninstallEntries()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var sources = new (RegistryHive Hive, RegistryView View, string Label)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM 64-bit"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM 32-bit"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
            (RegistryHive.CurrentUser, RegistryView.Registry32, "HKCU 32-bit"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((RegistryHive hive, RegistryView view, string label) in sources)
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? uninstall = baseKey.OpenSubKey(key);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (string subName in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? sub = uninstall.OpenSubKey(subName);
                    if (sub is null)
                    {
                        continue;
                    }

                    string? displayName = sub.GetValue("DisplayName") as string;
                    string? publisher = sub.GetValue("Publisher") as string;
                    string? location = sub.GetValue("InstallLocation") as string;
                    string? icon = sub.GetValue("DisplayIcon") as string;
                    string? uninstallString = sub.GetValue("UninstallString") as string;

                    // Every entry counts as a signal, even without a display name.
                    AddKey(displayName);
                    AddKey(publisher);
                    if (!NameMatcher.IsGuidLike(subName))
                    {
                        AddKey(subName);
                    }

                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        string trimmed = location.Trim().Trim('"');
                        AddKey(Path.GetFileName(trimmed.TrimEnd('\\')));
                        string? normalized = PathUtil.TryNormalize(trimmed, out _);
                        if (normalized is not null)
                        {
                            _referencedPaths.Add(normalized);
                        }
                    }

                    string? exe = CommandLineParser.ExtractPath(icon);
                    AddReferencedPath(icon);
                    AddReferencedPath(uninstallString);

                    if (string.IsNullOrWhiteSpace(displayName) || !seen.Add(displayName + "|" + location))
                    {
                        continue;
                    }

                    _apps.Add(new InstalledApp(
                        displayName.Trim(),
                        publisher?.Trim(),
                        string.IsNullOrWhiteSpace(location) ? null : location.Trim().Trim('"'),
                        exe,
                        subName,
                        label));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                AppLog.Warn($"Uninstall entries in {label} could not be read: {ex.Message}");
            }
        }
    }

    private void ReadStorePackages()
    {
        const string repository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository";
        bool any = false;
        try
        {
            using RegistryKey? families = Registry.CurrentUser.OpenSubKey(repository + @"\Families");
            if (families is not null)
            {
                foreach (string family in families.GetSubKeyNames())
                {
                    _packageFamilies.Add(family);
                    int underscore = family.LastIndexOf('_');
                    string name = underscore > 0 ? family[..underscore] : family;
                    _packageNames.Add(name);
                    AddKey(name.Split('.').LastOrDefault());
                    any = true;
                }
            }

            using RegistryKey? packages = Registry.CurrentUser.OpenSubKey(repository + @"\Packages");
            if (packages is not null)
            {
                foreach (string fullName in packages.GetSubKeyNames())
                {
                    // Name_Version_Architecture_ResourceId_PublisherId
                    string[] parts = fullName.Split('_');
                    if (parts.Length >= 2)
                    {
                        _packageNames.Add(parts[0]);
                        _packageFamilies.Add(parts[0] + "_" + parts[^1]);
                        any = true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn("Store package list could not be read: " + ex.Message);
        }

        PackagesKnown = any;
    }

    private void ReadFolderNames(SystemPaths paths)
    {
        foreach (string? root in new[] { paths.ProgramFiles, paths.ProgramFilesX86, Path.Combine(paths.LocalAppData, "Programs") })
        {
            if (root is null || !Directory.Exists(root))
            {
                continue;
            }

            foreach (DirectoryInfo dir in SafeEnumeration.EnumerateDirectories(root))
            {
                AddKey(dir.Name);
                _referencedPaths.Add(dir.FullName);

                // Vendor folders: also learn the product names below them.
                foreach (DirectoryInfo sub in SafeEnumeration.EnumerateDirectories(dir.FullName))
                {
                    AddKey(sub.Name);
                }
            }
        }

        string?[] startMenus =
        [
            Path.Combine(paths.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(paths.ProgramData, "Microsoft", "Windows", "Start Menu", "Programs"),
        ];

        foreach (string? menu in startMenus)
        {
            if (menu is null || !Directory.Exists(menu))
            {
                continue;
            }

            foreach (FileInfo link in SafeEnumeration.EnumerateFiles(menu, true, null, null, CancellationToken.None))
            {
                if (link.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    AddKey(Path.GetFileNameWithoutExtension(link.Name));
                    AddKey(link.Directory?.Name);
                }
            }
        }
    }

    private void ReadServices()
    {
        try
        {
            using RegistryKey? services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null)
            {
                return;
            }

            foreach (string name in services.GetSubKeyNames())
            {
                // Service names are program names too ("whesvc" owns ProgramData\Whesvc).
                AddKey(name);
                using RegistryKey? service = services.OpenSubKey(name);
                if (service?.GetValue("ImagePath") is string imagePath)
                {
                    AddReferencedPath(imagePath);
                }

                if (service?.GetValue("DisplayName") is string displayName && !displayName.StartsWith('@'))
                {
                    AddKey(displayName);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn("Services could not be read: " + ex.Message);
        }
    }

    private void ReadRunKeys()
    {
        string[] keys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        ];

        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string key in keys)
            {
                try
                {
                    using RegistryKey? run = root.OpenSubKey(key);
                    if (run is null)
                    {
                        continue;
                    }

                    foreach (string valueName in run.GetValueNames())
                    {
                        AddReferencedPath(run.GetValue(valueName) as string);
                        AddKey(valueName);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    AppLog.Debug($"Run key {key} could not be read: {ex.Message}");
                }
            }
        }
    }

    private void ReadAppPaths()
    {
        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using RegistryKey? appPaths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null)
                {
                    continue;
                }

                foreach (string name in appPaths.GetSubKeyNames())
                {
                    using RegistryKey? entry = appPaths.OpenSubKey(name);
                    AddReferencedPath(entry?.GetValue(null) as string);
                    AddKey(Path.GetFileNameWithoutExtension(name));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                AppLog.Debug("App Paths could not be read: " + ex.Message);
            }
        }
    }

    private void ReadScheduledTasks()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/Query /XML")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }

                AppLog.Warn("Scheduled task query timed out; tasks are not used as a signal.");
                return;
            }

            foreach (Match match in TaskCommand().Matches(output.Result))
            {
                AddReferencedPath(System.Net.WebUtility.HtmlDecode(match.Groups[1].Value));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn("Scheduled tasks could not be read: " + ex.Message);
        }
    }

    /// <summary>
    /// PATH entries and path-valued environment variables (JAVA_HOME, ANDROID_HOME,
    /// tool-chain homes). Package managers and SDKs often live in AppData or
    /// ProgramData without any uninstall entry; this is how they are recognised.
    /// </summary>
    private void ReadEnvironmentPaths()
    {
        foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            System.Collections.IDictionary variables;
            try
            {
                variables = Environment.GetEnvironmentVariables(target);
            }
            catch (System.Security.SecurityException)
            {
                continue;
            }

            foreach (System.Collections.DictionaryEntry entry in variables)
            {
                if (entry.Value is not string value || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                foreach (string part in Environment.ExpandEnvironmentVariables(value).Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = part.Trim().Trim('"');
                    if (candidate.Length < 4 || candidate[1] != ':')
                    {
                        continue;
                    }

                    string? normalized = PathUtil.TryNormalize(candidate, out _);
                    if (normalized is not null && PathUtil.Depth(normalized) >= 2)
                    {
                        _referencedPaths.Add(normalized);

                        // "C:\ProgramData\chocolatey\bin" also names the tool: learn "chocolatey".
                        foreach (string segment in PathUtil.Segments(normalized).Skip(1).TakeLast(2))
                        {
                            AddKey(segment);
                        }
                    }
                }
            }
        }
    }

    private void ReadRunningProcesses()
    {
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                AddKey(process.ProcessName);
                string? file = process.MainModule?.FileName;
                if (file is not null)
                {
                    string? normalized = PathUtil.TryNormalize(file, out _);
                    if (normalized is not null)
                    {
                        _referencedPaths.Add(normalized);
                    }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Protected or exited process: its name was still recorded above.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
