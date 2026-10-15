using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public partial class AppsView : UserControl, IPage
{
    private static readonly StringComparer TrSort = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);
    private bool _loaded, _loading;
    private Inventory? _inv;
    private List<InstalledApp> _all = new();

    public AppsView()
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

    private void Refresh_Click(object sender, RoutedEventArgs e) => AppState.I.Invalidate();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _loaded = true;
        RefreshButton.IsEnabled = false;
        EmptyText.Text = "Yükleniyor…";
        EmptyText.Visibility = Visibility.Visible;
        try
        {
            _inv = await AppState.I.GetInventoryAsync();
            _all = _inv.AllApps;
            ApplyFilter();
            _ = IconLoader.LoadAppIconsAsync(_all);
            MeasureText.Text = "Gerçek boyutlar ölçülüyor…";
            await AppState.I.EnsureAppSizesAsync();
            MeasureText.Text = "Boyutlar kurulum klasörlerinden ölçüldü · ≈ işaretliler programın bildirdiği tahmindir";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            EmptyText.Text = "Liste alınamadı: " + ex.Message;
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        if (_inv == null || !IsInitialized) return;
        IEnumerable<InstalledApp> q = _all;
        if (ChipWin32.IsChecked == true) q = q.Where(a => a.Source == AppSource.Win32);
        else if (ChipStore.IsChecked == true) q = q.Where(a => a.Source == AppSource.Store);
        if (HideGames.IsChecked == true) q = q.Where(a => !GameInventory.IsGameApp(a, _inv.Games));
        var s = SearchBox.Text.Trim();
        if (s.Length > 0) q = q.Where(a => TextMatch.Contains(a.Name, s) || TextMatch.Contains(a.Publisher, s));
        var list = (SortCombo.SelectedIndex switch
        {
            1 => q.OrderBy(a => a.Name, TrSort),
            2 => q.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue),
            _ => q.OrderByDescending(a => a.TotalSize),
        }).ToList();
        long max = list.Count > 0 ? list.Max(a => a.TotalSize) : 0;
        foreach (var a in list) a.Fraction = max > 0 && a.TotalSize > 0 ? Math.Max(0.006, (double)a.TotalSize / max) : 0;
        List.ItemsSource = list;
        SummaryText.Text = $"{Fmt.Count(list.Count)} uygulama · toplam {Fmt.Size(list.Sum(a => a.TotalSize))}";
        EmptyText.Text = "Eşleşen uygulama yok.";
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private static InstalledApp? AppOf(object sender) => (sender as FrameworkElement)?.DataContext as InstalledApp;

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (AppOf(sender) is not { } app) return;
        app.IsBusy = true;
        try { await UninstallFlow.UninstallAppAsync(app); }
        finally { app.IsBusy = false; }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } b) return;
        menu.DataContext = b.DataContext;
        menu.PlacementTarget = b;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (AppOf(sender) is not { } app) return;
        if (app.InstallLocation != null && Directory.Exists(app.InstallLocation)) PathUtil.OpenInExplorer(app.InstallLocation);
        else Dialogs.Info("Konum bilinmiyor", "Bu program kurulum klasörünü Windows'a bildirmemiş.");
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        if (AppOf(sender) is not { } app) return;
        var text = string.Join(Environment.NewLine, new[]
        {
            $"Ad: {app.Name}", $"Yayıncı: {app.Publisher}", $"Sürüm: {app.Version}", $"Tür: {app.SourceText}",
            $"Boyut: {app.SizeText}", $"Konum: {app.InstallLocation ?? "—"}",
            app.Source == AppSource.Store ? $"Paket: {app.PackageFullName}" : $"Kaldırma komutu: {app.UninstallString}",
        });
        try { Clipboard.SetText(text); } catch { }
    }

    private async void ForceRemove_Click(object sender, RoutedEventArgs e)
    {
        if (AppOf(sender) is not { } app) return;
        if (app.Source == AppSource.Store)
        {
            Dialogs.Info("Normal kaldırmayı kullanın", "Microsoft Store uygulamaları Windows tarafından eksiksiz kaldırılır; zorla kaldırmaya gerek yoktur.");
            return;
        }
        await UninstallFlow.ForceRemoveAsync(app);
    }
}
