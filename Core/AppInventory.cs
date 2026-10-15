using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Microsoft.Win32;

namespace TemizlikPaneli.Core;

public enum AppSource { Win32, Store }

public sealed class InstalledApp : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public AppSource Source { get; init; }

    // Win32
    public string? InstallLocation { get; set; }
    public bool LocationInferred { get; set; }
    public long RegistrySize { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public bool IsMsi { get; init; }
    public string? MsiProductCode { get; init; }
    public string? DisplayIcon { get; init; }
    public RegistryHive Hive { get; init; }
    public RegistryView View { get; init; }
    public string KeyName { get; init; } = "";
    public string KeyPath { get; init; } = "";

    // Store
    public string? PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? LogoPath { get; set; }
    public bool NonRemovable { get; init; }

    private long _size;
    private long _dataSize;
    private bool _measured;
    private ImageSource? _icon;
    private double _fraction;
    private bool _isBusy;

    public long Size { get => _size; set { if (Set(ref _size, value)) { OnPropertyChanged(nameof(TotalSize)); OnPropertyChanged(nameof(SizeText)); } } }
    public long DataSize { get => _dataSize; set { if (Set(ref _dataSize, value)) { OnPropertyChanged(nameof(TotalSize)); OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(SizeDetail)); } } }
    public bool Measured { get => _measured; set { if (Set(ref _measured, value)) { OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(SizeDetail)); } } }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public double Fraction { get => _fraction; set => Set(ref _fraction, value); }
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public long TotalSize => Size + DataSize;
    public string SizeText => TotalSize <= 0 ? "—" : (Measured ? "" : "≈ ") + Fmt.Size(TotalSize);
    public string SizeDetail => Source == AppSource.Store && DataSize > 0 ? $"Uygulama {Fmt.Size(Size)} + veri {Fmt.Size(DataSize)}" : "";
    public string DateText => Fmt.Date(InstallDate);
    public string SourceText => Source == AppSource.Store ? "Microsoft Store" : IsMsi ? "Windows Installer" : "Masaüstü";
    public string MetaText => string.Join("  ·  ", new[] { Publisher, Version, SourceText }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string LocationText => InstallLocation ?? (Source == AppSource.Store ? "" : "Konum bilinmiyor");
    public bool CanUninstall => Source == AppSource.Store ? !NonRemovable : (IsMsi && MsiProductCode != null) || !string.IsNullOrWhiteSpace(UninstallString);
    public string Initial => Name.Length > 0 ? Name[..1].ToUpper(CultureInfo.GetCultureInfo("tr-TR")) : "?";
}

