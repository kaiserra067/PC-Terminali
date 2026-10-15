using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TemizlikPaneli.Controls;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public partial class GamesView : UserControl, IPage
{
    private static readonly StringComparer TrSort = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);
    private bool _loaded, _loading;
    private Inventory? _inv;
    private List<GameEntry> _games = new();
    private string _platform = "";

    public GamesView()
    {
        InitializeComponent();
        AppState.I.InventoryChanged += () => Dispatcher.BeginInvoke(() =>
        {
            _loaded = false;
            if (IsVisible) _ = LoadAsync();
        });
    }

    public void OnShown()
    {
        if (!_loaded) _ = LoadAsync();
    }

    public static readonly DependencyProperty CardColumnsProperty =
        DependencyProperty.Register(nameof(CardColumns), typeof(int), typeof(GamesView), new PropertyMetadata(3));

    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        set => SetValue(CardColumnsProperty, value);
    }

    private void Cards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        int cols = Math.Max(1, (int)(e.NewSize.Width / 290));
        if (cols != CardColumns) CardColumns = cols;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => AppState.I.Invalidate();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _loaded = true;
        RefreshButton.IsEnabled = false;
        EmptyText.Text = "Oyunlar aranıyor…";
        EmptyText.Visibility = Visibility.Visible;
        try
        {
            _inv = await AppState.I.GetInventoryAsync();
            _games = _inv.Games.Games.Where(g => !g.IsTool).ToList();
            BuildChips();
            ApplyFilter();
            _ = IconLoader.LoadGameImagesAsync(_games);
            TotalSub.Text = "Klasör boyutları ölçülüyor…";
            await AppState.I.EnsureGameSizesAsync();
            ApplyFilter();
            UpdateSummary();
            await LoadOrphansAsync();
        }
        catch (Exception ex)
        {
            EmptyText.Text = "Oyunlar alınamadı: " + ex.Message;
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void BuildChips()
    {
        Chips.Children.Clear();
        void Add(string label, string key)
        {
            var rb = new RadioButton { Content = label, Style = (Style)FindResource("ChipToggle"), GroupName = "platform", Tag = key, IsChecked = key == _platform, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _platform = key; ApplyFilter(); };
            Chips.Children.Add(rb);
        }
        Add($"Tümü ({_games.Count})", "");
        foreach (var g in _games.GroupBy(g => g.Platform).OrderByDescending(g => g.Sum(x => x.TotalSize)))
            Add($"{g.Key} ({g.Count()})", g.Key);
        if (_platform.Length > 0 && !_games.Any(g => g.Platform == _platform)) _platform = "";
    }

    private void UpdateSummary()
    {
        long total = _games.Sum(g => g.TotalSize);
        TotalText.Text = Fmt.Size(total);
        string share = "";
        try
        {
            var d = new DriveInfo(KnownFolders.SystemDrive);
            long used = d.TotalSize - d.TotalFreeSpace;
            if (used > 0) share = $" · dolu alandaki payı %{100.0 * total / used:0}";
        }
        catch { }
        TotalSub.Text = $"{_games.Count} oyun, {_games.Select(g => g.Platform).Distinct().Count()} platform{share}";
        PlatformBars.ItemsSource = BarRow.Build(_games.GroupBy(g => g.Platform), g => $"{g.Key} ({g.Count()} oyun)", g => g.Sum(x => x.TotalSize), Fmt.Size, total);
    }

    private void ApplyFilter()
    {
        if (!IsInitialized) return;
        IEnumerable<GameEntry> q = _games;
        if (_platform.Length > 0) q = q.Where(g => g.Platform == _platform);
        var s = SearchBox.Text.Trim();
        if (s.Length > 0) q = q.Where(g => TextMatch.Contains(g.Name, s));
        q = SortCombo.SelectedIndex switch
        {
            1 => q.OrderBy(g => g.Name, TrSort),
            2 => q.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenByDescending(g => g.TotalSize),
            _ => q.OrderByDescending(g => g.TotalSize),
        };
        var list = q.ToList();
        Cards.ItemsSource = list;
        EmptyText.Text = _games.Count == 0 ? "Bilgisayarda kurulu oyun bulunamadı." : "Eşleşen oyun yok.";
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Sort_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private async Task LoadOrphansAsync()
    {
        var orphans = _inv?.Games.Orphans.Where(o => Directory.Exists(o.Path)).ToList() ?? new();
        if (orphans.Count == 0) { OrphanCard.Visibility = Visibility.Collapsed; return; }
        await Task.Run(() => { foreach (var o in orphans) o.Size = FsScan.Measure(o.Path).Size; });
        OrphanList.ItemsSource = orphans.OrderByDescending(o => o.Size).ToList();
        OrphanCard.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ İşlemler

    private static GameEntry? GameOf(object sender) => (sender as FrameworkElement)?.DataContext as GameEntry;

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (GameOf(sender) is { } g) await UninstallFlow.RemoveGameAsync(g);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } b || b.DataContext is not GameEntry g) return;
        menu.DataContext = g;
        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            var header = mi.Header as string ?? "";
            if (header.StartsWith("Gölgelendirici")) mi.IsEnabled = g.CacheSize > 0;
            else if (header.StartsWith("Atölye")) mi.IsEnabled = g.ExtraSize > 0;
            else mi.IsEnabled = g.InstallDir != null && Directory.Exists(g.InstallDir);
        }
        menu.PlacementTarget = b;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GameOf(sender) is { InstallDir: { } dir }) PathUtil.OpenInExplorer(dir);
    }

    private async void ClearShader_Click(object sender, RoutedEventArgs e)
    {
        if (GameOf(sender) is not { CacheFolder: { } dir } g) return;
        if (!Dialogs.Confirm("Gölgelendirici önbelleği temizlensin mi?", $"“{g.Name}” için {Fmt.Size(g.CacheSize)} önbellek silinecek. Oyun ilk açılışta gölgelendiricileri yeniden derler; birkaç dakika takılma olabilir.", "Temizle"))
            return;
        var st = await Task.Run(() => Deleter.CleanFolder(dir, null, true, null, CancellationToken.None));
        ActivityLog.Write("Oyun gölgelendirici önbelleği temizlendi", g.Name, st.Bytes, st.Items, st.Failed, "Kalıcı");
        g.CacheSize = FsScan.Measure(dir).Size;
        AnalyzerView.ShowResult(st);
    }

    private async void ClearWorkshop_Click(object sender, RoutedEventArgs e)
    {
        if (GameOf(sender) is not { ExtraFolder: { } dir } g) return;
        if (!Dialogs.Confirm("Atölye içeriği silinsin mi?",
                $"“{g.Name}” için indirilmiş {Fmt.Size(g.ExtraSize)} Workshop içeriği (modlar, haritalar) silinecek. Abone olduğunuz öğeleri Steam yeniden indirebilir; kalıcı yer açmak için Steam'de abonelikten de çıkın.",
                "Sil", true))
            return;
        var st = await Task.Run(() => Deleter.CleanFolder(dir, null, true, null, CancellationToken.None));
        ActivityLog.Write("Steam atölye içeriği silindi", g.Name, st.Bytes, st.Items, st.Failed, "Kalıcı");
        g.ExtraSize = FsScan.Measure(dir).Size;
        AnalyzerView.ShowResult(st);
    }

    private void OrphanOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OrphanFolder o) PathUtil.OpenInExplorer(o.Path);
    }

    private async void OrphanDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OrphanFolder o) return;
        bool recycle = AppSettings.Current.UseRecycleBin && o.Size < 20L * 1024 * 1024 * 1024;
        if (!Dialogs.Confirm("Klasör silinsin mi?", $"{o.Path} ({o.SizeText}) {(recycle ? "Geri Dönüşüm Kutusu'na taşınacak" : "kalıcı olarak silinecek")}.", recycle ? "Taşı" : "Kalıcı sil", true))
            return;
        var st = await Deleter.DeleteUserItemsAsync("Sahipsiz oyun klasörü silindi", new[] { new DeleteItem(o.Path, o.Size) }, recycle);
        AnalyzerView.ShowResult(st);
        await LoadOrphansAsync();
    }
}
