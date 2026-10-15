using System.IO.Enumeration;
using System.Security.Cryptography;

namespace TemizlikPaneli.Core;

public sealed class DupFile : ObservableObject
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public DupGroup? Group { get; set; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value)) Group?.Refresh(); } }

    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string DateText => Fmt.Date(Modified);
}

public sealed class DupGroup : ObservableObject
{
    public long FileSize { get; init; }
    public List<DupFile> Files { get; init; } = new();
    public string Name => Files.Count > 0 ? Files[0].Name : "";
    public long Wasted => FileSize * (Files.Count - 1);
    public string Summary => $"{Files.Count} kopya · her biri {Fmt.Size(FileSize)} · boşa giden {Fmt.Size(Wasted)}";
    public int SelectedCount => Files.Count(f => f.IsSelected);
    public bool AllSelected => Files.Count > 0 && Files.All(f => f.IsSelected);
    public void Refresh() { OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(AllSelected)); }
}

public sealed class DupProgress
{
    public long FilesSeen;
    public long Candidates;
    public long BytesHashed;
    public volatile string Stage = "";
}

public static class DuplicateFinder
{
    private const int PartBytes = 16 * 1024;

    public static List<DupGroup> Find(IReadOnlyList<string> roots, long minSize, bool skipAppData, DupProgress prog, CancellationToken ct)
    {
        prog.Stage = "Dosyalar listeleniyor";
        var bySize = new Dictionary<long, List<(string Path, DateTime Modified)>>();
        var appData = System.IO.Path.Combine(KnownFolders.UserProfile, "AppData");
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var en = new FileSystemEnumerable<(string, long, DateTime)>(root,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Length, e.LastWriteTimeUtc.LocalDateTime),
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    !e.IsDirectory && e.Length >= minSize && (e.Attributes & FsScan.CloudOnly) == 0,
                ShouldRecursePredicate = (ref FileSystemEntry e) =>
                {
                    if (!skipAppData) return true;
                    var name = e.FileName;
                    if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return false;
                    return !PathUtil.IsSameOrUnder(e.ToFullPath(), appData);
                },
            };
            foreach (var (path, len, mod) in en)
            {
                ct.ThrowIfCancellationRequested();
                prog.FilesSeen++;
                if (!bySize.TryGetValue(len, out var l)) bySize[len] = l = new List<(string, DateTime)>(2);
                l.Add((path, mod));
            }
        }

        var groups = bySize.Where(kv => kv.Value.Count > 1).ToList();
        prog.Candidates = groups.Sum(g => (long)g.Value.Count);
        prog.Stage = "Dosyaların başı ve sonu karşılaştırılıyor";

        var stage2 = new List<(long Size, List<(string Path, DateTime Modified)> Files)>();
        foreach (var (size, files) in groups)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var g in files.GroupBy(f => PartialHash(f.Path, size, prog)).Where(g => g.Key != null && g.Count() > 1))
                stage2.Add((size, g.ToList()));
        }

        prog.Stage = "Tam içerik doğrulanıyor";
        var result = new System.Collections.Concurrent.ConcurrentBag<DupGroup>();
        Parallel.ForEach(stage2, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, s =>
        {
            var sets = s.Size <= PartBytes * 2
                ? s.Files.GroupBy(_ => "same")
                : s.Files.GroupBy(f => FullHash(f.Path, prog, ct));
            foreach (var g in sets.Where(g => g.Key != null && g.Count() > 1))
            {
                var grp = new DupGroup { FileSize = s.Size };
                foreach (var f in g.OrderBy(f => f.Modified))
                    grp.Files.Add(new DupFile { Path = f.Path, Size = s.Size, Modified = f.Modified, Group = grp });
                result.Add(grp);
            }
        });
        prog.Stage = "Bitti";
        return result.OrderByDescending(g => g.Wasted).ToList();
    }

    private static string? PartialHash(string path, long size, DupProgress prog)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            var buf = new byte[(int)Math.Min(size, PartBytes * 2)];
            int head = (int)Math.Min(size, PartBytes);
            ReadExactly(fs, buf, 0, head);
            if (size > PartBytes)
            {
                int tail = (int)Math.Min(size - PartBytes, PartBytes);
                fs.Seek(size - tail, SeekOrigin.Begin);
                ReadExactly(fs, buf, head, tail);
            }
            Interlocked.Add(ref prog.BytesHashed, buf.Length);
            return Convert.ToHexString(MD5.HashData(buf));
        }
        catch { return null; }
    }

    private static void ReadExactly(Stream s, byte[] buf, int offset, int count)
    {
        while (count > 0)
        {
            int n = s.Read(buf, offset, count);
            if (n <= 0) throw new EndOfStreamException();
            offset += n; count -= n;
        }
    }

    private static string? FullHash(string path, DupProgress prog, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                md5.AppendData(buf, 0, n);
                Interlocked.Add(ref prog.BytesHashed, n);
            }
            return Convert.ToHexString(md5.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
