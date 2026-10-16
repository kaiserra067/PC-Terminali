using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace TemizlikPaneli.Core;

/// <summary>Bir anlık donanım ölçümü. Okunamayan değerler null kalır.</summary>
public sealed class HwSnapshot
{
    // İşlemci
    public string CpuName = "";
    public int Cores, Threads;
    public double CpuUsage;
    public double CpuClockMHz;
    public double CpuBaseMHz;
    public double? CpuTemp;
    public double? CpuPowerW;
    public double[] CoreUsage = Array.Empty<double>();

    // Ekran kartı
    public string GpuName = "";
    public double? GpuUsage;
    public double? GpuTemp;
    public double? GpuCoreMHz;
    public double? GpuMemMHz;
    public double? GpuFanRpm;
    public double? GpuPowerPct;
    public double? GpuPowerW, GpuHotspot;
    public long VramUsed, VramTotal;

    // Bellek
    public long RamUsed, RamTotal;
    public int RamSpeedMTs;
    public string RamType = "";
    public int RamModules;
    public long RamModuleSize;
    /// <summary>Modülün markası ve parça numarası (ör. "G.Skill F5-6000J3038F16G").</summary>
    public string RamModuleName = "";
    public RamDetails? RamDetails;

    // Ayarlar (tuning)
    public CpuTuning? CpuTuning;
    public GpuTuning? GpuTuning;
    public bool IsAmdCpu;

    public SensorLevel Level;
}

public enum SensorLevel
{
    /// <summary>Yalnızca Windows'un sürücüsüz verdiği değerler.</summary>
    Basic,
    /// <summary>Yönetici modunda ama PawnIO sürücüsü kurulu değil.</summary>
    AdminNoDriver,
    /// <summary>Tüm sensörler (yönetici + PawnIO).</summary>
    Full,
}

