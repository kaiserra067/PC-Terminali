using System.Windows;
using System.Windows.Controls;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public partial class LeftoversView : UserControl, IPage
{
    private List<LeftoverItem> _items = new();
    private string _filter = "all";
    private bool _loaded, _busy;

    public LeftoversView()
    {
        InitializeComponent();
        List.SelectionChanged += UpdateSummary;
        AppState.I.InventoryChanged += () => Dispatcher.BeginInvoke(() => { _loaded = false; AppState.I.LastLeftovers = null; });
    }

    public void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        if (AppState.I.LastLeftovers is { } cached) Show(cached);
        else _ = ScanAsync();
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        ScanButton.IsEnabled = DeleteButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Kurulu programlar, oyunlar, hizmetler ve çalışan süreçler toplanıyor…";
        EmptyPanel.Visibility = Visibility.Collapsed;
        try
        {
            var inv = await AppState.I.GetInventoryAsync();
            var progress = new Progress<string>(p => ProgressText.Text = "Denetleniyor: " + p);
            var items = await Task.Run(() => LeftoverScanner.ScanAll(inv.AllApps, inv.Packages, inv.Games, progress, CancellationToken.None));
            AppState.I.LastLeftovers = items;
            Show(items);
        }
        catch (Exception ex)
        {
            Dialogs.Error("Tarama başarısız", ex.Message);
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            UpdateSummary();
        }
    }

    private void Show(List<LeftoverItem> items)
    {
        _items = items;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!IsInitialized) return;
        var list = _filter == "all" ? _items : _items.Where(i => i.Confidence.ToString() == _filter).ToList();
        List.ItemsSource = list;
        EmptyPanel.Visibility = list.Count == 0 && !_busy ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _items.Count == 0 ? "Kalıntı bulunamadı" : "Bu filtrede öğe yok";
        UpdateSummary();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string f }) { _filter = f; ApplyFilter(); }
    }

    private void UpdateSummary()
    {
        var sel = _items.Where(i => i.IsSelected).ToList();
        int admin = _items.Count(i => !i.CanAct);
        SummaryText.Text = $"{_items.Count} öğe bulundu · {sel.Count} seçili ({Fmt.Size(sel.Sum(i => i.Size))})"
            + (admin > 0 ? $" · {admin} öğe için yönetici izni gerekiyor" : "");
        DeleteButton.IsEnabled = !_busy && sel.Count > 0;
    }

    private void SelectSure_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsSelected = i.Confidence == Confidence.High && i.CanAct;
        UpdateSummary();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsSelected = false;
        UpdateSummary();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = _items.Where(i => i.IsSelected && i.CanAct).ToList();
        if (sel.Count == 0) return;
        int risky = sel.Count(i => i.Confidence != Confidence.High);
        string method = AppSettings.Current.UseRecycleBin ? "Dosya ve klasörler Geri Dönüşüm Kutusu'na taşınacak" : "Dosya ve klasörler kalıcı olarak silinecek";
        string warn = risky > 0 ? $" Seçimde kesin olmayan {risky} öğe var; listeyi bir kez daha gözden geçirin." : "";
        if (!Dialogs.Confirm("Seçilen kalıntılar silinsin mi?",
                $"{sel.Count} öğe ({Fmt.Size(sel.Sum(i => i.Size))}). {method}; kayıt defteri anahtarları önce yedeklenir.{warn}",
                "Sil", true, sel.Select(i => $"{i.ConfidenceText} · {i.Path}")))
            return;
        _busy = true;
        UpdateSummary();
        try
        {
            var st = await WaitDialog.WhileAsync("Siliniyor", "Seçilen kalıntılar siliniyor…", LeftoverScanner.DeleteAsync(sel));
            AnalyzerView.ShowResult(st);
        }
        finally { _busy = false; }
        AppState.I.Invalidate();
        _loaded = true;
        await ScanAsync();
    }
}
