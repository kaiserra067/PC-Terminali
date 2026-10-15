using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TemizlikPaneli.Core;

public enum LeftoverKind { Folder, EmptyFolder, StoreData, SteamOrphan, BrokenShortcut, BrokenUninstallEntry, RegistryKey }
public enum Confidence { High, Medium, Low }

public sealed class LeftoverItem : ObservableObject
{
    public required LeftoverKind Kind { get; init; }
    public required string Path { get; init; }
    public required string Reason { get; init; }
    public string Location { get; init; } = "";
    public Confidence Confidence { get; init; }
    public DateTime? LastModified { get; init; }
    public bool NeedsAdmin { get; init; }
    public RegistryHive Hive { get; init; }
    public string? SubKey { get; init; }

    private long _size;
    private bool _isSelected;
    public long Size { get => _size; set { if (Set(ref _size, value)) OnPropertyChanged(nameof(SizeText)); } }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public bool IsRegistry => Kind is LeftoverKind.RegistryKey or LeftoverKind.BrokenUninstallEntry;
    public string Name => Kind == LeftoverKind.BrokenUninstallEntry ? Path
        : IsRegistry ? Path[(Path.LastIndexOf('\\') + 1)..]
        : System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    public string Glyph => Kind switch
    {
        LeftoverKind.Folder or LeftoverKind.EmptyFolder => "",
        LeftoverKind.StoreData => "",
        LeftoverKind.SteamOrphan => "",
        LeftoverKind.BrokenShortcut => "",
        _ => "",
    };
    public System.Windows.Media.Brush ConfidenceBrush => ThemeManager.Brush(Confidence switch
    {
        Confidence.High => "GoodBrush",
        Confidence.Medium => "WarningBrush",
        _ => "SeriousBrush",
    });
    public string? AdminTip => CanAct ? null : "Bu öğeyi silmek için uygulamayı yönetici olarak yeniden başlatın.";
    public string SizeText => IsRegistry ? "Kayıt" : Fmt.Size(Size);
    public string DateText => Fmt.Ago(LastModified);
    public string KindText => Kind switch
    {
        LeftoverKind.Folder => "Klasör",
        LeftoverKind.EmptyFolder => "Boş klasör",
        LeftoverKind.StoreData => "Store verisi",
        LeftoverKind.SteamOrphan => "Oyun klasörü",
        LeftoverKind.BrokenShortcut => "Bozuk kısayol",
        LeftoverKind.BrokenUninstallEntry => "Bozuk kayıt",
        _ => "Kayıt defteri",
    };
    public string ConfidenceText => Confidence switch { Confidence.High => "Kesin", Confidence.Medium => "Büyük olasılıkla", _ => "Emin değil" };
    public bool CanAct => !NeedsAdmin || Admin.IsElevated;
}

/// <summary>Kurulu olan her şeyin "sahiplik" bilgisi: hangi klasörler ve adlar kullanımda.</summary>
public sealed class Ownership
{
    public readonly List<string> OwnedPaths = new();
    public readonly HashSet<string> Tokens = new(StringComparer.Ordinal);
    public readonly HashSet<string> PackageFamilies = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex VersionRx = new(@"(\(.*?\))|(\[.*?\])|(\bv?\d+([.\-_]\d+)+\w*)|(\b(x64|x86|64-bit|32-bit|amd64|arm64)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "update", "version", "edition", "setup", "installer", "install", "software", "inc", "ltd", "llc",
        "gmbh", "corp", "corporation", "company", "limited", "microsoft", "windows", "app", "apps", "application", "tools", "tool",
        "driver", "drivers", "runtime", "redistributable", "service", "services", "client", "plugin", "plugins", "free", "player",
        "launcher", "games", "game", "studio", "studios", "technologies", "technology", "systems", "interactive", "entertainment",
        "online", "desktop", "manager", "program", "files", "data", "user", "visual", "support", "package", "suite", "beta", "core",
        "framework", "development", "kit", "sdk", "audio", "video", "media", "secure", "connect", "center", "control", "panel",
    };

    public static string CleanName(string name) => PathUtil.Simplify(VersionRx.Replace(name, " "));

