using System.Diagnostics;
using System.IO.Enumeration;

namespace TemizlikPaneli.Core;

public enum JunkKind { Folders, RecycleBin, DeliveryOptimization }

public sealed class JunkTarget
{
    public required string Path { get; init; }
    /// <summary>Dosya deseni; ';' ile birden fazla verilebilir. null = her şey.</summary>
    public string? Pattern { get; init; }
    /// <summary>Path altındaki bu desene uyan klasörlerin her biri ayrı kök olarak temizlenir.</summary>
    public string? DirPattern { get; init; }
    public bool Recursive { get; init; } = true;
    public int MinAgeDays { get; init; }
    public bool AdminOnly { get; init; }
}

public sealed class JunkCategory : ObservableObject
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool DefaultOn { get; init; } = true;
    public bool UsesRecycleBin { get; init; }
    public JunkKind Kind { get; init; } = JunkKind.Folders;
    public List<JunkTarget> Targets { get; init; } = new();
    public string[] ProcessNames { get; init; } = Array.Empty<string>();

    private long _size, _files;
    private bool _isSelected, _isScanning, _scanned;
    private string _warning = "";
    public readonly List<string> MatchedFiles = new();

    public long Size { get => _size; set { if (Set(ref _size, value)) { OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(IsEmpty)); } } }
    public long Files { get => _files; set { if (Set(ref _files, value)) OnPropertyChanged(nameof(FilesText)); } }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsScanning { get => _isScanning; set { if (Set(ref _isScanning, value)) OnPropertyChanged(nameof(SizeText)); } }
    public bool Scanned { get => _scanned; set { if (Set(ref _scanned, value)) { OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(IsEmpty)); } } }
    public string Warning { get => _warning; set { if (Set(ref _warning, value)) OnPropertyChanged(nameof(HasWarning)); } }

    public bool HasWarning => Warning.Length > 0;
    public bool Blocked => NeedsAdmin && !Admin.IsElevated;
    public bool CanSelect => !Blocked;
    public bool IsEmpty => Scanned && !Blocked && Size == 0;
    public string SizeText => IsScanning ? "Taranıyor…" : Blocked ? (Scanned && Size > 0 ? Fmt.Size(Size) : "Yönetici gerekli") : Scanned ? Fmt.Size(Size) : "—";
    public string FilesText => Kind == JunkKind.RecycleBin ? $"{Fmt.Count(Files)} öğe" : $"{Fmt.Count(Files)} dosya";
}

