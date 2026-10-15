using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

/// <summary>Bir işlem sürerken gösterilen bekleme penceresi.</summary>
public static class WaitDialog
{
    /// <summary>isDone true olunca ya da kullanıcı "bitti" deyince true; vazgeçerse false döner.</summary>
    public static bool Run(string title, string message, Func<bool> isDone, string? manualText = null, string? cancelText = null)
    {
        var owner = Application.Current.MainWindow;
        var win = new Window
        {
            Title = title, Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner is { IsVisible: true } ? owner : null,
            Background = ThemeManager.Brush("SurfaceBg"), Foreground = ThemeManager.Brush("TextPrimary"),
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13, ShowInTaskbar = false,
        };
        win.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(win);
        bool result = false;
        var panel = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("TextSecondary"), Margin = new Thickness(0, 8, 0, 0), LineHeight = 20 });
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 16, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (cancelText != null)
        {
            var c = new Button { Content = cancelText, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            c.Click += (_, _) => { result = false; win.Close(); };
            buttons.Children.Add(c);
        }
        if (manualText != null)
        {
            var m = new Button { Content = manualText, Style = (Style)Application.Current.FindResource("PrimaryButton") };
            m.Click += (_, _) => { result = true; win.Close(); };
            buttons.Children.Add(m);
        }
        if (buttons.Children.Count > 0) panel.Children.Add(buttons);
        win.Content = panel;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            bool done;
            try { done = isDone(); } catch { done = false; }
            if (done) { result = true; win.Close(); }
        };
        win.Loaded += (_, _) => timer.Start();
        win.Closed += (_, _) => timer.Stop();
        if (manualText == null && cancelText == null) win.Closing += (_, e) => { if (!result) e.Cancel = !SafeDone(isDone); };
        win.ShowDialog();
        return result;
    }

    private static bool SafeDone(Func<bool> f) { try { return f(); } catch { return true; } }

    /// <summary>Bir görev bitene kadar bekleme penceresi gösterir ve sonucunu döndürür.</summary>
    public static async Task<T> WhileAsync<T>(string title, string message, Task<T> task)
    {
        if (!task.IsCompleted) Run(title, message, () => task.IsCompleted);
        return await task;
    }
}

/// <summary>Kaldırma sonrası (ya da zorla kaldırmada) bulunan kalıntıları gösteren pencere.</summary>
public static class LeftoverDialog
{
    public static async Task ShowAsync(string title, string intro, List<LeftoverItem> items)
    {
        var owner = Application.Current.MainWindow;
        var win = new Window
        {
            Title = title, Width = 980, Height = 620, MinWidth = 760, MinHeight = 420,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner is { IsVisible: true } ? owner : null,
            Background = ThemeManager.Brush("WindowBg"), Foreground = ThemeManager.Brush("TextPrimary"),
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13, ShowInTaskbar = false,
        };
        win.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(win);

        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
        string method = AppSettings.Current.UseRecycleBin ? "Geri Dönüşüm Kutusu'na taşınır" : "kalıcı olarak silinir";
        head.Children.Add(new TextBlock
        {
            Text = $"{intro} Seçtiğiniz dosya ve klasörler {method}; kayıt defteri anahtarları silinmeden önce yedeklenir.",
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("TextSecondary"), Margin = new Thickness(0, 6, 0, 12),
        });
        root.Children.Add(head);

        var card = new Border { Style = (Style)Application.Current.FindResource("Card"), Padding = new Thickness(8) };
        var list = new LeftoverList { ItemsSource = items };
        card.Child = list;
        Grid.SetRow(card, 1);
        root.Children.Add(card);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("TextSecondary") };
        void Update()
        {
            var sel = items.Where(i => i.IsSelected).ToList();
            summary.Text = $"{sel.Count} / {items.Count} öğe seçili · {Fmt.Size(sel.Sum(i => i.Size))}";
        }
        list.SelectionChanged += Update;
        Update();
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(btns, Dock.Right);
        var skip = new Button { Content = "Atla", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var del = new Button { Content = "Seçilenleri sil", MinWidth = 130, Style = (Style)Application.Current.FindResource("DangerButton") };
        bool doDelete = false;
        skip.Click += (_, _) => win.Close();
        del.Click += (_, _) => { doDelete = true; win.Close(); };
        btns.Children.Add(skip);
        btns.Children.Add(del);
        footer.Children.Add(btns);
        footer.Children.Add(summary);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        win.Content = root;
        win.ShowDialog();

        var selected = items.Where(i => i.IsSelected).ToList();
        if (!doDelete || selected.Count == 0) return;
        var task = LeftoverScanner.DeleteAsync(selected);
        var st = await WaitDialog.WhileAsync("Siliniyor", "Seçilen kalıntılar siliniyor…", task);
        AnalyzerView.ShowResult(st);
    }
}

