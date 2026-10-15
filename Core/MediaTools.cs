using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace TemizlikPaneli.Core;

public enum MediaPlatform { YouTube, YouTubeMusic, Twitter, TikTok, Instagram, Other }

/// <summary>yt-dlp, gallery-dl ve ffmpeg araçlarının yönetimi (ilk kullanımda indirilir).</summary>
public static class MediaTools
{
    public static string Dir => Path.Combine(AppPaths.DataDir, "araclar");
    public static string YtDlp => Path.Combine(Dir, "yt-dlp.exe");
    public static string GalleryDl => Path.Combine(Dir, "gallery-dl.exe");
    public static string Ffmpeg => Path.Combine(Dir, "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(Dir, "ffprobe.exe");
    /// <summary>YouTube'un JavaScript doğrulamaları için yt-dlp'nin kullandığı çalışma zamanı.</summary>
    public static string Deno => Path.Combine(Dir, "deno.exe");
    private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string GalleryDlUrl = "https://github.com/gdl-org/builds/releases/latest/download/gallery-dl_windows.exe";
    private const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim EnsureGate = new(1);

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("PCTerminali/1.0");
        return h;
    }

    public static bool Ready => File.Exists(YtDlp) && File.Exists(Ffmpeg) && File.Exists(Ffprobe) && File.Exists(GalleryDl) && File.Exists(Deno);

