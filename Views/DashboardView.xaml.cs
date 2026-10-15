using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TemizlikPaneli.Controls;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public sealed class DriveCard
{
    public string Title { get; init; } = "";
    public string FreeText { get; init; } = "";
    public string UsedText { get; init; } = "";
    public double UsedPercent { get; init; }
    public Brush MeterBrush { get; init; } = Brushes.Gray;
    public string StatusText { get; init; } = "";
    public Brush? StatusBrush { get; init; }
    public Brush? StatusSoft { get; init; }
    public string Note { get; set; } = "";
}

public partial class DashboardView : UserControl, IPage
{
    private bool _started;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private List<DriveCard> _drives = new();

    public DashboardView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateScanProgress();
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(RefreshDrives);
        AppState.I.InventoryChanged += () => Dispatcher.BeginInvoke(() => { if (IsVisible) _ = LoadAsync(false); else _started = false; });
    }

    public void OnShown()
    {
        RefreshDrives();
        if (_started) return;
        _started = true;
        _ = LoadAsync(false);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        AppState.I.Invalidate(); // olay işleyicisi kuyruğa girer; aşağıdaki yükleme başladığı için o atlanır
        _started = true;
        _ = LoadAsync(true);
    }

    private bool _loading;

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key }) MainWindow.Current?.Navigate(key);
    }

    // ------------------------------------------------------------------ Sürücüler

    private void RefreshDrives()
    {
        var list = new List<DriveCard>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                long used = d.TotalSize - d.TotalFreeSpace;
                double pct = 100.0 * used / d.TotalSize;
                string status = pct >= 95 ? "Disk neredeyse dolu" : pct >= 85 ? "Disk dolmak üzere" : "";
                string brushKey = pct >= 95 ? "CriticalBrush" : pct >= 85 ? "WarningBrush" : "Series1";
                list.Add(new DriveCard
                {
                    Title = $"{(string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Yerel Disk" : d.VolumeLabel)} ({d.Name.TrimEnd('\\')})",
                    FreeText = Fmt.Size(d.TotalFreeSpace),
                    UsedText = $"{Fmt.Size(used)} / {Fmt.Size(d.TotalSize)} dolu (%{pct:0})",
                    UsedPercent = pct,
                    MeterBrush = ThemeManager.Brush(brushKey),
                    StatusText = status,
                    StatusBrush = ThemeManager.Brush(pct >= 95 ? "CriticalBrush" : "WarningBrush"),
                    StatusSoft = ThemeManager.Brush(pct >= 95 ? "CriticalSoftBrush" : "WarningSoftBrush"),
                    Note = _drives.FirstOrDefault(x => x.Title.EndsWith($"({d.Name.TrimEnd('\\')})"))?.Note ?? "",
                });
            }
            catch { }
        }
        _drives = list;
        DrivesList.ItemsSource = list;
    }

    // ------------------------------------------------------------------ Yükleme

    private async Task LoadAsync(bool refresh)
    {
        if (_loading) return;
        _loading = true;
        RescanButton.IsEnabled = false;
        JunkValue.Text = LeftValue.Text = GamesValue.Text = AppsValue.Text = "…";
        JunkSub.Text = LeftSub.Text = GamesSub.Text = AppsSub.Text = "Taranıyor";
        try
        {
            RefreshDrives();
            var root = KnownFolders.SystemDrive;
            var scanTask = AppState.I.GetDriveScanAsync(root, refresh);
            ScanProgress.Visibility = Visibility.Visible;
            _timer.Start();
            UpdateScanProgress();

            var inv = await AppState.I.GetInventoryAsync();
            var junkTask = Task.Run(() =>
            {
                var cats = JunkCatalog.Build(inv.Games);
                foreach (var c in cats) JunkCatalog.ScanCategory(c, CancellationToken.None);
                return cats;
            });
            var leftTask = Task.Run(() => LeftoverScanner.ScanAll(inv.AllApps, inv.Packages, inv.Games, null, CancellationToken.None));

            await AppState.I.EnsureGameSizesAsync();
            ShowGames(inv);
            await AppState.I.EnsureAppSizesAsync();
            ShowApps(inv);

            var cats = await junkTask;
            AppState.I.LastJunk = cats;
            ShowJunk(cats);

            var left = await leftTask;
            AppState.I.LastLeftovers = left;
            ShowLeftovers(left);

            try { ShowDriveScan(await scanTask); }
            catch (OperationCanceledException) { ScanNote.Text = "Tarama iptal edildi."; }
        }
        catch (Exception ex)
        {
            ScanNote.Text = "Bir hata oluştu: " + ex.Message;
        }
        finally
        {
            _timer.Stop();
            ScanProgress.Visibility = Visibility.Collapsed;
            RescanButton.IsEnabled = true;
            _loading = false;
        }
    }

    private void UpdateScanProgress()
    {
        var s = AppState.I.ActiveScanner;
        if (s == null) return;
        ScanNote.Text = $"{KnownFolders.SystemDrive} taranıyor… {Fmt.Count(Interlocked.Read(ref s.FilesScanned))} dosya, {Fmt.Size(Interlocked.Read(ref s.BytesScanned))}";
    }

    private void ShowDriveScan(DriveScanResult r)
    {
        _timer.Stop();
        ScanProgress.Visibility = Visibility.Collapsed;
        var root = r.Root;
        FoldersTitle.Text = $"{root.Name.TrimEnd('\\')} sürücüsünde en çok yer kaplayan klasörler";
        var items = root.Children.Select(c => (Label: c.Name, Size: c.Size, Path: c.FullPath)).ToList();
        if (root.OwnFilesSize > 0) items.Add(("Kök dizindeki dosyalar (pagefile.sys vb.)", root.OwnFilesSize, root.Name));
        FolderBars.ItemsSource = BarRow.Build(items.OrderByDescending(i => i.Size).Take(10), i => i.Label, i => i.Size, Fmt.Size, root.Size);

        string note = $"{Fmt.Count(root.FileCount)} dosya, {Fmt.Count(root.DirCount)} klasör · {Fmt.Duration(r.Elapsed)}";
        try
        {
            var d = new DriveInfo(root.Name);
            long used = d.TotalSize - d.TotalFreeSpace;
            long hidden = used - root.Size;
            if (hidden > (1L << 30))
                note += $" · {Fmt.Size(hidden)} okunamadı (sistem koruma alanları{(Admin.IsElevated ? "" : ", yönetici izni gereken klasörler")})";
        }
        catch { }
        ScanNote.Text = note;

        KindNote.Text = $"{root.Name.TrimEnd('\\')} sürücüsündeki tüm dosyalar, uzantılarına göre";
        var kinds = Enumerable.Range(0, FileKinds.Count).Select(k => (Name: FileKinds.Names[k], Size: root.KindSizes[k]));
        KindBars.ItemsSource = BarRow.Build(kinds, k => k.Name, k => k.Size, Fmt.Size, root.Size);

        foreach (var dc in _drives.Where(x => x.Title.EndsWith($"({root.Name.TrimEnd('\\')})")))
            dc.Note = $"Taranan: {Fmt.Size(root.Size)} · son tarama {r.ScannedAt:HH:mm}";
        DrivesList.ItemsSource = null;
        DrivesList.ItemsSource = _drives;
    }

    private void ShowGames(Inventory inv)
    {
        var games = inv.Games.Games.Where(g => !g.IsTool).ToList();
        long total = games.Sum(g => g.TotalSize);
        GamesValue.Text = Fmt.Size(total);
        GamesSub.Text = $"{games.Count} oyun · {games.Select(g => g.Platform).Distinct().Count()} platform";
        GameBars.ItemsSource = BarRow.Build(games.Take(1000), g => g.Name, g => g.TotalSize, Fmt.Size, total).Take(6).ToList();
    }

    private void ShowApps(Inventory inv)
    {
        var apps = inv.AllApps.Where(a => !GameInventory.IsGameApp(a, inv.Games)).ToList();
        long total = apps.Sum(a => a.TotalSize);
        AppsValue.Text = Fmt.Count(apps.Count);
        AppsSub.Text = $"toplam {Fmt.Size(total)} (oyunlar hariç)";
        AppBars.ItemsSource = BarRow.Build(apps, a => a.Name, a => a.TotalSize, Fmt.Size, total).Take(6).ToList();
    }

    private void ShowJunk(List<JunkCategory> cats)
    {
        var sel = cats.Where(c => c.IsSelected && !c.Blocked).ToList();
        JunkValue.Text = Fmt.Size(sel.Sum(c => c.Size));
        var bin = cats.FirstOrDefault(c => c.Kind == JunkKind.RecycleBin);
        long admin = cats.Where(c => c.Blocked).Sum(c => c.Size);
        JunkSub.Text = $"{sel.Count(c => c.Size > 0)} kategori seçili";
        var tips = new List<string>();
        if (admin > 0) tips.Add($"Yönetici izniyle {Fmt.Size(admin)} daha temizlenebilir.");
        if (bin is { Size: > 0 }) tips.Add($"Geri Dönüşüm Kutusu: {Fmt.Size(bin.Size)} (varsayılan olarak seçili değil).");
        JunkSub.ToolTip = tips.Count > 0 ? string.Join("\n", tips) : null;
    }

    private void ShowLeftovers(List<LeftoverItem> items)
    {
        LeftValue.Text = $"{Fmt.Count(items.Count)} öğe";
        int sure = items.Count(i => i.Confidence == Confidence.High);
        LeftSub.Text = $"{Fmt.Size(items.Sum(i => i.Size))} · {sure} kesin kalıntı";
    }
}