public static class UninstallFlow
{
    /// <summary>Kaldırıcı dosyası diskte olmayan (bozuk) masaüstü programı mı?</summary>
    public static bool UninstallerMissing(InstalledApp app)
    {
        if (app.Source != AppSource.Win32 || (app.IsMsi && app.MsiProductCode != null)) return false;
        var c = AppInventory.SplitCommand(app.UninstallString);
        return c != null && Path.IsPathRooted(c.Value.Exe) && !File.Exists(c.Value.Exe);
    }

    public static async Task<bool> UninstallAppAsync(InstalledApp app)
    {
        var inv = await AppState.I.GetInventoryAsync();
        if (app.Source == AppSource.Store)
        {
            if (app.NonRemovable)
            {
                Dialogs.Error("Bu uygulama kaldırılamaz", "Windows bu uygulamayı sistemin parçası olarak işaretlemiş.");
                return false;
            }
            if (!Dialogs.Confirm($"“{app.Name}” kaldırılsın mı?", "Uygulama ve bu kullanıcıya ait verileri kaldırılacak. İsterseniz daha sonra Microsoft Store'dan yeniden yükleyebilirsiniz.", "Kaldır", true))
                return false;
            var (ok, err) = await WaitDialog.WhileAsync("Kaldırılıyor", $"“{app.Name}” kaldırılıyor…", StoreApps.UninstallAsync(app));
            if (!ok)
            {
                Dialogs.Error("Uygulama kaldırılamadı", string.IsNullOrWhiteSpace(err) ? "Windows kaldırma işlemini reddetti." : err);
                return false;
            }
            ActivityLog.Write("Store uygulaması kaldırıldı", app.Name, app.TotalSize, 1, 0, "Remove-AppxPackage");
        }
        else
        {
            if (UninstallerMissing(app)) return await ForceRemoveAsync(app);
            string launcherWarning = LauncherWarning(app, inv);
            if (!Dialogs.Confirm($"“{app.Name}” kaldırılsın mı?",
                    "Programın kendi kaldırma sihirbazı açılacak. Kaldırma bitince geride kalan klasörler, ayarlar ve kısayollar otomatik olarak taranacak." + launcherWarning,
                    "Kaldır", true))
                return false;
            try { AppInventory.StartUninstall(app); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // UAC reddedildi
            catch (Exception ex)
            {
                Dialogs.Error("Kaldırıcı başlatılamadı", ex.Message);
                return false;
            }
            bool finished = WaitDialog.Run($"“{app.Name}” kaldırılıyor",
                "Açılan kaldırma penceresindeki adımları tamamlayın. Program listeden silindiğinde kalıntı taraması kendiliğinden başlar.",
                () => !AppInventory.RegistryEntryExists(app), "Kaldırma bitti, kalıntıları tara", "Vazgeç");
            if (!finished) { AppState.I.Invalidate(); return false; }
            bool stillThere = AppInventory.RegistryEntryExists(app);
            ActivityLog.Write(stillThere ? "Kaldırıcı çalıştırıldı" : "Program kaldırıldı", app.Name, stillThere ? 0 : app.TotalSize, 1, 0, "Programın kaldırıcısı");
        }
        await ScanLeftoversAsync(app, inv, $"“{app.Name}” kaldırıldı", "Program kaldırıldı ama aşağıdakiler geride kaldı.");
        AppState.I.Invalidate();
        return true;
    }

    /// <summary>Oyun platformlarını kaldırırken oyunların da etkileneceğini açıkça söyler.</summary>
    private static string LauncherWarning(InstalledApp app, Inventory inv)
    {
        string? platform = app.Name switch
        {
            var n when n.StartsWith("Steam", StringComparison.OrdinalIgnoreCase) && n.Length <= 6 => "Steam",
            var n when n.StartsWith("Epic Games Launcher", StringComparison.OrdinalIgnoreCase) => "Epic Games",
            var n when n.StartsWith("Riot Client", StringComparison.OrdinalIgnoreCase) => "Riot Games",
            var n when n.StartsWith("Battle.net", StringComparison.OrdinalIgnoreCase) => "Battle.net",
            var n when n.StartsWith("EA app", StringComparison.OrdinalIgnoreCase) => "EA",
            var n when n.StartsWith("Ubisoft Connect", StringComparison.OrdinalIgnoreCase) => "Ubisoft",
            _ => null,
        };
        if (platform == null) return "";
        var games = inv.Games.Games.Where(g => g.Platform == platform && !g.IsTool).ToList();
        if (games.Count == 0) return "";
        return $"\n\nDİKKAT: Bu bir oyun platformu. Kaldırırsanız {games.Count} oyun ({Fmt.Size(games.Sum(g => g.TotalSize))}) çalışmaz hale gelir"
               + (platform == "Steam" ? " ve Steam kütüphanesindeki oyun dosyaları da silinebilir." : ".");
    }

    /// <summary>Kaldırıcısı olmayan program: klasörleri, ayarları ve listedeki kaydı bulup silmeyi önerir.</summary>
    public static async Task<bool> ForceRemoveAsync(InstalledApp app)
    {
        if (!Dialogs.Confirm($"“{app.Name}” zorla kaldırılsın mı?",
                "Bu programın kaldırıcısı diskte yok, normal yolla kaldırılamıyor. Kurulum klasörü, ayarları ve Programlar listesindeki kaydı bulunacak; silmeden önce hepsini göreceksiniz.",
                "Devam", true))
            return false;
        var inv = await AppState.I.GetInventoryAsync();
        await ScanLeftoversAsync(app, inv, $"“{app.Name}” zorla kaldırma", "Programa ait bulunanlar aşağıda.");
        AppState.I.Invalidate();
        return true;
    }

    private static async Task ScanLeftoversAsync(InstalledApp app, Inventory inv, string title, string intro)
    {
        var remaining = inv.AllApps.Where(a => a.Id != app.Id).ToList();
        var items = await WaitDialog.WhileAsync("Kalıntılar taranıyor", "Geride kalan dosyalar, ayarlar ve kısayollar aranıyor…",
            Task.Run(() => LeftoverScanner.ScanForApp(app, remaining, inv.Packages, inv.Games, CancellationToken.None)));
        if (items.Count == 0) Dialogs.Success(title, "Geride kalan dosya, klasör ya da ayar bulunamadı.");
        else await LeftoverDialog.ShowAsync(title, intro, items);
    }

    // ------------------------------------------------------------------ Oyunlar

    public static async Task<bool> RemoveGameAsync(GameEntry g)
    {
        bool done = g.Removal switch
        {
            GameRemoval.SteamUri => await RemoveSteamAsync(g),
            GameRemoval.EpicManual => await RemoveEpicAsync(g),
            GameRemoval.Win32Uninstaller or GameRemoval.StorePackage when g.App != null => await UninstallAppAsync(g.App),
            GameRemoval.UbisoftUri => await RemoveUbisoftAsync(g),
            _ => await RemoveFolderAsync(g),
        };
        if (done) AppState.I.Invalidate();
        return done;
    }

    private static async Task<bool> RemoveSteamAsync(GameEntry g)
    {
        if (!Dialogs.Confirm($"“{g.Name}” kaldırılsın mı?",
                $"Steam'in kaldırma onayı açılacak; oyun dosyalarını ({g.SizeText}) Steam siler. Steam kapalıysa önce açılır.", "Steam'de kaldır", true))
            return false;
        var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(g.InstallDir!))!;
        var manifest = Path.Combine(steamapps, $"appmanifest_{g.SteamAppId}.acf");
        PathUtil.OpenUri($"steam://uninstall/{g.SteamAppId}");
        bool finished = WaitDialog.Run($"“{g.Name}” kaldırılıyor", "Steam'de açılan pencerede “Kaldır”a basın. Oyun kaldırılınca bu pencere kendiliğinden kapanır.",
            () => !File.Exists(manifest), "Kaldırma bitti", "Vazgeç");
        if (!finished) return false;
        ActivityLog.Write("Steam oyunu kaldırıldı", g.Name, g.TotalSize, 1, 0, "Steam");

        // Steam bazen oyun klasöründe kullanıcı dosyalarını ve atölye içeriğini bırakır
        var rest = new List<LeftoverItem>();
        foreach (var (path, reason) in new[] { (g.InstallDir, "Oyun klasörü Steam kaldırdıktan sonra kaldı"), (g.ExtraFolder, "Oyunun atölye (Workshop) içeriği"), (g.CacheFolder, "Oyunun gölgelendirici önbelleği") })
        {
            if (path == null || !Directory.Exists(path)) continue;
            var m = FsScan.Measure(path);
            rest.Add(new LeftoverItem { Kind = LeftoverKind.SteamOrphan, Path = path, Location = "Steam kütüphanesi", Size = m.Size, LastModified = m.Newest, Confidence = Confidence.High, Reason = reason, IsSelected = true });
        }
        if (rest.Count > 0) await LeftoverDialog.ShowAsync($"“{g.Name}” kaldırıldı", "Steam oyunu kaldırdı ama aşağıdakiler geride kaldı.", rest);
        else Dialogs.Success($"“{g.Name}” kaldırıldı", "Oyun Steam'den kaldırıldı ve geride dosya kalmadı.");
        return true;
    }

