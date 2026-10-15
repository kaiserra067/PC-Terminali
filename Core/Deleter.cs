namespace TemizlikPaneli.Core;

public sealed class DeleteStats
{
    public long Bytes;
    public int Items;
    public int Failed;
    public readonly List<string> Errors = new();

    public void AddError(string msg)
    {
        Failed++;
        if (Errors.Count < 200) Errors.Add(msg);
    }

    public void Merge(DeleteStats o)
    {
        Bytes += o.Bytes; Items += o.Items; Failed += o.Failed;
        foreach (var e in o.Errors) if (Errors.Count < 200) Errors.Add(e);
    }

    public string Summary =>
        Failed == 0
            ? $"{Fmt.Size(Bytes)} boşaltıldı ({Fmt.Count(Items)} öğe)."
            : $"{Fmt.Size(Bytes)} boşaltıldı ({Fmt.Count(Items)} öğe). {Fmt.Count(Failed)} öğe kullanımda olduğu ya da izin gerektiği için atlandı.";
}

/// <summary>Silinecek bir öğe ve (biliniyorsa) boyutu.</summary>
public readonly record struct DeleteItem(string Path, long Size = -1);

public static class Deleter
{
    private const FileAttributes CloudOnly = (FileAttributes)0x00400000 | FileAttributes.Offline | (FileAttributes)0x00040000;

    private static readonly EnumerationOptions OneLevel = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public static bool IsLink(FileSystemInfo fi)
    {
        if ((fi.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        try { return fi.LinkTarget != null; } catch { return true; }
    }

    // ------------------------------------------------------------------ Kalıcı silme

    /// <summary>Kullanıcının seçtiği dosya/klasörleri kalıcı olarak siler.</summary>
    public static DeleteStats DeletePermanent(IReadOnlyList<DeleteItem> items, IProgress<string>? progress, CancellationToken ct)
    {
        var st = new DeleteStats();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (!SafetyGuard.CanDelete(item.Path, out var why)) { st.AddError($"{item.Path}: {why}"); continue; }
            progress?.Report(item.Path);
            try
            {
                var attr = File.GetAttributes(item.Path);
                if ((attr & FileAttributes.Directory) != 0) DeleteTree(item.Path, st, ct, removeRoot: true, olderThan: null);
                else DeleteFile(new FileInfo(item.Path), st);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) { st.AddError($"{item.Path}: {ex.Message}"); }
        }
        return st;
    }

    /// <summary>
    /// Bir klasörün içeriğini temizler (klasörün kendisi kalır).
    /// pattern verilirse yalnızca eşleşen dosyalar silinir; olderThan verilirse daha yeni dosyalara dokunulmaz.
    /// </summary>
    public static DeleteStats CleanFolder(string root, string? pattern, bool recursive, DateTime? olderThan, CancellationToken ct)
    {
        var st = new DeleteStats();
        if (!Directory.Exists(root)) return st;
        var rootInfo = new DirectoryInfo(root);
        if (IsLink(rootInfo)) return st;

        if (pattern == null && recursive)
        {
            FileSystemInfo[] children;
            try { children = rootInfo.GetFileSystemInfos("*", OneLevel); }
            catch (Exception ex) { st.AddError($"{root}: {ex.Message}"); return st; }

            foreach (var c in children)
            {
                ct.ThrowIfCancellationRequested();
                if (!SafetyGuard.CanDelete(c.FullName)) continue;
                if (c is DirectoryInfo d)
                {
                    if (IsLink(d)) continue; // kök içindeki bağlantılara dokunma
                    DeleteTree(d.FullName, st, ct, removeRoot: true, olderThan);
                }
                else if (c is FileInfo f)
                {
                    if (olderThan is { } t && IsRecent(f, t)) continue;
                    DeleteFile(f, st);
                }
            }
            return st;
        }

        var opts = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            RecurseSubdirectories = recursive,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        IEnumerable<FileInfo> files;
        try { files = rootInfo.EnumerateFiles(pattern ?? "*", opts); }
        catch (Exception ex) { st.AddError($"{root}: {ex.Message}"); return st; }
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (olderThan is { } t && IsRecent(f, t)) continue;
            if (!SafetyGuard.CanDelete(f.FullName)) continue;
            DeleteFile(f, st);
        }
        return st;
    }

    private static bool IsRecent(FileSystemInfo f, DateTime threshold)
    {
        try { return f.LastWriteTime > threshold || f.CreationTime > threshold; }
        catch { return true; }
    }