    /// <summary>Eksik araçları indirir (yt-dlp ~18 MB, gallery-dl ~10 MB, ffmpeg ~140 MB).</summary>
    public static async Task EnsureAsync(IProgress<string> progress, CancellationToken ct)
    {
        await EnsureGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(YtDlp)) await DownloadFileAsync(YtDlpUrl, YtDlp, "yt-dlp", progress, ct);
            if (!File.Exists(GalleryDl)) await DownloadFileAsync(GalleryDlUrl, GalleryDl, "gallery-dl", progress, ct);
            if (!File.Exists(Ffmpeg) || !File.Exists(Ffprobe))
            {
                var zip = Path.Combine(Dir, "ffmpeg.zip");
                await DownloadFileAsync(FfmpegUrl, zip, "ffmpeg", progress, ct);
                progress.Report("ffmpeg arşivden çıkarılıyor…");
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(zip);
                    foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                    {
                        var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                                    ?? throw new InvalidDataException(name + " arşivde bulunamadı.");
                        entry.ExtractToFile(Path.Combine(Dir, name), overwrite: true);
                    }
                }, ct);
                File.Delete(zip);
            }
            if (!File.Exists(Deno))
            {
                var zip = Path.Combine(Dir, "deno.zip");
                await DownloadFileAsync(DenoUrl, zip, "deno (YouTube için)", progress, ct);
                progress.Report("deno arşivden çıkarılıyor…");
                await Task.Run(() =>
                {
                    using var archive = ZipFile.OpenRead(zip);
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException("deno.exe arşivde bulunamadı.");
                    entry.ExtractToFile(Deno, overwrite: true);
                }, ct);
                File.Delete(zip);
            }
            progress.Report("Araçlar hazır.");
        }
        finally { EnsureGate.Release(); }
    }

    /// <summary>gallery-dl'yi en son sürümle değiştirir (bağımsız derlemelerde kendini güncelleme yok).</summary>
    public static async Task UpdateGalleryDlAsync(IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        await DownloadFileAsync(GalleryDlUrl, GalleryDl, "gallery-dl", progress, ct);
    }

    private static async Task DownloadFileAsync(string url, string dest, string label, IProgress<string> progress, CancellationToken ct)
    {
        var part = dest + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(part);
            var buf = new byte[1 << 16];
            long done = 0, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (done - lastReport > (1 << 20))
                {
                    lastReport = done;
                    progress.Report(total is > 0
                        ? $"{label} indiriliyor… %{100.0 * done / total.Value:0} ({Fmt.Size(done)} / {Fmt.Size(total.Value)})"
                        : $"{label} indiriliyor… {Fmt.Size(done)}");
                }
            }
        }
        File.Move(part, dest, overwrite: true);
    }

    public static MediaPlatform Detect(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return MediaPlatform.Other;
        var h = u.Host.ToLowerInvariant();
        if (h.StartsWith("www.")) h = h[4..];
        if (h == "music.youtube.com") return MediaPlatform.YouTubeMusic;
        if (h is "youtube.com" or "m.youtube.com" or "youtu.be" or "youtube-nocookie.com") return MediaPlatform.YouTube;
        if (h is "twitter.com" or "x.com" or "mobile.twitter.com" or "mobile.x.com") return MediaPlatform.Twitter;
        if (h.EndsWith("tiktok.com")) return MediaPlatform.TikTok;
        if (h.EndsWith("instagram.com")) return MediaPlatform.Instagram;
        return MediaPlatform.Other;
    }

    public static string PlatformName(MediaPlatform p) => p switch
    {
        MediaPlatform.YouTube => "YouTube",
        MediaPlatform.YouTubeMusic => "YouTube Music",
        MediaPlatform.Twitter => "X (Twitter)",
        MediaPlatform.TikTok => "TikTok",
        MediaPlatform.Instagram => "Instagram",
        _ => "Diğer site",
    };

    /// <summary>Bir aracı çalıştırır; her çıktı satırını bildirir. İptalde süreç ağacını sonlandırır.</summary>
    public static async Task<int> RunAsync(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Dir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        using var p = new Process { StartInfo = psi };
        var gate = new object(); // stdout ve stderr aynı anda satır gönderebilir
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) onLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
            await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    public static async Task<string> YtDlpVersionAsync()
    {
        if (!File.Exists(YtDlp)) return "";
        var sb = new StringBuilder();
        try { await RunAsync(YtDlp, new[] { "--version" }, l => sb.Append(l.Trim()), CancellationToken.None); } catch { }
        return sb.ToString();
    }

    public static async Task<double> DurationSecondsAsync(string file, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await RunAsync(Ffprobe, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file }, l => sb.Append(l), ct);
        return double.TryParse(sb.ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>Çakışmayan bir çıktı dosyası adı üretir.</summary>
    public static string UniquePath(string dir, string name, string ext)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var path = Path.Combine(dir, name + ext);
        for (int i = 1; File.Exists(path); i++) path = Path.Combine(dir, $"{name} ({i}){ext}");
        return path;
    }

    /// <summary>yt-dlp / gallery-dl hata metnini anlaşılır Türkçeye çevirir.</summary>
    public static string FriendlyError(string raw, MediaPlatform platform)
    {
        var s = raw.ToLowerInvariant();
        if (s.Contains("cookie") && (s.Contains("decrypt") || s.Contains("copy") || s.Contains("dpapi") || s.Contains("could not find")))
            return "Tarayıcı çerezleri okunamadı. Tarayıcıyı tamamen kapatıp tekrar deneyin ya da Firefox veya cookies.txt dosyası seçin.";
        if (s.Contains("ip address is blocked") || s.Contains("not available in your country") || s.Contains("geo restrict") || s.Contains("geo-restrict"))
            return "Bu içerik bulunduğunuz bölgeden erişime kapalı (platform engelliyor). Başka bir içerik deneyin.";
        if (s.Contains("login") || s.Contains("log in") || s.Contains("sign in") || s.Contains("cookies") || s.Contains("rate-limit") || s.Contains("authentication"))
            return platform == MediaPlatform.Instagram
                ? "Instagram bu içerik için oturum istiyor. “Oturum” bölümünden Instagram'a giriş yaptığınız tarayıcıyı seçin (hikayeler için zorunludur)."
                : "Bu içerik için oturum açmak gerekiyor (yaş sınırı, gizli hesap vb.). “Oturum” bölümünden giriş yaptığınız tarayıcıyı seçin.";
        if (s.Contains("private")) return "İçerik gizli ya da erişiminiz yok.";
        if (s.Contains("unsupported url")) return "Bu bağlantı desteklenmiyor. Bağlantının doğru kopyalandığından emin olun.";
        if (s.Contains("requested format is not available")) return "Seçtiğiniz biçim ya da kalite bu içerik için yok. Başka bir biçim/kalite deneyin.";
        if (s.Contains("no video") || s.Contains("no media")) return "Bu gönderide indirilebilir video bulunamadı.";
        if (s.Contains("unable to download") || s.Contains("http error") || s.Contains("timed out") || s.Contains("getaddrinfo"))
            return "Bağlantı hatası. İnternet bağlantınızı kontrol edip tekrar deneyin.";
        if (s.Contains("unavailable") || s.Contains("removed") || s.Contains("not found") || s.Contains("404"))
            return "İçerik bulunamadı ya da kaldırılmış.";
        return raw.Replace("ERROR:", "").Trim();
    }
}