    private static readonly Regex LegalRx = new(@"\b(inc|ltd|llc|gmbh|corp|corporation|co|company|limited|s\.?a|s\.?r\.?l|b\.?v|ab|oy|a\.?s|plc|pty|kg|ag)\b\.?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Yayıncı adı, hukuki ekler olmadan ("Riot Games, Inc" → "riotgames").</summary>
    public static string CleanPublisher(string publisher) => PathUtil.Simplify(LegalRx.Replace(publisher, " "));

    public static IEnumerable<string> Words(string name)
    {
        foreach (var w in Regex.Split(VersionRx.Replace(name, " "), @"[^\p{L}\p{N}]+"))
        {
            var s = PathUtil.Simplify(w);
            if (s.Length >= 4 && !StopWords.Contains(s) && !s.All(char.IsDigit)) yield return s;
        }
    }

    public void AddName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var full = CleanName(name);
        if (full.Length >= 3) Tokens.Add(full);
        foreach (var w in Words(name)) Tokens.Add(w);
    }

    public void AddPath(string? path, bool addLeafToken = true)
    {
        var p = PathUtil.Normalize(path ?? "");
        if (p.Length <= 3) return;
        OwnedPaths.Add(p);
        if (addLeafToken) AddName(System.IO.Path.GetFileName(p));
    }

