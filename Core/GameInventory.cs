using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.Win32;

namespace TemizlikPaneli.Core;

public enum GameRemoval { SteamUri, EpicManual, Win32Uninstaller, StorePackage, UbisoftUri, FolderOnly }

public sealed class GameEntry : ObservableObject
{
    public string Name { get; init; } = "";
    public string Platform { get; init; } = "";
    public string? InstallDir { get; init; }
    public GameRemoval Removal { get; init; }
    public bool IsTool { get; init; }
    public DateTime? LastPlayed { get; init; }
    public DateTime? LastUpdated { get; init; }
    public string? SteamAppId { get; init; }
    public string? ExtraFolder { get; init; }       // Steam: workshop içeriği
    public string? CacheFolder { get; init; }       // Steam: gölgelendirici önbelleği
    public string? EpicManifest { get; init; }
    public string? EpicAppName { get; init; }
    public string? UbisoftId { get; init; }
    public InstalledApp? App { get; init; }
    public string? CoverPath { get; init; }
    public string? IconSource { get; init; }
    public string? Warning { get; init; }

    private long _size, _extraSize, _cacheSize;
    private bool _measured, _isBusy;
    private ImageSource? _cover, _icon;

    public long Size { get => _size; set { if (Set(ref _size, value)) Changed(); } }
    public long ExtraSize { get => _extraSize; set { if (Set(ref _extraSize, value)) Changed(); } }
    public long CacheSize { get => _cacheSize; set { if (Set(ref _cacheSize, value)) Changed(); } }
    public bool Measured { get => _measured; set { if (Set(ref _measured, value)) Changed(); } }
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public ImageSource? Cover { get => _cover; set => Set(ref _cover, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    private void Changed()
    {
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(DetailText));
    }

    public long TotalSize => Size + ExtraSize + CacheSize;
    public string SizeText => TotalSize > 0 ? (Measured ? "" : "≈ ") + Fmt.Size(TotalSize) : "—";
    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (ExtraSize > 0) parts.Add("Atölye " + Fmt.Size(ExtraSize));
            if (CacheSize > 0) parts.Add("Önbellek " + Fmt.Size(CacheSize));
            return string.Join(" · ", parts);
        }
    }
    public string LastPlayedText => LastPlayed is { } d ? "Son oynama: " + Fmt.Ago(d) : "";
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
}

public sealed class OrphanFolder
{
    public required string Path { get; init; }
    public required string Reason { get; init; }
    public long Size { get; set; }
    public string SizeText => Fmt.Size(Size);
}

public sealed class GameScanResult
{
    public List<GameEntry> Games { get; } = new();
    public List<OrphanFolder> Orphans { get; } = new();
    /// <summary>Program boyutlarından düşülecek oyun klasörleri.</summary>
    public HashSet<string> GameFolders { get; } = new(PathUtil.Cmp);
    public string? SteamRoot { get; set; }
    public List<string> SteamLibraries { get; } = new();
}

public static class GameInventory
{
    private static readonly HashSet<string> SteamToolIds = new()
    {
        "228980", "1070560", "1391110", "1628350", "1493710", "2180100", "1826330", "1161040", "250820", "1887720", "2348590", "3658110"
    };

    private static readonly string[] LauncherNames =
    {
        "Steam", "Epic Games Launcher", "Epic Online Services", "EA app", "EA Desktop", "Origin", "Ubisoft Connect", "Uplay",
        "Battle.net", "GOG GALAXY", "Riot Client", "Riot Vanguard", "Rockstar Games Launcher", "Rockstar Games Social Club",
        "Xbox", "Paradox Launcher", "CurseForge", "Minecraft Launcher", "Overwolf", "EasyAntiCheat", "BattlEye",
    };

    private static readonly string[] ComponentWords = { "SDK", "Social Club", "Anti-Cheat", "AntiCheat", "Redistributable", "Runtime", "Vanguard", "Prerequisites" };