// ====================================================================== İşler

public enum JobState { Waiting, Running, Done, Failed, Cancelled }

public sealed class MediaJob : ObservableObject
{
    public required string Glyph { get; init; }
    public string OutputDir { get; init; } = "";
    public readonly List<string> Files = new();
    public readonly StringBuilder Log = new();
    public readonly CancellationTokenSource Cts = new();

    private string _title = "", _detail = "", _status = "Sırada";
    private double _progress;
    private bool _indeterminate = true;
    private JobState _state = JobState.Waiting;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }

    public JobState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(HasFile));
            OnPropertyChanged(nameof(StateGlyph));
            OnPropertyChanged(nameof(StateBrush));
        }
    }

    public bool IsActive => State is JobState.Waiting or JobState.Running;
    public bool IsFinished => !IsActive;
    public bool HasFile => State == JobState.Done && Files.Count > 0;
    public string StateGlyph => State switch { JobState.Done => "\uE73E", JobState.Failed => "\uEA39", JobState.Cancelled => "\uE711", _ => "" };
    public System.Windows.Media.Brush StateBrush => ThemeManager.Brush(State switch
    {
        JobState.Done => "GoodBrush",
        JobState.Failed => "CriticalBrush",
        _ => "TextMuted",
    });

    public void AddFile(string path)
    {
        if (!Files.Contains(path, PathUtil.Cmp)) Files.Add(path);
    }
}

public sealed class DownloadOptions
{
    public required string Url { get; init; }
    public bool Audio { get; init; }
    /// <summary>Video: mp4/mkv/webm/mov/avi · Ses: opus-orig/m4a-orig/mp3/flac/wav/ogg/aac</summary>
    public required string Format { get; init; }
    public int MaxHeight { get; init; }
    public int AudioKbps { get; init; } = 320;
    public bool Playlist { get; init; }
    public bool EmbedMeta { get; init; } = true;
    public bool H264 { get; init; }
    /// <summary>null, "edge", "brave", "chrome", "firefox" ya da "file:C:\...\cookies.txt"</summary>
    public string? Cookies { get; init; }
    public required string OutputDir { get; init; }
}

public sealed class ConvertOptions
{
    public required string Input { get; init; }
    public bool ToGif { get; init; }
    public string AudioFormat { get; init; } = "mp3";
    public int AudioKbps { get; init; } = 320;
    public int GifFps { get; init; } = 15;
    public int GifWidth { get; init; } = 480;
    public double? Start { get; init; }
    public double? Length { get; init; }
    public required string OutputDir { get; init; }
}

public static class MediaService
{
    private static readonly SemaphoreSlim Slots = new(2);
    private static readonly Regex ProgRx = new(@"PROG:\s*([\d.]+)%\|([^|]*)\|(.*)", RegexOptions.Compiled);

    // ------------------------------------------------------------------ yt-dlp