    private static readonly HashSet<string> GenericFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine", "Binaries", "Content", "Data", "bin", "bin64", "Plugins", "Redist", "_CommonRedist", "CommonRedist", "Win64", "Win32", "Mods",
        "Maps", "Saves", "Config", "Logs", "Cache", "Shaders", "Movies", "Sound", "Sounds", "Audio", "Localization", "Resources", "Assets",
        "Packages", "Prerequisites", "Support", "DirectX", "vcredist", "EasyAntiCheat", "BattlEye", "Docs", "Tools", "Launcher", "game", "x64",
        "x86", "lib", "libs", "platform", "common", "cfg", "materials", "models", "scripts", "particles", "media", "managed", "MonoBleedingEdge",
        "Mono", "Modules", "Video", "Videos", "Music", "Fonts", "UI", "Textures", "Language", "Languages", "Settings", "Steam", "Installers",
        "Install", "Setup", "Update", "Updates", "Patch", "Crash", "CrashReporter", "Extras", "Manuals", "Documentation", "Licenses", "Legal",
    };

    /// <summary>Oyun klasöründeki iç adlar (ör. PUBG\TslGame, Among Us.exe) — AppData'daki oyun verisini doğru eşlemek için.</summary>
    public void AddInternalNames(string installDir)
    {
        try
        {
            if (!Directory.Exists(installDir)) return;
            foreach (var d in Directory.EnumerateDirectories(installDir).Take(60))
            {
                var name = System.IO.Path.GetFileName(d);
                if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
                if (GenericFolderNames.Contains(name)) continue;
                var s = PathUtil.Simplify(name);
                if (s.Length >= 5) Tokens.Add(s);
            }
            foreach (var f in Directory.EnumerateFiles(installDir, "*.exe").Take(30))
            {
                var s = PathUtil.Simplify(System.IO.Path.GetFileNameWithoutExtension(f));
                if (s.Length >= 5 && !s.StartsWith("unins", StringComparison.Ordinal) && !s.Contains("crash", StringComparison.Ordinal)
                    && !s.Contains("setup", StringComparison.Ordinal) && !s.Contains("launcher", StringComparison.Ordinal)) Tokens.Add(s);
            }
        }
        catch { }
    }

    public bool OwnsPath(string dir)
    {
        foreach (var o in OwnedPaths)
            if (PathUtil.IsSameOrUnder(o, dir) || PathUtil.IsSameOrUnder(dir, o)) return true;
        return false;
    }

    public bool MatchesName(string folderName)
    {
        var s = PathUtil.Simplify(folderName);
        if (s.Length < 3) return true; // çok kısa adlar belirsiz: sahipli say
        if (Tokens.Contains(s)) return true;
        if (s.Length < 4) return false;
        foreach (var t in Tokens)
        {
            if (t.Length < 4) continue;
            if (s.Contains(t, StringComparison.Ordinal) || t.Contains(s, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static Ownership Build(IReadOnlyList<InstalledApp> apps, IReadOnlyList<StorePackage> packages, GameScanResult? games)
    {
        var o = new Ownership();
        foreach (var a in apps)
        {
            o.AddName(a.Name);
            o.AddName(a.Publisher);
            if (a.InstallLocation != null)
            {
                o.AddPath(a.InstallLocation);
                var parent = System.IO.Path.GetDirectoryName(a.InstallLocation);
                if (parent != null && SafetyGuard.CanDelete(parent)) o.AddName(System.IO.Path.GetFileName(parent));
            }
            if (AppInventory.IconFile(a.DisplayIcon, out _) is { } icon) o.AddPath(System.IO.Path.GetDirectoryName(icon), false);
            if (AppInventory.SplitCommand(a.UninstallString) is { } cmd && System.IO.Path.IsPathRooted(cmd.Exe) && File.Exists(cmd.Exe))
            {
                var d = System.IO.Path.GetDirectoryName(cmd.Exe);
                if (d != null && AppInventory.IsAcceptableAppFolder(d)) o.AddPath(d, false);
            }
        }
        foreach (var p in packages)
        {
            o.PackageFamilies.Add(p.Family);
            if (p.IsFramework || p.IsResource) continue;
            foreach (var part in p.Name.Split('.', StringSplitOptions.RemoveEmptyEntries)) o.AddName(part);
        }
        if (games != null)
        {
            foreach (var g in games.Games)
            {
                o.AddName(g.Name);
                o.AddPath(g.InstallDir);
                if (g.InstallDir != null) o.AddInternalNames(g.InstallDir);
            }
            foreach (var l in games.SteamLibraries) o.AddPath(l, false);
        }
        foreach (var p in RunningProcessDirs()) o.AddPath(p, false);
        foreach (var p in ServiceAndStartupDirs()) o.AddPath(p, false);
        return o;
    }

    private static IEnumerable<string> RunningProcessDirs()
    {
        var dirs = new HashSet<string>(PathUtil.Cmp);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = Native.GetProcessPath(p.Id);
                var d = path == null ? null : System.IO.Path.GetDirectoryName(path);
                if (d != null && !PathUtil.IsSameOrUnder(d, KnownFolders.Windows)) dirs.Add(d);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return dirs;
    }

    private static IEnumerable<string> ServiceAndStartupDirs()
    {
        var dirs = new HashSet<string>(PathUtil.Cmp);
        void AddCmd(string? cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            cmd = cmd.Trim();
            if (cmd.StartsWith(@"\??\")) cmd = cmd[4..];
            var c = AppInventory.SplitCommand(cmd);
            if (c == null || !System.IO.Path.IsPathRooted(c.Value.Exe)) return;
            var d = System.IO.Path.GetDirectoryName(c.Value.Exe);
            if (d != null && !PathUtil.IsSameOrUnder(PathUtil.Normalize(d), KnownFolders.Windows)) dirs.Add(d);
        }
        try
        {
            using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (svc != null)
                foreach (var name in svc.GetSubKeyNames())
                {
                    try { using var k = svc.OpenSubKey(name); AddCmd(k?.GetValue("ImagePath") as string); } catch { }
                }
        }
        catch { }
        foreach (var (hive, path) in new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        })
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k != null) foreach (var v in k.GetValueNames()) AddCmd(k.GetValue(v) as string);
            }
            catch { }
        }
        return dirs;
    }
}

public static class LeftoverScanner
{
    /// <summary>Hiçbir zaman kalıntı sayılmayacak Windows ve sürücü klasörleri.</summary>
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Packages", "Temp", "Programs", "Publishers", "ConnectedDevicesPlatform", "Comms", "D3DSCache",
        "CrashDumps", "PeerDistRepub", "PlaceholderTileLogoFolder", "VirtualStore", "Microsoft Help", "Application Data", "History",
        "Temporary Internet Files", "MicrosoftEdge", "Microsoft Games", "Microsoft Office", "Microsoft Office 15", "OneDrive",
        "Microsoft OneDrive", "assembly", "ElevatedDiagnostics", "SquirrelTemp", "Package Cache", "regid.1991-06.com.microsoft",
        "Desktop", "Documents", "Start Menu", "Templates", "Favorites", "SoftwareDistribution", "USOPrivate", "USOShared", "ssh",
        "WindowsHolographicDevices", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail",
        "Windows Media Player", "Windows Multimedia Platform", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices",
        "Windows Security", "Windows Sidebar", "Windows Journal", "WindowsApps", "WindowsPowerShell", "PowerShell", "Common Files",
        "Internet Explorer", "ModifiableWindowsApps", "Reference Assemblies", "MSBuild", "Uninstall Information", "dotnet",
        "PackageManagement", "Microsoft.NET", "Microsoft SDKs", "Microsoft Visual Studio", "Windows Kits", "IIS", "IIS Express",
        "Microsoft SQL Server", "Microsoft Update Health Tools", "Microsoft_Corporation", "IsolatedStorage", "Apps", "Deployment",
        "Default", "Public", "All Users", "Default User", "Diagnostics", "GameDVR", "Downloaded Installations", "Downloaded Program Files",
        "Device Stage", "DeviceSync", "Windows App Certification Kit", "chocolatey", "MSECache", "Adobe", "Intel", "NVIDIA",
        "NVIDIA Corporation", "AMD", "ATI", "Realtek", "Dolby", "DTS", "Synaptics", "Elantech", "Conexant", "Waves", "Killer",
        "Qualcomm", "Broadcom", "MediaTek", "Lenovo", "Dell", "HP", "Hewlett-Packard", "ASUS", "ASUSTeK", "Acer", "MSI", "Gigabyte",
        "TemizlikPaneli", "PCTerminali", "CEF", "fontconfig", "Sun", "Macromedia", "Identities", "Media Center Programs", "Sync", "Logs",
        "Whesvc", "DigitalEntitlements", "WindowsApps", "Microsoft Help", "SystemTemp", "ProgramData", "Comms", "UNP", "WinGet",
        "Packages_Shared", "MicrosoftOneDrive", "Edge", "EdgeUpdate", "EdgeCore", "EdgeWebView", "AppRepository", "Containers", "WebView2",
    };

    private static readonly HashSet<string> StartMenuSystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Startup", "Başlangıç", "Administrative Tools", "Accessibility", "Accessories", "Maintenance", "System Tools",
        "Windows PowerShell", "Windows Tools", "Windows Accessories", "Windows Administrative Tools", "Windows Ease of Access", "Windows System",
    };

    /// <summary>Gerçek paket aile adı: Ad_yayıncıKimliği (13 karakter).</summary>
    private static readonly Regex PackageFamilyRx = new(@"^[A-Za-z0-9.\-]+_[a-z0-9]{13}$", RegexOptions.Compiled);

    private static readonly Regex GuidLike = new(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$", RegexOptions.Compiled);

    private static (string Path, string Label, bool Admin)[] Roots() => new[]
    {
        (KnownFolders.Roaming, "AppData\\Roaming", false),
        (KnownFolders.Local, "AppData\\Local", false),
        (KnownFolders.LocalLow, "AppData\\LocalLow", false),
        (System.IO.Path.Combine(KnownFolders.Local, "Programs"), "AppData\\Local\\Programs", false),
        (KnownFolders.ProgramData, "ProgramData", true),
        (KnownFolders.ProgramFiles, "Program Files", true),
        (KnownFolders.ProgramFilesX86, "Program Files (x86)", true),
    };

    public static List<LeftoverItem> ScanAll(IReadOnlyList<InstalledApp> apps, IReadOnlyList<StorePackage> packages, GameScanResult? games,
        IProgress<string>? progress, CancellationToken ct)
    {
        var own = Ownership.Build(apps, packages, games);
        var items = new List<LeftoverItem>();
        var now = DateTime.Now;

        // 1) Programlara ait olmayan klasörler
        var candidates = new List<(string Dir, string Label, bool Admin)>();
        foreach (var (root, label, admin) in Roots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); } catch { continue; }
            foreach (var d in dirs)
            {
                var leaf = System.IO.Path.GetFileName(d);
                if (SystemNames.Contains(leaf) || leaf.StartsWith('.') || leaf.StartsWith('$')) continue;
                if (Deleter.IsLink(new DirectoryInfo(d))) continue;
                if (own.OwnsPath(d) || own.MatchesName(leaf)) continue;
                if (SubfolderMatches(d, own)) continue;
                if (!SafetyGuard.CanDelete(d)) continue;
                candidates.Add((d, label, admin));
            }
        }

        var found = new System.Collections.Concurrent.ConcurrentBag<LeftoverItem>();
        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, c =>
        {
            progress?.Report(c.Dir);
            var m = FsScan.Measure(c.Dir, ct);
            var newest = m.Newest == DateTime.MinValue ? SafeWriteTime(c.Dir) : m.Newest;
            var age = now - newest;
            if (m.Files == 0 && m.Dirs == 0)
            {
                found.Add(new LeftoverItem { Kind = LeftoverKind.EmptyFolder, Path = c.Dir, Location = c.Label, Reason = "Boş klasör", Confidence = Confidence.High, LastModified = newest, NeedsAdmin = c.Admin });
                return;
            }
            if (age.TotalDays < 14) return; // yakın zamanda kullanılmış: büyük olasılıkla bir şeye ait
            var leaf = System.IO.Path.GetFileName(c.Dir);
            var conf = GuidLike.IsMatch(leaf) || age.TotalDays < 90 ? Confidence.Low : Confidence.Medium;
            found.Add(new LeftoverItem
            {
                Kind = LeftoverKind.Folder, Path = c.Dir, Location = c.Label, Size = m.Size, LastModified = newest, NeedsAdmin = c.Admin,
                Confidence = conf,
                Reason = conf == Confidence.Medium ? "Kurulu hiçbir program, oyun ya da hizmetle eşleşmedi; uzun süredir kullanılmıyor" : "Kurulu bir programla eşleşmedi",
            });
        });
        items.AddRange(found);

        // 2) Kaldırılmış Store uygulamalarının verileri
        var pkgRoot = System.IO.Path.Combine(KnownFolders.Local, "Packages");
        if (own.PackageFamilies.Count > 0 && Directory.Exists(pkgRoot))
        {
            foreach (var d in SafeDirs(pkgRoot))
            {
                ct.ThrowIfCancellationRequested();
                var fam = System.IO.Path.GetFileName(d);
                if (own.PackageFamilies.Contains(fam) || !PackageFamilyRx.IsMatch(fam)) continue;
                var m = FsScan.Measure(d, ct);
                bool ms = fam.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || fam.StartsWith("Windows", StringComparison.OrdinalIgnoreCase);
                items.Add(new LeftoverItem
                {
                    Kind = LeftoverKind.StoreData, Path = d, Location = "AppData\\Local\\Packages", Size = m.Size, LastModified = m.Newest,
                    Confidence = ms ? Confidence.Medium : Confidence.High,
                    Reason = "Artık kurulu olmayan bir Store uygulamasının verileri",
                });
            }
        }

        // 3) Steam'de sahipsiz oyun klasörleri
        if (games != null)
            foreach (var orphan in games.Orphans)
            {
                ct.ThrowIfCancellationRequested();
                var m = FsScan.Measure(orphan.Path, ct);
                items.Add(new LeftoverItem
                {
                    Kind = LeftoverKind.SteamOrphan, Path = orphan.Path, Location = "Steam kütüphanesi", Size = m.Size,
                    LastModified = m.Newest, Confidence = Confidence.Medium, Reason = orphan.Reason, NeedsAdmin = NeedsAdminToWrite(orphan.Path),
                });
            }

        // 4) Bozuk kısayollar ve boş Başlat menüsü klasörleri
        progress?.Report("Kısayollar denetleniyor…");
        items.AddRange(Sta.Run(() => ScanShortcuts(ct)).GetAwaiter().GetResult());

        // 5) Kaldırıcısı silinmiş bozuk program kayıtları
        foreach (var a in apps.Where(a => a.Source == AppSource.Win32))
        {
            if (a.IsMsi) continue;
            var cmd = AppInventory.SplitCommand(a.UninstallString);
            if (cmd == null || !System.IO.Path.IsPathRooted(cmd.Value.Exe) || File.Exists(cmd.Value.Exe)) continue;
            if (a.InstallLocation != null && Directory.Exists(a.InstallLocation)) continue;
            items.Add(new LeftoverItem
            {
                Kind = LeftoverKind.BrokenUninstallEntry, Path = $"{a.Name}", Location = HiveLabel(a.Hive),
                Confidence = Confidence.High, Hive = a.Hive, SubKey = RealUninstallKey(a),
                Reason = "Program silinmiş ama Programlar listesinde kaydı duruyor (kaldırıcı dosyası yok)",
                NeedsAdmin = a.Hive == RegistryHive.LocalMachine,
            });
        }

        foreach (var i in items) i.IsSelected = i.Confidence == Confidence.High && i.CanAct;
        return items.OrderByDescending(i => i.Confidence == Confidence.High).ThenByDescending(i => i.Size).ToList();
    }

    private static bool SubfolderMatches(string dir, Ownership own)
    {
        try
        {
            int n = 0;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (own.OwnsPath(sub) || own.MatchesName(System.IO.Path.GetFileName(sub))) return true;
                if (++n > 50) break;
            }
        }
        catch { return true; } // okunamıyorsa dokunma
        return false;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); } catch { return Array.Empty<string>(); }
    }

    private static DateTime SafeWriteTime(string d)
    {
        try { return Directory.GetLastWriteTime(d); } catch { return DateTime.Now; }
    }

    private static bool NeedsAdminToWrite(string path) =>
        PathUtil.IsSameOrUnder(path, KnownFolders.ProgramFiles) || PathUtil.IsSameOrUnder(path, KnownFolders.ProgramFilesX86) ||
        PathUtil.IsSameOrUnder(path, KnownFolders.ProgramData);

    private static string HiveLabel(RegistryHive h) => h == RegistryHive.LocalMachine ? "Tüm kullanıcılar (HKLM)" : "Bu kullanıcı (HKCU)";

    public static string RealUninstallKey(InstalledApp a)
    {
        var baseKey = a.Hive == RegistryHive.LocalMachine && a.View == RegistryView.Registry32
            ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        return baseKey + "\\" + a.KeyName;
    }

    private static List<LeftoverItem> ScanShortcuts(CancellationToken ct)
    {
        var items = new List<LeftoverItem>();
        var userRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs) };
        var commonRoots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) };
        foreach (var (roots, admin) in new[] { (userRoots, false), (commonRoots, true) })
        {
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                IEnumerable<string> links;
                try { links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
                catch { continue; }
                foreach (var lnk in links)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Native.GetShortcutTarget(lnk);
                    if (string.IsNullOrWhiteSpace(target) || target.StartsWith(@"\\")) continue;
                    if (target.Length < 3 || target[1] != ':') continue;
                    if (!Directory.Exists(System.IO.Path.GetPathRoot(target))) continue; // takılı olmayan sürücü
                    if (File.Exists(target) || Directory.Exists(target)) continue;
                    // 64 bit süreçte yönlendirme farkları: Program Files (x86) karşılığını da dene
                    var alt = target.Replace(@"\Program Files (x86)\", @"\Program Files\", StringComparison.OrdinalIgnoreCase);
                    if (alt != target && File.Exists(alt)) continue;
                    items.Add(new LeftoverItem
                    {
                        Kind = LeftoverKind.BrokenShortcut, Path = lnk, Location = admin ? "Ortak kısayollar" : "Masaüstü / Başlat menüsü",
                        Confidence = Confidence.High, NeedsAdmin = admin, LastModified = SafeWriteTime(lnk),
                        Reason = "Hedefi artık yok: " + target,
                    });
                }
                // Başlat menüsündeki boş klasörler
                if (root.EndsWith("Programs", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                        {
                            if (StartMenuSystemFolders.Contains(System.IO.Path.GetFileName(d)) || !SafetyGuard.CanDelete(d)) continue;
                            if (Directory.EnumerateFileSystemEntries(d).Any()) continue;
                            items.Add(new LeftoverItem
                            {
                                Kind = LeftoverKind.EmptyFolder, Path = d, Location = "Başlat menüsü", Reason = "Boş Başlat menüsü klasörü",
                                Confidence = Confidence.High, NeedsAdmin = admin, LastModified = SafeWriteTime(d),
                            });
                        }
                    }
                    catch { }
                }
            }
        }
        return items;
    }

    // ------------------------------------------------------------------ Kaldırma sonrası hedefli tarama

    /// <summary>Kaldırılan bir programdan geriye kalanları arar (klasörler, kayıt defteri, kısayollar).</summary>
    public static List<LeftoverItem> ScanForApp(InstalledApp removed, IReadOnlyList<InstalledApp> remaining, IReadOnlyList<StorePackage> packages,
        GameScanResult? games, CancellationToken ct)
    {
        var own = Ownership.Build(remaining.Where(a => a.Id != removed.Id).ToList(), packages, games);
        var items = new List<LeftoverItem>();
        var seen = new HashSet<string>(PathUtil.Cmp);

        string full = Ownership.CleanName(removed.Name);
        var words = Ownership.Words(removed.Name).Where(w => w.Length >= 5 && !own.Tokens.Contains(w)).ToHashSet();
        string pub = Ownership.CleanPublisher(removed.Publisher);
        bool pubIsUnique = pub.Length >= 3 && !remaining.Any(a => a.Id != removed.Id && Ownership.CleanPublisher(a.Publisher) == pub);

        // Kurulum klasörünün adı (ör. "Program Files\Discord" → AppData\Discord) güçlü bir işarettir
        string installLeaf = "";
        if (removed.InstallLocation != null)
        {
            var dir = removed.InstallLocation.TrimEnd('\\');
            installLeaf = PathUtil.Simplify(System.IO.Path.GetFileName(dir));
            // "Edge\Application", "VALORANT\live" gibi genel adlarda bir üst klasörün adı kullanılır
            if (GenericInstallLeaves.Contains(installLeaf))
                installLeaf = PathUtil.Simplify(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir) ?? ""));
        }
        if (installLeaf.Length < 4 || own.Tokens.Contains(installLeaf)) installLeaf = "";

        bool NameHit(string folder, out Confidence conf)
        {
            var s = PathUtil.Simplify(folder);
            conf = Confidence.High;
            if (s.Length < 3) return false;
            if (s == full || (installLeaf.Length > 0 && s == installLeaf)) return true;
            conf = Confidence.Medium;
            if (words.Contains(s)) return true;
            if (pubIsUnique && s == pub) return true;
            if (full.Length >= 6 && (s.StartsWith(full, StringComparison.Ordinal) || full.StartsWith(s, StringComparison.Ordinal)) && s.Length >= 5) return true;
            return false;
        }

        void AddFolder(string dir, string label, Confidence conf, string reason, bool admin)
        {
            if (!seen.Add(dir) || !SafetyGuard.CanDelete(dir) || Deleter.IsLink(new DirectoryInfo(dir))) return;
            if (own.OwnsPath(dir)) return;
            var m = FsScan.Measure(dir, ct);
            items.Add(new LeftoverItem
            {
                Kind = m.Files == 0 && m.Dirs == 0 ? LeftoverKind.EmptyFolder : LeftoverKind.Folder,
                Path = dir, Location = label, Size = m.Size, LastModified = m.Newest, Confidence = conf, Reason = reason, NeedsAdmin = admin,
            });
        }

        if (removed.InstallLocation != null && Directory.Exists(removed.InstallLocation))
            AddFolder(removed.InstallLocation, "Kurulum klasörü", Confidence.High, "Programın kurulum klasörü hâlâ duruyor", NeedsAdminToWrite(removed.InstallLocation));

        foreach (var (root, label, admin) in Roots())
        {
            foreach (var d in SafeDirs(root))
            {
                ct.ThrowIfCancellationRequested();
                var leaf = System.IO.Path.GetFileName(d);
                if (SystemNames.Contains(leaf)) continue;
                if (NameHit(leaf, out var conf))
                {
                    AddFolder(d, label, conf, "Kaldırılan programın adını taşıyor", admin);
                    continue;
                }
                // Yayıncı klasörü altında program klasörü: Roaming\Yayıncı\Program
                if (pub.Length >= 3 && PathUtil.Simplify(leaf) == pub)
                    foreach (var sub in SafeDirs(d))
                        if (NameHit(System.IO.Path.GetFileName(sub), out var c2))
                            AddFolder(sub, label + "\\" + leaf, c2, "Kaldırılan programın adını taşıyor", admin);
            }
        }

        // Kısayollar: kurulum klasörüne işaret eden ya da adı eşleşen
        items.AddRange(Sta.Run(() =>
        {
            var list = new List<LeftoverItem>();
            var roots = new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
                (Environment.GetFolderPath(Environment.SpecialFolder.Programs), false),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), true),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true),
            };
            foreach (var (root, admin) in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    var target = Native.GetShortcutTarget(lnk) ?? "";
                    bool inside = removed.InstallLocation != null && target.Length > 0 && PathUtil.IsSameOrUnder(PathUtil.Normalize(target), removed.InstallLocation);
                    bool broken = target.Length > 2 && target[1] == ':' && !File.Exists(target) && !Directory.Exists(target);
                    bool named = NameHit(System.IO.Path.GetFileNameWithoutExtension(lnk), out _);
                    if (inside || (named && broken))
                        list.Add(new LeftoverItem
                        {
                            Kind = LeftoverKind.BrokenShortcut, Path = lnk, Location = admin ? "Ortak kısayollar" : "Masaüstü / Başlat menüsü",
                            Confidence = Confidence.High, NeedsAdmin = admin, Reason = "Kaldırılan programın kısayolu",
                        });
                }
            }
            return list;
        }).GetAwaiter().GetResult());

        // Kayıt defteri: Software\Yayıncı\Program ve Software\Program
        foreach (var (hive, basePath, admin) in new[]
        {
            (RegistryHive.CurrentUser, @"Software", false),
            (RegistryHive.LocalMachine, @"SOFTWARE", true),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node", true),
        })
        {
            try
            {
                using var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var sw = b.OpenSubKey(basePath);
                if (sw == null) continue;
                foreach (var name in sw.GetSubKeyNames())
                {
                    if (SystemNames.Contains(name) || RegistrySystemKeys.Contains(name)) continue;
                    var s = PathUtil.Simplify(name);
                    if (NameHit(name, out var conf))
                        AddReg(items, hive, basePath + "\\" + name, conf, admin);
                    else if (pub.Length >= 3 && s == pub)
                    {
                        using var pk = sw.OpenSubKey(name);
                        if (pk == null) continue;
                        var subs = pk.GetSubKeyNames();
                        foreach (var sub in subs)
                            if (NameHit(sub, out var c2)) AddReg(items, hive, basePath + "\\" + name + "\\" + sub, c2, admin);
                        if (pubIsUnique && subs.All(x => NameHit(x, out _)))
                            AddReg(items, hive, basePath + "\\" + name, Confidence.Medium, admin);
                    }
                }
            }
            catch { }
        }

        if (removed.Source == AppSource.Win32 && AppInventory.RegistryEntryExists(removed))
            items.Add(new LeftoverItem
            {
                Kind = LeftoverKind.BrokenUninstallEntry, Path = removed.Name, Location = HiveLabel(removed.Hive), Hive = removed.Hive,
                SubKey = RealUninstallKey(removed), Confidence = Confidence.Medium, NeedsAdmin = removed.Hive == RegistryHive.LocalMachine,
                Reason = "Programlar listesindeki kaydı hâlâ duruyor",
            });

        foreach (var i in items) i.IsSelected = i.Confidence == Confidence.High && i.CanAct;
        return items.OrderBy(i => i.Confidence).ThenByDescending(i => i.Size).ToList();
    }

    private static readonly HashSet<string> GenericInstallLeaves = new(StringComparer.Ordinal)
    {
        "application", "program", "launcher", "client", "current", "game", "games", "bin", "app", "main", "latest", "stable",
        "release", "live", "default", "x64", "x86", "win64", "files", "install", "installation",
    };

    private static readonly HashSet<string> RegistrySystemKeys =new(StringComparer.OrdinalIgnoreCase)
    {
        "Classes", "Policies", "WOW6432Node", "Clients", "RegisteredApplications", "ODBC", "Wow6432Node", "Khronos", "DefaultUserEnvironment",
        "AppDataLow", "System", "Partner", "OEM", "Setup", "Wisp", "SyncEngines", "Mozilla Plugins",
    };

    private static void AddReg(List<LeftoverItem> items, RegistryHive hive, string subKey, Confidence conf, bool admin)
    {
        var prefix = hive == RegistryHive.LocalMachine ? "HKLM\\" : "HKCU\\";
        if (items.Any(i => i.IsRegistry && i.Hive == hive && (i.SubKey!.Equals(subKey, StringComparison.OrdinalIgnoreCase) || subKey.StartsWith(i.SubKey + "\\", StringComparison.OrdinalIgnoreCase))))
            return;
        items.Add(new LeftoverItem
        {
            Kind = LeftoverKind.RegistryKey, Path = prefix + subKey, Location = HiveLabel(hive), Hive = hive, SubKey = subKey,
            Confidence = conf, NeedsAdmin = admin, Reason = "Kaldırılan programın ayar kaydı",
        });
    }

    // ------------------------------------------------------------------ Silme

    public static async Task<DeleteStats> DeleteAsync(IReadOnlyList<LeftoverItem> items, CancellationToken ct = default)
    {
        var st = new DeleteStats();
        var files = items.Where(i => !i.IsRegistry && i.CanAct).Select(i => new DeleteItem(i.Path, i.Size)).ToList();
        if (files.Count > 0) st.Merge(await Deleter.DeleteUserItemsAsync("Kalıntı temizliği", files, null, ct));
        foreach (var r in items.Where(i => i.IsRegistry && i.CanAct))
        {
            var (ok, err) = DeleteRegistryKey(r.Hive, r.SubKey!);
            if (ok) st.Items++; else st.AddError($"{r.Path}: {err}");
            ActivityLog.Write("Kayıt defteri kalıntısı silindi", (r.Hive == RegistryHive.LocalMachine ? "HKLM\\" : "HKCU\\") + r.SubKey, 0, ok ? 1 : 0, ok ? 0 : 1, "Yedekli silme");
        }
        foreach (var i in items.Where(i => !i.CanAct)) st.AddError($"{i.Path}: yönetici izni gerekiyor");
        return st;
    }

    /// <summary>Kayıt defteri anahtarını önce .reg dosyasına yedekler, sonra siler.</summary>
    public static (bool Ok, string Error) DeleteRegistryKey(RegistryHive hive, string subKey)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RegistryBackupDir);
            var safeName = Regex.Replace(subKey, @"[^\w\-]+", "_");
            if (safeName.Length > 80) safeName = safeName[^80..];
            var file = System.IO.Path.Combine(AppPaths.RegistryBackupDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}.reg");
            var root = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
            var psi = new ProcessStartInfo("reg.exe", $"export \"{root}\\{subKey}\" \"{file}\" /y")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi)!)
            {
                p.WaitForExit(15000);
                if (p.ExitCode != 0 || !File.Exists(file)) return (false, "Yedek alınamadı, silme iptal edildi");
            }
            using var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            b.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
