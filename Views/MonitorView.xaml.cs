using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TemizlikPaneli.Core;

namespace TemizlikPaneli.Views;

public sealed class CoreBar
{
    public double Fraction { get; init; }
    public string Tip { get; init; } = "";
}

public partial class MonitorView : UserControl, IPage
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private HardwareMonitor? _hm;
    private Task? _starting;
    private bool _sampling;
    private SensorLevel? _shownLevel;

    public MonitorView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await TickAsync();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) _timer.Start(); else _timer.Stop(); };
        Application.Current.Exit += (_, _) => _hm?.Dispose();
    }

    public void OnShown()
    {
        if (_hm == null)
        {
            _hm = new HardwareMonitor();
            var hm = _hm;
            SensorText.Text = "Sensörler hazırlanıyor…";
            SensorButton.Visibility = Visibility.Collapsed;
            _starting = Task.Run(hm.Start);
        }
        _timer.Start();
    }

    private async Task TickAsync()
    {
        if (_sampling || _hm == null || _starting == null) return;
        _sampling = true;
        try
        {
            await _starting;
            var hm = _hm;
            var s = await Task.Run(hm.Sample);
            Show(s);
        }
        catch { }
        finally { _sampling = false; }
    }

    // ------------------------------------------------------------------ Yardımcılar

    private static string Pct(double v) => "%" + v.ToString(v < 10 ? "0.0" : "0", Tr);
    private static string N(double v, string f = "0") => v.ToString(f, Tr);
    private static string Signed(int v) => v > 0 ? "+" + v : v < 0 ? "−" + Math.Abs(v) : "0";

    /// <summary>Etiket–değer satırları. İpucu varsa değerin yanında bilgi simgesi gösterilir.</summary>
    private static void Rows(StackPanel panel, IEnumerable<(string Label, string Value, string? Tip)> rows)
    {
        panel.Children.Clear();
        foreach (var (label, value, tip) in rows)
        {
            var g = new DockPanel { Margin = new Thickness(0, 0, 0, 7), Background = System.Windows.Media.Brushes.Transparent, ToolTip = tip };
            var v = new TextBlock
            {
                Text = value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 210, Margin = new Thickness(12, 0, 0, 0),
            };
            v.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            DockPanel.SetDock(v, Dock.Right);
            var l = new TextBlock { Text = label + (tip != null ? "  ⓘ" : ""), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            g.Children.Add(v);
            g.Children.Add(l);
            panel.Children.Add(g);
        }
    }

    // ------------------------------------------------------------------ Gösterim

    private void Show(HwSnapshot s)
    {
        UpdateSensorBar(s.Level);
        bool full = s.Level == SensorLevel.Full;
        string needFull = s.Level switch
        {
            SensorLevel.Basic => "Yönetici modunda görünür",
            SensorLevel.AdminNoDriver => "PawnIO ile görünür",
            _ => "Okunamadı",
        };

        // ---- Özet şeridi
        SumCpuName.Text = s.CpuName;
        SumCpu.Text = $"{Pct(s.CpuUsage)} kullanım" + (s.CpuTemp is { } ct ? $" · {N(ct)} °C" : "");
        SumGpuName.Text = s.GpuName.Length > 0 ? s.GpuName : "Ekran kartı bulunamadı";
        SumGpu.Text = (s.GpuUsage is { } gu0 ? $"{Pct(gu0)} kullanım" : "—") + (s.GpuTemp is { } gt0 ? $" · {N(gt0)} °C" : "");
        SumRamName.Text = s.RamModuleName.Length > 0 ? $"{s.RamModuleName} · {Fmt.Size(s.RamTotal)}" : Fmt.Size(s.RamTotal);
        SumRam.Text = $"{Fmt.Size(s.RamUsed)} / {Fmt.Size(s.RamTotal)} kullanılıyor";

        // ---- İşlemci
        CpuUsage.Text = Pct(s.CpuUsage);
        CpuClock.Text = s.CpuClockMHz > 0 ? N(s.CpuClockMHz / 1000, "0.00") : "—";
        CpuTemp.Text = s.CpuTemp is { } t ? N(t) : "—";
        SetTempState(CpuTempIcon, CpuTempState, s.CpuTemp, s.CpuTuning?.ThermalLimitC is > 50 and var lim ? lim - 10 : 80, s.CpuTuning?.ThermalLimitC ?? 90, s.Level);
        CpuSpark.Push(s.CpuUsage);
        CoreBars.ItemsSource = s.CoreUsage.Select((u, i) => new CoreBar { Fraction = Math.Max(0.02, u / 100), Tip = $"İş parçacığı {i}: {Pct(u)}" }).ToList();
        Rows(CpuRows, new (string, string, string?)[]
        {
            ("Ad", s.CpuName, null),
            ("Çekirdek / iş parçacığı", $"{s.Cores} / {s.Threads}", null),
            ("Taban hız", s.CpuBaseMHz > 0 ? N(s.CpuBaseMHz / 1000, "0.00") + " GHz" : "—", null),
            ("Paket gücü", s.CpuPowerW is { } w ? N(w, "0.0") + " W" : needFull, null),
        });
        Rows(CpuTuneRows, CpuTuningRows(s, needFull));

        // ---- Ekran kartı
        GpuUsage.Text = s.GpuUsage is { } gu ? Pct(gu) : "—";
        GpuTemp.Text = s.GpuTemp is { } gt ? N(gt) : "—";
        SetTempState(GpuTempIcon, GpuTempState, s.GpuTemp, 80, 92, SensorLevel.Full);
        GpuClock.Text = s.GpuCoreMHz is { } gc ? N(gc) : "—";
        GpuSpark.Push(s.GpuUsage ?? 0);
        VramText.Text = s.VramTotal > 0 ? $"{Fmt.Size(s.VramUsed)} / {Fmt.Size(s.VramTotal)}" : Fmt.Size(s.VramUsed);
        VramBar.Value = s.VramTotal > 0 ? 100.0 * s.VramUsed / s.VramTotal : 0;
        var gpuRows = new List<(string, string, string?)>
        {
            ("Ad", s.GpuName.Length > 0 ? s.GpuName : "—", null),
            ("Bellek saati", s.GpuMemMHz is { } gm ? N(gm) + " MHz" : "—", null),
            ("Fan", s.GpuFanRpm is { } f ? (f == 0 ? "Durdu (sıfır devir)" : N(f, "N0") + " RPM") : "—", null),
        };
        if (s.GpuHotspot is { } hs) gpuRows.Add(("En sıcak nokta (hotspot)", N(hs) + " °C", null));
        gpuRows.Add(("Güç", s.GpuPowerW is { } gw ? N(gw) + " W" : s.GpuPowerPct is { } gp ? Pct(gp) : needFull, null));
        Rows(GpuRows, gpuRows);
        Rows(GpuTuneRows, GpuTuningRows(s));

        // ---- Bellek
        double ramPct = s.RamTotal > 0 ? 100.0 * s.RamUsed / s.RamTotal : 0;
        RamUsage.Text = Pct(ramPct);
        RamUsedBig.Text = N(s.RamUsed / 1073741824.0, "0.0");
        double clock = s.RamDetails?.MemClockMHz ?? (s.RamSpeedMTs > 0 ? s.RamSpeedMTs / 2.0 : 0);
        RamClock.Text = clock > 0 ? N(clock) : "—";
        RamSpark.Push(ramPct);
        RamUsedText.Text = $"{Fmt.Size(s.RamUsed)} / {Fmt.Size(s.RamTotal)}";
        RamBar.Value = ramPct;
        RamBar.Foreground = ThemeManager.Brush(ramPct >= 95 ? "CriticalBrush" : ramPct >= 85 ? "WarningBrush" : "Series1");
        var d = s.RamDetails;
        Rows(RamRows, new (string, string, string?)[]
        {
            ("Model", s.RamModuleName.Length > 0 ? s.RamModuleName : "—", null),
            ("Modüller", s.RamModules > 0 && s.RamModuleSize > 0 ? $"{s.RamModules} × {Fmt.Size(s.RamModuleSize)} {s.RamType}" : s.RamType, null),
            ("Bellek çipleri", d is { DramVendor.Length: > 0 } ? d.DramVendor + (d.DieDensityGb > 0 ? $" · {d.DieDensityGb} Gb yonga" : "") : needFull,
                "Modülün üzerindeki bellek çiplerinin üreticisi, modülün kendi SPD çipinden okunur."),
            ("Üretim tarihi", d?.ManufactureDate is { } md ? md.ToString("MMMM yyyy", Tr) : full ? "—" : needFull, null),
        });
        var tune = new List<(string, string, string?)>
        {
            ("Veri hızı", s.RamSpeedMTs > 0 ? $"{s.RamSpeedMTs} MT/s (DDR{(s.RamType.Contains('5') ? "5" : s.RamType.Contains('4') ? "4" : "")}-{s.RamSpeedMTs})" : "—", null),
            ("Saat", clock > 0 ? N(clock) + " MHz" : "—", "DDR bellekte saat hızı, veri hızının (MT/s) yarısıdır."),
        };
        if (d?.CL is { } cl)
        {
            tune.Add(("Ana zamanlamalar", $"CL{cl}-{d.RCD}-{d.RP}-{d.RAS}", "CL – tRCD – tRP – tRAS, bellek denetleyicisinden şu an çalışan değerler."));
            tune.Add(("tRC · tCWL", $"{d.RC} · {d.CWL}", null));
            tune.Add(("Gecikme (CL)", N(cl / clock * 1000, "0.0") + " ns", "CAS gecikmesinin nanosaniye karşılığı: düşük olan daha iyidir."));
        }
        else tune.Add(("Zamanlamalar (CL)", s.IsAmdCpu ? needFull : "Bu işlemcide okunamıyor", null));
        if (s.CpuTuning is { FclkMHz: > 0 } ft)
            tune.Add(("FCLK / UCLK", $"{N(ft.FclkMHz)} / {N(ft.UclkMHz)} MHz · " + (Math.Abs(ft.UclkMHz - ft.MclkMHz) < 1 ? "1:1" : "1:2"),
                "Infinity Fabric ve bellek denetleyicisi saatleri. UCLK'nin bellek saatine eşit olması (1:1) en düşük gecikmeyi verir."));
        Rows(RamTuneRows, tune);
    }

    private static IEnumerable<(string, string, string?)> CpuTuningRows(HwSnapshot s, string needFull)
    {
        if (!s.IsAmdCpu)
            return new (string, string, string?)[] { ("PBO / undervolt", "Yalnızca AMD Ryzen işlemcilerde okunabilir", null) };
        if (s.CpuTuning is not { } c)
            return new (string, string, string?)[] { ("PBO durumu", s.Level == SensorLevel.Full ? "Bu işlemci modelinde okunamıyor" : needFull, null) };

        // Ryzen masaüstü işlemcilerin fabrika PPT değerleri: 65 W → 88, 105 W → 142, 120 W → 162, 170 W → 230
        double[] stock = { 88, 142, 162, 230 };
        string pbo = c.PptLimitW >= 999 ? "Açık · anakart sınırları (sınırsız)"
            : stock.Any(x => Math.Abs(x - c.PptLimitW) < 1.5) ? "Kapalı · fabrika sınırları"
            : c.PptLimitW < 88 ? "Eco modu / düşürülmüş güç sınırı"
            : "Açık · elle ayarlanmış sınırlar";
        return new (string, string, string?)[]
        {
            ("PBO", pbo, "Precision Boost Overdrive. İşlemcinin güç (PPT) ve akım sınırlarına bakılarak belirlenir."),
            ("Güç sınırı (PPT)", $"{N(c.PptLimitW)} W · şu an {N(c.PptW)} W", null),
            ("Akım sınırı", N(c.CurrentLimitA) + " A", null),
            ("Sıcaklık sınırı", N(c.ThermalLimitC) + " °C", null),
            ("Boost sınırı", N(c.BoostLimitGHz, "0.00") + " GHz", "Çekirdeklerin çıkabileceği en yüksek frekans. Fabrika değerinden yüksekse boost aşırtması (PBO +MHz) var demektir."),
            ("Curve Optimizer", "Okunamıyor", "Çekirdek başına undervolt (Curve Optimizer) değerleri işlemcinin güç tablosunda yer almaz. Okumak için işlemcinin güç yöneticisine komut göndermek gerekir; bu uygulama güvenlik için bunu yapmaz. BIOS'tan ya da Ryzen Master'dan bakabilirsiniz."),
        };
    }

    private static IEnumerable<(string, string, string?)> GpuTuningRows(HwSnapshot s)
    {
        if (s.GpuTuning is not { } g)
            return new (string, string, string?)[] { ("Ayarlar", "Yalnızca AMD Radeon kartlarda okunabilir", null) };
        var rows = new List<(string, string, string?)>();
        if (g.VoltageOffsetMV is { } v)
            rows.Add(("Voltaj ofseti", v < 0 ? $"{Signed(v)} mV (undervolt)" : "0 mV (varsayılan)", $"Adrenalin'deki voltaj ofseti. İzin verilen aralık {Signed(g.VoltageOffsetMin ?? 0)}…0 mV."));
        if (g.PowerLimitPct is { } p)
            rows.Add(("Güç limiti", p == 0 ? "%0 (varsayılan)" : $"{(p > 0 ? "+" : "−")}%{Math.Abs(p)}", $"İzin verilen aralık {Signed(g.PowerLimitMin ?? 0)}…{Signed(g.PowerLimitMax ?? 0)} %."));
        if (g.ClockOffsetMHz is { } co)
            rows.Add(("Frekans ofseti", co == 0 ? "0 MHz (varsayılan)" : $"{Signed(co)} MHz", $"İzin verilen aralık {Signed(g.ClockOffsetMin ?? 0)}…{Signed(g.ClockOffsetMax ?? 0)} MHz."));
        if (g.MaxClockMHz is { } mc)
            rows.Add(("En yüksek çekirdek saati", mc == g.MaxClockDefault ? $"{mc} MHz (varsayılan)" : $"{mc} MHz (varsayılan {g.MaxClockDefault})", null));
        if (g.VramMaxMHz is { } vm)
            rows.Add(("VRAM saati", vm == g.VramMaxDefault ? $"{vm} MHz (varsayılan)" : $"{vm} MHz (hız aşırtma, varsayılan {g.VramMaxDefault})", null));
        if (g.FastMemoryTiming is { } fm) rows.Add(("Hızlı bellek zamanlaması", fm ? "Açık" : "Kapalı", null));
        var auto = new List<string>();
        if (g.AutoUndervolt == true) auto.Add("otomatik undervolt");
        if (g.AutoOverclock == true) auto.Add("otomatik hız aşırtma");
        if (g.AutoOverclockMemory == true) auto.Add("otomatik bellek aşırtma");
        rows.Add(("Otomatik ayar", auto.Count > 0 ? string.Join(", ", auto) : "Kapalı (elle ayar)", null));
        var fan = new List<string>();
        if (g.ZeroRpm is { } z) fan.Add(z ? "sıfır devir açık" : "sıfır devir kapalı");
        if (g.CustomFanCurve is { } cf) fan.Add(cf ? "özel fan eğrisi" : "varsayılan eğri");
        if (fan.Count > 0)
        {
            var text = string.Join(" · ", fan);
            rows.Add(("Fan", char.ToUpper(text[0], Tr) + text[1..], null));
        }
        return rows;
    }

    /// <summary>Sıcaklık durumu: renk her zaman simge ve metinle birlikte gösterilir.</summary>
    private static void SetTempState(TextBlock icon, TextBlock text, double? t, double warn, double crit, SensorLevel level)
    {
        if (t is not { } v)
        {
            icon.Text = "";
            icon.Foreground = ThemeManager.Brush("TextMuted");
            text.Text = level switch
            {
                SensorLevel.Basic => "Sıcaklık için yönetici modu gerekir",
                SensorLevel.AdminNoDriver => "Sıcaklık için PawnIO sürücüsü gerekir",
                _ => "Sensör okunamadı",
            };
            return;
        }
        var (glyph, brush, label) = v >= crit ? ("", "CriticalBrush", "Çok sıcak")
            : v >= warn ? ("", "WarningBrush", "Sıcak")
            : ("", "GoodBrush", "Normal");
        icon.Text = glyph;
        icon.Foreground = ThemeManager.Brush(brush);
        text.Text = $"{label} · uyarı sınırı {warn:0} °C";
    }

    private void UpdateSensorBar(SensorLevel level)
    {
        if (_shownLevel == level) return;
        _shownLevel = level;
        SensorBar.Visibility = Visibility.Visible;
        SensorButton.Visibility = Visibility.Visible;
        switch (level)
        {
            case SensorLevel.Basic:
                SensorIcon.Text = "";
                SensorText.Text = "Kullanım, hız, ekran kartı sıcaklığı ve ayarları okunuyor. İşlemci sıcaklığı, PBO, bellek çipi ve zamanlamaları için uygulamanın yönetici modunda çalışması gerekir.";
                SensorButton.Content = "Yönetici olarak yeniden başlat";
                break;
            case SensorLevel.AdminNoDriver:
                SensorIcon.Text = "";
                SensorText.Text = "İşlemci sıcaklığı, PBO, bellek çipi ve zamanlamaları için PawnIO sürücüsü gerekiyor. Açık kaynaklı, Microsoft onaylı ve imzalıdır; LibreHardwareMonitor da aynı sürücüyü kullanır.";
                SensorButton.Content = "PawnIO sürücüsünü kur";
                break;
            default:
                SensorBar.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async void SensorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_shownLevel == SensorLevel.Basic)
        {
            if (Admin.RestartElevated("--page monitor")) Application.Current.Shutdown();
            return;
        }
        if (!Dialogs.Confirm("PawnIO sürücüsü kurulsun mu?",
                "İşlemci sıcaklığı, PBO, bellek çipi ve zamanlamalarını okumak için PawnIO sürücüsü Windows Paket Yöneticisi (winget) ile kurulacak. Kurulumdan sonra izleyici kendiliğinden yeniden başlar.",
                "Kur"))
            return;
        var task = Task.Run(() =>
        {
            var psi = new ProcessStartInfo("winget", "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements")
            { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        });
        int code;
        try { code = await WaitDialog.WhileAsync("PawnIO kuruluyor", "Sürücü indirilip kuruluyor…", task); }
        catch (Exception ex) { Dialogs.Error("Kurulamadı", ex.Message); return; }

        if (!HardwareMonitor.PawnIoInstalled)
        {
            Dialogs.Error("PawnIO kurulamadı", $"winget çıkış kodu: {code}. pawnio.eu adresinden kurulum dosyasını indirip elle kurabilirsiniz.");
            return;
        }
        ActivityLog.Write("PawnIO sürücüsü kuruldu", "namazso.PawnIO", 0, 1, 0, "winget");
        _timer.Stop();
        _hm?.Dispose();
        _hm = null;
        _shownLevel = null;
        OnShown();
    }
}
