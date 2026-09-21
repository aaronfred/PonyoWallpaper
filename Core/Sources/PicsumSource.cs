using System.Text.Json.Serialization;

namespace PonyoWallpaper;

/// <summary>
/// Lorem Picsum（免注册，Unsplash 衍生图库，Unsplash License）。
/// 无分类无查询 —— 只有列表流，但支持任意尺寸按需生成，适合做高分壁纸兜底。
/// </summary>
internal sealed class PicsumSource : IWallpaperSource
{
    public string Key => "picsum";
    public string DisplayName => "Picsum 图库";
    public bool NeedsApiKey => false;
    public bool IsReady(AppConfig cfg) => true;
    public string StatusText(AppConfig cfg) => "免注册";

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        var (w, h) = ParseResolution(cfg.Resolution);
        var page = Math.Max(1, req.Page);
        var limit = Math.Clamp(req.PerPage, 1, 100);
        var url = $"https://picsum.photos/v2/list?page={page}&limit={limit}";

        try
        {
            using var resp = await SourceHttp.Get().GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<PicsumPhoto>>(json);
            if (arr == null || arr.Count == 0) return Array.Empty<WallpaperItem>();

            var list = new List<WallpaperItem>();
            foreach (var p in arr)
            {
                if (string.IsNullOrEmpty(p.Id)) continue;
                list.Add(new WallpaperItem
                {
                    SourceKey = Key,
                    Id = p.Id,
                    // 按需生成任意尺寸：/id/{id}/{w}/{h}
                    Path = $"https://picsum.photos/id/{p.Id}/{w}/{h}",
                    Thumb = $"https://picsum.photos/id/{p.Id}/400/{400 * h / Math.Max(1, w)}",
                    Resolution = $"{w}x{h}",
                    Category = "精选",
                    Purity = "sfw",
                    PageUrl = p.Url ?? "https://picsum.photos/"
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn($"picsum fetch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>把配置里的分辨率档位拆成宽高；解析失败回落 2560x1440。</summary>
    private static (int W, int H) ParseResolution(string res)
    {
        var parts = (res ?? "").Split('x', '×', 'X');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
            return (w, h);
        return (2560, 1440);
    }

    private sealed class PicsumPhoto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    }
}
