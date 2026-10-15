using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public partial class DuplicatesView : UserControl, IPage
{
    private List<DupGroup> _groups = new();
    private CancellationTokenSource? _cts;
    private bool _busy;

    public DuplicatesView()
    {
        InitializeComponent();
        var roots = new List<RootOption>
        {
            new() { Label = "Kullanıcı klasörüm (AppData hariç)", Path = KnownFolders.UserProfile },
            new() { Label = "Belgeler", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
            new() { Label = "İndirilenler", Path = KnownFolders.Downloads },
            new() { Label = "Masaüstü", Path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) },
            new() { Label = "Resimler", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) },
            new() { Label = "Videolar", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) },
            new() { Label = "Müzik", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) },
        };
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady && !PathUtil.Cmp.Equals(d.Name, KnownFolders.SystemDrive))
                    roots.Add(new RootOption { Label = $"{(string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Disk" : d.VolumeLabel)} ({d.Name.TrimEnd('\\')})", Path = d.Name });
            }
            catch { }
        }
        RootCombo.ItemsSource = roots;
        RootCombo.SelectedIndex = 0;
        UpdateSummary();
    }

    public void OnShown() { }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Yinelenen dosyaların aranacağı klasörü seçin" };
        if (dlg.ShowDialog() != true) return;
        var list = (List<RootOption>)RootCombo.ItemsSource;
        var opt = new RootOption { Label = dlg.FolderName, Path = dlg.FolderName };
        list.Add(opt);
        RootCombo.Items.Refresh();
        RootCombo.SelectedItem = opt;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RootCombo.SelectedItem is not RootOption root) return;
        long min = long.Parse((MinCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "1") * 1024 * 1024;
        _busy = true;
        _cts = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        List.ItemsSource = null;
        var prog = new DupProgress();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => ProgressText.Text = $"{prog.Stage} · {Fmt.Count(prog.FilesSeen)} dosya · {Fmt.Size(Interlocked.Read(ref prog.BytesHashed))} okundu";
        timer.Start();
        try
        {
            bool skipAppData = PathUtil.Cmp.Equals(root.Path, KnownFolders.UserProfile);
            var ct = _cts.Token;
            _groups = await Task.Run(() => DuplicateFinder.Find(new[] { root.Path }, min, skipAppData, prog, ct));
            List.ItemsSource = _groups;
            ResultText.Text = _groups.Count == 0 ? "" : $"{Fmt.Count(_groups.Count)} grup · {Fmt.Count(_groups.Sum(g => g.Files.Count))} dosya · boşa giden {Fmt.Size(_groups.Sum(g => g.Wasted))}";
            EmptyText.Text = "Yinelenen dosya bulunamadı.";
            EmptyText.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            EmptyText.Text = "Arama durduruldu.";
            EmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            timer.Stop();
            _busy = false;
            ScanButton.IsEnabled = true;
            StopButton.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            UpdateSummary();
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        var sel = _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        SummaryText.Text = _groups.Count == 0 ? "" : $"{sel.Count} dosya seçili · {Fmt.Size(sel.Sum(f => f.Size))} boşalacak";
        DeleteButton.IsEnabled = sel.Count > 0 && !_busy;
        KeepOldestButton.IsEnabled = KeepNewestButton.IsEnabled = ClearButton.IsEnabled = _groups.Count > 0 && !_busy;
    }

    private void KeepOldest_Click(object sender, RoutedEventArgs e) => AutoSelect(newest: false);
    private void KeepNewest_Click(object sender, RoutedEventArgs e) => AutoSelect(newest: true);

    private void AutoSelect(bool newest)
    {
        foreach (var g in _groups)
        {
            var keep = newest ? g.Files.OrderByDescending(f => f.Modified).First() : g.Files.OrderBy(f => f.Modified).First();
            foreach (var f in g.Files) f.IsSelected = !ReferenceEquals(f, keep);
        }
        UpdateSummary();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _groups.SelectMany(g => g.Files)) f.IsSelected = false;
        UpdateSummary();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DupFile f) PathUtil.OpenInExplorer(f.Path);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var all = _groups.Where(g => g.AllSelected).ToList();
        if (all.Count > 0)
        {
            Dialogs.Error("Her gruptan en az bir dosya kalmalı",
                $"{all.Count} grupta tüm kopyalar seçili. Hangisini saklayacağınızı seçmek için birinin işaretini kaldırın.", all.Select(g => g.Name));
            return;
        }
        var sel = _groups.SelectMany(g => g.Files).Where(f => f.IsSelected).ToList();
        if (sel.Count == 0) return;
        if (!Dialogs.Confirm("Kopyalar Geri Dönüşüm Kutusu'na taşınsın mı?",
                $"{sel.Count} kopya ({Fmt.Size(sel.Sum(f => f.Size))}) Geri Dönüşüm Kutusu'na taşınacak. Her dosyanın en az bir kopyası yerinde kalır.",
                "Taşı", false, sel.Select(f => f.Path)))
            return;
        var st = await WaitDialog.WhileAsync("Taşınıyor", "Kopyalar Geri Dönüşüm Kutusu'na taşınıyor…",
            Deleter.DeleteUserItemsAsync("Yinelenen dosyalar", sel.Select(f => new DeleteItem(f.Path, f.Size)).ToList(), recycle: true));
        foreach (var g in _groups) g.Files.RemoveAll(f => !File.Exists(f.Path));
        _groups = _groups.Where(g => g.Files.Count > 1).ToList();
        List.ItemsSource = _groups;
        ResultText.Text = _groups.Count == 0 ? "" : $"{Fmt.Count(_groups.Count)} grup kaldı · boşa giden {Fmt.Size(_groups.Sum(g => g.Wasted))}";
        UpdateSummary();
        AnalyzerView.ShowResult(st);
    }
}
