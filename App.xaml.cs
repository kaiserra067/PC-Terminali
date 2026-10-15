using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using TemizlikPaneli.Core;

namespace TemizlikPaneli;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.MigrateOldData();
        DispatcherUnhandledException += OnUiException;
        TaskScheduler.UnobservedTaskException += (_, a) => a.SetObserved();

        SafetyGuard.SetUserExclusions(AppSettings.Current.Exclusions);
        var args = e.Args;
        int ti = Array.IndexOf(args, "--theme");
        ThemeManager.Apply(ti >= 0 && ti + 1 < args.Length
            ? (args[ti + 1] == "light" ? ThemeChoice.Light : ThemeChoice.Dark)
            : AppSettings.Current.Theme);

        if (args.Length >= 3 && args[0] == "--selftest-media")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await SelfTest.RunMediaTestAsync(args[1], args[2]);
            Shutdown();
            return;
        }
        if (args.Length >= 2 && args[0] == "--selftest-uninstall")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await SelfTest.RunUninstallTestAsync(args[1]);
            Shutdown();
            return;
        }
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await SelfTest.RunAsync(args[1], args.Length >= 3 ? args[2] : null); }
            catch (Exception ex) { try { File.AppendAllText(args[1], "\nÖZ TEST HATASI: " + ex); } catch { } }
            Shutdown();
            return;
        }

        var w = new MainWindow(args);
        MainWindow = w;
        w.Show();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "hatalar.log"), $"{DateTime.Now:u} {e.Exception}\n\n");
        }
        catch { }
        Views.Dialogs.Error("Beklenmeyen bir hata oluştu", e.Exception.Message);
    }
}

/// <summary>Açık/koyu tema geçişi. Renkler DynamicResource ile bağlı olduğundan anında değişir.</summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Apply(ThemeChoice choice)
    {
        bool dark = choice switch { ThemeChoice.Dark => true, ThemeChoice.Light => false, _ => SystemPrefersDark() };
        IsDark = dark;
        var dicts = Application.Current.Resources.MergedDictionaries;
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        if (dicts.Count > 0) dicts[0] = dict; else dicts.Add(dict);
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void ApplyTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = IsDark ? 1 : 0;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch { }
    }

    public static System.Windows.Media.Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

    public static System.Windows.Media.Color Color(string key) =>
        Brush(key) is System.Windows.Media.SolidColorBrush b ? b.Color : System.Windows.Media.Colors.Gray;
}