    /// <summary>Uygulamalar listesindeki bir girdinin aslında oyun olup olmadığı (oyunlar sayfasında gösterilir).</summary>
    public static bool IsGameApp(InstalledApp a, GameScanResult games)
    {
        if (a.KeyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) return true;
        if (a.InstallLocation != null && games.GameFolders.Contains(a.InstallLocation)) return true;
        foreach (var g in games.Games)
        {
            if (g.App == null) continue;
            if (ReferenceEquals(g.App, a)) return true;
            if (a.PackageFullName != null && a.PackageFullName == g.App.PackageFullName) return true;
        }
        return false;
    }

    public static bool IsLauncher(string name) =>
        LauncherNames.Any(l => name.StartsWith(l, StringComparison.OrdinalIgnoreCase)) ||
        ComponentWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static GameScanResult Scan(IReadOnlyList<InstalledApp> win32Apps, IReadOnlyList<StorePackage> packages)
    {
        var r = new GameScanResult();
        try { ScanSteam(r); } catch { }
        try { ScanEpic(r); } catch { }
        try { ScanWin32(r, win32Apps); } catch { }
        try { ScanXbox(r, packages); } catch { }
        try { ScanMinecraft(r); } catch { }

        foreach (var g in r.Games)
            if (g.InstallDir != null) r.GameFolders.Add(g.InstallDir);
        foreach (var lib in r.SteamLibraries)
        {
            r.GameFolders.Add(Path.Combine(lib, "steamapps"));
            SafetyGuard.ProtectFolder(Path.Combine(lib, "steamapps"));
            SafetyGuard.ProtectFolder(Path.Combine(lib, "steamapps", "common"));
        }
        return r;
    }

    // ------------------------------------------------------------------ Steam