    private static async Task<bool> RemoveEpicAsync(GameEntry g)
    {
        while (Process.GetProcessesByName("EpicGamesLauncher").Length > 0)
        {
            if (!Dialogs.Confirm("Epic Games Launcher açık",
                    "Epic kayıtlarının doğru güncellenmesi için Epic Games Launcher'ı kapatın (sistem tepsisindeki simgeye sağ tıklayıp “Çıkış”). Kapattıktan sonra Devam'a basın.",
                    "Devam"))
                return false;
        }
        if (!Dialogs.Confirm($"“{g.Name}” kaldırılsın mı?",
                $"Oyun klasörü ({g.SizeText}) kalıcı olarak silinecek ve Epic Games kayıtlarından çıkarılacak. Bu boyuttaki bir klasör Geri Dönüşüm Kutusu'na sığmaz. Kayıtlı oyunlarınız genelde Belgeler klasöründe olduğu için etkilenmez.",
                "Kalıcı olarak kaldır", true, new[] { g.InstallDir! }))
            return false;
        var st = await WaitDialog.WhileAsync("Kaldırılıyor", $"“{g.Name}” siliniyor… Büyük oyunlarda bu birkaç dakika sürebilir.", GameInventory.RemoveEpicGameAsync(g, recycle: false));
        AnalyzerView.ShowResult(st);
        return st.Items > 0;
    }

