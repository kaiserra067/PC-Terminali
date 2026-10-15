using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public sealed class Option
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public string Hint { get; init; } = "";
    public int Number { get; init; }
}

public partial class MediaView : UserControl, IPage
{
    private readonly ObservableCollection<MediaJob> _jobs = new();
    private MediaPlatform _platform = MediaPlatform.Other;
    private string[] _files = Array.Empty<string>();
    private bool _init, _toolsBusy;

    private static readonly Option[] VideoFormats =
    {
        new() { Label = "MP4 (önerilen)", Value = "mp4", Hint = "Her cihazda ve oynatıcıda açılır." },
        new() { Label = "MKV", Value = "mkv", Hint = "En yüksek kaliteyi kayıpsız saklar; bazı telefonlar açamayabilir." },
        new() { Label = "WEBM", Value = "webm", Hint = "YouTube'un kendi biçimi (VP9/AV1 + Opus)." },
        new() { Label = "MOV", Value = "mov", Hint = "Apple cihazları ve video düzenleyiciler için. H.264 ile kaydedilir (YouTube'da en fazla 1080p)." },
        new() { Label = "AVI", Value = "avi", Hint = "Eski oynatıcılar için. H.264 ile kaydedilir (YouTube'da en fazla 1080p)." },
    };

    private static readonly Option[] Qualities =
    {
        new() { Label = "En iyi kalite", Value = "0", Number = 0 },
        new() { Label = "4K (2160p)", Value = "2160", Number = 2160 },
        new() { Label = "1440p", Value = "1440", Number = 1440 },
        new() { Label = "1080p (Full HD)", Value = "1080", Number = 1080 },
        new() { Label = "720p (HD)", Value = "720", Number = 720 },
        new() { Label = "480p", Value = "480", Number = 480 },
        new() { Label = "360p (en küçük)", Value = "360", Number = 360 },
    };

    private static readonly Option[] Bitrates =
    {
        new() { Label = "320 kbps (en iyi)", Value = "320", Number = 320 },
        new() { Label = "256 kbps", Value = "256", Number = 256 },
        new() { Label = "192 kbps", Value = "192", Number = 192 },
        new() { Label = "128 kbps (küçük dosya)", Value = "128", Number = 128 },
    };

    private static readonly Option[] AudioFormats =
    {
        new() { Label = "MP3", Value = "mp3", Hint = "Her yerde çalışır." },
        new() { Label = "M4A (AAC) · orijinal kalite", Value = "m4a-orig", Hint = "Kaynaktaki ses yeniden kodlanmadan kaydedilir; kalite kaybı olmaz." },
        new() { Label = "OPUS · orijinal kalite", Value = "opus-orig", Hint = "YouTube'un en yüksek kaliteli sesi, yeniden kodlanmadan." },
        new() { Label = "FLAC (kayıpsız kapsayıcı)", Value = "flac", Hint = "Büyük dosya; kaynağın üzerine kalite eklemez." },
        new() { Label = "WAV (sıkıştırmasız)", Value = "wav", Hint = "Çok büyük dosya; ses düzenleme programları için." },
        new() { Label = "OGG (Vorbis)", Value = "ogg", Hint = "" },
        new() { Label = "AAC", Value = "aac", Hint = "" },
    };

    /// <summary>YouTube Music'in sunduğu biçimler (Opus ve AAC/M4A) ve MP3.</summary>
    private static readonly Option[] MusicFormats =
    {
        new() { Label = "MP3", Value = "mp3", Hint = "Her yerde çalışır. YouTube Music sesi MP3'e dönüştürülür." },
        new() { Label = "M4A (AAC) · YouTube Music'in kendi biçimi", Value = "m4a-orig", Hint = "YouTube Music'teki ses dosyası birebir kaydedilir; kalite kaybı olmaz. iPhone ve iTunes ile uyumlu." },
        new() { Label = "OPUS · YouTube Music'in kendi biçimi", Value = "opus-orig", Hint = "YouTube Music'in en yüksek kaliteli sesi birebir kaydedilir; kalite kaybı olmaz." },
    };

