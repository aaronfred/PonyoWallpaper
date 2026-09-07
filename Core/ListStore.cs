using System.Text.Json;

namespace PonyoWallpaper;

internal sealed class FavRecord
{
    public string Id { get; set; } = "";
    public DateTime AddedAt { get; set; }
    public string Resolution { get; set; } = "";
    public string PageUrl { get; set; } = "";
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
        lock (_lock)
        {
            if (_items.Any(x => x.Id == item.Id)) return;
            _items.Add(new FavRecord { Id = item.Id, AddedAt = DateTime.Now, Resolution = item.Resolution, PageUrl = item.PageUrl });
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