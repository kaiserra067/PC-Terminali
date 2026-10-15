using System.Diagnostics;
using System.Text;

namespace TemizlikPaneli.Core;

/// <summary>
/// Geliştirme amaçlı öz test: tarayıcıları çalıştırır, sonuçları bir metin dosyasına yazar.
/// Gerçek sistemde HİÇBİR ŞEY silmez; silme testleri yalnızca verilen deneme klasöründe yapılır.
/// </summary>
public static class SelfTest
{
    public static async Task RunAsync(string outFile, string? sandbox)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); try { File.WriteAllText(outFile, sb.ToString()); } catch { } }
        var ct = CancellationToken.None;
        var sw = Stopwatch.StartNew();
        W($"Öz test {DateTime.Now}  yönetici={Admin.IsElevated}");

        // 1) Güvenlik kapısı
        W("\n== Güvenlik kapısı ==");
        string[] probes =
        {
            @"C:\", @"C:\Windows", @"C:\Windows\System32\drivers", @"C:\Windows\Temp", @"C:\Windows\Temp\x.tmp",
            @"C:\Windows\SoftwareDistribution", @"C:\Windows\SoftwareDistribution\Download\abc", @"C:\Program Files",
            @"C:\Program Files\WindowsApps\x", KnownFolders.UserProfile, Path.Combine(KnownFolders.UserProfile, "AppData"),
            KnownFolders.Temp, Path.Combine(KnownFolders.Temp, "a.tmp"), KnownFolders.Downloads, Path.Combine(KnownFolders.Downloads, "kur.exe"),
            @"C:\Users", @"C:\pagefile.sys", @"C:\AMD\x", Path.Combine(KnownFolders.Local, "Discord"), Path.Combine(KnownFolders.Roaming, "Microsoft"),
            @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive\x", @"C:\ProgramData\Microsoft\Crypto",
        };
        foreach (var p in probes) W($"  {(SafetyGuard.CanDelete(p, out var why) ? "İZİN " : "RED  ")} {p}  {why}");

        // 2) Silme motoru (yalnızca deneme klasöründe)
        if (sandbox != null)
        {
            W("\n== Silme motoru (deneme klasörü) ==");
            try { W(DeleteEngineTest(sandbox)); } catch (Exception ex) { W("  HATA: " + ex); }
        }

        // 3) Programlar
        W("\n== Programlar ==");
        sw.Restart();
        var win32 = AppInventory.ReadWin32Apps();
        W($"  Masaüstü programı: {win32.Count} ({sw.ElapsedMilliseconds} ms)");
        sw.Restart();
        var pkgs = await StoreApps.GetAllPackagesAsync(ct);
        var store = StoreApps.ToApps(pkgs);
        W($"  Store paketi: {pkgs.Count}, listelenecek: {store.Count} ({sw.ElapsedMilliseconds} ms)");
        foreach (var s in store.Take(12)) W($"    {s.Name} | {s.Publisher} | logo={(s.LogoPath != null ? "var" : "yok")}");

        // 4) Oyunlar
        W("\n== Oyunlar ==");
        sw.Restart();
        var games = GameInventory.Scan(win32, pkgs);
        GameInventory.MeasureSizes(games.Games, ct);
        W($"  {games.Games.Count} oyun, {games.Orphans.Count} sahipsiz klasör ({sw.ElapsedMilliseconds} ms)");
        foreach (var g in games.Games.OrderByDescending(g => g.TotalSize))
            W($"    [{g.Platform}] {g.Name} | {g.SizeText} {g.DetailText} | {g.InstallDir} | kapak={(g.CoverPath != null ? "var" : "yok")} simge={(g.IconSource != null ? "var" : "yok")} {g.LastPlayedText}");
        foreach (var o in games.Orphans) W($"    SAHİPSİZ: {o.Path}");

        sw.Restart();
        AppInventory.MeasureSizes(win32.Concat(store), games.GameFolders, ct);
        W($"\n  Program boyutları ölçüldü ({sw.ElapsedMilliseconds} ms). En büyük 25:");
        foreach (var a in win32.Concat(store).OrderByDescending(a => a.TotalSize).Take(25))
            W($"    {a.Name} | {a.SizeText} | {a.SourceText} | {a.InstallLocation}{(a.LocationInferred ? " (tahmin)" : "")}");

        // 5) Gereksiz dosyalar (yalnızca tarama)
        W("\n== Gereksiz dosyalar (tarama) ==");
        sw.Restart();
        var cats = JunkCatalog.Build(games);
        foreach (var c in cats)
        {
            JunkCatalog.ScanCategory(c, ct);
            W($"  [{c.Group}] {c.Name}: {c.SizeText} ({c.FilesText}) seçili={c.IsSelected} engelli={c.Blocked} {c.Warning}");
        }
        W($"  Toplam: {Fmt.Size(cats.Where(c => !c.Blocked).Sum(c => c.Size))} ({sw.ElapsedMilliseconds} ms)");

        // 6) Kalıntılar (yalnızca tarama)
        W("\n== Kalıntılar (tarama) ==");
        sw.Restart();
        var left = LeftoverScanner.ScanAll(win32.Concat(store).ToList(), pkgs, games, null, ct);
        W($"  {left.Count} öğe ({sw.ElapsedMilliseconds} ms)");
        foreach (var l in left) W($"    [{l.ConfidenceText}] {l.KindText} | {l.SizeText} | {l.Path} | {l.Reason} | seçili={l.IsSelected}");

        // 7) Disk analizi (kullanıcı klasörü)
        W("\n== Disk analizi ==");
        sw.Restart();
        var ts = new TreeScanner();
        var root = ts.Scan(KnownFolders.UserProfile, ct);
        W($"  {KnownFolders.UserProfile}: {Fmt.Size(root.Size)}, {Fmt.Count(root.FileCount)} dosya, {Fmt.Count(root.DirCount)} klasör ({sw.ElapsedMilliseconds} ms)");
        foreach (var c in root.Children.Take(12)) W($"    {c.Name}: {Fmt.Size(c.Size)}{(c.IsLink ? " (bağlantı)" : "")}{(c.Inaccessible ? " (erişilemedi)" : "")}");
        for (int k = 0; k < FileKinds.Count; k++) W($"    tür {FileKinds.Names[k]}: {Fmt.Size(root.KindSizes[k])}");
        foreach (var f in ts.TopFiles().Take(8)) W($"    büyük: {Fmt.Size(f.Size)} {f.Path}");
        foreach (var e in ts.ExtensionStats().Take(8)) W($"    uzantı {e.Ext}: {Fmt.Size(e.Size)} ({e.Count})");

        W($"\nBitti.");
    }

    /// <summary>
    /// Kaldırma ve kalıntı mantığını sahte bir uygulamayla uçtan uca dener (yalnızca HKCU ve kullanıcı klasörlerinde,
    /// adı "ZzTemizlikTest" ile başlayan test öğeleri oluşturur ve sonunda hepsini siler).
    /// </summary>
    public static async Task RunUninstallTestAsync(string outFile)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); try { File.WriteAllText(outFile, sb.ToString()); } catch { } }
        const string key = "ZzTemizlikTestApp";
        const string name = "Zz Temizlik Test Uygulaması";
        const string pub = "ZzTestYayinci";
        string install = Path.Combine(KnownFolders.Local, key);
        string roaming = Path.Combine(KnownFolders.Roaming, key);
        string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + key;
        string settingsKey = $@"Software\{pub}\{key}";
        AppSettings.Current.UseRecycleBin = false; // test öğeleri Geri Dönüşüm Kutusu'nu doldurmasın (kaydedilmez)
        try
        {
            Directory.CreateDirectory(Path.Combine(install, "bin"));
            File.WriteAllText(Path.Combine(install, "bin", "app.dll"), new string('x', 4096));
            Directory.CreateDirectory(roaming);
            File.WriteAllText(Path.Combine(roaming, "settings.json"), "{}");
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(uninstallKey))
            {
                k.SetValue("DisplayName", name);
                k.SetValue("Publisher", pub);
                k.SetValue("DisplayVersion", "1.0");
                k.SetValue("InstallLocation", install);
                k.SetValue("UninstallString", $"\"{Path.Combine(install, "unins000.exe")}\"");
            }
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(settingsKey)) k.SetValue("Ayar", 1);
            W("Test öğeleri oluşturuldu.");

            var apps = AppInventory.ReadWin32Apps();
            var pkgs = await StoreApps.GetAllPackagesAsync();
            var all = apps.Concat(StoreApps.ToApps(pkgs)).ToList();
            var test = apps.FirstOrDefault(a => a.KeyName == key);
            W($"1) Program listesinde bulundu: {test != null}");
            if (test == null) return;
            W($"   Konum: {test.InstallLocation}  kaldırıcı eksik: {Views.UninstallFlow.UninstallerMissing(test)}");

            var games = GameInventory.Scan(apps, pkgs);
            var global = LeftoverScanner.ScanAll(all, pkgs, games, null, CancellationToken.None);
            bool falsePositive = global.Any(i => i.Path.Contains(key, StringComparison.OrdinalIgnoreCase));
            W($"2) Kurulu iken genel taramada yanlışlıkla kalıntı sayıldı mı: {falsePositive} (beklenen: False)");

            var items = LeftoverScanner.ScanForApp(test, all.Where(a => a.Id != test.Id).ToList(), pkgs, games, CancellationToken.None);
            W($"3) Hedefli tarama {items.Count} öğe buldu:");
            foreach (var i in items) W($"   [{i.ConfidenceText}] {i.KindText} {i.Path}  seçili={i.IsSelected}");
            bool foundAll = items.Any(i => PathUtil.Cmp.Equals(i.Path, install)) && items.Any(i => PathUtil.Cmp.Equals(i.Path, roaming))
                            && items.Any(i => i.Kind == LeftoverKind.RegistryKey) && items.Any(i => i.Kind == LeftoverKind.BrokenUninstallEntry);
            W($"   Beklenen dört öğenin hepsi var: {foundAll}");

            var st = await LeftoverScanner.DeleteAsync(items);
            W($"4) Silme: {st.Summary}");
            foreach (var e in st.Errors) W("   hata: " + e);
            using (var k1 = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(uninstallKey))
            using (var k2 = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(settingsKey))
                W($"   Klasörler silindi: {!Directory.Exists(install) && !Directory.Exists(roaming)} · kayıtlar silindi: {k1 == null && k2 == null}");
            var backups = Directory.Exists(AppPaths.RegistryBackupDir) ? Directory.GetFiles(AppPaths.RegistryBackupDir, "*ZzTemizlikTestApp*") : Array.Empty<string>();
            W($"   Kayıt defteri yedekleri: {backups.Length} dosya");
            foreach (var b in backups) { W("   " + Path.GetFileName(b)); File.Delete(b); }
        }
        catch (Exception ex) { W("HATA: " + ex); }
        finally
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(uninstallKey, false); } catch { }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"Software\{pub}", false); } catch { }
            try { if (Directory.Exists(install)) Directory.Delete(install, true); } catch { }
            try { if (Directory.Exists(roaming)) Directory.Delete(roaming, true); } catch { }
            W("Temizlik yapıldı; test öğesi kalmadı.");
        }
    }

    /// <summary>Medya indirici: araçları kurar, örnek bağlantılarla indirme ve dönüştürmeyi dener.</summary>
    public static async Task RunMediaTestAsync(string outFile, string outDir)
    {
        var sb = new StringBuilder();
        void W(string s) { lock (sb) { sb.AppendLine(s); try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        var sw = Stopwatch.StartNew();
        try
        {
            await MediaTools.EnsureAsync(new Progress<string>(s => { if (!s.Contains('%')) W("  araç: " + s); }), CancellationToken.None);
            W($"Araçlar hazır: {MediaTools.Ready} · yt-dlp {await MediaTools.YtDlpVersionAsync()} ({sw.Elapsed.TotalSeconds:0} sn)");
        }
        catch (Exception ex) { W("ARAÇ HATASI: " + ex.Message); return; }

        const string yt = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
        var cases = new (string Name, DownloadOptions O)[]
        {
            ("YouTube video MP4 720p", new() { Url = yt, Format = "mp4", MaxHeight = 720, OutputDir = outDir }),
            ("YouTube video MKV en iyi", new() { Url = yt, Format = "mkv", OutputDir = outDir }),
            ("YouTube ses MP3 320", new() { Url = yt, Audio = true, Format = "mp3", AudioKbps = 320, OutputDir = outDir }),
            ("YouTube Music M4A orijinal", new() { Url = "https://music.youtube.com/watch?v=jNQXAC9IVRw", Audio = true, Format = "m4a-orig", OutputDir = outDir }),
            ("YouTube Music OPUS orijinal", new() { Url = "https://music.youtube.com/watch?v=jNQXAC9IVRw", Audio = true, Format = "opus-orig", OutputDir = outDir }),
            ("X (Twitter) video", new() { Url = "https://x.com/SouthamptonFC/status/1347577658079641604", Format = "mp4", OutputDir = outDir }),
            ("X (Twitter) ses MP3", new() { Url = "https://twitter.com/UltimaShadowX/status/1577719286659006464", Audio = true, Format = "mp3", OutputDir = outDir }),
            ("TikTok video", new() { Url = "https://www.tiktok.com/@tiktok/video/7106594312292453675", Format = "mp4", OutputDir = outDir }),
            ("TikTok bölge engelli", new() { Url = "https://www.tiktok.com/@khaby.lame/video/7151617374318628101", Format = "mp4", OutputDir = outDir }),
            ("YouTube video MOV + H.264", new() { Url = yt, Format = "mov", H264 = true, OutputDir = outDir }),
            ("Instagram Reels", new() { Url = "https://www.instagram.com/reel/Chunk8-jurw/", Format = "mp4", OutputDir = outDir }),
        };
        foreach (var (name, o) in cases)
        {
            var job = new MediaJob { Glyph = "" };
            var t = Stopwatch.StartNew();
            await MediaService.RunDownloadAsync(job, o);
            W($"\n[{name}] {job.State} ({t.Elapsed.TotalSeconds:0} sn) başlık=\"{job.Title}\"\n  durum: {job.Status}");
            foreach (var f in job.Files) W($"  dosya: {Path.GetFileName(f)} ({(File.Exists(f) ? Fmt.Size(new FileInfo(f).Length) : "YOK")})");
            if (job.State != JobState.Done)
                foreach (var l in job.Log.ToString().Split('\n').Where(l => l.Contains("ERROR") || l.Contains("WARNING") || l.Contains("[error]")).Take(4)) W("  > " + l.Trim());
        }

        var mp4 = Directory.GetFiles(outDir, "*.mp4").FirstOrDefault();
        if (mp4 != null)
        {
            foreach (var (name, o) in new (string, ConvertOptions)[]
            {
                ("GIF 15 fps 480 px, 2. saniyeden 5 sn", new() { Input = mp4, ToGif = true, GifFps = 15, GifWidth = 480, Start = 2, Length = 5, OutputDir = outDir }),
                ("Ses MP3 192", new() { Input = mp4, AudioFormat = "mp3", AudioKbps = 192, OutputDir = outDir }),
                ("Ses FLAC", new() { Input = mp4, AudioFormat = "flac", OutputDir = outDir }),
                ("Ses OPUS", new() { Input = mp4, AudioFormat = "opus", AudioKbps = 128, OutputDir = outDir }),
            })
            {
                var job = new MediaJob { Glyph = "" };
                await MediaService.RunConvertAsync(job, o);
                W($"\n[Dönüştür: {name}] {job.State} · {job.Status}");
            }
        }
        W($"\nBitti ({sw.Elapsed.TotalSeconds:0} sn).");
    }

    private static string DeleteEngineTest(string sandbox)
    {
        var sb = new StringBuilder();
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
        var target = Path.Combine(sandbox, "temizlenecek");
        var outside = Path.Combine(sandbox, "dokunulmamali");
        Directory.CreateDirectory(Path.Combine(target, "alt", "derin"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "canary.txt"), "silinmemeli");
        File.WriteAllText(Path.Combine(target, "a.tmp"), new string('x', 1000));
        File.WriteAllText(Path.Combine(target, "alt", "b.tmp"), new string('x', 2000));
        File.WriteAllText(Path.Combine(target, "alt", "derin", "c.tmp"), new string('x', 3000));
        var ro = Path.Combine(target, "salt-okunur.tmp");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var fresh = Path.Combine(target, "yeni.tmp");
        File.WriteAllText(fresh, "yeni");
        foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            if (!f.EndsWith("yeni.tmp")) { File.SetLastWriteTime(f, DateTime.Now.AddDays(-10)); File.SetCreationTime(f, DateTime.Now.AddDays(-10)); }
        foreach (var d in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories))
            Directory.SetCreationTime(d, DateTime.Now.AddDays(-10));
        // Dışarıyı gösteren bir bağlantı (junction): içi asla silinmemeli
        var link = Path.Combine(target, "baglanti");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false };
        Process.Start(psi)!.WaitForExit();
        sb.AppendLine($"  bağlantı oluşturuldu: {Directory.Exists(link)}");

        var st = Deleter.CleanFolder(target, null, true, DateTime.Now.AddDays(-1), CancellationToken.None);
        sb.AppendLine($"  CleanFolder: {st.Summary}");
        sb.AppendLine($"  yeni dosya korundu: {File.Exists(fresh)}");
        sb.AppendLine($"  salt okunur silindi: {!File.Exists(ro)}");
        sb.AppendLine($"  derin klasör silindi: {!Directory.Exists(Path.Combine(target, "alt"))}");
        sb.AppendLine($"  bağlantı yerinde (dokunulmadı): {Directory.Exists(link)}");
        sb.AppendLine($"  canary korundu: {File.Exists(Path.Combine(outside, "canary.txt"))}");

        var st2 = Deleter.DeletePermanent(new[] { new DeleteItem(target) }, null, CancellationToken.None);
        sb.AppendLine($"  DeletePermanent: {st2.Summary}");
        sb.AppendLine($"  hedef silindi: {!Directory.Exists(target)}");
        sb.AppendLine($"  canary hâlâ korunuyor: {File.Exists(Path.Combine(outside, "canary.txt"))}");

        var rb = Path.Combine(sandbox, "TemizlikPaneli_test_dosyasi.txt");
        File.WriteAllText(rb, "Geri Dönüşüm Kutusu testi");
        var st3 = Deleter.RecycleAsync(new[] { new DeleteItem(rb) }, null, CancellationToken.None).GetAwaiter().GetResult();
        sb.AppendLine($"  Geri Dönüşüm Kutusu: {st3.Summary} taşındı={!File.Exists(rb)}");
        var (rbSize, rbCount) = Deleter.QueryRecycleBin();
        sb.AppendLine($"  Geri Dönüşüm Kutusu boyutu: {Fmt.Size(rbSize)} ({rbCount} öğe)");
        return sb.ToString();
    }
}