    public MediaView()
    {
        InitializeComponent();
        Jobs.ItemsSource = _jobs;
        _jobs.CollectionChanged += (_, _) => NoJobs.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QualityCombo.ItemsSource = Qualities;
        QualityCombo.SelectedIndex = 0;
        BuildFormats();
        BuildCookies();
        UpdateOutputText();
        _init = true;
    }

    public void OnShown() => _ = RefreshToolsAsync();

    private static string OutputDir => !string.IsNullOrWhiteSpace(AppSettings.Current.MediaOutputDir) && Directory.Exists(AppSettings.Current.MediaOutputDir)
        ? AppSettings.Current.MediaOutputDir!
        : KnownFolders.Downloads;

    // ------------------------------------------------------------------ Araçlar

    private async Task RefreshToolsAsync()
    {
        if (_toolsBusy) return;
        if (MediaTools.Ready)
        {
            var v = await MediaTools.YtDlpVersionAsync();
            ToolsText.Text = $"İndirme araçları hazır · yt-dlp {v} · ffmpeg · gallery-dl · deno. Siteler değiştikçe indirme bozulursa araçları güncelleyin.";
            ToolsIcon.Text = "";
            ToolsIcon.Foreground = ThemeManager.Brush("GoodBrush");
            ToolsButton.Content = "Araçları güncelle";
        }
        else
        {
            ToolsText.Text = "İndirme araçları (yt-dlp, gallery-dl, ffmpeg, deno · yaklaşık 280 MB) ilk kullanımda otomatik indirilecek.";
            ToolsIcon.Text = "";
            ToolsIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            ToolsButton.Content = "Araçları şimdi indir";
        }
    }

