using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TemizlikPaneli.Core;
using TemizlikPaneli.Views;

namespace TemizlikPaneli;

/// <summary>Bir sayfa gösterildiğinde çağrılır (veriler ilk gösterimde yüklenir).</summary>
public interface IPage
{
    void OnShown();
}

public partial class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }

    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly string[] _args;
    private string _currentKey = "dashboard";

    public MainWindow(string[] args)
    {
        InitializeComponent();
        Current = this;
        _args = args;
        LogoImage.Source = AppIcon(64);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        UpdateAdminCard();
        ApplySizeArg();
        Loaded += OnLoaded;
    }

    public static ImageSource? AppIcon(int maxSize)
    {
        try
        {
            var dec = new IconBitmapDecoder(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return dec.Frames.Where(f => f.PixelWidth <= maxSize).OrderByDescending(f => f.PixelWidth).FirstOrDefault() ?? dec.Frames[0];
        }
        catch { return null; }
    }

    private string? Arg(string name)
    {
        int i = Array.IndexOf(_args, name);
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    private void ApplySizeArg()
    {
        var size = Arg("--size")?.Split('x');
        if (size is { Length: 2 } && double.TryParse(size[0], out var w) && double.TryParse(size[1], out var h))
        {
            Width = w;
            Height = h;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Navigate(Arg("--page") ?? "dashboard");
        var shots = Arg("--shots");
        var outDir = shots != null ? _args[Array.IndexOf(_args, "--shots") + 2] : null;
        if (shots != null && outDir != null)
        {
            int delay = int.TryParse(Arg("--delay"), out var d) ? d : 6000;
            _ = ShootAsync(shots.Split(','), outDir, delay);
        }
    }

    private void UpdateAdminCard()
    {
        if (Admin.IsElevated)
        {
            AdminText.Text = "Yönetici modu";
            AdminIcon.Foreground = ThemeManager.Brush("GoodBrush");
            AdminHint.Text = "Tüm temizlik işlemleri kullanılabilir.";
            ElevateButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminText.Text = "Normal mod";
            AdminIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            AdminHint.Text = "Windows klasörlerini ve tüm kullanıcılar için kurulu programları temizlemek için yönetici izni gerekir.";
        }
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Admin.RestartElevated("--page " + _currentKey)) Application.Current.Shutdown();
    }

    public void Navigate(string key)
    {
        foreach (var rb in Nav.Children.OfType<RadioButton>())
        {
            if (rb.CommandParameter as string != key) continue;
            if (rb.IsChecked == true) ShowPage(key);
            else rb.IsChecked = true;
            return;
        }
        ShowPage(key);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { CommandParameter: string key }) ShowPage(key);
    }

    private void ShowPage(string key)
    {
        _currentKey = key;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pages[key] = page;
        }
        PageHost.Content = page;
        if (page is IPage p) p.OnShown();
    }

    private static FrameworkElement CreatePage(string key) => key switch
    {
        "dashboard" => new DashboardView(),
        "analyzer" => new AnalyzerView(),
        "apps" => new AppsView(),
        "games" => new GamesView(),
        "junk" => new JunkView(),
        "leftovers" => new LeftoversView(),
        "large" => new LargeFilesView(),
        "dupes" => new DuplicatesView(),
        "media" => new MediaView(),
        "history" => new HistoryView(),
        "settings" => new SettingsView(),
        _ => new TextBlock { Text = "Hazırlanıyor…", Margin = new Thickness(32) },
    };

    // ------------------------------------------------------------------ Ekran görüntüsü (geliştirme amaçlı)

    private async Task ShootAsync(string[] pages, string outDir, int delay)
    {
        Directory.CreateDirectory(outDir);
        foreach (var page in pages)
        {
            Navigate(page);
            await Task.Delay(delay);
            SaveScreenshot(Path.Combine(outDir, page + ".png"));
            DumpWide(Path.Combine(outDir, page + "-genislik.txt"));
        }
        Application.Current.Shutdown();
    }

    /// <summary>Geliştirme: sayfa alanından geniş yer isteyen öğeleri listeler (yerleşim taşmalarını bulmak için).</summary>
    private void DumpWide(string file)
    {
        var host = PageHost;
        double limit = host.ActualWidth;
        var lines = new List<string> { $"Sayfa alanı: {limit:0}" };
        void Walk(DependencyObject d, int depth)
        {
            if (d is FrameworkElement fe && fe.DesiredSize.Width > limit - 60 && depth < 60)
                lines.Add($"{new string(' ', depth)}{fe.GetType().Name} '{fe.Name}' istenen={fe.DesiredSize.Width:0} gerçek={fe.ActualWidth:0}");
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), depth + 1);
        }
        Walk(host, 0);
        File.WriteAllLines(file, lines);
    }

    public void SaveScreenshot(string file)
    {
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            dc.DrawRectangle(Background, null, rect);
            // Görünür alanı birebir çiz (içerik sınırlarına göre esnetme yapma)
            dc.DrawRectangle(new VisualBrush(root)
            {
                Viewbox = rect, ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = rect, ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
            }, null, rect);
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