public static class AppInventory
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly Regex GuidRx = new(@"^\{[0-9A-Fa-f\-]{36}\}$", RegexOptions.Compiled);
    private static readonly HashSet<string> SkipReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    { "Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack" };

    /// <summary>Kayıt defterindeki masaüstü programları (Denetim Masası'ndaki liste ile aynı kurallar).</summary>
    public static List<InstalledApp> ReadWin32Apps()
    {
        var result = new List<InstalledApp>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (RegistryHive hive, RegistryView view)[] sources =
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };

        foreach (var (hive, view) in sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(UninstallPath);
                if (root == null) continue;
                foreach (var keyName in root.GetSubKeyNames())
                {
                    if (!seenKeys.Add($"{hive}|{view}|{keyName}")) continue;
                    try
                    {
                        using var k = root.OpenSubKey(keyName);
                        if (k == null) continue;
                        var app = ReadApp(k, keyName, hive, view);
                        if (app == null) continue;
                        if (!seenNames.Add(app.Name + "|" + app.Version)) continue;
                        result.Add(app);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return result;
    }

    private static InstalledApp? ReadApp(RegistryKey k, string keyName, RegistryHive hive, RegistryView view)
    {
        string name = Str(k, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Int(k, "SystemComponent") == 1) return null;
        if (!string.IsNullOrEmpty(Str(k, "ParentKeyName")) || !string.IsNullOrEmpty(Str(k, "ParentDisplayName"))) return null;
        if (SkipReleaseTypes.Contains(Str(k, "ReleaseType"))) return null;

        bool isMsi = Int(k, "WindowsInstaller") == 1;
        string uninstall = Str(k, "UninstallString");
        string? productCode = GuidRx.IsMatch(keyName) ? keyName : null;
        if (string.IsNullOrWhiteSpace(uninstall) && !(isMsi && productCode != null)) return null;

        long estKb = (uint)Int(k, "EstimatedSize");
        string icon = Str(k, "DisplayIcon");
        string loc = PathUtil.Normalize(Str(k, "InstallLocation"));
        bool inferred = false;
        if (loc.Length == 0 || !Directory.Exists(loc) || !IsAcceptableAppFolder(loc))
        {
            loc = InferLocation(icon, uninstall) ?? "";
            inferred = loc.Length > 0;
        }

        return new InstalledApp
        {
            Id = $"{hive}|{view}|{keyName}",
            Name = name.Trim(),
            Publisher = Str(k, "Publisher").Trim(),
            Version = Str(k, "DisplayVersion").Trim(),
            InstallDate = ParseDate(Str(k, "InstallDate")),
            Source = AppSource.Win32,
            InstallLocation = loc.Length > 0 ? loc : null,
            LocationInferred = inferred,
            RegistrySize = estKb * 1024,
            Size = estKb * 1024,
            UninstallString = uninstall,
            QuietUninstallString = Str(k, "QuietUninstallString"),
            IsMsi = isMsi,
            MsiProductCode = productCode,
            DisplayIcon = icon,
            Hive = hive,
            View = view,
            KeyName = keyName,
            KeyPath = UninstallPath + "\\" + keyName,
        };
    }

    private static string Str(RegistryKey k, string name)
    {
        try { return k.GetValue(name) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : ""; } catch { return ""; }
    }

    private static int Int(RegistryKey k, string name)
    {
        try
        {
            return k.GetValue(name) switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var p) => p,
                _ => 0,
            };
        }
        catch { return 0; }
    }

    private static DateTime? ParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "d.M.yyyy", "dd.MM.yyyy" };
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>"C:\yol\a.exe",0 biçimindeki simge yolundan dosya yolunu çıkarır.</summary>
    public static string? IconFile(string? displayIcon, out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var s = displayIcon.Trim();
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out var idx)) { index = idx; s = s[..comma]; }
        s = PathUtil.Normalize(s);
        return s.Length > 0 && File.Exists(s) ? s : null;
    }

    private static string? InferLocation(string icon, string uninstall)
    {
        var candidates = new List<string?> { IconFile(icon, out _) };
        var cmd = SplitCommand(uninstall);
        if (cmd != null && Path.IsPathRooted(cmd.Value.Exe)) candidates.Add(cmd.Value.Exe);
        foreach (var exe in candidates)
        {
            if (exe == null) continue;
            var dir = Path.GetDirectoryName(exe);
            if (dir == null) continue;
            var leaf = Path.GetFileName(dir);
            if (leaf.StartsWith("uninst", StringComparison.OrdinalIgnoreCase) || leaf.Equals("_uninst", StringComparison.OrdinalIgnoreCase))
                dir = Path.GetDirectoryName(dir) ?? dir;
            dir = PathUtil.Normalize(dir);
            if (dir.Length > 0 && Directory.Exists(dir) && IsAcceptableAppFolder(dir)) return dir;
        }
        return null;
    }

    private static readonly string[] SharedFolders =
    {
        Path.Combine(KnownFolders.ProgramData, "Package Cache"),
        Path.Combine(KnownFolders.Windows, "Installer"),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
        Path.Combine(KnownFolders.ProgramData, "Battle.net"),
        Path.Combine(KnownFolders.ProgramData, "Microsoft"),
        KnownFolders.Temp,
    };

    /// <summary>Bir klasörün tek bir programa ait olabilecek bir kurulum klasörü olup olmadığı.</summary>
    public static bool IsAcceptableAppFolder(string dir)
    {
        if (!SafetyGuard.CanDelete(dir)) return false;
        foreach (var s in SharedFolders)
            if (s.Length > 0 && PathUtil.IsSameOrUnder(dir, PathUtil.Normalize(s))) return false;
        return true;
    }

    /// <summary>Komut satırını program yolu ve argümanlara ayırır (tırnaksız ve boşluklu yollar dahil).</summary>
    public static (string Exe, string Args)? SplitCommand(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var s = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 1) return (s[1..end], s[(end + 1)..].Trim());
            return (s.Trim('"'), "");
        }
        int idx = -1;
        while (true)
        {
            idx = s.IndexOf(' ', idx + 1);
            string cand = idx < 0 ? s : s[..idx];
            if (Path.IsPathRooted(cand))
            {
                if (File.Exists(cand)) return (cand, idx < 0 ? "" : s[(idx + 1)..].Trim());
                if (!cand.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(cand + ".exe"))
                    return (cand + ".exe", idx < 0 ? "" : s[(idx + 1)..].Trim());
            }
            if (idx < 0) break;
        }
        int sp = s.IndexOf(' ');
        return sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim());
    }

    /// <summary>Masaüstü programının kendi kaldırıcısını başlatır (gerekirse UAC sorar).</summary>
    public static Process? StartUninstall(InstalledApp app)
    {
        string exe, args;
        if (app.IsMsi && app.MsiProductCode != null)
        {
            exe = "msiexec.exe";
            args = "/x " + app.MsiProductCode;
        }
        else
        {
            var c = SplitCommand(app.UninstallString) ?? throw new InvalidOperationException("Kaldırma komutu bulunamadı.");
            exe = c.Exe;
            args = c.Args;
            if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                args = Regex.Replace(args, @"/I\s*(?=\{)", "/X", RegexOptions.IgnoreCase);
        }
        if (Path.IsPathRooted(exe) && !File.Exists(exe))
            throw new FileNotFoundException("Programın kaldırıcısı diskte yok. Kalıntılar bölümünden bozuk kaydı temizleyebilirsiniz.", exe);

        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
        var dir = Path.IsPathRooted(exe) ? Path.GetDirectoryName(exe) : null;
        if (dir != null && Directory.Exists(dir)) psi.WorkingDirectory = dir;
        return Process.Start(psi);
    }

    /// <summary>Kaldırma kaydı hâlâ duruyor mu?</summary>
    public static bool RegistryEntryExists(InstalledApp app)
    {
        try
        {
            using var b = RegistryKey.OpenBaseKey(app.Hive, app.View);
            using var k = b.OpenSubKey(app.KeyPath);
            return k != null;
        }
        catch { return false; }
    }

    /// <summary>Programlara ait klasör boyutlarını ölçer. excluded: başka yerde sayılan klasörler (ör. oyun kütüphaneleri).</summary>
    public static void MeasureSizes(IEnumerable<InstalledApp> apps, ICollection<string> excluded, CancellationToken ct)
    {
        var list = apps.ToList();
        Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, app =>
        {
            try
            {
                if (app.Source == AppSource.Win32)
                {
                    if (app.InstallLocation == null) return;
                    var m = FsScan.Measure(app.InstallLocation, ct, excluded);
                    if (m.Files == 0 && app.RegistrySize > 0) return;
                    app.Size = m.Size;
                    app.Measured = true;
                }
                else
                {
                    if (app.InstallLocation != null)
                    {
                        var m = FsScan.Measure(app.InstallLocation, ct);
                        app.Size = m.Size;
                    }
                    if (app.PackageFamilyName != null)
                        app.DataSize = FsScan.Measure(Path.Combine(KnownFolders.Local, "Packages", app.PackageFamilyName), ct).Size;
                    app.Measured = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        });
    }
}