    private async Task<bool> EnsureToolsAsync()
    {
        if (MediaTools.Ready) return true;
        _toolsBusy = true;
        SetBusy(true);
        try
        {
            ToolsIcon.Text = "";
            var progress = new Progress<string>(s => ToolsText.Text = s);
            await MediaTools.EnsureAsync(progress, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error("Araçlar indirilemedi", "İnternet bağlantınızı kontrol edip tekrar deneyin.\n\n" + ex.Message);
            return false;
        }
        finally
        {
            _toolsBusy = false;
            SetBusy(false);
            await RefreshToolsAsync();
        }
    }

    private async void Tools_Click(object sender, RoutedEventArgs e)
    {
        if (!MediaTools.Ready) { await EnsureToolsAsync(); return; }
        _toolsBusy = true;
        SetBusy(true);
        try
        {
            ToolsText.Text = "yt-dlp ve gallery-dl güncelleniyor…";
            var log = new List<string>();
            await MediaTools.RunAsync(MediaTools.YtDlp, new[] { "-U" }, l => log.Add(l), CancellationToken.None);
            await MediaTools.UpdateGalleryDlAsync(new Progress<string>(s => ToolsText.Text = s), CancellationToken.None);
            log.Add("gallery-dl en son sürüme güncellendi.");
            Dialogs.Info("Araçlar güncellendi", "İndirme araçları en son sürümü kullanıyor.", log.Where(l => l.Trim().Length > 0));
        }
        catch (Exception ex) { Dialogs.Error("Güncellenemedi", ex.Message); }
        finally
        {
            _toolsBusy = false;
            SetBusy(false);
            await RefreshToolsAsync();
        }
    }

    private void SetBusy(bool busy)
    {
        DownloadButton.IsEnabled = ConvertButton.IsEnabled = ToolsButton.IsEnabled = !busy;
    }

    // ------------------------------------------------------------------ Bağlantı ve biçimler

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) UrlBox.Text = Clipboard.GetText().Trim(); } catch { }
        UrlBox.Focus();
        UrlBox.CaretIndex = UrlBox.Text.Length;
    }

    private void Url_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Download_Click(sender, e);
    }

    private void Url_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_init) return;
        var url = UrlBox.Text.Trim();
        var p = url.Length == 0 ? MediaPlatform.Other : MediaTools.Detect(url);
        bool changed = p != _platform;
        _platform = p;
        if (url.Length == 0)
        {
            PlatformText.Text = "Desteklenenler: YouTube, YouTube Music, X (Twitter), TikTok, Instagram (Reels, hikaye, gönderi)";
            PlatformIcon.Text = "";
        }
        else if (p == MediaPlatform.Other)
        {
            PlatformText.Text = Uri.TryCreate(url, UriKind.Absolute, out _)
                ? "Tanınmayan site — yine de denenebilir (yt-dlp binlerce siteyi destekler)."
                : "Geçerli bir bağlantı değil.";
            PlatformIcon.Text = "";
        }
        else
        {
            PlatformIcon.Text = "";
            PlatformText.Text = "Algılandı: " + MediaTools.PlatformName(p) + p switch
            {
                MediaPlatform.Instagram => " · Reels, video gönderi, fotoğraf gönderi ve hikaye (hikayeler için oturum gerekir)",
                MediaPlatform.YouTubeMusic => " · şarkı, albüm ve çalma listesi",
                _ => "",
            };
        }
        bool isList = url.Contains("list=", StringComparison.OrdinalIgnoreCase) && !url.Contains("watch?v=", StringComparison.OrdinalIgnoreCase)
                      || url.Contains("/playlist", StringComparison.OrdinalIgnoreCase) || url.Contains("/browse/", StringComparison.OrdinalIgnoreCase);
        PlaylistCheck.IsChecked = isList;
        if (changed)
        {
            if (p == MediaPlatform.YouTubeMusic) ModeAudio.IsChecked = true;
            BuildFormats();
        }
    }

    private bool Audio => ModeAudio.IsChecked == true;

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_init) BuildFormats();
    }

    private void BuildFormats()
    {
        var keep = (FormatCombo.SelectedItem as Option)?.Value;
        var list = Audio ? (_platform == MediaPlatform.YouTubeMusic ? MusicFormats : AudioFormats) : VideoFormats;
        FormatCombo.ItemsSource = list;
        FormatCombo.SelectedItem = list.FirstOrDefault(o => o.Value == keep) ?? list[0];
        UpdateQuality();
    }

    private void Format_Changed(object sender, SelectionChangedEventArgs e) => UpdateQuality();

    private void UpdateQuality()
    {
        var f = FormatCombo.SelectedItem as Option;
        FormatHint.Text = f?.Hint ?? "";
        H264Check.Visibility = !Audio && f?.Value == "mp4" ? Visibility.Visible : Visibility.Collapsed;
        if (Audio)
        {
            bool needsBitrate = f?.Value is "mp3" or "ogg" or "aac";
            QualityPanel.Visibility = needsBitrate ? Visibility.Visible : Visibility.Hidden;
            QualityLabel.Text = "Ses kalitesi";
            if (!ReferenceEquals(QualityCombo.ItemsSource, Bitrates)) { QualityCombo.ItemsSource = Bitrates; QualityCombo.SelectedIndex = 0; }
        }
        else
        {
            QualityPanel.Visibility = Visibility.Visible;
            QualityLabel.Text = "Kalite (en fazla)";
            if (!ReferenceEquals(QualityCombo.ItemsSource, Qualities)) { QualityCombo.ItemsSource = Qualities; QualityCombo.SelectedIndex = 0; }
        }
    }

    // ------------------------------------------------------------------ Oturum ve kayıt klasörü

    private void BuildCookies()
    {
        var list = new List<Option>
        {
            new() { Label = "Kullanma", Value = "" },
            new() { Label = "Microsoft Edge", Value = "edge" },
            new() { Label = "Brave", Value = "brave" },
            new() { Label = "Google Chrome", Value = "chrome" },
            new() { Label = "Mozilla Firefox (en güvenilir)", Value = "firefox" },
            new() { Label = "cookies.txt dosyası seç…", Value = "pick" },
        };
        var saved = AppSettings.Current.MediaCookies ?? "";
        if (saved.StartsWith("file:")) list.Insert(list.Count - 1, new Option { Label = "Dosya: " + Path.GetFileName(saved[5..]), Value = saved });
        CookieCombo.ItemsSource = list;
        CookieCombo.SelectedItem = list.FirstOrDefault(o => o.Value == saved) ?? list[0];
    }

    private void Cookie_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init || CookieCombo.SelectedItem is not Option o) return;
        if (o.Value == "pick")
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "cookies.txt dosyasını seçin (Netscape biçimi)", Filter = "Çerez dosyası (*.txt)|*.txt|Tüm dosyalar|*.*" };
            if (dlg.ShowDialog() == true) AppSettings.Current.MediaCookies = "file:" + dlg.FileName;
            AppSettings.Current.Save();
            _init = false;
            BuildCookies();
            _init = true;
            return;
        }
        AppSettings.Current.MediaCookies = o.Value.Length == 0 ? null : o.Value;
        AppSettings.Current.Save();
        if (o.Value is "edge" or "chrome" or "brave")
            FormatHint.Text = "Not: Chromium tabanlı tarayıcıların çerezleri yalnızca tarayıcı tamamen kapalıyken okunabilir. Sorun yaşarsanız Firefox ya da cookies.txt kullanın.";
    }

    private void UpdateOutputText()
    {
        OutputText.Text = OutputDir;
        OutputText.ToolTip = OutputDir;
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "İndirilen ve dönüştürülen dosyaların kaydedileceği klasör", InitialDirectory = OutputDir };
        if (dlg.ShowDialog() != true) return;
        AppSettings.Current.MediaOutputDir = dlg.FolderName;
        AppSettings.Current.Save();
        UpdateOutputText();
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e) => PathUtil.OpenInExplorer(OutputDir);

    // ------------------------------------------------------------------ İndir

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
        {
            Dialogs.Info("Bağlantı gerekli", "İndirmek istediğiniz videonun ya da şarkının bağlantısını (https://… ile başlayan) kutuya yapıştırın.");
            return;
        }
        if (!await EnsureToolsAsync()) return;

        var f = (Option)FormatCombo.SelectedItem;
        var q = QualityCombo.SelectedItem as Option;
        var cookies = AppSettings.Current.MediaCookies;
        var opts = new DownloadOptions
        {
            Url = url,
            Audio = Audio,
            Format = f.Value,
            MaxHeight = Audio ? 0 : q?.Number ?? 0,
            AudioKbps = Audio ? q?.Number ?? 320 : 320,
            Playlist = PlaylistCheck.IsChecked == true,
            EmbedMeta = MetaCheck.IsChecked == true,
            H264 = H264Check.IsChecked == true && H264Check.Visibility == Visibility.Visible,
            Cookies = cookies,
            OutputDir = OutputDir,
        };
        var platform = MediaTools.Detect(url);
        string detail = $"{MediaTools.PlatformName(platform)} · {(Audio ? "Ses" : "Video")} · {f.Label}";
        if (Audio && q != null && QualityPanel.Visibility == Visibility.Visible) detail += " · " + q.Label;
        if (!Audio && q is { Number: > 0 }) detail += " · " + q.Label;
        if (opts.Playlist) detail += " · tüm liste";
        var job = new MediaJob { Glyph = Audio ? "" : "", OutputDir = OutputDir, Title = url, Detail = detail };
        _jobs.Insert(0, job);
        UrlBox.Text = "";
        await MediaService.RunDownloadAsync(job, opts);
    }

    // ------------------------------------------------------------------ Dönüştür

    private bool ToGif => ConvGif.IsChecked == true;

    private void Conv_Changed(object sender, RoutedEventArgs e)
    {
        if (!_init) return;
        GifPanel.Visibility = ToGif ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = ToGif ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ConvFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init) return;
        var tag = (ConvFormatCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        ConvBitratePanel.Visibility = tag is "flac" or "wav" ? Visibility.Hidden : Visibility.Visible;
    }

    private void PickFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Dönüştürülecek videoları seçin",
            Multiselect = true,
            Filter = "Video dosyaları|*.mp4;*.mkv;*.webm;*.mov;*.avi;*.wmv;*.flv;*.m4v;*.mpg;*.mpeg;*.ts;*.3gp|Tüm dosyalar|*.*",
            InitialDirectory = OutputDir,
        };
        if (dlg.ShowDialog() != true) return;
        _files = dlg.FileNames;
        FilesText.Text = _files.Length == 1 ? Path.GetFileName(_files[0]) : $"{_files.Length} dosya seçildi";
        FilesText.ToolTip = string.Join("\n", _files);
    }

    private static double? ParseSeconds(string s)
    {
        s = s.Trim().Replace(',', '.');
        if (s.Length == 0) return null;
        if (s.Contains(':'))
        {
            double total = 0;
            foreach (var part in s.Split(':'))
                total = total * 60 + (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0);
            return total > 0 ? total : null;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Length == 0)
        {
            Dialogs.Info("Video seçin", "Önce “Video seç” ile dönüştürmek istediğiniz dosyaları seçin.");
            return;
        }
        if (!await EnsureToolsAsync()) return;
        int fps = int.Parse((FpsCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "15");
        int width = int.Parse((WidthCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "480");
        var fmt = (ConvFormatCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "mp3";
        int kbps = int.Parse((ConvBitrateCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "320");
        var start = ParseSeconds(StartBox.Text);
        var length = ParseSeconds(LengthBox.Text);
        var tasks = new List<Task>();
        foreach (var file in _files)
        {
            var o = new ConvertOptions
            {
                Input = file, ToGif = ToGif, GifFps = fps, GifWidth = width, Start = ToGif ? start : null, Length = ToGif ? length : null,
                AudioFormat = fmt, AudioKbps = kbps, OutputDir = OutputDir,
            };
            string detail = ToGif
                ? $"GIF · {fps} fps · {(width > 0 ? width + " px" : "orijinal boyut")}" + (start != null ? $" · {start:0.#}. saniyeden" : "") + (length != null ? $" · {length:0.#} sn" : "")
                : $"Ses · {fmt.ToUpperInvariant()}" + (fmt is "flac" or "wav" ? "" : $" · {kbps} kbps");
            var job = new MediaJob { Glyph = ToGif ? "" : "", OutputDir = OutputDir, Title = Path.GetFileName(file), Detail = detail };
            _jobs.Insert(0, job);
            tasks.Add(MediaService.RunConvertAsync(job, o));
        }
        await Task.WhenAll(tasks);
    }

    // ------------------------------------------------------------------ İş listesi

    private static MediaJob? JobOf(object sender) => (sender as FrameworkElement)?.DataContext as MediaJob;

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { } j) return;
        if (j.Files.Count == 1) PathUtil.OpenUri(j.Files[0]);
        else PathUtil.OpenInExplorer(Path.GetDirectoryName(j.Files[0]) ?? j.OutputDir);
    }

    private void RevealJob_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { Files.Count: > 0 } j) PathUtil.OpenInExplorer(j.Files[0]);
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e) => JobOf(sender)?.Cts.Cancel();

    private void LogJob_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { } j) return;
        var kv = new System.Text.RegularExpressions.Regex(@"^[a-z_0-9]+=");
        var lines = j.Log.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.StartsWith("PROG:") && !kv.IsMatch(l)).ToList();
        Dialogs.Info("Ayrıntılı günlük", j.Status, lines.TakeLast(300).Concat(j.Files.Select(f => "Dosya: " + f)));
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var j in _jobs.Where(j => j.IsFinished).ToList()) _jobs.Remove(j);
    }
}
