using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace TemizlikPaneli.Core;

/// <summary>Arayüze bağlanan nesnelerin temel sınıfı.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _run(p);
}

/// <summary>Boyut, sayı ve tarih biçimlendirme (Türkçe).</summary>
public static class Fmt
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Size(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        if (u == 0) return bytes.ToString(Tr) + " B";
        string num = v >= 100 ? v.ToString("0", Tr) : v >= 10 ? v.ToString("0.0", Tr) : v.ToString("0.00", Tr);
        return num + " " + Units[u];
    }

    public static string Count(long n) => n.ToString("N0", Tr);

    public static string Percent(double fraction) => (fraction * 100).ToString(fraction < 0.1 ? "0.0" : "0", Tr) + "%";

    public static string Date(DateTime? d) => d is { } x && x.Year > 1980 ? x.ToString("dd.MM.yyyy", Tr) : "—";

    public static string DateTime(DateTime d) => d.ToString("dd.MM.yyyy HH:mm", Tr);

    public static string Ago(DateTime? d)
    {
        if (d is not { } x || x.Year < 1981) return "—";
        var span = System.DateTime.Now - x;
        if (span.TotalDays < 1) return "bugün";
        if (span.TotalDays < 2) return "dün";
        if (span.TotalDays < 31) return $"{(int)span.TotalDays} gün önce";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} ay önce";
        return $"{(int)(span.TotalDays / 365)} yıl önce";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0} sn" : $"{(int)t.TotalMinutes} dk {t.Seconds} sn";
}

/// <summary>Yol işlemleri. Türkçe yerel ayarda büyük/küçük harf hatalarını önlemek için hep Ordinal karşılaştırma kullanılır.</summary>
public static class PathUtil
{
    public static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path.Trim().Trim('"').Trim();
        p = Environment.ExpandEnvironmentVariables(p).Replace('/', '\\');
        try { p = Path.GetFullPath(p); } catch { return ""; }
        if (p.Length > 3) p = p.TrimEnd('\\');
        return p;
    }

    /// <summary>child, parent'ın kendisi ya da altında mı?</summary>
    public static bool IsSameOrUnder(string child, string parent)
    {
        if (child.Length == 0 || parent.Length == 0) return false;
        if (child.Length == parent.Length) return child.Equals(parent, StringComparison.OrdinalIgnoreCase);
        if (child.Length < parent.Length) return false;
        if (!child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return false;
        return parent.EndsWith('\\') || child[parent.Length] == '\\';
    }

    public static bool IsStrictlyUnder(string child, string parent) =>
        child.Length > parent.Length && IsSameOrUnder(child, parent);

    public static bool IsDriveRoot(string p) => p.Length <= 3 && p.Length >= 2 && p[1] == ':';

    /// <summary>Yolu diskteki gerçek büyük/küçük harf yazımıyla döndürür ("c:/program files" → "C:\Program Files").</summary>
    public static string TrueCase(string path)
    {
        try
        {
            var full = Normalize(path);
            if (full.Length == 0) return path;
            var root = (Path.GetPathRoot(full) ?? "").ToUpperInvariant();
            var cur = root;
            foreach (var part in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Directory.Exists(cur) ? Directory.EnumerateFileSystemEntries(cur, part).FirstOrDefault() : null;
                cur = match ?? Path.Combine(cur, part);
            }
            return cur;
        }
        catch { return path; }
    }

    /// <summary>Karşılaştırma için ad sadeleştirme: küçük harf, yalnızca harf ve rakam.</summary>
    public static string Simplify(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
        {
            char ch = c switch { 'ı' => 'i', 'ş' => 's', 'ğ' => 'g', 'ü' => 'u', 'ö' => 'o', 'ç' => 'c', _ => c };
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else
            {
                var parent = Path.GetDirectoryName(path);
                if (parent != null && Directory.Exists(parent))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parent) { UseShellExecute = true });
            }
        }
        catch { }
    }

    public static void OpenUri(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { }
    }
}

/// <summary>Türkçe I/ı/İ/i farkından etkilenmeyen arama.</summary>
public static class TextMatch
{
    private static string Fold(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is 'İ' or 'I' or 'ı' ? 'i' : char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static bool Contains(string? haystack, string needle) =>
        haystack != null && Fold(haystack).Contains(Fold(needle), StringComparison.Ordinal);
}

/// <summary>COM/kabuk çağrıları için STA iş parçacığında çalıştırma.</summary>
public static class Sta
{
    public static Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}

public static class Admin
{
    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Uygulamayı yönetici olarak yeniden başlatır. Kullanıcı UAC'yi reddederse false döner.</summary>
    public static bool RestartElevated(string? args = null)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = args ?? ""
            });
            return true;
        }
        catch { return false; }
    }
}
