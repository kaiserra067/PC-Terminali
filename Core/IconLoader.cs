using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TemizlikPaneli.Core;

/// <summary>Program simgeleri, Store logoları ve oyun kapakları (arka planda yüklenir, önbelleğe alınır).</summary>
public static class IconLoader
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Throttle = new(4);

    public static ImageSource? FromExe(string path, int index = 0)
    {
        return Cache.GetOrAdd(path + "," + index, _ =>
        {
            var large = new IntPtr[1];
            try
            {
                if (Native.ExtractIconEx(path, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { if (large[0] != IntPtr.Zero) Native.DestroyIcon(large[0]); }
        });
    }

    public static ImageSource? FromImage(string path, int decodeWidth)
    {
        return Cache.GetOrAdd(path + "|" + decodeWidth, _ =>
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.UriSource = new Uri(path);
                bi.DecodePixelWidth = decodeWidth;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        });
    }

    /// <summary>Simge dosyası exe/dll/ico ise sistem simgesini, png/jpg ise resmi yükler.</summary>
    public static ImageSource? Auto(string? path, int decodeWidth = 64)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" ? FromImage(path, decodeWidth) : FromExe(path);
    }

    public static async Task LoadAppIconsAsync(IEnumerable<InstalledApp> apps)
    {
        foreach (var app in apps.Where(a => a.Icon == null).ToList())
        {
            await Throttle.WaitAsync();
            try
            {
                var img = await Task.Run(() =>
                {
                    if (app.Source == AppSource.Store) return Auto(app.LogoPath, 64);
                    var file = AppInventory.IconFile(app.DisplayIcon, out var idx);
                    if (file != null) return Path.GetExtension(file).Equals(".ico", StringComparison.OrdinalIgnoreCase) ? FromExe(file) : FromExe(file, idx);
                    if (app.InstallLocation != null && Directory.Exists(app.InstallLocation))
                    {
                        var exe = Directory.EnumerateFiles(app.InstallLocation, "*.exe")
                            .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase));
                        if (exe != null) return FromExe(exe);
                    }
                    return null;
                });
                if (img != null) app.Icon = img;
            }
            catch { }
            finally { Throttle.Release(); }
        }
    }

    public static async Task LoadGameImagesAsync(IEnumerable<GameEntry> games)
    {
        foreach (var g in games.ToList())
        {
            await Throttle.WaitAsync();
            try
            {
                var (cover, icon) = await Task.Run(() => (Auto(g.CoverPath, 460), Auto(g.IconSource, 64)));
                if (cover != null) g.Cover = cover;
                if (icon != null) g.Icon = icon;
            }
            catch { }
            finally { Throttle.Release(); }
        }
    }
}
