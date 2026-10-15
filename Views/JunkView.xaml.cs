using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public sealed class JunkGroup : ObservableObject
{
    public required string Name { get; init; }
    public required List<JunkCategory> Items { get; init; }

    public bool? AllSelected
    {
        get
        {
            var sel = Items.Where(i => i.CanSelect).ToList();
            if (sel.Count == 0) return false;
            int n = sel.Count(i => i.IsSelected);
            return n == 0 ? false : n == sel.Count ? true : null;
        }
        set
        {
            foreach (var i in Items.Where(i => i.CanSelect)) i.IsSelected = value ?? false;
            OnPropertyChanged();
        }
    }

    public string TotalText => Fmt.Size(Items.Sum(i => i.Size));
    public void Refresh() { OnPropertyChanged(nameof(AllSelected)); OnPropertyChanged(nameof(TotalText)); }
}

public sealed class JunkTool
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string ButtonText { get; init; }
    public required Action Run { get; init; }
}

public partial class JunkView : UserControl, IPage
{
    private List<JunkCategory> _cats = new();
    private List<JunkGroup> _groups = new();
    private bool _loaded, _busy;
    private CancellationTokenSource? _cts;

    public JunkView()
    {
        InitializeComponent();
        BuildTools();
    }

    public void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        if (AppState.I.LastJunk is { } cached) { Show(cached); return; }
        _ = ScanAsync();
    }

    private void Show(List<JunkCategory> cats)
    {
        _cats = cats;
        _groups = cats.GroupBy(c => c.Group).Select(g => new JunkGroup { Name = g.Key, Items = g.OrderByDescending(c => c.Size).ToList() }).ToList();
        Groups.ItemsSource = _groups;
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var sel = _cats.Where(c => c.IsSelected && !c.Blocked).ToList();
        SelectedText.Text = Fmt.Size(sel.Sum(c => c.Size));
        long found = _cats.Where(c => !c.Blocked).Sum(c => c.Size);
        SelectedSub.Text = $"{sel.Count(c => c.Size > 0)} kategori seçili · bulunan toplam {Fmt.Size(found)}";
        foreach (var g in _groups) g.Refresh();
        long blocked = _cats.Where(c => c.Blocked).Sum(c => c.Size);
        bool anyBlocked = _cats.Any(c => c.Blocked);
        AdminNotice.Visibility = anyBlocked ? Visibility.Visible : Visibility.Collapsed;
        AdminNoticeText.Text = blocked > 0
            ? $"Windows klasörlerindeki {Fmt.Size(blocked)} daha temizlenebilir; bunun için yönetici izni gerekiyor."
            : "Windows klasörlerindeki bazı kategoriler yönetici izni gerektiriyor.";
        CleanButton.IsEnabled = !_busy && sel.Any(c => c.Size > 0);
    }

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateTotals();
    private void GroupCheck_Click(object sender, RoutedEventArgs e) => UpdateTotals();
    private void Elevate_Click(object sender, RoutedEventArgs e) { if (Admin.RestartElevated("--page junk")) Application.Current.Shutdown(); }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not JunkCategory c) return;
        if (c.Kind == JunkKind.RecycleBin) { PathUtil.OpenUri("shell:RecycleBinFolder"); return; }
        var t = c.Targets.FirstOrDefault(t => Directory.Exists(t.Path));
        if (t != null) PathUtil.OpenInExplorer(t.Path);
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true, "Taranıyor…");
        try
        {
            var inv = await AppState.I.GetInventoryAsync();
            var cats = JunkCatalog.Build(inv.Games);
            Show(cats);
            Progress.IsIndeterminate = false;
            Progress.Maximum = cats.Count;
            int i = 0;
            foreach (var c in cats)
            {
                c.IsScanning = true;
                ProgressText.Text = $"Taranıyor: {c.Name}";
                await Task.Run(() => JunkCatalog.ScanCategory(c, CancellationToken.None));
                c.IsScanning = false;
                Progress.Value = ++i;
                UpdateTotals();
            }
            Show(cats);
            AppState.I.LastJunk = cats;
        }
        finally { SetBusy(false, ""); }
    }

    private void SetBusy(bool busy, string text)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = text;
        Progress.Value = 0;
        UpdateTotals();
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var sel = _cats.Where(c => c.IsSelected && !c.Blocked && c.Size > 0).ToList();
        if (sel.Count == 0 || _busy) return;
        long total = sel.Sum(c => c.Size);
        var lines = sel.Select(c => $"{c.Name} — {c.SizeText}").ToList();
        string extra = sel.Any(c => c.Kind == JunkKind.RecycleBin) ? " Geri Dönüşüm Kutusu boşaltılacak." : "";
        if (sel.Any(c => c.UsesRecycleBin)) extra += " İndirilenler'deki kurulum dosyaları Geri Dönüşüm Kutusu'na taşınacak.";
        if (!Dialogs.Confirm("Temizlik başlatılsın mı?",
                $"Seçili {sel.Count} kategoride {Fmt.Size(total)} temizlenecek. Önbellek ve geçici dosyalar kalıcı olarak silinir; programlar gerektiğinde bunları yeniden oluşturur.{extra}",
                "Temizle", false, lines))
            return;

        SetBusy(true, "Temizleniyor…");
        CleanButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var all = new DeleteStats();
        Progress.Maximum = sel.Count;
        int i = 0;
        try
        {
            foreach (var c in sel)
            {
                ProgressText.Text = $"Temizleniyor: {c.Name}";
                all.Merge(await JunkCatalog.CleanCategoryAsync(c, _cts.Token));
                Progress.Value = ++i;
            }
        }
        catch (OperationCanceledException) { }
        finally { SetBusy(false, ""); }

        string msg = $"{Fmt.Size(all.Bytes)} boşaltıldı ({Fmt.Count(all.Items)} dosya silindi).";
        if (all.Failed > 0) msg += $" {Fmt.Count(all.Failed)} dosya o an kullanımda olduğu için atlandı; bu normaldir, programlar kapatılınca bir sonraki temizlikte silinir.";
        Dialogs.Success("Temizlik tamamlandı", msg);
        await ScanAsync();
    }

    // ------------------------------------------------------------------ Windows araçları

    private void BuildTools()
    {
        var tools = new List<JunkTool>
        {
            new()
            {
                Title = "Windows Update bileşen deposunu temizle (WinSxS)",
                Description = "Yüklenmiş güncellemelerin yerini alan eski bileşenleri kaldırır. Genellikle birkaç GB açar. 5-15 dakika sürebilir; pencere açık kalır, bitince sonucu görürsünüz.",
                ButtonText = "Başlat",
                Run = () => RunElevated("cmd.exe", "/k dism.exe /Online /Cleanup-Image /StartComponentCleanup", visible: true, "WinSxS bileşen temizliği başlatıldı"),
            },
            new()
            {
                Title = "Windows Disk Temizleme",
                Description = "Eski Windows kurulumu (Windows.old), yükseltme artıkları ve sistem dosyaları için Windows'un kendi aracı. Açıldığında “Sistem dosyalarını temizle”ye basın.",
                ButtonText = "Aç",
                Run = () => Start("cleanmgr.exe", "/d " + KnownFolders.SystemDrive.TrimEnd('\\')),
            },
            new()
            {
                Title = "Depolama Algısı",
                Description = "Windows'un geçici dosyaları ve Geri Dönüşüm Kutusu'nu belirli aralıklarla kendiliğinden temizlemesini sağlar.",
                ButtonText = "Ayarları aç",
                Run = () => PathUtil.OpenUri("ms-settings:storagesense"),
            },
            new()
            {
                Title = "Sistem Geri Yükleme alanı",
                Description = "Geri yükleme noktalarının kullanabileceği en fazla alanı “Yapılandır” bölümünden ayarlayabilirsiniz.",
                ButtonText = "Ayarları aç",
                Run = () => Start("SystemPropertiesProtection.exe", ""),
            },
        };

        var hiber = Path.Combine(KnownFolders.SystemDrive, "hiberfil.sys");
        long hiberSize = 0;
        try { if (File.Exists(hiber)) hiberSize = new FileInfo(hiber).Length; } catch { }
        if (hiberSize > 0)
            tools.Insert(1, new JunkTool
            {
                Title = $"Hazırda bekletme dosyası (hiberfil.sys): {Fmt.Size(hiberSize)}",
                Description = "Hazırda bekletmeyi kullanmıyorsanız kapatarak bu alanı geri kazanabilirsiniz. Hızlı başlatma da kapanır; normal kapatma ve uyku etkilenmez.",
                ButtonText = "Kapat",
                Run = () =>
                {
                    if (Dialogs.Confirm("Hazırda bekletme kapatılsın mı?", $"{Fmt.Size(hiberSize)} boşalacak. İstediğiniz zaman yönetici komut isteminde “powercfg /h on” ile geri açabilirsiniz.", "Kapat"))
                        RunElevated("powercfg.exe", "/hibernate off", visible: false, "Hazırda bekletme kapatıldı");
                },
            });
        Tools.ItemsSource = tools;
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is JunkTool t) t.Run();
    }

    private static void Start(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error("Açılamadı", ex.Message); }
    }

    private static void RunElevated(string exe, string args, bool visible, string log)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            });
            ActivityLog.Write(log, exe + " " + args, 0, 1, 0, "Windows aracı");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
        catch (Exception ex) { Dialogs.Error("Başlatılamadı", ex.Message); }
    }
}