    public static List<string> BuildYtDlpArgs(DownloadOptions o, string pathsFile)
    {
        bool convertContainer = !o.Audio && o.Format is "mov" or "avi";
        var a = new List<string>
        {
            "--newline", "--progress", "--no-mtime", "--windows-filenames", "--no-part", "--encoding", "utf-8",
            "--ffmpeg-location", MediaTools.Dir,
            "--js-runtimes", "deno:" + MediaTools.Deno,
            "-P", o.OutputDir,
            "--print", "before_dl:TITLE:%(title)s",
            // Dosya yolları konsol kodlamasından etkilenmesin diye UTF-8 dosyaya yazılır
            "--print-to-file", "after_move:%(filepath)s", pathsFile,
            "--progress-template", "download:PROG:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            o.Playlist ? "--yes-playlist" : "--no-playlist",
            // MOV/AVI farklı adla kaydedilir: aynı videonun daha önce indirilmiş MP4'üne asla dokunulmaz
            "-o", (o.Playlist ? "%(playlist_title,uploader|İndirilenler).80B/%(playlist_index|0)03d - %(title).150B" : "%(title).150B [%(id)s]")
                  + (convertContainer ? $" ({o.Format})" : "") + ".%(ext)s",
        };
        string res = o.MaxHeight > 0 ? $"res:{o.MaxHeight}" : "res";

        if (o.Audio)
        {
            switch (o.Format)
            {
                case "opus-orig": a.AddRange(new[] { "-f", "bestaudio[acodec=opus]/bestaudio", "-x", "--audio-format", "opus" }); break;
                case "m4a-orig": a.AddRange(new[] { "-f", "bestaudio[ext=m4a]/bestaudio", "-x", "--audio-format", "m4a" }); break;
                case "ogg": a.AddRange(new[] { "-f", "bestaudio", "-x", "--audio-format", "vorbis", "--audio-quality", $"{o.AudioKbps}K" }); break;
                case "flac" or "wav": a.AddRange(new[] { "-f", "bestaudio", "-x", "--audio-format", o.Format }); break;
                default: a.AddRange(new[] { "-f", "bestaudio", "-x", "--audio-format", o.Format, "--audio-quality", $"{o.AudioKbps}K" }); break;
            }
            if (o.EmbedMeta)
            {
                a.Add("--embed-metadata");
                if (o.Format is not ("wav" or "aac")) a.AddRange(new[] { "--embed-thumbnail", "--convert-thumbnails", "jpg" });
            }
        }
        else
        {
            switch (o.Format)
            {
                case "mkv": a.AddRange(new[] { "-S", res, "--merge-output-format", "mkv" }); break;
                case "webm": a.AddRange(new[] { "-S", res + ",ext:webm:webm", "--merge-output-format", "webm" }); break;
                case "mov" or "avi":
                    // H.264 + AAC her iki kapsayıcıya da sorunsuz girer; birleştirme doğrudan hedef biçimde yapılır
                    a.AddRange(new[] { "-S", $"vcodec:h264,{res},acodec:m4a", "--merge-output-format", o.Format, "--remux-video", o.Format });
                    break;
                default:
                    a.AddRange(new[] { "-S", o.H264 ? $"vcodec:h264,{res},acodec:m4a" : res + ",ext:mp4:m4a", "--merge-output-format", "mp4" });
                    break;
            }
            if (o.EmbedMeta)
            {
                a.Add("--embed-metadata");
                if (o.Format is "mp4" or "mkv" or "mov") a.Add("--embed-thumbnail");
            }
        }
        AddCookies(a, o.Cookies, "--cookies-from-browser", "--cookies");
        a.Add("--");
        a.Add(o.Url);
        return a;
    }

    private static void AddCookies(List<string> a, string? cookies, string browserFlag, string fileFlag)
    {
        if (string.IsNullOrEmpty(cookies)) return;
        if (cookies.StartsWith("file:")) { a.Add(fileFlag); a.Add(cookies[5..]); }
        else { a.Add(browserFlag); a.Add(cookies); }
    }