    private static void DeleteTree(string dir, DeleteStats st, CancellationToken ct, bool removeRoot, DateTime? olderThan)
    {
        var di = new DirectoryInfo(dir);
        if (IsLink(di))
        {
            // Bağlantı: hedefin içine girmeden yalnızca bağlantının kendisini kaldır.
            if (removeRoot) { try { di.Delete(false); st.Items++; } catch (Exception ex) { st.AddError($"{dir}: {ex.Message}"); } }
            return;
        }

        DateTime created;
        try { created = di.CreationTime; } catch { created = DateTime.MaxValue; }

        FileSystemInfo[] entries;
        try { entries = di.GetFileSystemInfos("*", OneLevel); }
        catch (DirectoryNotFoundException) { return; }
        catch (Exception ex) { st.AddError($"{dir}: {ex.Message}"); return; }

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e is DirectoryInfo sub) DeleteTree(sub.FullName, st, ct, removeRoot: true, olderThan);
            else if (e is FileInfo f)
            {
                if (olderThan is { } t && IsRecent(f, t)) continue;
                DeleteFile(f, st);
            }
        }

        if (!removeRoot) return;
        if (olderThan is { } th && created > th) return; // yeni oluşturulmuş klasörlere dokunma
        try
        {
            if ((di.Attributes & FileAttributes.ReadOnly) != 0) di.Attributes &= ~FileAttributes.ReadOnly;
            di.Delete(false); // yalnızca boşsa silinir
        }
        catch (IOException) { /* boş değil: içinde atlanan dosyalar var */ }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteFile(FileInfo f, DeleteStats st)
    {
        long size = 0;
        try
        {
            var attr = f.Attributes;
            size = (attr & CloudOnly) != 0 ? 0 : f.Length;
            if ((attr & FileAttributes.ReadOnly) != 0) f.Attributes = attr & ~FileAttributes.ReadOnly;
            f.Delete();
            st.Bytes += size;
            st.Items++;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) { st.AddError($"{f.FullName}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ Geri Dönüşüm Kutusu

    /// <summary>Öğeleri Geri Dönüşüm Kutusu'na taşır. Sonuç, öğelerin gerçekten kaybolup kaybolmadığına bakılarak doğrulanır.</summary>
    public static Task<DeleteStats> RecycleAsync(IReadOnlyList<DeleteItem> items, IProgress<string>? progress, CancellationToken ct)
    {
        return Sta.Run(() =>
        {
            var st = new DeleteStats();
            var batch = new List<DeleteItem>();
            foreach (var item in items)
            {
                if (!SafetyGuard.CanDelete(item.Path, out var why)) { st.AddError($"{item.Path}: {why}"); continue; }
                if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) continue;
                batch.Add(item.Size >= 0 ? item : item with { Size = FsScan.Measure(item.Path, ct).Size });
                if (batch.Count >= 40) { RecycleBatch(batch, st, progress); batch.Clear(); }
                ct.ThrowIfCancellationRequested();
            }
            if (batch.Count > 0) RecycleBatch(batch, st, progress);
            return st;
        });
    }

    private static void RecycleBatch(List<DeleteItem> batch, DeleteStats st, IProgress<string>? progress)
    {
        progress?.Report(batch[0].Path);
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = Native.FO_DELETE,
            pFrom = string.Join("\0", batch.Select(b => b.Path)) + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI | Native.FOF_WANTNUKEWARNING),
        };
        try { Native.SHFileOperation(ref op); } catch { }

        foreach (var b in batch)
        {
            bool gone = !File.Exists(b.Path) && !Directory.Exists(b.Path);
            if (gone) { st.Items++; st.Bytes += Math.Max(0, b.Size); }
            else st.AddError($"{b.Path}: taşınamadı (kullanımda, izin gerekli ya da yol çok uzun)");
        }
    }

    // ------------------------------------------------------------------ Geri Dönüşüm Kutusu bilgisi

    public static (long Size, long Count) QueryRecycleBin()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        try
        {
            return Native.SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
        }
        catch { return (0, 0); }
    }

    public static bool EmptyRecycleBin()
    {
        try
        {
            int hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
            return hr == 0 || hr == unchecked((int)0x8000FFFF); // E_UNEXPECTED = zaten boş
        }
        catch { return false; }
    }

    /// <summary>Ayarlara göre (Geri Dönüşüm Kutusu ya da kalıcı) siler ve işlem geçmişine yazar.</summary>
    public static async Task<DeleteStats> DeleteUserItemsAsync(string action, IReadOnlyList<DeleteItem> items, bool? recycle = null, CancellationToken ct = default)
    {
        bool useBin = recycle ?? AppSettings.Current.UseRecycleBin;
        var st = useBin
            ? await RecycleAsync(items, null, ct)
            : await Task.Run(() => DeletePermanent(items, null, ct), ct);
        string target = items.Count == 1 ? items[0].Path : $"{items.Count} öğe";
        ActivityLog.Write(action, target, st.Bytes, st.Items, st.Failed, useBin ? "Geri Dönüşüm Kutusu" : "Kalıcı");
        return st;
    }
}