/// <summary>
/// Donanım sensörleri. Temel katman sürücü ve yönetici izni istemez (PDH sayaçları, D3DKMT, DXGI).
/// Gelişmiş katman (işlemci/bellek sıcaklığı) LibreHardwareMonitor + PawnIO ile yalnızca yönetici modunda çalışır.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private IntPtr _query;
    private IntPtr _cpuUtil, _cpuPerf, _cpuFreq, _coreUtil, _gpuEngine, _gpuMem;
    private GpuInfo? _gpu;
    private Computer? _lhm;
    private AmdCpuReader? _amd;
    private readonly object _gate = new();
    private readonly HwSnapshot _static = new();
    private bool _first = true;
    private int _tick;
    private CpuTuning? _cpuTuning;
    private GpuTuning? _gpuTuning;

    private static string CleanVendor(string v) => v.Trim() switch
    {
        "G Skill Intl" or "G.Skill" or "GSkill" => "G.Skill",
        "Kingston" => "Kingston",
        "Corsair" => "Corsair",
        "Samsung" or "Samsung Electronics" => "Samsung",
        "Crucial Technology" or "Micron Technology" => "Crucial",
        "SK Hynix" or "Hynix" => "SK Hynix",
        var s => s,
    };

    public static bool PawnIoInstalled
    {
        get
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                return k != null;
            }
            catch { return false; }
        }
    }

    public void Start()
    {
        // ---- PDH sayaçları (Türkçe Windows'ta da çalışması için İngilizce adlarla)
        if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
        {
            Pdh.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuUtil);
            Pdh.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _cpuPerf);
            Pdh.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _cpuFreq);
            Pdh.PdhAddEnglishCounter(_query, @"\Processor Information(*)\% Processor Utility", IntPtr.Zero, out _coreUtil);
            Pdh.PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuEngine);
            Pdh.PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _gpuMem);
            Pdh.PdhCollectQueryData(_query);
        }

        // ---- Sabit bilgiler
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _static.CpuName = (k?.GetValue("ProcessorNameString") as string ?? "İşlemci").Trim();
            _static.CpuBaseMHz = k?.GetValue("~MHz") is int mhz ? mhz : 0;
        }
        catch { }
        _static.Threads = Environment.ProcessorCount;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT NumberOfCores, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
            {
                _static.Cores += Convert.ToInt32(o["NumberOfCores"]);
                if (o["MaxClockSpeed"] is uint max && max > 0) _static.CpuBaseMHz = max;
            }
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed, SMBIOSMemoryType, Manufacturer, PartNumber, Capacity FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
            {
                _static.RamModules++;
                if (_static.RamModuleName.Length == 0)
                    _static.RamModuleName = $"{CleanVendor(o["Manufacturer"] as string ?? "")} {(o["PartNumber"] as string ?? "").Trim()}".Trim();
                _static.RamModuleSize = Math.Max(_static.RamModuleSize, Convert.ToInt64(o["Capacity"] ?? 0L));
                int speed = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0);
                if (speed > _static.RamSpeedMTs) _static.RamSpeedMTs = speed;
                _static.RamType = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) switch
                {
                    34 => "DDR5", 35 => "LPDDR5", 26 => "DDR4", 30 => "LPDDR4", 24 => "DDR3", 29 => "LPDDR3", _ => _static.RamType,
                };
            }
        }
        catch { }

        _gpu = GpuInfo.FindPrimary();
        if (_gpu != null)
        {
            _static.GpuName = _gpu.Name;
            _static.VramTotal = _gpu.DedicatedMemory;
        }

        _static.IsAmdCpu = _static.CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);

        // ---- Gelişmiş sensörler: yalnızca yönetici modunda (PawnIO sürücüsü kullanılır)
        if (Admin.IsElevated && PawnIoInstalled)
        {
            // Bellek modülü, SPD veri yolunu PawnIO ile hazırlar; bellek sıcaklıkları ise kullanılmaz (güvenilir değil).
            try
            {
                _lhm = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
                _lhm.Open();
            }
            catch { _lhm = null; }

            // Değişmeyen bilgiler bir kez okunur: modül SPD'si ve bellek denetleyicisindeki zamanlamalar
            (string Vendor, int DensityGb, DateTime? Date)? spd = null;
            for (int i = 0; i < 3 && spd == null; i++)
            {
                spd = SpdReader.Read();
                if (spd == null) Thread.Sleep(400);
            }
            var details = _static.IsAmdCpu ? AmdCpuReader.ReadUmcTimings() : null;

            if (spd != null)
            {
                details ??= new RamDetails();
                details.DramVendor = spd.Value.Vendor;
                details.DieDensityGb = spd.Value.DensityGb;
                details.ManufactureDate = spd.Value.Date;
            }
            _static.RamDetails = details;
            if (_static.IsAmdCpu) _amd = new AmdCpuReader();
        }
    }

    public HwSnapshot Sample()
    {
        lock (_gate)
        {
            var s = new HwSnapshot
            {
                CpuName = _static.CpuName, Cores = _static.Cores, Threads = _static.Threads, CpuBaseMHz = _static.CpuBaseMHz,
                GpuName = _static.GpuName, VramTotal = _static.VramTotal,
                RamSpeedMTs = _static.RamSpeedMTs, RamType = _static.RamType, RamModules = _static.RamModules,
                RamModuleName = _static.RamModuleName, RamModuleSize = _static.RamModuleSize, RamDetails = _static.RamDetails,
                IsAmdCpu = _static.IsAmdCpu,
                Level =!Admin.IsElevated ? SensorLevel.Basic : PawnIoInstalled ? SensorLevel.Full : SensorLevel.AdminNoDriver,
            };

            // ---- İşlemci (PDH)
            if (_query != IntPtr.Zero && Pdh.PdhCollectQueryData(_query) == 0 && !_first)
            {
                s.CpuUsage = Math.Clamp(Pdh.Value(_cpuUtil) ?? 0, 0, 100);
                double perf = Pdh.Value(_cpuPerf) ?? 0;
                double baseMhz = Pdh.Value(_cpuFreq) ?? s.CpuBaseMHz;
                if (baseMhz > 0) s.CpuBaseMHz = baseMhz;
                s.CpuClockMHz = baseMhz * perf / 100.0; // Görev Yöneticisi'nin "Hız" değeriyle aynı formül

                var cores = Pdh.Array(_coreUtil)
                    .Where(kv => !kv.Key.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                    .Select(kv => (Key: ParseCore(kv.Key), kv.Value))
                    .OrderBy(x => x.Key.Group).ThenBy(x => x.Key.Index)
                    .Select(x => Math.Clamp(x.Value, 0, 100)).ToArray();
                s.CoreUsage = cores;

                // ---- Ekran kartı kullanımı: her motor türünün toplamı, en yüksek olanı (Görev Yöneticisi gibi)
                if (_gpu != null)
                {
                    var byType = new Dictionary<string, double>();
                    foreach (var (name, val) in Pdh.Array(_gpuEngine))
                    {
                        if (!name.Contains(_gpu.LuidText, StringComparison.OrdinalIgnoreCase)) continue;
                        var m = EngType.Match(name);
                        var t = m.Success ? m.Groups[1].Value : "other";
                        byType[t] = byType.GetValueOrDefault(t) + val;
                    }
                    s.GpuUsage = byType.Count > 0 ? Math.Clamp(byType.Values.Max(), 0, 100) : 0;
                    foreach (var (name, val) in Pdh.Array(_gpuMem))
                        if (name.Contains(_gpu.LuidText, StringComparison.OrdinalIgnoreCase)) s.VramUsed += (long)val;
                }
            }
            _first = false;

            // ---- Ekran kartı sıcaklığı, saatler, fan (D3DKMT — Görev Yöneticisi'nin kaynağı)
            _gpu?.ReadPerf(s);

            // ---- Bellek
            var ms = new Native2.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native2.MEMORYSTATUSEX>() };
            if (Native2.GlobalMemoryStatusEx(ref ms))
            {
                s.RamTotal = (long)ms.ullTotalPhys;
                s.RamUsed = (long)(ms.ullTotalPhys - ms.ullAvailPhys);
            }

            // ---- Gelişmiş sensörler
            if (_lhm != null) ReadLhm(s);

            // ---- Ayarlar (tuning): beş saniyede bir yeterli
            if (_tick++ % 5 == 0)
            {
                _gpuTuning = AdlTuning.Read() ?? _gpuTuning;
                if (_amd != null) _cpuTuning = _amd.ReadTuning() ?? _cpuTuning;
            }
            s.GpuTuning = _gpuTuning;
            s.CpuTuning = _cpuTuning;
            return s;
        }
    }

    private static readonly Regex EngType = new(@"engtype_([A-Za-z0-9]+)", RegexOptions.Compiled);

    private static (int Group, int Index) ParseCore(string name)
    {
        var parts = name.Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out var g) && int.TryParse(parts[1], out var i) ? (g, i) : (99, 0);
    }

    private void ReadLhm(HwSnapshot s)
    {
        try
        {
            var clocks = new List<double>();
            foreach (var hw in _lhm!.Hardware)
            {
                if (hw.HardwareType == HardwareType.Memory) continue; // bellek sıcaklığı gösterilmiyor
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
                var sensors = hw.Sensors.Concat(hw.SubHardware.SelectMany(x => x.Sensors)).ToList();
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    // AMD: "Core (Tctl/Tdie)", Intel: "CPU Package"
                    var temp = sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value is > 0 &&
                                   (x.Name.Contains("Tctl") || x.Name.Contains("Tdie") || x.Name.Contains("Package")))
                               ?? sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value is > 0 && x.Name.Contains("Core"));
                    if (temp?.Value is { } t) s.CpuTemp = t;
                    var power = sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && x.Name.Contains("Package") && x.Value is > 0);
                    if (power?.Value is { } p) s.CpuPowerW = p;
                    clocks.AddRange(sensors.Where(x => x.SensorType == SensorType.Clock && x.Name.StartsWith("Core #") && x.Value is > 100).Select(x => (double)x.Value!.Value));
                }
                else if (hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
                {
                    var hot = sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Name.Contains("Hot Spot") && x.Value is > 0);
                    if (hot?.Value is { } h) s.GpuHotspot = h;
                    var pw = sensors.FirstOrDefault(x => x.SensorType == SensorType.Power && (x.Name.Contains("Package") || x.Name.Contains("Total")) && x.Value is > 0);
                    if (pw?.Value is { } w) s.GpuPowerW = w;
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_query != IntPtr.Zero) { Pdh.PdhCloseQuery(_query); _query = IntPtr.Zero; }
            _gpu?.Dispose();
            _amd?.Dispose();
            try { _lhm?.Close(); } catch { }
            _lhm = null;
        }
    }
}

