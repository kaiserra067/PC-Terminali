using System.Runtime.InteropServices;
using LibreHardwareMonitor.PawnIo;

namespace TemizlikPaneli.Core;

/// <summary>Ekran kartı ayarları (AMD Adrenalin / Overdrive8). Yalnızca okunur.</summary>
public sealed class GpuTuning
{
    public int? ClockOffsetMHz, ClockOffsetMin, ClockOffsetMax;
    public int? MaxClockMHz, MaxClockDefault;           // eski kartlarda mutlak üst frekans
    public int? VoltageOffsetMV, VoltageOffsetMin;
    public int? PowerLimitPct, PowerLimitMin, PowerLimitMax;
    public int? VramMaxMHz, VramMaxDefault;
    public bool? ZeroRpm, AutoUndervolt, AutoOverclock, AutoOverclockMemory, FastMemoryTiming;
    public bool? CustomFanCurve;
}

/// <summary>İşlemci ayarları: SMU güç tablosundan (yalnızca doğrulanmış tablo sürümlerinde).</summary>
public sealed class CpuTuning
{
    public double PptLimitW, PptW, CurrentLimitA, ThermalLimitC, BoostLimitGHz;
    public double FclkMHz, UclkMHz, MclkMHz;
}

/// <summary>Bellek modülü bilgileri (SPD) ve anlık zamanlamalar (bellek denetleyicisi).</summary>
public sealed class RamDetails
{
    public string DramVendor = "";
    public int DieDensityGb;
    public DateTime? ManufactureDate;
    public int? CL, RCD, RP, RAS, RC, CWL;
    public double? MemClockMHz;
}

/// <summary>AMD Display Library (atiadlxx.dll) — yönetici izni gerekmez.</summary>
internal static class AdlTuning
{
    private delegate IntPtr AllocFn(int size);
    private static readonly AllocFn Alloc = size => Marshal.AllocHGlobal(size);
    private static IntPtr _ctx;
    private static int _adapter = -2;

