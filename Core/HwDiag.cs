using System.Runtime.InteropServices;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace TemizlikPaneli.Core;

/// <summary>
/// Geliştirme amaçlı donanım tanılama dökümü (yönetici + PawnIO). Yalnızca OKUR: LHM sensörleri, SPD, bellek denetleyicisi
/// yazmaçları (SMN), SMU güç tablosu ve AMD Overdrive8 ayarları. Hiçbir ayarı değiştirmez.
/// </summary>
public static class HwDiag
{
    public static void Run(string outFile)
    {
        var sb = new StringBuilder();
        void W(string s) { sb.AppendLine(s); try { File.WriteAllText(outFile, sb.ToString()); } catch { } }
        W($"Tanılama {DateTime.Now} yönetici={Admin.IsElevated} PawnIO={HardwareMonitor.PawnIoInstalled}");

        // Bu çalıştırmada yalnızca eksik kalan bölümler (SMU ve Overdrive8) gerekiyorsa
        bool onlyMissing = Environment.GetCommandLineArgs().Contains("--yalniz-eksik");

        // 1) LHM sensörleri
        if (!onlyMissing)
        try
        {
            var c = new Computer { IsCpuEnabled = true, IsMemoryEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
            c.Open();
            foreach (var hw in c.Hardware)
            {
                hw.Update();
                W($"\n[{hw.HardwareType}] {hw.Name}");
                foreach (var s in hw.Sensors) W($"  {s.SensorType,-12} {s.Name,-40} {s.Value}");
                foreach (var sub in hw.SubHardware)
                {
                    sub.Update();
                    W($"  <{sub.HardwareType}> {sub.Name}");
                    foreach (var s in sub.Sensors) W($"    {s.SensorType,-12} {s.Name,-40} {s.Value}");
                }
            }
            c.Close();
        }
        catch (Exception ex) { W("LHM HATA: " + ex.Message); }

        // 2) SPD
        W("\n== SPD ==");
        if (!onlyMissing)
        try
        {
            W("Sürücü: " + RAMSPDToolkit.Windows.Driver.DriverManager.LoadDriver());
            RAMSPDToolkit.I2CSMBus.SMBusManager.DetectSMBuses();
            foreach (var bus in RAMSPDToolkit.I2CSMBus.SMBusManager.RegisteredSMBuses)
            {
                W("Veri yolu: " + bus.GetType().Name);
                for (byte a = 0x50; a <= 0x57; a++)
                {
                    var det = new RAMSPDToolkit.SPD.SPDDetector(bus, a, RAMSPDToolkit.SPD.Interop.Shared.SPDMemoryType.SPD_DDR5_SDRAM);
                    if (!det.IsValid || det.Accessor == null) continue;
                    var x = det.Accessor;
                    W($"  0x{a:X2}: modül={x.GetModuleManufacturerString()} parça={x.ModulePartNumber()?.Trim()} çip={x.GetDRAMManufacturerString()} " +
                      $"kapasite={x.GetCapacity()} tarih={x.ModuleManufacturingDate()} seri={x.ModuleSerialNumber()}");
                    var bytes = new StringBuilder();
                    foreach (ushort i in new ushort[] { 0, 1, 2, 4, 5, 6, 8, 9, 10, 20, 21, 30, 31, 32, 33, 512, 513, 514, 552, 553, 554, 640, 641, 642 })
                        bytes.Append($"{i}={x.At(i):X2} ");
                    W("    baytlar: " + bytes);
                    var expo = new StringBuilder();
                    for (ushort i = 832; i < 896; i++) expo.Append($"{x.At(i):X2} ");
                    W("    832..895: " + expo);
                }
            }
        }
        catch (Exception ex) { W("SPD HATA: " + ex); }

        // 3) Bellek denetleyicisi yazmaçları (SMN, yalnızca okuma)
        W("\n== SMN UMC ==");
        try
        {
            var amd = new LibreHardwareMonitor.PawnIo.AmdFamily17();
            foreach (uint ch in new uint[] { 0x50000, 0x150000 })
            {
                var line = new StringBuilder();
                for (uint off = 0x200; off <= 0x2FC; off += 4) line.Append($"{off:X3}={amd.ReadSmn(ch + off):X8} ");
                W($"  UMC 0x{ch:X}: {line}");
            }
            amd.Close();
        }
        catch (Exception ex) { W("SMN HATA: " + ex.Message); }

        // 4) SMU güç tablosu (başka izleme programı kilidi tutuyorsa birkaç kez dene)
        W("\n== SMU PM tablosu ==");
        for (int attempt = 1; attempt <= 5; attempt++)
        try
        {
            var smu = new LibreHardwareMonitor.PawnIo.RyzenSmu();
            W($"  SMU sürümü={smu.GetSmuVersion():X8} kod adı={smu.GetCodeName()}");
            smu.ResolvePmTable(out uint ver, out uint tbase);
            W($"  tablo sürümü={ver:X8} taban={tbase:X8}");
            smu.UpdatePmTable();
            var t = smu.ReadPmTable(0x200);
            var line = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                long v = t[i];
                float lo = BitConverter.Int32BitsToSingle((int)(v & 0xFFFFFFFF));
                float hi = BitConverter.Int32BitsToSingle((int)(v >> 32));
                line.Append($"[{i * 2}]={lo:0.###} [{i * 2 + 1}]={hi:0.###}  ");
                if (i % 8 == 7) { W("  " + line); line.Clear(); }
            }
            if (line.Length > 0) W("  " + line);
            smu.Close();
            break;
        }
        catch (Exception ex) { W($"SMU HATA (deneme {attempt}): " + ex.Message); Thread.Sleep(1500); }

        // 5) AMD Overdrive8 (ekran kartı ayarları, yalnızca okuma)
        W("\n== AMD Overdrive8 ==");
        try { W(Adl.Dump()); } catch (Exception ex) { W("ADL HATA: " + ex.Message); }
        W("\nBitti.");
    }
}