// ====================================================================== PDH

internal static class Pdh
{
    private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQuery(string? src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint fmt, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint fmt, ref uint size, out uint count, IntPtr buffer);

    public static double? Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0 && v.CStatus <= 1 ? v.doubleValue : null;
    }

    public static List<KeyValuePair<string, double>> Array(IntPtr counter)
    {
        var list = new List<KeyValuePair<string, double>>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buf) != 0) return list;
            int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * stride);
                if (item.FmtValue.CStatus > 1) continue;
                list.Add(new(Marshal.PtrToStringUni(item.szName) ?? "", item.FmtValue.doubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }
}

internal static class Native2
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}

// ====================================================================== Ekran kartı (DXGI + D3DKMT)

internal sealed class GpuInfo : IDisposable
{
    public string Name = "";
    public long DedicatedMemory;
    public uint LuidLow;
    public int LuidHigh;
    public string LuidText => $"luid_0x{(uint)LuidHigh:X8}_0x{LuidLow:X8}";
    private uint _hAdapter;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc1Fn(IntPtr self, out DXGI_ADAPTER_DESC1 desc);

    [StructLayout(LayoutKind.Sequential)] private struct D3DKMT_OPENADAPTERFROMLUID { public LUID AdapterLuid; public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential)] private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential)] private struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power, Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_NODE_PERFDATA
    {
        public uint NodeOrdinal, PhysicalAdapterIndex;
        public ulong Frequency, MaxFrequency, MaxFrequencyOC;
        public uint Voltage, VoltageMax, VoltageMaxOC;
        public ulong MaxTransitionLatency;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);

    private const int KMTQAITYPE_NODEPERFDATA = 61, KMTQAITYPE_ADAPTERPERFDATA = 62;

    /// <summary>En çok ayrılmış belleğe sahip donanım ekran kartı (yazılım bağdaştırıcıları hariç).</summary>
    public static GpuInfo? FindPrimary()
    {
        GpuInfo? best = null;
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out factory) != 0) return null;
            var enumFn = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Fn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            for (uint i = 0; enumFn(factory, i, out var adapter) == 0; i++)
            {
                try
                {
                    var descFn = Marshal.GetDelegateForFunctionPointer<GetDesc1Fn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                    if (descFn(adapter, out var d) != 0 || (d.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                    long mem = (long)d.DedicatedVideoMemory.ToUInt64();
                    if (best == null || mem > best.DedicatedMemory)
                        best = new GpuInfo { Name = d.Description.Trim(), DedicatedMemory = mem, LuidLow = d.AdapterLuid.LowPart, LuidHigh = d.AdapterLuid.HighPart };
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch { }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }

        if (best != null)
        {
            var open = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = new LUID { LowPart = best.LuidLow, HighPart = best.LuidHigh } };
            if (D3DKMTOpenAdapterFromLuid(ref open) == 0) best._hAdapter = open.hAdapter;
        }
        return best;
    }

    private T? Query<T>(int type, T value) where T : struct
    {
        if (_hAdapter == 0) return null;
        int size = Marshal.SizeOf<T>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, buf, false);
            var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = _hAdapter, Type = type, pPrivateDriverData = buf, PrivateDriverDataSize = (uint)size };
            return D3DKMTQueryAdapterInfo(ref q) == 0 ? Marshal.PtrToStructure<T>(buf) : null;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void ReadPerf(HwSnapshot s)
    {
        if (Query(KMTQAITYPE_ADAPTERPERFDATA, new D3DKMT_ADAPTER_PERFDATA()) is { } a)
        {
            if (a.Temperature is > 0 and < 1500) s.GpuTemp = a.Temperature / 10.0;   // onda bir °C
            if (a.MemoryFrequency > 0) s.GpuMemMHz = a.MemoryFrequency / 1e6;
            if (a.FanRPM is > 0 and < 20000) s.GpuFanRpm = a.FanRPM; else if (a.Temperature > 0) s.GpuFanRpm = 0;
            if (a.Power is > 0 and <= 1000) s.GpuPowerPct = a.Power / 10.0;           // onda bir %
        }
        double maxNode = 0;
        for (uint n = 0; n < 8; n++)
        {
            if (Query(KMTQAITYPE_NODEPERFDATA, new D3DKMT_NODE_PERFDATA { NodeOrdinal = n }) is not { } node) break;
            if (node.Frequency > maxNode) maxNode = node.Frequency;
        }
        if (maxNode > 0) s.GpuCoreMHz = maxNode / 1e6;
    }

    public void Dispose()
    {
        if (_hAdapter == 0) return;
        var c = new D3DKMT_CLOSEADAPTER { hAdapter = _hAdapter };
        D3DKMTCloseAdapter(ref c);
        _hAdapter = 0;
    }
}