    [DllImport("atiadlxx.dll")] private static extern int ADL2_Main_Control_Create(AllocFn cb, int connectedOnly, out IntPtr ctx);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr ctx, out int n);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr ctx, IntPtr info, int size);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Init_Setting_Get(IntPtr ctx, int adapter, IntPtr init);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Current_Setting_Get(IntPtr ctx, int adapter, IntPtr cur);

    private const int AdapterInfoSize = 1572, Od8Count = 73;
    // Özellik kimlikleri (ADL_OD8_*)
    private const int GfxClock = 0x1, UclkMax = 0x4, PowerLimit = 0x8, MemTiming = 0x100, ZeroRpmF = 0x200,
        AutoUv = 0x400, AutoOc = 0x800, AutoOcMem = 0x1000, FanCurve = 0x2000, VoltOffset = 0x20000;

    private static bool Init()
    {
        if (_adapter != -2) return _adapter >= 0;
        _adapter = -1;
        try
        {
            if (ADL2_Main_Control_Create(Alloc, 1, out _ctx) != 0) return false;
            if (ADL2_Adapter_NumberOfAdapters_Get(_ctx, out int n) != 0 || n <= 0) return false;
            var buf = Marshal.AllocHGlobal(AdapterInfoSize * n);
            try
            {
                if (ADL2_Adapter_AdapterInfo_Get(_ctx, buf, AdapterInfoSize * n) != 0) return false;
                for (int i = 0; i < n; i++)
                {
                    var p = buf + i * AdapterInfoSize;
                    int vendor = Marshal.ReadInt32(p, 276), present = Marshal.ReadInt32(p, 792);
                    if ((vendor == 1002 || vendor == 0x1002) && present != 0) { _adapter = Marshal.ReadInt32(p, 4); break; }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { _adapter = -1; } // AMD sürücüsü yok
        return _adapter >= 0;
    }

    public static GpuTuning? Read()
    {
        if (!Init()) return null;
        var init = Marshal.AllocHGlobal(8 + Od8Count * 16);
        var cur = Marshal.AllocHGlobal(4 + Od8Count * 4);
        try
        {
            for (int i = 0; i < 8 + Od8Count * 16; i += 4) Marshal.WriteInt32(init, i, 0);
            for (int i = 0; i < 4 + Od8Count * 4; i += 4) Marshal.WriteInt32(cur, i, 0);
            Marshal.WriteInt32(init, 0, Od8Count);
            Marshal.WriteInt32(cur, 0, Od8Count);
            if (ADL2_Overdrive8_Init_Setting_Get(_ctx, _adapter, init) != 0 || ADL2_Overdrive8_Current_Setting_Get(_ctx, _adapter, cur) != 0) return null;

            // ADLOD8SingleInitSetting: featureID, minValue, maxValue, defaultValue
            var items = new List<(int Feature, int Min, int Max, int Def, int Cur)>();
            for (int i = 0; i < Od8Count; i++)
            {
                int o = 8 + i * 16;
                items.Add((Marshal.ReadInt32(init, o), Marshal.ReadInt32(init, o + 4), Marshal.ReadInt32(init, o + 8), Marshal.ReadInt32(init, o + 12), Marshal.ReadInt32(cur, 4 + i * 4)));
            }
            var t = new GpuTuning();
            var gfx = items.FirstOrDefault(x => x.Feature == GfxClock && (x.Min != 0 || x.Max != 0));
            if (gfx.Feature != 0)
            {
                if (gfx.Min < 0) { t.ClockOffsetMHz = gfx.Cur; t.ClockOffsetMin = gfx.Min; t.ClockOffsetMax = gfx.Max; }
                else { t.MaxClockMHz = gfx.Cur; t.MaxClockDefault = gfx.Def; }
            }
            if (Find(items, VoltOffset) is { } v && v.Min < 0) { t.VoltageOffsetMV = v.Cur; t.VoltageOffsetMin = v.Min; }
            if (Find(items, PowerLimit) is { } p) { t.PowerLimitPct = p.Cur; t.PowerLimitMin = p.Min; t.PowerLimitMax = p.Max; }
            if (Find(items, UclkMax) is { } u) { t.VramMaxMHz = u.Cur; t.VramMaxDefault = u.Def; }
            if (Find(items, ZeroRpmF) is { } z) t.ZeroRpm = z.Cur != 0;
            if (Find(items, AutoUv) is { } a1) t.AutoUndervolt = a1.Cur != 0;
            if (Find(items, AutoOc) is { } a2) t.AutoOverclock = a2.Cur != 0;
            if (Find(items, AutoOcMem) is { } a3) t.AutoOverclockMemory = a3.Cur != 0;
            if (Find(items, MemTiming) is { } m) t.FastMemoryTiming = m.Cur != 0;
            var fan = items.Where(x => x.Feature == FanCurve).ToList();
            if (fan.Count > 0) t.CustomFanCurve = fan.Any(x => x.Cur != x.Def);
            return t;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(init); Marshal.FreeHGlobal(cur); }
    }

    private static (int Feature, int Min, int Max, int Def, int Cur)? Find(List<(int Feature, int Min, int Max, int Def, int Cur)> items, int feature)
    {
        foreach (var x in items) if (x.Feature == feature) return x;
        return null;
    }
}

/// <summary>AMD işlemci ayrıntıları: güç tablosu ve bellek denetleyicisi (yönetici + PawnIO). Yalnızca okur.</summary>
internal sealed class AmdCpuReader : IDisposable
{
    private RyzenSmu? _smu;
    private uint _tableVersion;

    /// <summary>Alan yerlerini doğruladığımız güç tablosu sürümleri (Zen 4 masaüstü, Raphael).</summary>
    private static readonly HashSet<uint> VerifiedTables = new() { 0x540104 };

    public uint TableVersion => _tableVersion;

    public CpuTuning? ReadTuning()
    {
        try
        {
            if (_smu == null)
            {
                _smu = new RyzenSmu();
                _smu.ResolvePmTable(out _tableVersion, out _);
            }
            if (!VerifiedTables.Contains(_tableVersion)) return null;
            _smu.UpdatePmTable();
            var raw = _smu.ReadPmTable(0x100);
            float F(int i) => i / 2 < raw.Length
                ? BitConverter.Int32BitsToSingle((int)(i % 2 == 0 ? raw[i / 2] & 0xFFFFFFFF : raw[i / 2] >> 32))
                : 0;
            var t = new CpuTuning
            {
                PptLimitW = F(2), PptW = F(3), CurrentLimitA = F(8), ThermalLimitC = F(10),
                FclkMHz = F(70), UclkMHz = F(74), MclkMHz = F(78),
                BoostLimitGHz = Enumerable.Range(365, 8).Max(i => (double)F(i)),
            };
            // Tutarlılık denetimi: beklenmeyen değerler varsa hiç gösterme
            if (t.ThermalLimitC is < 50 or > 115 || t.MclkMHz is < 400 or > 6000 || t.BoostLimitGHz is < 1 or > 7) return null;
            return t;
        }
        catch { return null; } // başka bir izleme programı veri yolunu tutuyor olabilir
    }

    /// <summary>Anlık bellek zamanlamaları (UMC yazmaçları, Zen 4/Zen 5). ZenTimings'in okuduğu yazmaçlar.</summary>
    public static RamDetails? ReadUmcTimings()
    {
        try
        {
            var amd = new AmdFamily17();
            try
            {
                const uint umc = 0x50000;
                uint cfg = amd.ReadSmn(umc + 0x200), t1 = amd.ReadSmn(umc + 0x204), t2 = amd.ReadSmn(umc + 0x208), t5 = amd.ReadSmn(umc + 0x214);
                var d = new RamDetails
                {
                    MemClockMHz = cfg & 0xFFFF,
                    CL = (int)(t1 & 0x3F),
                    RAS = (int)((t1 >> 8) & 0x7F),
                    RCD = (int)((t1 >> 16) & 0x3F),
                    RC = (int)(t2 & 0xFF),
                    RP = (int)((t2 >> 16) & 0x3F),
                    CWL = (int)(t5 & 0x3F),
                };
                bool sane = d.MemClockMHz is >= 800 and <= 5000 && d.CL is >= 8 and <= 80 && d.RCD is >= 8 and <= 100 && d.RP is >= 8 and <= 100 && d.RAS >= d.RCD;
                return sane ? d : null;
            }
            finally { amd.Close(); }
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _smu?.Close(); } catch { }
        _smu = null;
    }
}

/// <summary>Bellek modüllerinin SPD bilgisi (yönetici + PawnIO).</summary>
internal static class SpdReader
{
    public static (string Vendor, int DensityGb, DateTime? Date)? Read()
    {
        try
        {
            // Veri yolları LibreHardwareMonitor'ın bellek modülü tarafından PawnIO ile hazırlanır (bkz. HardwareMonitor.Start)
            if (RAMSPDToolkit.I2CSMBus.SMBusManager.RegisteredSMBuses.Count == 0)
            {
                if (!RAMSPDToolkit.Windows.Driver.DriverManager.LoadDriver()) return null;
                RAMSPDToolkit.I2CSMBus.SMBusManager.DetectSMBuses();
            }
            foreach (var bus in RAMSPDToolkit.I2CSMBus.SMBusManager.RegisteredSMBuses)
            {
                foreach (var type in new[] { RAMSPDToolkit.SPD.Interop.Shared.SPDMemoryType.SPD_DDR5_SDRAM, RAMSPDToolkit.SPD.Interop.Shared.SPDMemoryType.SPD_DDR4_SDRAM })
                    for (byte a = 0x50; a <= 0x57; a++)
                    {
                        var det = new RAMSPDToolkit.SPD.SPDDetector(bus, a, type);
                        if (!det.IsValid || det.Accessor == null) continue;
                        var x = det.Accessor;
                        int density = 0;
                        if (type == RAMSPDToolkit.SPD.Interop.Shared.SPDMemoryType.SPD_DDR5_SDRAM)
                            density = (x.At(4) & 0x1F) switch { 1 => 4, 2 => 8, 3 => 12, 4 => 16, 5 => 24, 6 => 32, 7 => 48, 8 => 64, _ => 0 };
                        return (x.GetDRAMManufacturerString()?.Trim() ?? "", density, x.ModuleManufacturingDate());
                    }
            }
        }
        catch { }
        return null;
    }
}
