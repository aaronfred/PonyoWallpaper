using System.Text.Json.Serialization;

namespace PonyoWallpaper;

/// <summary>
/// 360 壁纸（免注册，纯国内源，18 个分类）。
/// 有分类但无查询；默认取「风景大片 cid=9」，轮换时按配置里的 360 分类取。
/// </summary>
internal sealed class Qh360Source : IWallpaperSource
{
    /// <summary>分类轮转顺序：风景大片 → 小清新 → 萌宠动物 → 炫酷时尚 → 动漫卡通 → 4K专区。</summary>
    public static readonly (string Cid, string Name)[] Categories =
    {
        ("9",  "风景大片"),
        ("15", "小清新"),
        ("14", "萌宠动物"),
        ("10", "炫酷时尚"),
        ("26", "动漫卡通"),
        ("36", "4K专区"),
    };

    public string Key => "qh360";
    public string DisplayName => "360 壁纸";
    public bool NeedsApiKey => false;
    public bool IsReady(AppConfig cfg) => true;
    public string StatusText(AppConfig cfg) => "免注册 · 国内源";

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        var limit = Math.Clamp(req.PerPage, 1, 30);
        var start = (req.Page - 1) * limit;
        // 按页轮换分类，避免翻页时一直是同一类
        var cat = Categories[(req.Page - 1) % Categories.Length];

        // 接口是 http（该站不支持 https），保持原样
        var url = $"http://wallpaper.apc.360.cn/index.php?c=WallPaper&a=getAppsByCategory&cid={cat.Cid}&start={start}&count={limit}&from=360chrome";
        try
        {
            using var resp = await SourceHttp.Get().GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var data = System.Text.Json.JsonSerializer.Deserialize<Qh360Response>(json);
            var items = data?.Data;
            if (items == null || items.Count == 0) return Array.Empty<WallpaperItem>();

            var list = new List<WallpaperItem>();
            foreach (var it in items)
            {
                var full = NormalizeUrl(it.Img1600x900) ?? NormalizeUrl(it.Url);
                var thumb = NormalizeUrl(it.UrlThumb) ?? full;
                if (string.IsNullOrEmpty(full) || string.IsNullOrEmpty(it.Id)) continue;
                list.Add(new WallpaperItem
                {
                    SourceKey = Key,
                    Id = it.Id ?? "",
                    Path = full,
                    Thumb = thumb ?? full,
                    Resolution = string.IsNullOrWhiteSpace(it.Resolution) ? "1920x1080" : it.Resolution,
                    Category = cat.Name,
                    Purity = "sfw",
                    PageUrl = "https://wallpaper.360.cn/"
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn($"360 fetch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>接口返回的是 http 链接；升级成 https 失败也不影响（qhimg 两者都支持）。</summary>
    private static string? NormalizeUrl(string? u)
    {
        if (string.IsNullOrWhiteSpace(u)) return null;
        var s = u.Replace("\\/", "/");
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s["http://".Length..];
        return s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    private sealed class Qh360Response
    {
        [JsonPropertyName("errno")] public string? ErrNo { get; set; }
        [JsonPropertyName("total")] public string? Total { get; set; }
        [JsonPropertyName("data")] public List<Qh360Item>? Data { get; set; }
    }

    private sealed class Qh360Item
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("resolution")] public string? Resolution { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("url_thumb")] public string? UrlThumb { get; set; }
        [JsonPropertyName("url_mid")] public string? UrlMid { get; set; }
        [JsonPropertyName("img_1600_900")] public string? Img1600x900 { get; set; }
        [JsonPropertyName("utag")] public string? Utag { get; set; }
    }
}