    private static async Task<bool> RemoveUbisoftAsync(GameEntry g)
    {
        if (!Dialogs.Confirm($"“{g.Name}” kaldırılsın mı?", "Ubisoft Connect'in kaldırma ekranı açılacak.", "Ubisoft Connect'te kaldır", true)) return false;
        PathUtil.OpenUri($"uplay://uninstall/{g.UbisoftId}");
        if (g.App == null) return true;
        bool finished = WaitDialog.Run($"“{g.Name}” kaldırılıyor", "Ubisoft Connect'teki adımları tamamlayın.", () => !AppInventory.RegistryEntryExists(g.App), "Kaldırma bitti", "Vazgeç");
        if (!finished) return false;
        var inv = await AppState.I.GetInventoryAsync();
        await ScanLeftoversAsync(g.App, inv, $"“{g.Name}” kaldırıldı", "Oyun kaldırıldı ama aşağıdakiler geride kaldı.");
        return true;
    }

    private static async Task<bool> RemoveFolderAsync(GameEntry g)
    {
        if (g.InstallDir == null) return false;
        bool recycle = AppSettings.Current.UseRecycleBin && g.TotalSize < 20L * 1024 * 1024 * 1024;
        string how = recycle ? "Geri Dönüşüm Kutusu'na taşınacak" : "kalıcı olarak silinecek";
        string warn = g.Warning != null ? "\n\n" + g.Warning : "";
        if (!Dialogs.Confirm($"“{g.Name}” silinsin mi?", $"Klasör ({g.SizeText}) {how}.{warn}", recycle ? "Taşı" : "Kalıcı sil", true, new[] { g.InstallDir }))
            return false;
        var st = await WaitDialog.WhileAsync("Siliniyor", $"“{g.Name}” siliniyor…",
            Deleter.DeleteUserItemsAsync("Oyun klasörü silindi: " + g.Name, new[] { new DeleteItem(g.InstallDir, g.Size) }, recycle));
        AnalyzerView.ShowResult(st);
        return st.Items > 0;
    }
}
