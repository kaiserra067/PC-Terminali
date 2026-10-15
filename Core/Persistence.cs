using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemizlikPaneli.Core;

public sealed class LogEntry
{
    public DateTime Time { get; set; }
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public long Bytes { get; set; }
    public int Items { get; set; }
    public int Failed { get; set; }
    public string Method { get; set; } = "";

    [JsonIgnore] public string TimeText => Fmt.DateTime(Time);
    [JsonIgnore] public string SizeText => Bytes > 0 ? Fmt.Size(Bytes) : "—";
    [JsonIgnore] public string ResultText => Failed > 0 ? $"{Fmt.Count(Items)} öğe, {Fmt.Count(Failed)} atlandı" : $"{Fmt.Count(Items)} öğe";
}

/// <summary>Yapılan tüm temizlik işlemlerinin kalıcı kaydı (JSON satırları).</summary>
public static class ActivityLog
{
    private static readonly object Gate = new();
    public static event Action? Changed;

    public static void Write(string action, string target, long bytes, int items, int failed, string method)
    {
        var e = new LogEntry { Time = DateTime.Now, Action = action, Target = target, Bytes = bytes, Items = items, Failed = failed, Method = method };
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(AppPaths.LogFile, JsonSerializer.Serialize(e) + Environment.NewLine);
            }
        }
        catch { }
        Changed?.Invoke();
    }

    public static List<LogEntry> ReadAll()
    {
        var list = new List<LogEntry>();
        try
        {
            lock (Gate)
            {
                if (!File.Exists(AppPaths.LogFile)) return list;
                foreach (var line in File.ReadAllLines(AppPaths.LogFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { var e = JsonSerializer.Deserialize<LogEntry>(line); if (e != null) list.Add(e); } catch { }
                }
            }
        }
        catch { }
        list.Reverse();
        return list;
    }

    public static void Clear()
    {
        try { lock (Gate) { if (File.Exists(AppPaths.LogFile)) File.Delete(AppPaths.LogFile); } } catch { }
        Changed?.Invoke();
    }
}

public enum ThemeChoice { System, Light, Dark }

public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>Kullanıcı dosyaları (büyük dosyalar, yinelenenler, kalıntılar) için: true = Geri Dönüşüm Kutusu.</summary>
    public bool UseRecycleBin { get; set; } = true;
    /// <summary>Geçici dosyalarda en az bu kadar gün dokunulmamış dosyalar silinir.</summary>
    public int TempMinAgeDays { get; set; } = 1;
    public int LargeFileMinMb { get; set; } = 500;
    public List<string> Exclusions { get; set; } = new();
    /// <summary>Medya indiricinin kayıt klasörü (boşsa İndirilenler).</summary>
    public string? MediaOutputDir { get; set; }
    /// <summary>Oturum için çerez kaynağı: null, edge, brave, chrome, firefox ya da "file:yol".</summary>
    public string? MediaCookies { get; set; }

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        SafetyGuard.SetUserExclusions(Exclusions);
    }
}
