using System.Runtime.InteropServices;

namespace TemizlikPaneli.Core;

/// <summary>Bilinen klasörler (taşınmış klasörler dahil gerçek konumlar).</summary>
public static class KnownFolders
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static string Get(string guid, string fallback)
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid(guid), 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        return fallback;
    }

    public static string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Roaming { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string Local { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string LocalLow { get; } = Get("A520A1A4-1780-4FF6-BD18-167343C5AF16", Path.Combine(UserProfile, "AppData", "LocalLow"));
    public static string Downloads { get; } = Get("374DE290-123F-4565-9164-39C4925E467B", Path.Combine(UserProfile, "Downloads"));
    public static string SavedGames { get; } = Get("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4", Path.Combine(UserProfile, "Saved Games"));
    public static string ProgramData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string ProgramFiles { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static string ProgramFilesX86 { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public static string Windows { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string SystemDrive { get; } = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
    public static string Temp { get; } = PathUtil.Normalize(Path.GetTempPath());
    public static string UsersRoot { get; } = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "C:\\Users";
}

/// <summary>
/// Silme işlemlerinin son güvenlik kapısı. Her silme bu sınıftan geçer.
/// Sistem klasörleri, sürücü kökleri, ana kullanıcı klasörleri ve bunların ataları asla silinemez.
/// </summary>
public static class SafetyGuard
{
    private static readonly HashSet<string> Exact = new(PathUtil.Cmp);
    private static readonly List<string> Trees = new();
    private static readonly List<string> AllowedInTrees = new();
    private static readonly List<string> UserExclusions = new();
    private static readonly object Gate = new();

    private static readonly HashSet<string> RootSystemFiles = new(PathUtil.Cmp)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT", "BOOTSECT.BAK"
    };

    static SafetyGuard()
    {
        foreach (var d in DriveInfo.GetDrives()) AddExact(d.Name);

        Environment.SpecialFolder[] special =
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.Programs, Environment.SpecialFolder.Startup,
            Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonStartup,
            Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonDocuments,
            Environment.SpecialFolder.Templates, Environment.SpecialFolder.Favorites, Environment.SpecialFolder.Fonts,
            Environment.SpecialFolder.Recent, Environment.SpecialFolder.SendTo, Environment.SpecialFolder.InternetCache,
            Environment.SpecialFolder.Cookies, Environment.SpecialFolder.History,
        };
        foreach (var f in special) AddExact(Environment.GetFolderPath(f));

        var up = KnownFolders.UserProfile;
        AddExact(KnownFolders.UsersRoot);
        AddExact(Path.Combine(KnownFolders.UsersRoot, "Public"));
        AddExact(Path.Combine(KnownFolders.UsersRoot, "Default"));
        AddExact(Path.Combine(up, "AppData"));
        AddExact(KnownFolders.LocalLow);
        AddExact(KnownFolders.Downloads);
        AddExact(KnownFolders.SavedGames);
        AddExact(KnownFolders.Temp);
        AddExact(Path.Combine(KnownFolders.Local, "Temp"));
        AddExact(Path.Combine(KnownFolders.Local, "Programs"));
        AddExact(Path.Combine(KnownFolders.Local, "Packages"));
        AddExact(Path.Combine(KnownFolders.Local, "Microsoft"));
        AddExact(Path.Combine(KnownFolders.Roaming, "Microsoft"));
        AddExact(Path.Combine(up, "OneDrive"));
        var od = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(od)) AddExact(od);

        var win = KnownFolders.Windows;
        var sys = KnownFolders.SystemDrive;
        AddExact(Path.Combine(win, "Temp"));
        AddExact(Path.Combine(win, "SoftwareDistribution", "Download"));

        AddTree(win);
        AddTree(Path.Combine(KnownFolders.ProgramFiles, "WindowsApps"));
        AddTree(Path.Combine(KnownFolders.ProgramFiles, "Windows Defender"));
        AddTree(Path.Combine(KnownFolders.ProgramFiles, "Windows Defender Advanced Threat Protection"));
        AddTree(Path.Combine(KnownFolders.ProgramFiles, "Windows NT"));
        AddTree(Path.Combine(KnownFolders.ProgramFiles, "Windows Security"));
        AddTree(Path.Combine(KnownFolders.ProgramData, "Microsoft"));
        AddTree(Path.Combine(KnownFolders.Local, "Microsoft", "WindowsApps"));
        AddTree(Path.Combine(sys, "System Volume Information"));
        AddTree(Path.Combine(sys, "$Recycle.Bin"));
        AddTree(Path.Combine(sys, "Recovery"));
        AddTree(Path.Combine(sys, "Boot"));
        AddTree(Path.Combine(sys, "EFI"));
        AddTree(Path.Combine(sys, "Config.Msi"));
        AddTree(Path.Combine(sys, "Documents and Settings"));

        AddAllowed(Path.Combine(win, "Temp"));
        AddAllowed(Path.Combine(win, "SoftwareDistribution", "Download"));
        AddAllowed(Path.Combine(win, "SoftwareDistribution", "DeliveryOptimization"));
        AddAllowed(Path.Combine(win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"));
        AddAllowed(Path.Combine(win, "Logs", "CBS"));
        AddAllowed(Path.Combine(win, "Logs", "DISM"));
        AddAllowed(Path.Combine(win, "LiveKernelReports"));
        AddAllowed(Path.Combine(win, "Minidump"));
        AddAllowed(Path.Combine(win, "MEMORY.DMP"));
        AddAllowed(Path.Combine(KnownFolders.ProgramData, "Microsoft", "Windows", "WER"));

        // Uygulamanın kendisi
        var self = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (!string.IsNullOrEmpty(self)) AddTree(self);
        AddTree(AppPaths.DataDir);
    }

    private static void AddExact(string? p) { var n = PathUtil.Normalize(p ?? ""); if (n.Length > 0) lock (Gate) Exact.Add(n); }
    private static void AddTree(string? p) { var n = PathUtil.Normalize(p ?? ""); if (n.Length > 0) lock (Gate) Trees.Add(n); }
    private static void AddAllowed(string p) { var n = PathUtil.Normalize(p); if (n.Length > 0) lock (Gate) AllowedInTrees.Add(n); }

    /// <summary>Oyun kütüphanesi kökü gibi çalışma anında bulunan önemli klasörleri korumaya ekler.</summary>
    public static void ProtectFolder(string path) => AddExact(path);

    public static void SetUserExclusions(IEnumerable<string> paths)
    {
        lock (Gate)
        {
            UserExclusions.Clear();
            foreach (var p in paths) { var n = PathUtil.Normalize(p); if (n.Length > 0) UserExclusions.Add(n); }
        }
    }

    public static bool CanDelete(string path) => CanDelete(path, out _);

    public static bool CanDelete(string path, out string reason)
    {
        var p = PathUtil.Normalize(path);
        if (p.Length == 0) { reason = "Geçersiz yol"; return false; }
        if (PathUtil.IsDriveRoot(p)) { reason = "Sürücü kökü silinemez"; return false; }

        lock (Gate)
        {
            if (Exact.Contains(p)) { reason = "Korunan sistem veya kullanıcı klasörü"; return false; }
            foreach (var e in Exact)
                if (PathUtil.IsStrictlyUnder(e, p)) { reason = "İçinde korunan bir klasör var: " + e; return false; }

            foreach (var t in Trees)
            {
                if (PathUtil.IsStrictlyUnder(t, p)) { reason = "İçinde korunan bir sistem klasörü var"; return false; }
                if (PathUtil.IsSameOrUnder(p, t))
                {
                    bool allowed = false;
                    foreach (var a in AllowedInTrees)
                        if (PathUtil.IsSameOrUnder(p, a)) { allowed = true; break; }
                    if (!allowed) { reason = "Windows'un korunan bir parçası"; return false; }
                }
            }

            foreach (var x in UserExclusions)
                if (PathUtil.IsSameOrUnder(p, x) || PathUtil.IsStrictlyUnder(x, p)) { reason = "Hariç tutulanlar listesinde"; return false; }
        }

        var dir = Path.GetDirectoryName(p);
        if (dir != null && PathUtil.IsDriveRoot(dir) && RootSystemFiles.Contains(Path.GetFileName(p)))
        { reason = "Windows sistem dosyası"; return false; }

        reason = "";
        return true;
    }
}

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCTerminali");

    /// <summary>Eski adla ("TemizlikPaneli") oluşturulmuş ayar, geçmiş ve araç klasörünü yeni adına taşır.</summary>
    public static void MigrateOldData()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TemizlikPaneli");
        try
        {
            if (!Directory.Exists(old)) return;
            if (!Directory.Exists(DataDir)) { Directory.Move(old, DataDir); return; }

            // İki klasör de varsa birleştir: geçmiş kayıtları eklenir, yedekler taşınır, ayarlar yoksa alınır
            Directory.CreateDirectory(DataDir);
            var oldLog = Path.Combine(old, "islem-gecmisi.jsonl");
            if (File.Exists(oldLog))
            {
                var existing = File.Exists(LogFile) ? new HashSet<string>(File.ReadAllLines(LogFile)) : new HashSet<string>();
                var add = File.ReadAllLines(oldLog).Where(l => l.Trim().Length > 0 && !existing.Contains(l)).ToList();
                if (add.Count > 0)
                {
                    var all = (File.Exists(LogFile) ? File.ReadAllLines(LogFile) : Array.Empty<string>()).Concat(add)
                        .Where(l => l.Trim().Length > 0)
                        .OrderBy(l => l, StringComparer.Ordinal); // satırlar "Time" ile başladığından zamana göre sıralanır
                    File.WriteAllLines(LogFile, all);
                }
            }
            var oldSettings = Path.Combine(old, "ayarlar.json");
            if (File.Exists(oldSettings) && !File.Exists(SettingsFile)) File.Copy(oldSettings, SettingsFile);
            var oldBackups = Path.Combine(old, "KayitYedekleri");
            if (Directory.Exists(oldBackups))
            {
                Directory.CreateDirectory(RegistryBackupDir);
                foreach (var f in Directory.GetFiles(oldBackups))
                {
                    var dest = Path.Combine(RegistryBackupDir, Path.GetFileName(f));
                    if (!File.Exists(dest)) File.Move(f, dest);
                }
            }
            Directory.Delete(old, recursive: true); // araçlar yeni klasörde zaten var
        }
        catch { }
    }
    public static string LogFile => Path.Combine(DataDir, "islem-gecmisi.jsonl");
    public static string SettingsFile => Path.Combine(DataDir, "ayarlar.json");
    public static string RegistryBackupDir => Path.Combine(DataDir, "KayitYedekleri");
}
