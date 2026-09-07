namespace PonyoWallpaper;

/// <summary>
/// LRU 缓存管理。原图按 ID 存储，按 LRU 淘汰至配额上限。
/// 首次构造时扫描已有文件建立访问时间索引。
/// </summary>
internal sealed class CacheManager
{
    private readonly string _fullDir;
    private readonly string _thumbDir;
    private long _limitBytes;
    private readonly Dictionary<string, DateTime> _access = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public CacheManager(string fullDir, string thumbDir, int limitMb)
    {
        _fullDir = fullDir;
        _thumbDir = thumbDir;
        _limitBytes = (long)limitMb * 1024L * 1024L;
        Directory.CreateDirectory(_fullDir);
        Directory.CreateDirectory(_thumbDir);
        Scan();
    }

    public string FullPath(string id) => Path.Combine(_fullDir, $"{id}.jpg");
    public string ThumbPath(string id) => Path.Combine(_thumbDir, $"{id}.jpg");

    public bool HasFull(string id) => File.Exists(FullPath(id));

    /// <summary>运行时调整缓存配额并立即执行一次 LRU 淘汰。</summary>
    public void SetLimitMb(int mb)
    {
        _limitBytes = (long)mb * 1024L * 1024L;
        EnforceLimit();
    }

    public void Touch(string id)
    {
        lock (_lock) _access[id] = DateTime.UtcNow;
    }

    private void Scan()
    {
        lock (_lock)
        {
            foreach (var f in Directory.EnumerateFiles(_fullDir))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                _access[id] = File.GetLastAccessTimeUtc(f);
            }
        }
    }

    /// <summary>总字节数（用于显示）。</summary>
    public long TotalBytes()
    {
        lock (_lock)
        {
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(_fullDir))
                total += new FileInfo(f).Length;
            return total;
        }
    }

    /// <summary>清空全部缓存（原图 + 缩略图），返回释放的字节数。</summary>
    public long ClearAll()
    {
        long freed = 0;
        lock (_lock)
        {
            freed += DeleteAllFiles(_fullDir);
            freed += DeleteAllFiles(_thumbDir);
            _access.Clear();
        }
        Logger.Info($"cache cleared, freed {freed / 1024 / 1024}MB");
        return freed;
    }

    private static long DeleteAllFiles(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var len = new FileInfo(f).Length;
                    File.Delete(f);
                    total += len;
                }
                catch { /* 单个文件被占用则跳过 */ }
            }
        }
        catch (Exception ex) { Logger.Warn($"delete cache dir {dir}: {ex.Message}"); }
        return total;
    }

    /// <summary>超过配额则按 LRU 淘汰。</summary>
    public int EnforceLimit()
    {
        lock (_lock)
        {
            long total = 0;
            var sizes = new Dictionary<string, long>();
            foreach (var f in Directory.EnumerateFiles(_fullDir))
            {
                var len = new FileInfo(f).Length;
                sizes[Path.GetFileNameWithoutExtension(f)] = len;
                total += len;
            }
            if (total <= _limitBytes) return 0;
            Logger.Info($"cache {total / 1024 / 1024}MB > {_limitBytes / 1024 / 1024}MB, evicting LRU");
            var ordered = _access.OrderBy(kv => kv.Value).ToList();
            int evicted = 0;
            foreach (var (id, _) in ordered)
            {
                if (total <= _limitBytes) break;
                var p = FullPath(id);
                if (File.Exists(p))
                {
                    total -= sizes.TryGetValue(id, out var s) ? s : 0L;
                    try { File.Delete(p); evicted++; }
                    catch (Exception ex) { Logger.Warn($"evict {id}: {ex.Message}"); }
                }
                _access.Remove(id);
            }
            return evicted;
        }
    }

    /// <summary>随机返回 N 个已缓存 ID。</summary>
    public IEnumerable<string> RandomIds(int count)
    {
        lock (_lock)
        {
            var keys = _access.Keys.ToList();
            for (int i = 0; i < count && keys.Count > 0; i++)
            {
                int idx = Random.Shared.Next(keys.Count);
                yield return keys[idx];
                keys.RemoveAt(idx);
            }
        }
    }
}