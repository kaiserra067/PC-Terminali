using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TemizlikPaneli.Controls;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public sealed class FsRow
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsFolder { get; init; }
    public long Size { get; init; }
    public long Files { get; init; }
    public DateTime? Modified { get; init; }
    public DirNode? Node { get; init; }
    public FileKind Kind { get; init; }
    public double Fraction { get; init; }
    public string Note { get; init; } = "";
    public Brush? GlyphBrush { get; init; }
    public Brush? BarBrush { get; init; }

    public string Glyph => IsFolder ? "" : "";
    public string SizeText => Fmt.Size(Size);
    public string PercentText => Size > 0 ? Fmt.Percent(Fraction) : "";
    public string FilesText => IsFolder ? Fmt.Count(Files) : "";
    public string DateText => Fmt.Date(Modified);
}

public sealed class RootOption
{
    public required string Label { get; init; }
    public required string Path { get; init; }
}

public sealed class Crumb
{
    public required string Label { get; init; }
    public required DirNode Node { get; init; }
    public bool HasNext { get; init; }
}

public partial class AnalyzerView : UserControl, IPage
{
    private static readonly StringComparer TrSort = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DriveScanResult? _scan;
    private DirNode? _current;
    private List<FsRow> _rows = new();
    private string _sortKey = "size";
    private bool _sortDesc = true;
    private bool _scanning;

