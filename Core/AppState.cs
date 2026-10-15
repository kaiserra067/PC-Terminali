namespace TemizlikPaneli.Core;

public sealed class Inventory
{
    public required List<InstalledApp> Win32 { get; init; }
    public required List<StorePackage> Packages { get; init; }
    public required List<InstalledApp> Store { get; init; }
    public required GameScanResult Games { get; init; }
    public List<InstalledApp> AllApps => Win32.Concat(Store).ToList();
}

/// <summary>Sayfalar arasında paylaşılan tarama sonuçları (programlar, Store paketleri, oyunlar).</summary>
public sealed class AppState
{
    public static AppState I { get; } = new();

    private Task<Inventory>? _inventory;
    private Task? _appSizes, _gameSizes;
    private readonly object _gate = new();

    /// <summary>Kaldırma gibi değişikliklerden sonra tetiklenir; sayfalar listelerini yeniler.</summary>
    public event Action? InventoryChanged;

    /// <summary>Son gereksiz dosya ve kalıntı taramaları (sayfalar arasında yeniden kullanılır).</summary>
    public List<JunkCategory>? LastJunk { get; set; }
    public List<LeftoverItem>? LastLeftovers { get; set; }

    public Task<Inventory> GetInventoryAsync()
    {
        lock (_gate)
        {
            if (_inventory == null || _inventory.IsFaulted || _inventory.IsCanceled)
            {
                _inventory = Task.Run(LoadAsync);
                _appSizes = null;
                _gameSizes = null;
            }
            return _inventory;
        }
    }

    private static async Task<Inventory> LoadAsync()
    {
        var win32 = AppInventory.ReadWin32Apps();
        var pkgs = await StoreApps.GetAllPackagesAsync();
        var store = StoreApps.ToApps(pkgs);
        var games = GameInventory.Scan(win32, pkgs);
        return new Inventory { Win32 = win32, Packages = pkgs, Store = store, Games = games };
    }

    /// <summary>Program klasörlerinin gerçek boyutlarını bir kez ölçer.</summary>
    public async Task EnsureAppSizesAsync()
    {
        var inv = await GetInventoryAsync();
        Task t;
        lock (_gate) t = _appSizes ??= Task.Run(() => AppInventory.MeasureSizes(inv.AllApps, inv.Games.GameFolders, CancellationToken.None));
        await t;
    }

    public async Task EnsureGameSizesAsync()
    {
        var inv = await GetInventoryAsync();
        Task t;
        lock (_gate) t = _gameSizes ??= Task.Run(() => GameInventory.MeasureSizes(inv.Games.Games, CancellationToken.None));
        await t;
    }

    public void Invalidate()
    {
        lock (_gate) { _inventory = null; _appSizes = null; _gameSizes = null; }
        InventoryChanged?.Invoke();
    }

    // ------------------------------------------------------------------ Sürücü taraması (genel bakış + disk analizi)

    private Task<DriveScanResult>? _driveScan;
    private string? _driveScanRoot;
    private CancellationTokenSource? _driveCts;

    /// <summary>Şu an çalışan taramanın sayaçları (ilerleme göstermek için).</summary>
    public TreeScanner? ActiveScanner { get; private set; }
    public event Action<DriveScanResult>? DriveScanCompleted;

    public Task<DriveScanResult>? CurrentDriveScan(string root)
    {
        lock (_gate) return _driveScanRoot != null && PathUtil.Cmp.Equals(_driveScanRoot, root) ? _driveScan : null;
    }

    public Task<DriveScanResult> GetDriveScanAsync(string root, bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _driveScan != null && !_driveScan.IsFaulted && !_driveScan.IsCanceled && PathUtil.Cmp.Equals(_driveScanRoot, root))
                return _driveScan;
            _driveCts?.Cancel();
            _driveCts = new CancellationTokenSource();
            var ct = _driveCts.Token;
            var scanner = new TreeScanner();
            ActiveScanner = scanner;
            _driveScanRoot = root;
            _driveScan = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var node = scanner.Scan(root, ct);
                var result = new DriveScanResult
                {
                    Root = node,
                    TopFiles = scanner.TopFiles(),
                    Extensions = scanner.ExtensionStats(),
                    Elapsed = sw.Elapsed,
                    ScannedAt = DateTime.Now,
                };
                return result;
            }, ct);
            _driveScan.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) DriveScanCompleted?.Invoke(t.Result); },
                TaskScheduler.Default);
            return _driveScan;
        }
    }

    public void CancelDriveScan()
    {
        lock (_gate) _driveCts?.Cancel();
    }
}

public sealed class DriveScanResult
{
    public required DirNode Root { get; init; }
    public required List<TopFile> TopFiles { get; init; }
    public required List<ExtStat> Extensions { get; init; }
    public TimeSpan Elapsed { get; init; }
    public DateTime ScannedAt { get; init; }
}
