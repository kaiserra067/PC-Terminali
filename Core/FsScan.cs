using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace TemizlikPaneli.Core;

public readonly record struct Measurement(long Size, long Files, long Dirs, DateTime Newest, bool Partial);

/// <summary>Dosya türü grupları. Sıra arayüzdeki gösterim sırasıdır.</summary>
public enum FileKind { Video, Image, Audio, Archive, Program, Document, GameData, Other }

public static class FileKinds
{
    public const int Count = 8;

    public static readonly string[] Names =
    {
        "Video", "Resim", "Ses ve müzik", "Arşiv ve disk görüntüsü", "Program ve sistem", "Belge", "Oyun verisi", "Diğer"
    };

    private static readonly Dictionary<string, FileKind> Map = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var d = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind k, string exts) { foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries)) d[e] = k; }
        Add(FileKind.Video, ".mp4 .mkv .avi .mov .wmv .flv .webm .m4v .mpg .mpeg .ts .m2ts .3gp .vob .bik .bk2 .usm");
        Add(FileKind.Image, ".jpg .jpeg .png .gif .bmp .tif .tiff .webp .heic .heif .raw .cr2 .nef .arw .dng .psd .svg .ico .tga .dds .avif .jxl");
        Add(FileKind.Audio, ".mp3 .wav .flac .aac .ogg .m4a .wma .opus .aiff .mid .flp .fsb .bank .wem .bnk .xwm");
        Add(FileKind.Archive, ".zip .rar .7z .tar .gz .bz2 .xz .zst .cab .iso .img .vhd .vhdx .vmdk .wim .esd .dmg .tgz");
        Add(FileKind.Program, ".exe .dll .msi .msix .msixbundle .appx .sys .drv .ocx .cpl .scr .com .bat .cmd .ps1 .jar .node .so .lib .pdb .msp .mui .cat .manifest");
        Add(FileKind.Document, ".pdf .doc .docx .xls .xlsx .ppt .pptx .odt .ods .odp .txt .rtf .csv .md .epub .mobi .one .pst .ost .accdb .xps");
        Add(FileKind.GameData, ".pak .vpk .bundle .assets .ress .resource .uasset .umap .ucas .utoc .bsa .ba2 .esm .esp .wad .upk .forge .rpf .arc .big .gcf .chunk .mca .pck .ytd .yft .wpk .bnk .vfs .pck .gma");
        return d;
    }

    public static FileKind Of(ReadOnlySpan<char> fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length < 2 || ext.Length > 12) return FileKind.Other;
        return Map.TryGetValue(ext.ToString(), out var k) ? k : FileKind.Other;
    }

    public static FileKind Of(string fileName) => Of(fileName.AsSpan());
}

/// <summary>Disk analizi ağacındaki bir klasör.</summary>
public sealed class DirNode
{
    public static readonly DirNode[] None = Array.Empty<DirNode>();

    public string Name = "";
    public DirNode? Parent;
    public DirNode[] Children = None;
    public long Size;
    public long OwnFilesSize;
    public int OwnFileCount;
    public long FileCount;
    public long DirCount;
    /// <summary>En yeni değişiklik zamanı (UTC).</summary>
    public DateTime Newest;
    public bool Inaccessible;
    public bool IsLink;
    public long[] KindSizes = new long[FileKinds.Count];

    public string FullPath => Parent == null ? Name : Path.Combine(Parent.FullPath, Name);

    /// <summary>Silinen bir alt öğenin boyutunu ağaç boyunca yukarı doğru düşer.</summary>
    public void Subtract(long bytes, long files, long dirs)
    {
        for (var n = this; n != null; n = n.Parent)
        {
            n.Size = Math.Max(0, n.Size - bytes);
            n.FileCount = Math.Max(0, n.FileCount - files);
            n.DirCount = Math.Max(0, n.DirCount - dirs);
        }
    }

    public void RemoveChild(DirNode child)
    {
        Children = Children.Where(c => !ReferenceEquals(c, child)).ToArray();
        Subtract(child.Size, child.FileCount, child.DirCount + 1);
        for (var n = this; n != null; n = n.Parent)
            for (int k = 0; k < FileKinds.Count; k++) n.KindSizes[k] = Math.Max(0, n.KindSizes[k] - child.KindSizes[k]);
    }

    /// <summary>Bu klasörden doğrudan silinen bir dosyayı ağaçtan düşer.</summary>
    public void RemoveFile(long size, FileKind kind)
    {
        OwnFilesSize = Math.Max(0, OwnFilesSize - size);
        OwnFileCount = Math.Max(0, OwnFileCount - 1);
        Subtract(size, 1, 0);
        for (var n = this; n != null; n = n.Parent) n.KindSizes[(int)kind] = Math.Max(0, n.KindSizes[(int)kind] - size);
    }
}