/// <summary>AMD Display Library: Overdrive8 ayarlarını okur (değiştirmez).</summary>
internal static class Adl
{
    private delegate IntPtr AllocFn(int size);
    private static readonly AllocFn Alloc = size => Marshal.AllocHGlobal(size);

    [DllImport("atiadlxx.dll")] private static extern int ADL2_Main_Control_Create(AllocFn cb, int connectedOnly, out IntPtr ctx);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Main_Control_Destroy(IntPtr ctx);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr ctx, out int n);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr ctx, IntPtr info, int size);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive_Caps(IntPtr ctx, int adapter, out int supported, out int enabled, out int version);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Init_SettingX2_Get(IntPtr ctx, int adapter, out int caps, out int count, out IntPtr list);
    [DllImport("atiadlxx.dll")] private static extern int ADL2_Overdrive8_Current_SettingX2_Get(IntPtr ctx, int adapter, out int count, out IntPtr list);

    private const int AdapterInfoSize = 1572;

    public static int FindAdapter(IntPtr ctx)
    {
        if (ADL2_Adapter_NumberOfAdapters_Get(ctx, out int n) != 0 || n <= 0) return -1;
        var buf = Marshal.AllocHGlobal(AdapterInfoSize * n);
        try
        {
            if (ADL2_Adapter_AdapterInfo_Get(ctx, buf, AdapterInfoSize * n) != 0) return -1;
            for (int i = 0; i < n; i++)
            {
                var p = buf + i * AdapterInfoSize;
                int index = Marshal.ReadInt32(p, 4);
                int vendor = Marshal.ReadInt32(p, 8 + 256 + 12);
                int present = Marshal.ReadInt32(p, 8 + 256 + 16 + 512);
                if ((vendor == 1002 || vendor == 0x1002) && present != 0) return index; // ADL kimliği ondalık "1002" verir
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return -1;
    }

    public static string Dump()
    {
        var sb = new StringBuilder();
        if (ADL2_Main_Control_Create(Alloc, 1, out var ctx) != 0) return "ADL başlatılamadı (AMD sürücüsü yok?)";
        try
        {
            int ad = FindAdapter(ctx);
            sb.AppendLine($"bağdaştırıcı={ad}");
            if (ad < 0) return sb.ToString();
            ADL2_Overdrive_Caps(ctx, ad, out int sup, out int en, out int ver);
            sb.AppendLine($"OD destek={sup} etkin={en} sürüm={ver}");
            if (ADL2_Overdrive8_Init_SettingX2_Get(ctx, ad, out int caps, out int n, out var init) == 0 &&
                ADL2_Overdrive8_Current_SettingX2_Get(ctx, ad, out int n2, out var cur) == 0)
            {
                sb.AppendLine($"yetenekler=0x{caps:X} özellik sayısı={n}/{n2}");
                for (int i = 0; i < Math.Min(n, n2); i++)
                {
                    int feature = Marshal.ReadInt32(init, i * 16), def = Marshal.ReadInt32(init, i * 16 + 4);
                    int min = Marshal.ReadInt32(init, i * 16 + 8), max = Marshal.ReadInt32(init, i * 16 + 12);
                    int value = Marshal.ReadInt32(cur, i * 4);
                    if (feature != 0 || def != 0 || min != 0 || max != 0 || value != 0)
                        sb.AppendLine($"  [{i}] özellik={feature} varsayılan={def} aralık={min}..{max} şimdiki={value}");
                }
            }
            else sb.AppendLine("Overdrive8 ayarları okunamadı");
        }
        finally { ADL2_Main_Control_Destroy(ctx); }
        return sb.ToString();
    }
}