    public AnalyzerView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateProgress();
        Map.ItemClicked += OnMapClicked;
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(() => { if (_current != null) _ = ShowFolderAsync(_current); });

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
        RootCombo.ItemsSource = roots;
        RootCombo.SelectedIndex = Math.Max(0, roots.FindIndex(r => PathUtil.Cmp.Equals(r.Path, KnownFolders.SystemDrive)));
    }

    public void OnShown()
    {
        if (_scan != null || _scanning) return;
        var root = (RootCombo.SelectedItem as RootOption)?.Path ?? KnownFolders.SystemDrive;
        _ = ScanAsync(root, refresh: false); // genel bakışta başlamış tarama varsa onu kullanır
    }

    // ------------------------------------------------------------------ Tarama

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (RootCombo.SelectedItem is RootOption r) _ = ScanAsync(r.Path, refresh: _scan != null && PathUtil.Cmp.Equals(_scan.Root.Name, r.Path));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => AppState.I.CancelDriveScan();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Analiz edilecek klasörü seçin" };
        if (dlg.ShowDialog() != true) return;
        var list = ((List<RootOption>)RootCombo.ItemsSource);
        var opt = list.FirstOrDefault(o => PathUtil.Cmp.Equals(o.Path, dlg.FolderName));
        if (opt == null)
        {
            opt = new RootOption { Label = dlg.FolderName, Path = dlg.FolderName };
            list.Add(opt);
            RootCombo.Items.Refresh();
        }
        RootCombo.SelectedItem = opt;
        _ = ScanAsync(opt.Path, refresh: true);
    }

    private async Task ScanAsync(string root, bool refresh)
    {
        if (_scanning) return;
        _scanning = true;
        ScanButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        EmptyText.Text = "Taranıyor…";
        _timer.Start();
        try
        {
            var task = AppState.I.GetDriveScanAsync(root, refresh);
            UpdateProgress();
            _scan = await task;
            StatusText.Text = $"Son tarama {_scan.ScannedAt:HH:mm} · {Fmt.Count(_scan.Root.FileCount)} dosya · {Fmt.Duration(_scan.Elapsed)}";
            ScanButtonText.Text = "Yeniden tara";
            await ShowFolderAsync(_scan.Root);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Tarama durduruldu.";
            if (_scan == null) EmptyText.Text = "Tarama durduruldu. Yeniden başlatmak için Tara'ya basın.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Tarama başarısız: " + ex.Message;
        }
        finally
        {
            _timer.Stop();
            _scanning = false;
            ScanButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateProgress()
    {
        var s = AppState.I.ActiveScanner;
        if (s == null) return;
        ProgressText.Text = $"{Fmt.Count(Interlocked.Read(ref s.FilesScanned))} dosya · {Fmt.Count(Interlocked.Read(ref s.DirsScanned))} klasör · {Fmt.Size(Interlocked.Read(ref s.BytesScanned))} · {s.CurrentPath}";
    }

    // ------------------------------------------------------------------ Gezinme

    private async Task ShowFolderAsync(DirNode node)
    {
        _current = node;
        var chain = new List<DirNode>();
        for (var n = node; n != null; n = n.Parent) chain.Insert(0, n);
        Crumbs.ItemsSource = chain.Select((n, i) => new Crumb { Label = i == 0 ? n.Name.TrimEnd('\\') : n.Name, Node = n, HasNext = i < chain.Count - 1 }).ToList();
        UpButton.IsEnabled = node.Parent != null;
        FolderSummary.Text = $"{Fmt.Size(node.Size)} · {Fmt.Count(node.FileCount)} dosya · {Fmt.Count(node.DirCount)} klasör"
            + (node.Inaccessible ? " · bazı alt klasörlere erişilemedi" : "");

        var folderBrush = ThemeManager.Brush("Series1");
        var fileBrush = ThemeManager.Brush("Series2");
        var muted = ThemeManager.Brush("TextMuted");
        long parent = Math.Max(1, node.Size);

        var rows = node.Children.Select(c => new FsRow
        {
            Name = c.Name,
            FullPath = c.FullPath,
            IsFolder = true,
            Size = c.Size,
            Files = c.FileCount,
            Modified = c.Newest == default ? null : c.Newest.ToLocalTime(),
            Node = c,
            Fraction = (double)c.Size / parent,
            Note = c.IsLink ? "bağlantı" : c.Inaccessible ? "erişilemedi" : "",
            GlyphBrush = folderBrush,
            BarBrush = folderBrush,
        }).ToList();
        _rows = rows;
        ApplySort();

        var path = node.FullPath;
        var files = await Task.Run(() => FsScan.ListFiles(path));
        if (!ReferenceEquals(_current, node)) return;
        rows.AddRange(files.Select(f => new FsRow
        {
            Name = Path.GetFileName(f.Path),
            FullPath = f.Path,
            Size = f.Size,
            Modified = f.Modified,
            Kind = FileKinds.Of(f.Path),
            Fraction = (double)f.Size / parent,
            GlyphBrush = muted,
            BarBrush = fileBrush,
        }));
        _rows = rows;
        ApplySort();
        EmptyText.Text = rows.Count == 0 ? "Bu klasör boş." : "";

        Map.Total = node.Size;
        var items = node.Children.Select(c =>
        {
            var kids = c.Children.Select(k => new TreemapItem { Label = k.Name, Size = k.Size, IsFolder = true, Tag = k }).ToList();
            if (c.OwnFilesSize > 0) kids.Add(new TreemapItem { Label = $"{Fmt.Count(c.OwnFileCount)} dosya", Size = c.OwnFilesSize });
            return new TreemapItem { Label = c.Name, Size = c.Size, IsFolder = true, Tag = c, Children = kids };
        }).ToList();
        items.AddRange(files.Select(f => new TreemapItem { Label = Path.GetFileName(f.Path), Size = f.Size, Tag = f }));
        Map.Items = items;

        var kinds = Enumerable.Range(0, FileKinds.Count).Select(k => (Name: FileKinds.Names[k], Size: node.KindSizes[k]));
        KindBars.ItemsSource = BarRow.Build(kinds, k => k.Name, k => k.Size, Fmt.Size, node.Size).Take(5).ToList();
    }

    private void ApplySort()
    {
        IEnumerable<FsRow> q = _sortKey switch
        {
            "name" => _sortDesc ? _rows.OrderByDescending(r => r.Name, TrSort) : _rows.OrderBy(r => r.Name, TrSort),
            "files" => _sortDesc ? _rows.OrderByDescending(r => r.Files) : _rows.OrderBy(r => r.Files),
            "date" => _sortDesc ? _rows.OrderByDescending(r => r.Modified) : _rows.OrderBy(r => r.Modified),
            _ => _sortDesc ? _rows.OrderByDescending(r => r.Size) : _rows.OrderBy(r => r.Size),
        };
        List.ItemsSource = q.ToList();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        if (_sortKey == key) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = key != "name"; }
        ApplySort();
    }

    private void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DirNode n }) _ = ShowFolderAsync(n);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Parent is { } p) _ = ShowFolderAsync(p);
    }

    private void OnMapClicked(TreemapItem item)
    {
        if (item.Tag is DirNode d) _ = ShowFolderAsync(d);
        else if (item.Tag is TopFile f)
        {
            var row = (List.ItemsSource as IEnumerable<FsRow>)?.FirstOrDefault(r => PathUtil.Cmp.Equals(r.FullPath, f.Path));
            if (row != null) { List.SelectedItem = row; List.ScrollIntoView(row); }
        }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
        else if (e.Key == Key.Back) { Up_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { _ = DeleteSelectedAsync(recycle: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
    }

    private void OpenSelected()
    {
        if (List.SelectedItem is not FsRow r) return;
        if (r.Node != null && !r.Node.IsLink) _ = ShowFolderAsync(r.Node);
        else if (!r.IsFolder) PathUtil.OpenUri(r.FullPath);
    }

    // ------------------------------------------------------------------ Sağ tık menüsü

    private List<FsRow> Selected() => List.SelectedItems.Cast<FsRow>().ToList();

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Selected().FirstOrDefault() is { } r) PathUtil.OpenInExplorer(r.FullPath);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (s.Count > 0) { try { Clipboard.SetText(string.Join(Environment.NewLine, s.Select(r => r.FullPath))); } catch { } }
    }

    private void Recycle_Click(object sender, RoutedEventArgs e) => _ = DeleteSelectedAsync(recycle: true);
    private void DeletePermanent_Click(object sender, RoutedEventArgs e) => _ = DeleteSelectedAsync(recycle: false);

    private async Task DeleteSelectedAsync(bool recycle)
    {
        var rows = Selected();
        if (rows.Count == 0 || _current == null) return;
        var blocked = new List<string>();
        foreach (var r in rows.ToList())
            if (!SafetyGuard.CanDelete(r.FullPath, out var why)) { blocked.Add($"{r.FullPath} — {why}"); rows.Remove(r); }
        if (blocked.Count > 0)
            Dialogs.Show("Bazı öğeler silinemez", "Bu konumlar Windows'un çalışması için gerekli ya da korunuyor. Bunlara dokunulmayacak.", Dialogs.Kind.Warning, "Tamam", null, blocked);
        if (rows.Count == 0) return;

        long total = rows.Sum(r => r.Size);
        bool ok = recycle
            ? Dialogs.Confirm("Geri Dönüşüm Kutusu'na taşınsın mı?", $"{rows.Count} öğe ({Fmt.Size(total)}) Geri Dönüşüm Kutusu'na taşınacak. Yer, kutu boşaltılınca açılır.", "Taşı", false, rows.Select(r => $"{r.FullPath}  ({r.SizeText})"))
            : Dialogs.Confirm("Kalıcı olarak silinsin mi?", $"{rows.Count} öğe ({Fmt.Size(total)}) kalıcı olarak silinecek. Bu işlem geri alınamaz.", "Kalıcı sil", true, rows.Select(r => $"{r.FullPath}  ({r.SizeText})"));
        if (!ok) return;

        var st = await Deleter.DeleteUserItemsAsync("Disk analizinden silme", rows.Select(r => new DeleteItem(r.FullPath, r.Size)).ToList(), recycle);
        var node = _current;
        foreach (var r in rows)
        {
            if (File.Exists(r.FullPath) || Directory.Exists(r.FullPath)) continue;
            if (r.Node != null) r.Node.Parent?.RemoveChild(r.Node);
            else node.RemoveFile(r.Size, r.Kind);
        }
        ShowResult(st);
        await ShowFolderAsync(node);
    }

    public static void ShowResult(DeleteStats st)
    {
        if (st.Failed == 0) Dialogs.Success("İşlem tamamlandı", st.Summary);
        else Dialogs.Show("İşlem kısmen tamamlandı", st.Summary, Dialogs.Kind.Warning, "Tamam", null, st.Errors);
    }
}