public sealed class TopFile
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
}

public sealed class ExtStat
{
    public string Ext = "";
    public long Size;
    public long Count;
}

public static class FsScan
{
    internal const FileAttributes CloudOnly = (FileAttributes)0x00400000 | FileAttributes.Offline | (FileAttributes)0x00040000;

    private static readonly EnumerationOptions OneLevel = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private struct Entry
    {
        public bool IsDir;
        public bool IsLinkCandidate;
        public long Length;
        public DateTime Write;
        public string? DirPath;
    }

    private static FileSystemEnumerable<Entry> Enumerate(string dir) =>
        new(dir, static (ref FileSystemEntry e) =>
        {
            var a = e.Attributes;
            bool isDir = e.IsDirectory;
            return new Entry
            {
                IsDir = isDir,
                IsLinkCandidate = (a & FileAttributes.ReparsePoint) != 0,
                Length = isDir || (a & CloudOnly) != 0 ? 0 : e.Length,
                Write = e.LastWriteTimeUtc.UtcDateTime,
                DirPath = isDir ? e.ToFullPath() : null,
            };
        }, OneLevel);

    private static bool IsDirLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget != null; } catch { return true; }
    }

    /// <summary>Bir dosya veya klasörün toplam boyutu. Bağlantıların içine girilmez.</summary>
    public static Measurement Measure(string path, CancellationToken ct = default, ICollection<string>? excludeDirs = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return new Measurement((fi.Attributes & CloudOnly) != 0 ? 0 : fi.Length, 1, 0, fi.LastWriteTime, false);
            }
        }
        catch { return default; }
        if (!Directory.Exists(path)) return default;

        long size = 0, files = 0, dirs = 0;
        DateTime newest = DateTime.MinValue;
        bool partial = false;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var e in Enumerate(dir))
                {
                    if (e.IsDir)
                    {
                        if (e.IsLinkCandidate && IsDirLink(e.DirPath!)) continue;
                        if (excludeDirs != null && excludeDirs.Contains(e.DirPath!)) continue;
                        dirs++;
                        stack.Push(e.DirPath!);
                    }
                    else
                    {
                        files++;
                        size += e.Length;
                    }
                    if (e.Write > newest) newest = e.Write;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { partial = true; }
        }
        return new Measurement(size, files, dirs, newest == DateTime.MinValue ? newest : newest.ToLocalTime(), partial);
    }

    /// <summary>Bir klasörün yalnızca doğrudan içindeki dosyalar (disk analizi görünümü için).</summary>
    public static List<TopFile> ListFiles(string dir)
    {
        var list = new List<TopFile>();
        try
        {
            var en = new FileSystemEnumerable<TopFile?>(dir, static (ref FileSystemEntry e) =>
                e.IsDirectory ? null : new TopFile
                {
                    Path = e.ToFullPath(),
                    Size = (e.Attributes & CloudOnly) != 0 ? 0 : e.Length,
                    Modified = e.LastWriteTimeUtc.LocalDateTime,
                }, OneLevel);
            foreach (var f in en) if (f != null) list.Add(f);
        }
        catch { }
        return list;
    }
}

/// <summary>Disk analizi: tüm ağacı paralel tarar, dosya türü istatistiği ve en büyük dosyaları toplar.</summary>
public sealed class TreeScanner
{
    public long FilesScanned;
    public long DirsScanned;
    public long BytesScanned;
    public volatile string CurrentPath = "";

    private readonly int _topCapacity;
    private readonly long _topMinSize;
    private readonly PriorityQueue<TopFile, long> _top = new();
    private readonly object _topGate = new();
    private long _topThreshold;
    private readonly ConcurrentBag<Dictionary<string, ExtStat>> _extBags = new();
    private readonly ThreadLocal<Dictionary<string, ExtStat>> _ext;

    public TreeScanner(int topCapacity = 1000, long topMinSize = 1L << 20)
    {
        _topCapacity = topCapacity;
        _topMinSize = topMinSize;
        _topThreshold = topMinSize;
        _ext = new ThreadLocal<Dictionary<string, ExtStat>>(() =>
        {
            var d = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
            _extBags.Add(d);
            return d;
        });
    }

    private struct ScanEntry
    {
        public bool IsDir;
        public bool IsReparse;
        public long Length;
        public DateTime Write;
        public string? FullPath;
        public FileKind Kind;
        public string? Ext;
    }

    public DirNode Scan(string rootPath, CancellationToken ct)
    {
        var root = new DirNode { Name = rootPath };
        ScanDir(root, rootPath, 0, ct);
        return root;
    }