    public static string? FindSteamRoot()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = PathUtil.Normalize(k?.GetValue("SteamPath") as string ?? "");
            if (p.Length > 0 && Directory.Exists(p)) return PathUtil.TrueCase(p);
        }
        catch { }
        try
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var k = b.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var p = PathUtil.Normalize(k?.GetValue("InstallPath") as string ?? "");
            if (p.Length > 0 && Directory.Exists(p)) return PathUtil.TrueCase(p);
        }
        catch { }
        return null;
    }

    public static List<string> FindSteamLibraries(string steamRoot)
    {
        var libs = new List<string> { steamRoot };
        var vdf = VdfNode.Load(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"))
               ?? VdfNode.Load(Path.Combine(steamRoot, "config", "libraryfolders.vdf"));
        var lf = vdf?.Node("libraryfolders") ?? vdf?.Node("LibraryFolders");
        if (lf != null)
        {
            foreach (var kv in lf.Items)
            {
                string? path = kv.Value switch
                {
                    VdfNode n => n.Str("path"),
                    string s when long.TryParse(kv.Key, out _) => s,
                    _ => null,
                };
                var norm = PathUtil.Normalize(path ?? "");
                if (norm.Length > 0 && Directory.Exists(norm) && !libs.Contains(norm, PathUtil.Cmp)) libs.Add(PathUtil.TrueCase(norm));
            }
        }
        return libs;
    }

    private static void ScanSteam(GameScanResult r)
    {
        var root = FindSteamRoot();
        if (root == null) return;
        r.SteamRoot = root;
        var cacheRoot = Path.Combine(root, "appcache", "librarycache");
        foreach (var lib in FindSteamLibraries(root))
        {
            r.SteamLibraries.Add(lib);
            var steamapps = Path.Combine(lib, "steamapps");
            var common = Path.Combine(steamapps, "common");
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Steam Controller Configs" };
            if (!Directory.Exists(steamapps)) continue;
            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                var st = VdfNode.Load(acf)?.Node("AppState");
                if (st == null) continue;
                var id = st.Str("appid") ?? "";
                var dirName = st.Str("installdir") ?? "";
                if (dirName.Length == 0) continue;
                known.Add(dirName);
                var dir = Path.Combine(common, dirName);
                r.Games.Add(new GameEntry
                {
                    Name = st.Str("name") ?? dirName,
                    Platform = "Steam",
                    InstallDir = dir,
                    Removal = GameRemoval.SteamUri,
                    SteamAppId = id,
                    IsTool = SteamToolIds.Contains(id),
                    Size = st.Long("SizeOnDisk"),
                    LastPlayed = Unix(st.Long("LastPlayed")),
                    LastUpdated = Unix(st.Long("LastUpdated")),
                    ExtraFolder = Path.Combine(steamapps, "workshop", "content", id),
                    CacheFolder = Path.Combine(steamapps, "shadercache", id),
                    CoverPath = FindSteamImage(cacheRoot, id, "header.jpg") ?? FindSteamImage(cacheRoot, id, "library_header.jpg")
                                ?? FindSteamImage(cacheRoot, id, "library_hero.jpg"),
                    IconSource = FindSteamIcon(cacheRoot, id),
                });
            }
            if (Directory.Exists(common))
            {
                foreach (var d in Directory.EnumerateDirectories(common))
                {
                    if (known.Contains(Path.GetFileName(d))) continue;
                    r.Orphans.Add(new OrphanFolder { Path = d, Reason = "Steam'de kurulu bir oyuna ait değil (kaldırılmış oyundan kalmış)" });
                }
            }
        }
    }

    private static DateTime? Unix(long s) => s > 100000 ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime : null;

    private static string? FindSteamImage(string cacheRoot, string id, string fileName)
    {
        try
        {
            var dir = Path.Combine(cacheRoot, id);
            if (Directory.Exists(dir))
            {
                var direct = Path.Combine(dir, fileName);
                if (File.Exists(direct)) return direct;
                var nested = Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (nested != null) return nested;
            }
            var legacy = Path.Combine(cacheRoot, $"{id}_{Path.GetFileNameWithoutExtension(fileName)}.jpg");
            if (File.Exists(legacy)) return legacy;
        }
        catch { }
        return null;
    }

    private static string? FindSteamIcon(string cacheRoot, string id)
    {
        try
        {
            var dir = Path.Combine(cacheRoot, id);
            if (!Directory.Exists(dir)) return null;
            // Steam simgesi: klasörde 40 karakterlik karma adlı küçük jpg
            return Directory.EnumerateFiles(dir, "*.jpg")
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Length == 40);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ Epic Games

    public static string EpicManifestDir => Path.Combine(KnownFolders.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
    public static string EpicLauncherInstalled => Path.Combine(KnownFolders.ProgramData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

    private static void ScanEpic(GameScanResult r)
    {
        if (!Directory.Exists(EpicManifestDir)) return;
        var entries = new List<(GameEntry Game, long Size)>();
        foreach (var file in Directory.EnumerateFiles(EpicManifestDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var e = doc.RootElement;
                string Get(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var appName = Get("AppName");
                var main = Get("MainGameAppName");
                if (main.Length > 0 && !main.Equals(appName, StringComparison.OrdinalIgnoreCase)) continue; // DLC
                var loc = PathUtil.Normalize(Get("InstallLocation"));
                if (loc.Length == 0) continue;
                long size = e.TryGetProperty("InstallSize", out var s) && s.TryGetInt64(out var l) ? l : 0;
                var exe = Get("LaunchExecutable");
                entries.Add((new GameEntry
                {
                    Name = Get("DisplayName") is { Length: > 0 } n ? n : appName,
                    Platform = "Epic Games",
                    InstallDir = loc,
                    Removal = GameRemoval.EpicManual,
                    EpicManifest = file,
                    EpicAppName = appName,
                    Size = size,
                    IconSource = exe.Length > 0 ? Path.Combine(loc, exe) : null,
                }, size));
            }
            catch { }
        }
        // Aynı klasörü paylaşan girişlerden (ek paketler) en büyüğünü tut
        foreach (var grp in entries.GroupBy(x => x.Game.InstallDir!, PathUtil.Cmp))
            r.Games.Add(grp.OrderByDescending(x => x.Size).First().Game);
    }

    /// <summary>Epic oyununu Epic'in kendi kayıtlarıyla birlikte kaldırır: klasör + .item bildirimi + LauncherInstalled.dat girdisi.</summary>
    public static async Task<DeleteStats> RemoveEpicGameAsync(GameEntry g, bool recycle)
    {
        var st = await Deleter.DeleteUserItemsAsync("Epic oyunu kaldırıldı: " + g.Name, new[] { new DeleteItem(g.InstallDir!, g.Size) }, recycle);
        if (Directory.Exists(g.InstallDir)) return st; // klasör silinemediyse kayıtlara dokunma
        try { if (g.EpicManifest != null && File.Exists(g.EpicManifest)) File.Delete(g.EpicManifest); } catch (Exception ex) { st.AddError("Epic bildirimi silinemedi: " + ex.Message); }
        try
        {
            if (File.Exists(EpicLauncherInstalled))
            {
                var node = JsonNode.Parse(File.ReadAllText(EpicLauncherInstalled));
                if (node?["InstallationList"] is JsonArray arr)
                {
                    for (int i = arr.Count - 1; i >= 0; i--)
                    {
                        var loc = PathUtil.Normalize(arr[i]?["InstallLocation"]?.GetValue<string>() ?? "");
                        var app = arr[i]?["AppName"]?.GetValue<string>() ?? "";
                        if (PathUtil.Cmp.Equals(loc, g.InstallDir) || app.Equals(g.EpicAppName, StringComparison.OrdinalIgnoreCase))
                            arr.RemoveAt(i);
                    }
                    File.WriteAllText(EpicLauncherInstalled, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch (Exception ex) { st.AddError("LauncherInstalled.dat güncellenemedi: " + ex.Message); }
        return st;
    }

    // ------------------------------------------------------------------ Kayıt defterindeki oyunlar (Riot, Battle.net, EA, Ubisoft, GOG, Rockstar...)

    private static void ScanWin32(GameScanResult r, IReadOnlyList<InstalledApp> apps)
    {
        var existing = new HashSet<string>(r.Games.Where(g => g.InstallDir != null).Select(g => g.InstallDir!), PathUtil.Cmp);
        foreach (var a in apps)
        {
            if (a.KeyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsLauncher(a.Name)) continue;
            var platform = Classify(a);
            if (platform == null) continue;
            if (a.InstallLocation != null && existing.Contains(a.InstallLocation)) continue;
            string? ubiId = a.KeyName.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase) ? a.KeyName["Uplay Install ".Length..].Trim() : null;
            r.Games.Add(new GameEntry
            {
                Name = a.Name,
                Platform = platform,
                InstallDir = a.InstallLocation,
                Removal = ubiId != null ? GameRemoval.UbisoftUri : GameRemoval.Win32Uninstaller,
                UbisoftId = ubiId,
                App = a,
                Size = a.RegistrySize,
                IconSource = AppInventory.IconFile(a.DisplayIcon, out _),
            });
            if (a.InstallLocation != null) existing.Add(a.InstallLocation);
        }
    }

    private static string? Classify(InstalledApp a)
    {
        string u = a.UninstallString ?? "", p = a.Publisher, loc = a.InstallLocation ?? "";
        if (a.KeyName.StartsWith("Riot Game ", StringComparison.OrdinalIgnoreCase)) return "Riot Games";
        if (u.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) || u.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase)) return "Battle.net";
        if (a.KeyName.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase)) return "Ubisoft";
        if (u.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Electronic Arts", StringComparison.OrdinalIgnoreCase)) return "EA";
        if (p.Equals("GOG.com", StringComparison.OrdinalIgnoreCase) || loc.Contains("\\GOG Games\\", StringComparison.OrdinalIgnoreCase)) return "GOG";
        if (p.StartsWith("Rockstar Games", StringComparison.OrdinalIgnoreCase)) return "Rockstar";
        if (loc.Contains("\\XboxGames\\", StringComparison.OrdinalIgnoreCase)) return "Xbox";
        foreach (var seg in loc.Split('\\'))
            if (seg.Equals("Games", StringComparison.OrdinalIgnoreCase) || seg.EndsWith(" Games", StringComparison.OrdinalIgnoreCase)) return "Diğer";
        return null;
    }

    // ------------------------------------------------------------------ Xbox (C:\XboxGames)

    private static void ScanXbox(GameScanResult r, IReadOnlyList<StorePackage> packages)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = Path.Combine(drive.Name, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var leaf = Path.GetFileName(dir);
                if (leaf.Equals("GameSave", StringComparison.OrdinalIgnoreCase)) continue;
                string? identity = null, display = null;
                try
                {
                    var man = Path.Combine(dir, "Content", "appxmanifest.xml");
                    if (File.Exists(man))
                    {
                        var x = XDocument.Load(man).Root;
                        identity = x?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
                        display = x?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties")?
                            .Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;
                    }
                }
                catch { }
                var pkg = identity == null ? null : packages.FirstOrDefault(p => p.Name.Equals(identity, StringComparison.OrdinalIgnoreCase));
                InstalledApp? app = pkg == null ? null : new InstalledApp
                {
                    Id = "store|" + pkg.FullName,
                    Name = leaf,
                    Source = AppSource.Store,
                    PackageFullName = pkg.FullName,
                    PackageFamilyName = pkg.Family,
                    InstallLocation = pkg.Location,
                };
                r.Games.Add(new GameEntry
                {
                    Name = display != null && !display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase) ? display : leaf,
                    Platform = "Xbox",
                    InstallDir = dir,
                    Removal = app != null ? GameRemoval.StorePackage : GameRemoval.FolderOnly,
                    App = app,
                    IconSource = FirstExisting(Path.Combine(dir, "Content", "GraphicsLogo.png"), Path.Combine(dir, "GraphicsLogo.png")),
                    Warning = app == null ? "Xbox uygulamasından kaldırılması önerilir." : null,
                });
            }
        }
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    // ------------------------------------------------------------------ Minecraft / CurseForge / Modrinth / Prism

    private static void ScanMinecraft(GameScanResult r)
    {
        var up = KnownFolders.UserProfile;
        var vanilla = Path.Combine(KnownFolders.Roaming, ".minecraft");
        if (Directory.Exists(vanilla))
            r.Games.Add(new GameEntry
            {
                Name = "Minecraft: Java Edition (oyun verileri)",
                Platform = "Minecraft",
                InstallDir = vanilla,
                Removal = GameRemoval.FolderOnly,
                Warning = "Bu klasörde dünyalarınız (saves) da var. Silerseniz kayıtlı dünyalar da gider.",
            });

        void Instances(string root, string platform)
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root))
                r.Games.Add(new GameEntry
                {
                    Name = Path.GetFileName(d),
                    Platform = platform,
                    InstallDir = d,
                    Removal = GameRemoval.FolderOnly,
                    Warning = "Mod paketi örneği. İçindeki dünyalar da silinir.",
                });
        }
        Instances(Path.Combine(up, "curseforge", "minecraft", "Instances"), "CurseForge");
        Instances(Path.Combine(KnownFolders.Roaming, "ModrinthApp", "profiles"), "Modrinth");
        Instances(Path.Combine(KnownFolders.Roaming, "com.modrinth.theseus", "profiles"), "Modrinth");
        Instances(Path.Combine(KnownFolders.Roaming, "PrismLauncher", "instances"), "Prism Launcher");
    }

    /// <summary>Oyun klasörlerini ölçer (Steam için atölye ve gölgelendirici önbelleği dahil).</summary>
    public static void MeasureSizes(IEnumerable<GameEntry> games, CancellationToken ct)
    {
        Parallel.ForEach(games.ToList(), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, g =>
        {
            try
            {
                if (g.InstallDir != null && Directory.Exists(g.InstallDir))
                {
                    var m = FsScan.Measure(g.InstallDir, ct);
                    if (m.Files > 0 || g.Size == 0) g.Size = m.Size;
                }
                if (g.ExtraFolder != null && Directory.Exists(g.ExtraFolder)) g.ExtraSize = FsScan.Measure(g.ExtraFolder, ct).Size;
                if (g.CacheFolder != null && Directory.Exists(g.CacheFolder)) g.CacheSize = FsScan.Measure(g.CacheFolder, ct).Size;
                g.Measured = true;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        });
    }
}
