using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace TemizlikPaneli.Core;

public sealed class StorePackage
{
    public string Name = "";
    public string FullName = "";
    public string Family = "";
    public string? Location;
    public string Version = "";
    public string Publisher = "";
    public string Signature = "";
    public bool NonRemovable;
    public bool IsFramework;
    public bool IsResource;
}

public static class StoreApps
{
    /// <summary>Kısa bir PowerShell betiğini çalıştırır, standart çıktıyı döndürür.</summary>
    public static async Task<(int Code, string Out, string Err)> RunPowerShellAsync(string script, CancellationToken ct = default)
    {
        var full = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await outTask, await errTask);
    }

    /// <summary>Geçerli kullanıcının tüm paketleri (çerçeveler ve sistem paketleri dahil).</summary>
    public static async Task<List<StorePackage>> GetAllPackagesAsync(CancellationToken ct = default)
    {
        const string script =
            "Get-AppxPackage | ForEach-Object { [pscustomobject]@{ N=$_.Name; F=$_.PackageFullName; Fam=$_.PackageFamilyName; " +
            "L=$_.InstallLocation; V=[string]$_.Version; P=$_.Publisher; S=[string]$_.SignatureKind; NR=[bool]$_.NonRemovable; " +
            "FW=[bool]$_.IsFramework; RP=[bool]$_.IsResourcePackage } } | ConvertTo-Json -Compress -Depth 2";
        var list = new List<StorePackage>();
        try
        {
            var (_, output, _) = await RunPowerShellAsync(script, ct);
            output = output.Trim();
            if (output.Length == 0) return list;
            using var doc = JsonDocument.Parse(output);
            IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : new[] { doc.RootElement };
            foreach (var e in items)
            {
                list.Add(new StorePackage
                {
                    Name = S(e, "N"),
                    FullName = S(e, "F"),
                    Family = S(e, "Fam"),
                    Location = S(e, "L") is { Length: > 0 } l ? l : null,
                    Version = S(e, "V"),
                    Publisher = S(e, "P"),
                    Signature = S(e, "S"),
                    NonRemovable = B(e, "NR"),
                    IsFramework = B(e, "FW"),
                    IsResource = B(e, "RP"),
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return list;
    }

    private static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Listede gösterilecek Store uygulamaları (çerçeveler, kaynak paketleri ve Windows'un parçaları hariç).</summary>
    public static List<InstalledApp> ToApps(IEnumerable<StorePackage> packages)
    {
        var apps = new List<InstalledApp>();
        foreach (var p in packages)
        {
            if (p.IsFramework || p.IsResource || p.Location == null) continue;
            if (p.Signature.Equals("System", StringComparison.OrdinalIgnoreCase)) continue;
            var (display, publisher, logo) = ReadManifest(p);
            if (display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) display = p.Name;
            apps.Add(new InstalledApp
            {
                Id = "store|" + p.FullName,
                Name = display,
                Publisher = publisher,
                Version = p.Version,
                Source = AppSource.Store,
                InstallLocation = p.Location,
                PackageFullName = p.FullName,
                PackageFamilyName = p.Family,
                LogoPath = logo,
                NonRemovable = p.NonRemovable,
                InstallDate = SafeCreationTime(p.Location),
            });
        }
        return apps;
    }

    private static DateTime? SafeCreationTime(string dir)
    {
        try { return Directory.GetCreationTime(dir); } catch { return null; }
    }

    private static (string Display, string Publisher, string? Logo) ReadManifest(StorePackage p)
    {
        string display = p.Name, publisher = "", logoRel = "";
        try
        {
            var manifest = Path.Combine(p.Location!, "AppxManifest.xml");
            if (File.Exists(manifest))
            {
                var doc = XDocument.Load(manifest);
                var props = doc.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "Properties");
                if (props != null)
                {
                    display = props.Elements().FirstOrDefault(x => x.Name.LocalName == "DisplayName")?.Value ?? display;
                    publisher = props.Elements().FirstOrDefault(x => x.Name.LocalName == "PublisherDisplayName")?.Value ?? "";
                    logoRel = props.Elements().FirstOrDefault(x => x.Name.LocalName == "Logo")?.Value ?? "";
                }
            }
        }
        catch { }
        display = Resolve(display, p) ?? p.Name;
        publisher = Resolve(publisher, p) ?? "";
        return (display.Trim(), publisher.Trim(), FindLogo(p.Location!, logoRel));
    }

    /// <summary>"ms-resource:" ile başlayan yerelleştirilmiş adları çözer.</summary>
    private static string? Resolve(string value, StorePackage p)
    {
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value;
        var candidates = new List<string>();
        if (value.StartsWith("ms-resource://", StringComparison.OrdinalIgnoreCase)) candidates.Add(value);
        var key = value["ms-resource:".Length..].TrimStart('/');
        if (key.Contains('/')) candidates.Add($"ms-resource://{p.Name}/{key}");
        candidates.Add($"ms-resource://{p.Name}/Resources/{key}");
        candidates.Add($"ms-resource://{p.Name}/{key}");
        foreach (var c in candidates)
        {
            var sb = new StringBuilder(1024);
            try
            {
                if (Native.SHLoadIndirectString($"@{{{p.FullName}?{c}}}", sb, sb.Capacity, IntPtr.Zero) == 0)
                {
                    var s = sb.ToString();
                    if (s.Length > 0 && !s.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) return s;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? FindLogo(string location, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        try
        {
            var full = Path.Combine(location, rel);
            if (File.Exists(full)) return full;
            var dir = Path.GetDirectoryName(full);
            if (dir == null || !Directory.Exists(dir)) return null;
            var stem = Path.GetFileNameWithoutExtension(full);
            var ext = Path.GetExtension(full);
            var files = Directory.GetFiles(dir, stem + "*" + ext);
            return files.FirstOrDefault(f => f.Contains("scale-100", StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => f.Contains("scale-200", StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault();
        }
        catch { return null; }
    }

    public static async Task<(bool Ok, string Error)> UninstallAsync(InstalledApp app, CancellationToken ct = default)
    {
        if (app.PackageFullName == null) return (false, "Paket adı yok");
        var name = app.PackageFullName.Replace("'", "''");
        var (code, _, err) = await RunPowerShellAsync($"Remove-AppxPackage -Package '{name}' -ErrorAction Stop", ct);
        return (code == 0 && string.IsNullOrWhiteSpace(err), err.Trim());
    }
}