    public List<TopFile> TopFiles()
    {
        lock (_topGate) return _top.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ToList();
    }

    public List<ExtStat> ExtensionStats()
    {
        var merged = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var bag in _extBags)
            foreach (var kv in bag)
            {
                if (!merged.TryGetValue(kv.Key, out var m)) merged[kv.Key] = m = new ExtStat { Ext = kv.Key };
                m.Size += kv.Value.Size;
                m.Count += kv.Value.Count;
            }
        return merged.Values.OrderByDescending(e => e.Size).ToList();
    }

    private void ScanDir(DirNode node, string path, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CurrentPath = path;
        var subdirs = new List<DirNode>();
        var exts = _ext.Value!;
        long localBytes = 0;
        int localFiles = 0;
        try
        {
            var en = new FileSystemEnumerable<ScanEntry>(path, static (ref FileSystemEntry e) =>
            {
                var a = e.Attributes;
                if (e.IsDirectory)
                    return new ScanEntry { IsDir = true, IsReparse = (a & FileAttributes.ReparsePoint) != 0, FullPath = e.ToFullPath(), Write = e.LastWriteTimeUtc.UtcDateTime };
                var ext = Path.GetExtension(e.FileName);
                return new ScanEntry
                {
                    Length = (a & FsScan.CloudOnly) != 0 ? 0 : e.Length,
                    Write = e.LastWriteTimeUtc.UtcDateTime,
                    Kind = FileKinds.Of(e.FileName),
                    Ext = ext.Length is > 1 and <= 12 ? ext.ToString().ToLowerInvariant() : "(uzantısız)",
                    FullPath = e.Length >= (1L << 20) ? e.ToFullPath() : null,
                };
            }, new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false });

            foreach (var e in en)
            {
                if (e.Write > node.Newest) node.Newest = e.Write;
                if (e.IsDir)
                {
                    var child = new DirNode { Name = System.IO.Path.GetFileName(e.FullPath!), Parent = node };
                    if (e.IsReparse && IsLink(e.FullPath!)) child.IsLink = true;
                    subdirs.Add(child);
                    continue;
                }
                localFiles++;
                localBytes += e.Length;
                node.KindSizes[(int)e.Kind] += e.Length;
                if (!exts.TryGetValue(e.Ext!, out var st)) exts[e.Ext!] = st = new ExtStat { Ext = e.Ext! };
                st.Size += e.Length;
                st.Count++;
                if (e.FullPath != null && e.Length >= Interlocked.Read(ref _topThreshold)) OfferTop(e.FullPath, e.Length, e.Write);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { node.Inaccessible = true; }

        node.OwnFilesSize = localBytes;
        node.OwnFileCount = localFiles;
        Interlocked.Add(ref FilesScanned, localFiles);
        Interlocked.Add(ref BytesScanned, localBytes);
        Interlocked.Increment(ref DirsScanned);

        var toScan = subdirs.Where(s => !s.IsLink).ToList();
        if (depth < 3 && toScan.Count > 1)
        {
            Parallel.ForEach(toScan, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                s => ScanDir(s, System.IO.Path.Combine(path, s.Name), depth + 1, ct));
        }
        else
        {
            foreach (var s in toScan) ScanDir(s, System.IO.Path.Combine(path, s.Name), depth + 1, ct);
        }

        long size = localBytes, files = localFiles, dirs = 0;
        foreach (var s in subdirs)
        {
            size += s.Size;
            files += s.FileCount;
            dirs += 1 + s.DirCount;
            if (s.Newest > node.Newest) node.Newest = s.Newest;
            for (int k = 0; k < FileKinds.Count; k++) node.KindSizes[k] += s.KindSizes[k];
        }
        node.Size = size;
        node.FileCount = files;
        node.DirCount = dirs;
        node.Children = subdirs.OrderByDescending(s => s.Size).ToArray();
    }

    private static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget != null; } catch { return true; }
    }

    private void OfferTop(string path, long size, DateTime writeUtc)
    {
        if (size < _topMinSize) return;
        lock (_topGate)
        {
            if (_top.Count < _topCapacity)
            {
                _top.Enqueue(new TopFile { Path = path, Size = size, Modified = writeUtc.ToLocalTime() }, size);
            }
            else if (_top.TryPeek(out _, out var min) && size > min)
            {
                _top.DequeueEnqueue(new TopFile { Path = path, Size = size, Modified = writeUtc.ToLocalTime() }, size);
            }
            if (_top.Count >= _topCapacity && _top.TryPeek(out _, out var newMin))
                Interlocked.Exchange(ref _topThreshold, newMin);
        }
    }
}
