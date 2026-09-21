using System.Text.Json.Serialization;

namespace PonyoWallpaper;

/// <summary>
/// Bing 每日壁纸（免注册，国内直连 92ms）。
/// 无查询能力、无分类 —— 只有「每日精选」一条流；用 idx 翻历史，每页 8 张。
/// </summary>
internal sealed class BingSource : IWallpaperSource
{
    private const string Api = "https://cn.bing.com/HPImageArchive.aspx?format=js";

    public string Key => "bing";
    public string DisplayName => "Bing 每日壁纸";
    public bool NeedsApiKey => false;
    public bool IsReady(AppConfig cfg) => true;
    public string StatusText(AppConfig cfg) => "免注册";

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        // 每页 8 张（接口上限）；idx 越大越旧
        var n = Math.Clamp(req.PerPage, 1, 8);
        var idx = (req.Page - 1) * n;
        if (idx > 120) return Array.Empty<WallpaperItem>();   // 只翻约 4 个月历史

        var url = $"{Api}&idx={idx}&n={n}&mkt=zh-CN";
        try
        {
            using var resp = await SourceHttp.Get().GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var data = System.Text.Json.JsonSerializer.Deserialize<BingResponse>(json);
            var imgs = data?.Images;
            if (imgs == null || imgs.Count == 0) return Array.Empty<WallpaperItem>();

            var list = new List<WallpaperItem>();
            foreach (var im in imgs)
            {
                if (string.IsNullOrEmpty(im.UrlBase)) continue;
                var baseUrl = im.UrlBase.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? im.UrlBase
                    : "https://cn.bing.com" + im.UrlBase;
                list.Add(new WallpaperItem
                {
                    SourceKey = Key,
                    Id = im.StartDate ?? Guid.NewGuid().ToString("N")[..8],
                    Path = baseUrl + "_UHD.jpg",
                    Thumb = baseUrl + "_400x240.jpg",
                    Resolution = "1920x1080",
                    Category = "每日精选",
                    Purity = "sfw",
                    PageUrl = "https://cn.bing.com/"
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn($"bing fetch failed: {ex.Message}");
            return null;
        }
    }

    private sealed class BingResponse
    {
        [JsonPropertyName("images")] public List<BingImage>? Images { get; set; }
    }

    private sealed class BingImage
    {
        [JsonPropertyName("startdate")] public string? StartDate { get; set; }
        [JsonPropertyName("urlbase")] public string? UrlBase { get; set; }
        [JsonPropertyName("copyright")] public string? Copyright { get; set; }
    }
}