public static class JunkCatalog
{
    public static List<JunkCategory> Build(GameScanResult? games)
    {
        string L = KnownFolders.Local, R = KnownFolders.Roaming, LL = KnownFolders.LocalLow, PD = KnownFolders.ProgramData;
        string W = KnownFolders.Windows, SD = KnownFolders.SystemDrive, UP = KnownFolders.UserProfile;
        int tempAge = Math.Max(0, AppSettings.Current.TempMinAgeDays);
        var list = new List<JunkCategory>();

        JunkTarget T(string path, string? pattern = null, bool recursive = true, int age = 0, bool admin = false, string? dirPattern = null) =>
            new() { Path = path, Pattern = pattern, Recursive = recursive, MinAgeDays = age, AdminOnly = admin, DirPattern = dirPattern };

        // ---------------- Windows
        list.Add(new JunkCategory
        {
            Id = "user-temp", Group = "Windows", Name = "Kullanıcı geçici dosyaları",
            Description = $"Programların bıraktığı geçici dosyalar (%TEMP%). Son {Math.Max(1, tempAge)} günde kullanılanlara dokunulmaz.",
            Targets = { T(KnownFolders.Temp, age: tempAge) },
        });
        list.Add(new JunkCategory
        {
            Id = "win-temp", Group = "Windows", Name = "Windows geçici dosyaları", NeedsAdmin = true,
            Description = "Windows ve hizmetlerin geçici dosyaları (C:\\Windows\\Temp).",
            Targets = { T(Path.Combine(W, "Temp"), age: tempAge, admin: true) },
        });
        list.Add(new JunkCategory
        {
            Id = "wu-download", Group = "Windows", Name = "Windows Update indirme önbelleği", NeedsAdmin = true,
            Description = "Kurulmuş güncellemelerin indirilen dosyaları. Son 3 günün dosyalarına dokunulmaz.",
            Targets = { T(Path.Combine(W, "SoftwareDistribution", "Download"), age: 3, admin: true) },
        });
        list.Add(new JunkCategory
        {
            Id = "delivery-opt", Group = "Windows", Name = "Teslim İyileştirme önbelleği", NeedsAdmin = true, Kind = JunkKind.DeliveryOptimization,
            Description = "Windows Update'in diğer bilgisayarlarla paylaşmak için sakladığı güncelleme parçaları.",
            Targets = { T(Path.Combine(W, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"), admin: true) },
        });
        list.Add(new JunkCategory
        {
            Id = "wer", Group = "Windows", Name = "Hata raporları",
            Description = "Çöken programlar için oluşturulan Windows Hata Raporlama dosyaları.",
            Targets =
            {
                T(Path.Combine(L, "Microsoft", "Windows", "WER", "ReportArchive")),
                T(Path.Combine(L, "Microsoft", "Windows", "WER", "ReportQueue")),
                T(Path.Combine(L, "Microsoft", "Windows", "WER", "Temp")),
                T(Path.Combine(PD, "Microsoft", "Windows", "WER", "ReportArchive"), admin: true),
                T(Path.Combine(PD, "Microsoft", "Windows", "WER", "ReportQueue"), admin: true),
                T(Path.Combine(PD, "Microsoft", "Windows", "WER", "Temp"), admin: true),
            },
        });
        list.Add(new JunkCategory
        {
            Id = "dumps", Group = "Windows", Name = "Bellek döküm dosyaları",
            Description = "Çökme anında yazılan döküm dosyaları. Yalnızca hata ayıklama için gerekir.",
            Targets =
            {
                T(Path.Combine(L, "CrashDumps")),
                T(Path.Combine(W, "Minidump"), admin: true),
                T(W, "MEMORY.DMP", recursive: false, admin: true),
                T(Path.Combine(W, "LiveKernelReports"), "*.dmp", admin: true),
            },
        });
        list.Add(new JunkCategory
        {
            Id = "thumbs", Group = "Windows", Name = "Küçük resim önbelleği",
            Description = "Gezgin'in resim önizlemeleri. Gerektikçe yeniden oluşturulur; Gezgin açıkken bazıları silinemez.",
            Targets = { T(Path.Combine(L, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", recursive: false) },
        });
        list.Add(new JunkCategory
        {
            Id = "inetcache", Group = "Windows", Name = "İnternet geçici dosyaları",
            Description = "Windows bileşenlerinin ve eski uygulamaların web önbelleği (INetCache).",
            Targets = { T(Path.Combine(L, "Microsoft", "Windows", "INetCache")) },
        });
        list.Add(new JunkCategory
        {
            Id = "d3d", Group = "Windows", Name = "DirectX gölgelendirici önbelleği",
            Description = "Oyun ve uygulamaların derlenmiş gölgelendiricileri. Gerektikçe yeniden oluşturulur.",
            Targets = { T(Path.Combine(L, "D3DSCache")) },
        });
        list.Add(new JunkCategory
        {
            Id = "win-logs", Group = "Windows", Name = "Windows günlük dosyaları", NeedsAdmin = true,
            Description = "Bileşen ve DISM kurulum günlükleri. Son 7 günün günlüklerine dokunulmaz.",
            Targets =
            {
                T(Path.Combine(W, "Logs", "CBS"), "*.log;*.cab", age: 7, admin: true),
                T(Path.Combine(W, "Logs", "DISM"), "*.log", age: 7, admin: true),
            },
        });
        list.Add(new JunkCategory
        {
            Id = "recycle", Group = "Windows", Name = "Geri Dönüşüm Kutusu", Kind = JunkKind.RecycleBin, DefaultOn = false,
            Description = "Sildiğiniz dosyalar. Boşaltınca geri getirilemez.",
        });

        // ---------------- Ekran kartı
        list.Add(new JunkCategory
        {
            Id = "gpu-amd", Group = "Ekran kartı", Name = "AMD gölgelendirici önbelleği",
            Description = "AMD sürücüsünün derlediği gölgelendiriciler. Oyunlar ilk açılışta yeniden oluşturur.",
            Targets = { T(Path.Combine(L, "AMD", "DxCache")), T(Path.Combine(L, "AMD", "DxcCache")), T(Path.Combine(L, "AMD", "VkCache")),
                        T(Path.Combine(L, "AMD", "GLCache")), T(Path.Combine(L, "AMD", "OglCache")), T(Path.Combine(L, "AMD", "Dx9Cache")) },
            ProcessNames = new[] { "RadeonSoftware" },
        });
        list.Add(new JunkCategory
        {
            Id = "gpu-nvidia", Group = "Ekran kartı", Name = "NVIDIA gölgelendirici önbelleği",
            Description = "NVIDIA sürücüsünün derlediği gölgelendiriciler. Oyunlar ilk açılışta yeniden oluşturur.",
            Targets = { T(Path.Combine(L, "NVIDIA", "DXCache")), T(Path.Combine(L, "NVIDIA", "GLCache")),
                        T(Path.Combine(LL, "NVIDIA", "PerDriverVersion", "DXCache")), T(Path.Combine(LL, "NVIDIA", "PerDriverVersion", "GLCache")),
                        T(Path.Combine(PD, "NVIDIA Corporation", "NV_Cache"), admin: true) },
        });
        list.Add(new JunkCategory
        {
            Id = "gpu-intel", Group = "Ekran kartı", Name = "Intel gölgelendirici önbelleği",
            Description = "Intel grafik sürücüsünün gölgelendirici önbelleği.",
            Targets = { T(Path.Combine(LL, "Intel", "ShaderCache")) },
        });
        list.Add(new JunkCategory
        {
            Id = "driver-leftovers", Group = "Ekran kartı", Name = "Sürücü kurulum artıkları", NeedsAdmin = true,
            Description = "Sürücü kurulumundan sonra diskte kalan çıkarılmış kurulum paketleri (C:\\AMD, C:\\NVIDIA).",
            Targets = { T(Path.Combine(SD, "AMD"), admin: true), T(Path.Combine(SD, "NVIDIA"), admin: true),
                        T(Path.Combine(KnownFolders.ProgramFiles, "NVIDIA Corporation", "Installer2"), admin: true) },
        });

        // ---------------- Tarayıcılar (yalnızca önbellek: çerez, şifre, geçmiş silinmez)
        AddChromium(list, "edge", "Microsoft Edge", Path.Combine(L, "Microsoft", "Edge", "User Data"), "msedge");
        AddChromium(list, "chrome", "Google Chrome", Path.Combine(L, "Google", "Chrome", "User Data"), "chrome");
        AddChromium(list, "brave", "Brave", Path.Combine(L, "BraveSoftware", "Brave-Browser", "User Data"), "brave");
        AddChromium(list, "vivaldi", "Vivaldi", Path.Combine(L, "Vivaldi", "User Data"), "vivaldi");
        AddChromium(list, "yandex", "Yandex Browser", Path.Combine(L, "Yandex", "YandexBrowser", "User Data"), "browser");
        AddOpera(list, "opera", "Opera", "Opera Stable");
        AddOpera(list, "operagx", "Opera GX", "Opera GX Stable");
        var ffProfiles = Path.Combine(L, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(ffProfiles))
            list.Add(new JunkCategory
            {
                Id = "browser-firefox", Group = "Tarayıcılar", Name = "Mozilla Firefox önbelleği",
                Description = "Sayfa önbelleği. Çerezler, şifreler ve geçmiş silinmez.",
                Targets = { T(ffProfiles, dirPattern: "*", pattern: null) },
                ProcessNames = new[] { "firefox" },
            });

        // ---------------- Uygulamalar
        list.Add(new JunkCategory
        {
            Id = "discord", Group = "Uygulamalar", Name = "Discord önbelleği",
            Description = "Görsel ve kod önbelleği. Oturumunuz kapanmaz.",
            Targets = new[] { "discord", "discordptb", "discordcanary" }
                .SelectMany(d => new[] { "Cache", "Code Cache", "GPUCache" }.Select(s => T(Path.Combine(R, d, s)))).ToList(),
            ProcessNames = new[] { "Discord", "DiscordPTB", "DiscordCanary" },
        });
        list.Add(new JunkCategory
        {
            Id = "spotify", Group = "Uygulamalar", Name = "Spotify önbelleği",
            Description = "Çevrimdışı olmayan şarkıların geçici önbelleği.",
            Targets = { T(Path.Combine(L, "Spotify", "Data")), T(Path.Combine(L, "Packages", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0", "LocalCache", "Spotify", "Data")) },
            ProcessNames = new[] { "Spotify" },
        });
        list.Add(new JunkCategory
        {
            Id = "teams", Group = "Uygulamalar", Name = "Microsoft Teams önbelleği",
            Description = "Teams'in web önbelleği. Oturumunuz kapanmaz.",
            Targets = { T(Path.Combine(R, "Microsoft", "Teams", "Cache")), T(Path.Combine(R, "Microsoft", "Teams", "Code Cache")), T(Path.Combine(R, "Microsoft", "Teams", "GPUCache")),
                        T(Path.Combine(L, "Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "EBWebView", "Default", "Cache")),
                        T(Path.Combine(L, "Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache", "Microsoft", "MSTeams", "EBWebView", "Default", "Code Cache")) },
            ProcessNames = new[] { "ms-teams", "Teams" },
        });
        list.Add(new JunkCategory
        {
            Id = "vscode", Group = "Uygulamalar", Name = "Visual Studio Code önbelleği",
            Description = "Önbellekler, indirilen eklenti paketleri ve eski günlükler. Ayarlar ve eklentiler silinmez.",
            Targets = { T(Path.Combine(R, "Code", "Cache")), T(Path.Combine(R, "Code", "CachedData"), age: 1), T(Path.Combine(R, "Code", "Code Cache")),
                        T(Path.Combine(R, "Code", "GPUCache")), T(Path.Combine(R, "Code", "CachedExtensionVSIXs")), T(Path.Combine(R, "Code", "logs"), age: 1) },
            ProcessNames = new[] { "Code" },
        });
        list.Add(new JunkCategory
        {
            Id = "epic-cache", Group = "Uygulamalar", Name = "Epic Games Launcher önbelleği",
            Description = "Mağaza sayfalarının web önbelleği ve günlükler.",
            Targets = { T(Path.Combine(L, "EpicGamesLauncher", "Saved"), dirPattern: "webcache*"), T(Path.Combine(L, "EpicGamesLauncher", "Saved", "Logs"), age: 1) },
            ProcessNames = new[] { "EpicGamesLauncher" },
        });
        list.Add(new JunkCategory
        {
            Id = "riot-logs", Group = "Uygulamalar", Name = "Riot Client günlükleri",
            Description = "Riot Client'ın eski günlük dosyaları.",
            Targets = { T(Path.Combine(L, "Riot Games", "Riot Client", "Logs"), age: 1) },
        });
        list.Add(new JunkCategory
        {
            Id = "adobe", Group = "Uygulamalar", Name = "Adobe medya önbelleği",
            Description = "Premiere/After Effects medya önbellek dosyaları. Gerektiğinde yeniden oluşturulur.",
            Targets = { T(Path.Combine(R, "Adobe", "Common", "Media Cache Files")), T(Path.Combine(R, "Adobe", "Common", "Media Cache")) },
        });
        list.Add(new JunkCategory
        {
            Id = "java", Group = "Uygulamalar", Name = "Java önbelleği",
            Description = "Java'nın indirdiği uygulama önbelleği.",
            Targets = { T(Path.Combine(LL, "Sun", "Java", "Deployment", "cache")) },
        });

        // ---------------- Oyun platformları
        var steamRoot = games?.SteamRoot;
        if (steamRoot != null)
        {
            list.Add(new JunkCategory
            {
                Id = "steam-web", Group = "Oyun platformları", Name = "Steam web önbelleği ve günlükleri",
                Description = "Steam arayüzünün web önbelleği, hata dökümleri ve eski günlükleri.",
                Targets = { T(Path.Combine(L, "Steam", "htmlcache")), T(Path.Combine(steamRoot, "appcache", "httpcache")),
                            T(Path.Combine(steamRoot, "logs"), age: 1), T(Path.Combine(steamRoot, "dumps")) },
                ProcessNames = new[] { "steam", "steamwebhelper" },
            });
            var libs = games!.SteamLibraries;
            list.Add(new JunkCategory
            {
                Id = "steam-downloads", Group = "Oyun platformları", Name = "Steam yarım kalmış indirmeler", DefaultOn = false,
                Description = "Tamamlanmamış indirme ve güncelleme dosyaları. Duraklatılmış bir indirme varsa baştan başlar.",
                Targets = libs.SelectMany(l => new[] { T(Path.Combine(l, "steamapps", "downloading")), T(Path.Combine(l, "steamapps", "temp")) }).ToList(),
                ProcessNames = new[] { "steam" },
            });
            list.Add(new JunkCategory
            {
                Id = "steam-shader", Group = "Oyun platformları", Name = "Steam gölgelendirici önbelleği", DefaultOn = false,
                Description = "Oyunların önceden derlenmiş gölgelendiricileri. Silinirse oyunlar ilk açılışta kısa süre takılabilir.",
                Targets = libs.Select(l => T(Path.Combine(l, "steamapps", "shadercache"))).ToList(),
                ProcessNames = new[] { "steam" },
            });
        }

        // ---------------- Geliştirici
        list.Add(new JunkCategory
        {
            Id = "dev", Group = "Geliştirici araçları", Name = "Paket yöneticisi önbellekleri", DefaultOn = false,
            Description = "npm, pip, NuGet, Yarn, Gradle, Go ve Cargo indirme önbellekleri. Gerektiğinde yeniden indirilir.",
            Targets = { T(Path.Combine(L, "npm-cache")), T(Path.Combine(R, "npm-cache")), T(Path.Combine(L, "pip", "Cache")),
                        T(Path.Combine(L, "NuGet", "v3-cache")), T(Path.Combine(L, "NuGet", "plugins-cache")), T(Path.Combine(L, "Yarn", "Cache")),
                        T(Path.Combine(UP, ".gradle", "caches")), T(Path.Combine(L, "go-build")), T(Path.Combine(UP, ".cargo", "registry", "cache")) },
        });

        // ---------------- Kullanıcı dosyaları (Geri Dönüşüm Kutusu'na taşınır)
        list.Add(new JunkCategory
        {
            Id = "downloads-installers", Group = "Kullanıcı dosyaları", Name = "İndirilenler: eski kurulum dosyaları", DefaultOn = false, UsesRecycleBin = true,
            Description = "İndirilenler klasöründe 30 günden eski .exe/.msi kurulum dosyaları. Geri Dönüşüm Kutusu'na taşınır.",
            Targets = { T(KnownFolders.Downloads, "*.exe;*.msi;*.msix;*.msixbundle;*.appx", recursive: false, age: 30) },
        });

        // Hiçbir hedefi diskte olmayan kategorileri gösterme
        list.RemoveAll(c => c.Kind == JunkKind.Folders && !c.Targets.Any(t => Directory.Exists(t.Path)));
        foreach (var c in list) c.IsSelected = c.DefaultOn && !c.Blocked;
        return list;
    }

    private static readonly string[] ChromiumCacheDirs = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" };

    private static void AddChromium(List<JunkCategory> list, string id, string name, string userData, string process)
    {
        if (!Directory.Exists(userData)) return;
        var targets = new List<JunkTarget>();
        try
        {
            foreach (var profile in Directory.EnumerateDirectories(userData))
            {
                if (!File.Exists(Path.Combine(profile, "Preferences"))) continue;
                foreach (var d in ChromiumCacheDirs) targets.Add(new JunkTarget { Path = Path.Combine(profile, d) });
            }
        }
        catch { }
        foreach (var d in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
            targets.Add(new JunkTarget { Path = Path.Combine(userData, d) });
        list.Add(new JunkCategory
        {
            Id = "browser-" + id, Group = "Tarayıcılar", Name = name + " önbelleği",
            Description = "Sayfa, kod ve GPU önbelleği. Çerezler, şifreler, sekmeler ve geçmiş silinmez.",
            Targets = targets,
            ProcessNames = new[] { process },
        });
    }

    private static void AddOpera(List<JunkCategory> list, string id, string name, string folder)
    {
        var local = Path.Combine(KnownFolders.Local, "Opera Software", folder);
        var roaming = Path.Combine(KnownFolders.Roaming, "Opera Software", folder);
        if (!Directory.Exists(local) && !Directory.Exists(roaming)) return;
        var targets = new List<JunkTarget>();
        foreach (var root in new[] { local, roaming })
            foreach (var d in ChromiumCacheDirs.Concat(new[] { "ShaderCache", "GrShaderCache" }))
                targets.Add(new JunkTarget { Path = Path.Combine(root, d) });
        list.Add(new JunkCategory
        {
            Id = "browser-" + id, Group = "Tarayıcılar", Name = name + " önbelleği",
            Description = "Sayfa, kod ve GPU önbelleği. Çerezler, şifreler, sekmeler ve geçmiş silinmez.",
            Targets = targets,
            ProcessNames = new[] { "opera" },
        });
    }

    // ------------------------------------------------------------------ Tarama

    private static IEnumerable<string> Roots(JunkTarget t)
    {
        if (t.DirPattern == null) { yield return t.Path; yield break; }
        if (!Directory.Exists(t.Path)) yield break;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(t.Path, t.DirPattern).ToList(); } catch { yield break; }
        foreach (var d in dirs)
        {
            // Firefox profilleri: yalnızca önbellek alt klasörleri
            if (t.DirPattern == "*") { yield return Path.Combine(d, "cache2"); yield return Path.Combine(d, "startupCache"); }
            else yield return d;
        }
    }

    private static string[] Patterns(JunkTarget t) => t.Pattern?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

    public static void ScanCategory(JunkCategory c, CancellationToken ct)
    {
        c.MatchedFiles.Clear();
        long size = 0, files = 0;
        if (c.Kind == JunkKind.RecycleBin)
        {
            var (s, n) = Deleter.QueryRecycleBin();
            size = s; files = n;
        }
        else
        {
            foreach (var t in c.Targets)
            {
                // Tamamı yönetici isteyen kategoriler bilgi amaçlı ölçülür; karışık kategorilerde yalnızca temizlenebilecek kısım gösterilir.
                if (t.AdminOnly && !Admin.IsElevated && !c.NeedsAdmin) continue;
                var threshold = t.MinAgeDays > 0 ? DateTime.Now.AddDays(-t.MinAgeDays) : (DateTime?)null;
                var patterns = Patterns(t);
                foreach (var root in Roots(t))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Directory.Exists(root)) continue;
                    var en = new FileSystemEnumerable<(long Size, string? Path)>(root,
                        (ref FileSystemEntry e) => ((e.Attributes & FsScan.CloudOnly) != 0 ? 0 : e.Length, c.UsesRecycleBin ? e.ToFullPath() : null),
                        new EnumerationOptions { RecurseSubdirectories = t.Recursive, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
                    {
                        ShouldIncludePredicate = (ref FileSystemEntry e) =>
                        {
                            if (e.IsDirectory) return false;
                            if (patterns.Length > 0)
                            {
                                bool any = false;
                                foreach (var p in patterns)
                                    if (FileSystemName.MatchesSimpleExpression(p, e.FileName, ignoreCase: true)) { any = true; break; }
                                if (!any) return false;
                            }
                            if (threshold is { } th)
                            {
                                if (e.LastWriteTimeUtc.LocalDateTime > th || e.CreationTimeUtc.LocalDateTime > th) return false;
                            }
                            return true;
                        },
                    };
                    try
                    {
                        foreach (var (s, p) in en)
                        {
                            size += s; files++;
                            if (p != null) c.MatchedFiles.Add(p);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
        }
        c.Size = size;
        c.Files = files;
        c.Warning = RunningWarning(c);
        c.Scanned = true;
    }

    private static string RunningWarning(JunkCategory c)
    {
        foreach (var name in c.ProcessNames)
        {
            try
            {
                var ps = Process.GetProcessesByName(name);
                bool running = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                if (running) return "Program açık. Açıkken kullanılan dosyalar atlanır; tam temizlik için kapatın.";
            }
            catch { }
        }
        return "";
    }

    // ------------------------------------------------------------------ Temizlik

    public static async Task<DeleteStats> CleanCategoryAsync(JunkCategory c, CancellationToken ct)
    {
        var st = new DeleteStats();
        if (c.Blocked) return st;
        switch (c.Kind)
        {
            case JunkKind.RecycleBin:
            {
                var (size, count) = Deleter.QueryRecycleBin();
                if (Deleter.EmptyRecycleBin()) { st.Bytes = size; st.Items = (int)Math.Min(int.MaxValue, count); }
                else st.AddError("Geri Dönüşüm Kutusu boşaltılamadı.");
                break;
            }
            case JunkKind.DeliveryOptimization:
            {
                long before = c.Size;
                var (code, _, err) = await StoreApps.RunPowerShellAsync("Delete-DeliveryOptimizationCache -Force -ErrorAction Stop", ct);
                if (code == 0 && string.IsNullOrWhiteSpace(err)) { st.Bytes = before; st.Items = (int)Math.Min(int.MaxValue, c.Files); }
                else
                {
                    foreach (var t in c.Targets)
                        st.Merge(await Task.Run(() => Deleter.CleanFolder(t.Path, null, true, null, ct), ct));
                }
                break;
            }
            default:
                if (c.UsesRecycleBin)
                {
                    var items = c.MatchedFiles.Select(f => new DeleteItem(f)).ToList();
                    st.Merge(await Deleter.RecycleAsync(items, null, ct));
                    break;
                }
                await Task.Run(() =>
                {
                    foreach (var t in c.Targets)
                    {
                        if (t.AdminOnly && !Admin.IsElevated) continue;
                        var threshold = t.MinAgeDays > 0 ? DateTime.Now.AddDays(-t.MinAgeDays) : (DateTime?)null;
                        var patterns = Patterns(t);
                        foreach (var root in Roots(t))
                        {
                            if (!Directory.Exists(root)) continue;
                            if (patterns.Length == 0) st.Merge(Deleter.CleanFolder(root, null, t.Recursive, threshold, ct));
                            else foreach (var p in patterns) st.Merge(Deleter.CleanFolder(root, p, t.Recursive, threshold, ct));
                        }
                    }
                }, ct);
                break;
        }
        ActivityLog.Write("Gereksiz dosya temizliği: " + c.Name, string.Join("; ", c.Targets.Select(t => t.Path).Take(3)), st.Bytes, st.Items, st.Failed,
            c.UsesRecycleBin ? "Geri Dönüşüm Kutusu" : "Kalıcı");
        return st;
    }
}