    public static async Task RunDownloadAsync(MediaJob job, DownloadOptions o)
    {
        var platform = MediaTools.Detect(o.Url);
        var ct = job.Cts.Token;
        string? error = null;
        try
        {
            await Slots.WaitAsync(ct);
            try
            {
                job.State = JobState.Running;
                job.Status = "Bağlanılıyor…";
                Directory.CreateDirectory(o.OutputDir);
                var pathsFile = Path.Combine(MediaTools.Dir, $"yollar-{Guid.NewGuid():N}.txt");
                int code;
                try
                {
                    code = await MediaTools.RunAsync(MediaTools.YtDlp, BuildYtDlpArgs(o, pathsFile), line => OnYtDlpLine(job, line, ref error), ct);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(pathsFile))
                        {
                            foreach (var p in File.ReadAllLines(pathsFile, Encoding.UTF8))
                                if (p.Trim().Length > 0 && File.Exists(p.Trim())) job.AddFile(p.Trim());
                            File.Delete(pathsFile);
                        }
                    }
                    catch { }
                }

                // Instagram fotoğraf gönderileri için gallery-dl ile yeniden dene
                if (job.Files.Count == 0 && platform == MediaPlatform.Instagram && !o.Audio)
                {
                    job.Status = "Fotoğraf gönderisi deneniyor…";
                    job.Indeterminate = true;
                    var ga = new List<string> { "--directory", o.OutputDir, "--no-mtime" };
                    AddCookies(ga, o.Cookies, "--cookies-from-browser", "--cookies");
                    ga.Add(o.Url);
                    string? gerr = null;
                    // Yeni dosyalar klasörün önceki ve sonraki hali karşılaştırılarak bulunur (konsol kodlamasından bağımsız)
                    var before = new HashSet<string>(Directory.EnumerateFiles(o.OutputDir), PathUtil.Cmp);
                    code = await MediaTools.RunAsync(MediaTools.GalleryDl, ga, line =>
                    {
                        job.Log.AppendLine(line);
                        if (line.Contains("[error]", StringComparison.OrdinalIgnoreCase)) gerr = line;
                    }, ct);
                    foreach (var f in Directory.EnumerateFiles(o.OutputDir).Where(f => !before.Contains(f) && !f.EndsWith(".part")))
                        job.AddFile(f);
                    if (job.Files.Count > 0) { error = null; if (job.Title.Length == 0) job.Title = "Instagram gönderisi"; }
                    else if (gerr != null && error == null) error = gerr;
                }

                if (job.Files.Count > 0)
                {
                    job.Progress = 100;
                    job.Indeterminate = false;
                    job.State = JobState.Done;
                    long size = job.Files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
                    job.Status = job.Files.Count == 1
                        ? $"Tamamlandı · {Fmt.Size(size)} · {Path.GetFileName(job.Files[0])}"
                        : $"Tamamlandı · {job.Files.Count} dosya · {Fmt.Size(size)}";
                    ActivityLog.Write("Medya indirildi: " + MediaTools.PlatformName(platform), job.Title, 0, job.Files.Count, 0, o.Audio ? "Ses " + o.Format : "Video " + o.Format);
                }
                else
                {
                    job.State = JobState.Failed;
                    job.Indeterminate = false;
                    job.Status = MediaTools.FriendlyError(error ?? "İndirme başarısız oldu.", platform);
                }
            }
            finally { Slots.Release(); }
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Cancelled;
            job.Indeterminate = false;
            job.Status = "İptal edildi";
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.Indeterminate = false;
            job.Status = ex.Message;
        }
    }

    private static void OnYtDlpLine(MediaJob job, string line, ref string? error)
    {
        job.Log.AppendLine(line);
        if (line.StartsWith("TITLE:")) { job.Title = line[6..].Trim(); job.Status = "İndiriliyor…"; return; }
        if (line.StartsWith("FILE:")) { job.AddFile(line[5..].Trim()); return; }
        if (line.StartsWith("ERROR:")) { error ??= line; return; }
        var m = ProgRx.Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            job.Indeterminate = false;
            job.Progress = pct;
            var speed = m.Groups[2].Value.Trim();
            var eta = m.Groups[3].Value.Trim();
            job.Status = pct >= 99.9
                ? "Dönüştürülüyor ve birleştiriliyor…"
                : $"İndiriliyor %{pct.ToString("0.0", CultureInfo.GetCultureInfo("tr-TR"))}"
                  + (speed.Length > 0 && !speed.Contains("Unknown") ? $" · {speed}" : "")
                  + (eta.Length > 0 && !eta.Contains("Unknown") ? $" · kalan {eta}" : "");
        }
    }

    // ------------------------------------------------------------------ ffmpeg dönüştürme

    private static readonly Dictionary<string, (string Ext, string[] Codec)> AudioCodecs = new()
    {
        ["mp3"] = (".mp3", new[] { "-c:a", "libmp3lame", "-b:a", "{k}k" }),
        ["m4a"] = (".m4a", new[] { "-c:a", "aac", "-b:a", "{k}k" }),
        ["aac"] = (".aac", new[] { "-c:a", "aac", "-b:a", "{k}k" }),
        ["ogg"] = (".ogg", new[] { "-c:a", "libvorbis", "-b:a", "{k}k" }),
        ["opus"] = (".opus", new[] { "-c:a", "libopus", "-b:a", "{k}k" }),
        ["flac"] = (".flac", new[] { "-c:a", "flac" }),
        ["wav"] = (".wav", new[] { "-c:a", "pcm_s16le" }),
    };

    public static async Task RunConvertAsync(MediaJob job, ConvertOptions o)
    {
        var ct = job.Cts.Token;
        try
        {
            await Slots.WaitAsync(ct);
            try
            {
                job.State = JobState.Running;
                job.Status = "Hazırlanıyor…";
                Directory.CreateDirectory(o.OutputDir);
                double total = await MediaTools.DurationSecondsAsync(o.Input, ct);
                if (o.Start is > 0) total = Math.Max(0, total - o.Start.Value);
                if (o.Length is > 0) total = Math.Min(total > 0 ? total : o.Length.Value, o.Length.Value);

                var name = Path.GetFileNameWithoutExtension(o.Input);
                var args = new List<string> { "-y", "-hide_banner", "-nostats", "-progress", "pipe:1" };
                if (o.Start is > 0) { args.Add("-ss"); args.Add(o.Start.Value.ToString(CultureInfo.InvariantCulture)); }
                if (o.Length is > 0) { args.Add("-t"); args.Add(o.Length.Value.ToString(CultureInfo.InvariantCulture)); }
                args.Add("-i"); args.Add(o.Input);
                string output;
                if (o.ToGif)
                {
                    output = MediaTools.UniquePath(o.OutputDir, name, ".gif");
                    // Küçük videolar büyütülmez (dosya boyutu boşuna artmasın)
                    var scale = o.GifWidth > 0 ? $",scale=w='min({o.GifWidth},iw)':h=-1:flags=lanczos" : "";
                    args.AddRange(new[] { "-vf", $"fps={o.GifFps}{scale},split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5", "-loop", "0", output });
                }
                else
                {
                    var (ext, codec) = AudioCodecs.TryGetValue(o.AudioFormat, out var c) ? c : AudioCodecs["mp3"];
                    output = MediaTools.UniquePath(o.OutputDir, name, ext);
                    args.AddRange(new[] { "-vn", "-map_metadata", "0" });
                    args.AddRange(codec.Select(x => x.Replace("{k}", o.AudioKbps.ToString())));
                    args.Add(output);
                }

                var err = new StringBuilder();
                int code = await MediaTools.RunAsync(MediaTools.Ffmpeg, args, line =>
                {
                    job.Log.AppendLine(line);
                    if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && total > 0)
                    {
                        double pct = Math.Clamp(us / 1e6 / total * 100, 0, 100);
                        job.Indeterminate = false;
                        job.Progress = pct;
                        job.Status = $"Dönüştürülüyor %{pct:0}";
                    }
                    else if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) || line.Contains("Invalid", StringComparison.Ordinal)) err.AppendLine(line);
                }, ct);

                if (code == 0 && File.Exists(output))
                {
                    job.AddFile(output);
                    job.Progress = 100;
                    job.Indeterminate = false;
                    job.State = JobState.Done;
                    job.Status = $"Tamamlandı · {Fmt.Size(new FileInfo(output).Length)} · {Path.GetFileName(output)}";
                    ActivityLog.Write(o.ToGif ? "Video GIF'e çevrildi" : "Video sese çevrildi", Path.GetFileName(o.Input), 0, 1, 0, o.ToGif ? "GIF" : o.AudioFormat.ToUpperInvariant());
                }
                else
                {
                    job.State = JobState.Failed;
                    job.Indeterminate = false;
                    var e = err.ToString().Trim();
                    job.Status = e.Contains("does not contain any stream") || e.Contains("matches no streams")
                        ? "Bu dosyada ses bulunmuyor."
                        : "Dönüştürme başarısız: " + (e.Length > 0 ? e.Split('\n').Last().Trim() : "dosya okunamadı");
                    try { if (File.Exists(output)) File.Delete(output); } catch { }
                }
            }
            finally { Slots.Release(); }
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Cancelled;
            job.Indeterminate = false;
            job.Status = "İptal edildi";
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.Indeterminate = false;
            job.Status = ex.Message;
        }
    }
}
