using SafeSweep.Core.Apps;
using SafeSweep.Core.Cleaning;
using SafeSweep.Core.History;
using SafeSweep.Core.Logging;
using SafeSweep.Core.Platform;
using SafeSweep.Core.Safety;
using SafeSweep.Core.Settings;

namespace SafeSweep.Core;

/// <summary>
/// Composition root shared by the desktop UI and the headless scheduled run.
/// </summary>
public sealed class SafeSweepServices
{
    private static readonly TimeSpan InventoryMaxAge = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _inventoryGate = new(1, 1);
    private InstalledAppInventory? _inventory;

    private SafeSweepServices(SystemPaths paths, bool isElevated)
    {
        Paths = paths;
        IsElevated = isElevated;
        Directory.CreateDirectory(paths.AppDataRoot);
        AppLog.Initialize(Path.Combine(paths.AppDataRoot, "Logs"));

        SettingsStore = new SettingsStore(Path.Combine(paths.AppDataRoot, "settings.json"));
        Settings = SettingsStore.Load();
        Policy = new ProtectionPolicy(paths);
        Policy.SetUserExclusions(Settings.Exclusions);
        Guard = new PathGuard(Policy);
        History = new HistoryStore(Path.Combine(paths.AppDataRoot, "history.jsonl"));
        Quarantine = new QuarantineStore(Path.Combine(paths.AppDataRoot, "Quarantine"), paths);
    }

    public SystemPaths Paths { get; }

    public bool IsElevated { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; private set; }

    public ProtectionPolicy Policy { get; }

    public PathGuard Guard { get; }

    public HistoryStore History { get; }

    public QuarantineStore Quarantine { get; }

    public InstalledAppInventory? CurrentInventory => _inventory;

    public static SafeSweepServices Create()
    {
        var services = new SafeSweepServices(SystemPaths.FromEnvironment(), Elevation.IsElevated);
        AppLog.Info($"SafeSweep started (elevated: {services.IsElevated}). Protection policy: {services.Policy.StaticLocations.Count} protected locations, {services.Policy.CleanupExceptions.Count} cleanable exceptions.");
        return services;
    }

    /// <summary>For tests: build against a sandboxed set of paths.</summary>
    public static SafeSweepServices CreateFor(SystemPaths paths, bool isElevated = false) => new(paths, isElevated);

    public void SaveSettings(AppSettings settings)
    {
        SettingsStore.Save(settings);
        Settings = settings;
        Policy.SetUserExclusions(settings.Exclusions);
        AppLog.Info("Settings saved.");
    }

    /// <summary>
    /// Returns a fresh-enough installed-program inventory, rebuilding it when
    /// older than ten minutes. Install folders are (re)protected on every build.
    /// </summary>
    public InstalledAppInventory GetInventory(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        _inventoryGate.Wait(cancellationToken);
        try
        {
            if (!forceRefresh && _inventory is not null && DateTime.UtcNow - _inventory.BuiltUtc < InventoryMaxAge)
            {
                return _inventory;
            }

            InstalledAppInventory inventory = InstalledAppInventory.Build(Paths, cancellationToken);
            Policy.SetInstalledApplicationFolders(inventory.InstallLocations());
            _inventory = inventory;
            return inventory;
        }
        finally
        {
            _inventoryGate.Release();
        }
    }

    public Task<InstalledAppInventory> GetInventoryAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        => Task.Run(() => GetInventory(forceRefresh, cancellationToken), cancellationToken);
}
