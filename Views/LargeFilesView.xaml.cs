using System.IO.Enumeration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public sealed class LargeFile : ObservableObject
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public FileKind Kind { get; init; }
    public bool CanDelete { get; init; }
    public string Note { get; init; } = "";
    public double Fraction { get; set; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string KindName => FileKinds.Names[(int)Kind];
    public string SizeText => Fmt.Size(Size);
    public string DateText => Fmt.Date(Modified);
    public string Glyph => Kind switch
    {
        FileKind.Video => "",
        FileKind.Image => "",
        FileKind.Audio => "",
        FileKind.Archive => "",
        FileKind.Document => "",
        FileKind.GameData => "",
        _ => "",
    };
}

public partial class LargeFilesView : UserControl, IPage
{
    private List<LargeFile> _files = new();
    private FileKind? _kind;
    private bool _loaded, _busy;

    public LargeFilesView()
    {
        InitializeComponent();
        var roots = new List<RootOption>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                    roots.Add(new RootOption { Label = $"{(string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Yerel Disk" : d.VolumeLabel)} ({d.Name.TrimEnd('\\')})", Path = d.Name });
            }
            catch { }
        }
        roots.Add(new RootOption { Label = "Kullanıcı klasörüm", Path = KnownFolders.UserProfile });
        roots.Add(new RootOption { Label = "İndirilenler", Path = KnownFolders.Downloads });
        RootCombo.ItemsSource = roots;
        RootCombo.SelectedIndex = 0;
        int mb = AppSettings.Current.LargeFileMinMb;
        foreach (ComboBoxItem it in MinCombo.Items) if (it.Tag as string == mb.ToString()) it.IsSelected = true;
        UpdateSummary();
    }

    public void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        _ = ScanAsync();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Büyük dosyaların aranacağı klasörü seçin" };
        if (dlg.ShowDialog() != true) return;
        var list = (List<RootOption>)RootCombo.ItemsSource;
        var opt = new RootOption { Label = dlg.FolderName, Path = dlg.FolderName };
        list.Add(opt);
        RootCombo.Items.Refresh();
        RootCombo.SelectedItem = opt;
        _ = ScanAsync();
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy || RootCombo.SelectedItem is not RootOption root) return;
        long minBytes = long.Parse((MinCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "500") * 1024 * 1024;
        _busy = true;
        ScanButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        List.ItemsSource = null;
        try
        {
            var inv = await AppState.I.GetInventoryAsync();
            var locked = BuildLockedFolders(inv);
            long dirs = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => ProgressText.Text = $"{root.Path} içinde aranıyor… {Fmt.Count(Interlocked.Read(ref dirs))} klasör";
            timer.Start();
            var found = await Task.Run(() =>
            {
                var en = new FileSystemEnumerable<(string Path, long Size, DateTime Mod)>(root.Path,
                    (ref FileSystemEntry e) => (e.ToFullPath(), e.Length, e.LastWriteTimeUtc.LocalDateTime),
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                {
                    ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.Length >= minBytes && (e.Attributes & FsScan.CloudOnly) == 0,
                    ShouldRecursePredicate = (ref FileSystemEntry e) =>
                    {
                        Interlocked.Increment(ref dirs);
                        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) return true;
                        try { return new DirectoryInfo(e.ToFullPath()).LinkTarget == null; } catch { return false; }
                    },
                };
                var list = new List<LargeFile>();
                foreach (var (path, size, mod) in en) list.Add(Make(path, size, mod, locked));
                return list;
            });
            timer.Stop();
            _files = found.OrderByDescending(f => f.Size).ToList();
            long max = _files.Count > 0 ? _files[0].Size : 1;
            foreach (var f in _files) f.Fraction = Math.Max(0.006, (double)f.Size / max);
            BuildChips();
            ApplyFilter();
            if (int.TryParse((MinCombo.SelectedItem as ComboBoxItem)?.Tag as string, out var mb) && mb != AppSettings.Current.LargeFileMinMb)
            {
                AppSettings.Current.LargeFileMinMb = mb;
                AppSettings.Current.Save();
            }
        }
        catch (Exception ex) { Dialogs.Error("Arama başarısız", ex.Message); }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static List<(string Dir, string Note)> BuildLockedFolders(Inventory inv)
    {
        var list = new List<(string, string)>();
        foreach (var g in inv.Games.GameFolders) list.Add((g, "Oyun dosyası · Oyunlar bölümünden kaldırın"));
        foreach (var a in inv.AllApps) if (a.InstallLocation != null) list.Add((a.InstallLocation, "Program dosyası · Uygulamalar bölümünden kaldırın"));
        list.Add((KnownFolders.ProgramFiles, "Program dosyası"));
        list.Add((KnownFolders.ProgramFilesX86, "Program dosyası"));
        list.Add((KnownFolders.ProgramData, "Program verisi"));
        list.Add((KnownFolders.Windows, "Windows dosyası"));
        return list;
    }

    private static LargeFile Make(string path, long size, DateTime mod, List<(string Dir, string Note)> locked)
    {
        string note = "";
        bool can = SafetyGuard.CanDelete(path, out var why);
        if (!can) note = why;
        else
        {
            foreach (var (dir, n) in locked)
                if (PathUtil.IsStrictlyUnder(path, dir)) { can = false; note = n; break; }
        }
        if (can && PathUtil.IsStrictlyUnder(path, Path.Combine(KnownFolders.UserProfile, "AppData"))) note = "Uygulama verisi";
        return new LargeFile { Path = path, Size = size, Modified = mod, Kind = FileKinds.Of(path), CanDelete = can, Note = note };
    }

    private void BuildChips()
    {
        Chips.Children.Clear();
        void Add(string label, FileKind? kind)
        {
            var rb = new RadioButton { Content = label, Style = (Style)FindResource("ChipToggle"), GroupName = "kind", IsChecked = kind == _kind, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _kind = kind; ApplyFilter(); };
            Chips.Children.Add(rb);
        }
        Add("Tümü", null);
        foreach (var g in _files.GroupBy(f => f.Kind).OrderByDescending(g => g.Sum(f => f.Size)))
            Add($"{FileKinds.Names[(int)g.Key]} ({g.Count()})", g.Key);
    }

    private void ApplyFilter()
    {
        if (!IsInitialized) return;
        IEnumerable<LargeFile> q = _files;
        if (ShowLocked.IsChecked != true) q = q.Where(f => f.CanDelete);
        if (_kind is { } k) q = q.Where(f => f.Kind == k);
        var s = SearchBox.Text.Trim();
        if (s.Length > 0) q = q.Where(f => TextMatch.Contains(f.Name, s));
        var list = q.ToList();
        List.ItemsSource = list;
        int hidden = _files.Count(f => !f.CanDelete);
        EmptyText.Visibility = list.Count == 0 && !_busy ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _files.Count == 0 ? "Bu boyutun üzerinde dosya bulunamadı."
            : hidden > 0 && ShowLocked.IsChecked != true ? $"Silinebilir büyük dosya yok. Programlara ait {hidden} dosya gizli." : "Eşleşen dosya yok.";
        UpdateSummary();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
    private void Check_Click(object sender, RoutedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        var sel = _files.Where(f => f.IsSelected).ToList();
        var free = _files.Where(f => f.CanDelete).ToList();
        int locked = _files.Count - free.Count;
        SummaryText.Text = _files.Count == 0 ? "" :
            $"Silinebilir {Fmt.Count(free.Count)} dosya ({Fmt.Size(free.Sum(f => f.Size))})"
            + (locked > 0 ? $" · programlara ait {Fmt.Count(locked)} dosya kilitli" : "")
            + $" · {sel.Count} seçili ({Fmt.Size(sel.Sum(f => f.Size))})";
        RecycleButton.IsEnabled = DeleteButton.IsEnabled = sel.Count > 0;
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LargeFile f) PathUtil.OpenInExplorer(f.Path);
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is LargeFile f) PathUtil.OpenInExplorer(f.Path);
    }

    private void Recycle_Click(object sender, RoutedEventArgs e) => _ = DeleteAsync(true);
    private void Delete_Click(object sender, RoutedEventArgs e) => _ = DeleteAsync(false);

    private async Task DeleteAsync(bool recycle)
    {
        var sel = _files.Where(f => f.IsSelected && f.CanDelete).ToList();
        if (sel.Count == 0) return;
        long total = sel.Sum(f => f.Size);
        bool ok = recycle
            ? Dialogs.Confirm("Geri Dönüşüm Kutusu'na taşınsın mı?", $"{sel.Count} dosya ({Fmt.Size(total)}) Geri Dönüşüm Kutusu'na taşınacak. Yer, kutu boşaltılınca açılır. Kutuya sığmayan çok büyük dosyalar için Windows ayrıca onay ister.", "Taşı", false, sel.Select(f => $"{f.Path}  ({f.SizeText})"))
            : Dialogs.Confirm("Kalıcı olarak silinsin mi?", $"{sel.Count} dosya ({Fmt.Size(total)}) kalıcı olarak silinecek. Bu işlem geri alınamaz.", "Kalıcı sil", true, sel.Select(f => $"{f.Path}  ({f.SizeText})"));
        if (!ok) return;
        var st = await WaitDialog.WhileAsync("Siliniyor", "Seçilen dosyalar siliniyor…",
            Deleter.DeleteUserItemsAsync("Büyük dosya silindi", sel.Select(f => new DeleteItem(f.Path, f.Size)).ToList(), recycle));
        _files.RemoveAll(f => !File.Exists(f.Path));
        BuildChips();
        ApplyFilter();
        AnalyzerView.ShowResult(st);
    }
}
