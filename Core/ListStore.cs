using System.Text.Json;

namespace PonyoWallpaper;

internal sealed class FavRecord
{
    /// <summary>
    /// 落盘键。v1.4.0 起为复合键（"源_原始id"，wallhaven 保持裸 id 以兼容旧数据），
    /// 因为不同壁纸源的 id 会相撞。
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>来源标识（仅新增记录有；旧记录为空，按 wallhaven 处理）。</summary>
    public string SourceKey { get; set; } = "wallhaven";

    public DateTime AddedAt { get; set; }
    public string Resolution { get; set; } = "";
    public string PageUrl { get; set; } = "";

    /// <summary>
    /// 原图 / 缩略图直链。v1.4.0 起随收藏一起存 —— 多源后无法再从 id 反推 URL
    /// （wallhaven 有固定规则，Bing / 360 / Unsplash 各不相同），必须原样记录。
    /// 旧记录为空，回退到 wallhaven 规则。
    /// </summary>
    public string Path { get; set; } = "";
    public string Thumb { get; set; } = "";
}

/// <summary>
/// 收藏夹与黑名单（JSON 数组文件）。均无外部依赖。
/// </summary>
internal sealed class ListStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<FavRecord> _items;

    public ListStore(string path)
    {
        _path = path;
        _items = LoadFromDisk();
    }

    private List<FavRecord> LoadFromDisk()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<FavRecord>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) { Logger.Warn($"list load failed ({_path}): {ex.Message}"); }
        return new();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Logger.Warn($"list save failed: {ex.Message}"); }
    }

    public bool Contains(string id)
    {
        lock (_lock) return _items.Any(x => x.Id == id);
    }

    public void Add(WallpaperItem item)
    {
        var key = item.StoreId;   // 复合键：不同源的 id 会撞，必须带上源前缀
        lock (_lock)
        {
            if (_items.Any(x => x.Id == key)) return;
            _items.Add(new FavRecord
            {
                Id = key,
                AddedAt = DateTime.Now,
                Resolution = item.Resolution,
                PageUrl = item.PageUrl,
                SourceKey = item.SourceKey,
                Path = item.Path,
                Thumb = item.Thumb
            });
            Save();
        }
    }

    public void AddId(string id)
    {
        lock (_lock)
        {
            if (_items.Any(x => x.Id == id)) return;
            _items.Add(new FavRecord { Id = id, AddedAt = DateTime.Now });
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            _items.RemoveAll(x => x.Id == id);
            Save();
        }
    }

    public List<FavRecord> All()
    {
        lock (_lock) return _items.ToList();
    }
}