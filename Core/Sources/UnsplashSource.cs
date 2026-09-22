using System.Text.Json.Serialization;

namespace PonyoWallpaper;

/// <summary>
/// Unsplash（需 API Key，免费额度 50 次/小时；国内图片 CDN 351ms 最快）。
/// v1.4.1：频道有关键词时走 /search/photos 检索，否则 /photos 精选流。
/// </summary>
internal sealed class UnsplashSource : IWallpaperSource
{
    public string Key => "unsplash";
    public string DisplayName => "Unsplash";
    public bool NeedsApiKey => true;
    public bool IsReady(AppConfig cfg) => !string.IsNullOrWhiteSpace(cfg.UnsplashKey);
    public string StatusText(AppConfig cfg) => IsReady(cfg) ? "已配置" : "未配置 Key";

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        if (!IsReady(cfg)) return null;
        var per = Math.Clamp(req.PerPage, 1, 30);
        var kw = (req.Keywords ?? "").Trim();
        var searching = kw.Length > 0;
        var url = searching
            ? $"https://api.unsplash.com/search/photos?query={Uri.EscapeDataString(kw)}&page={Math.Max(1, req.Page)}&per_page={per}&content_filter=high&order_by=relevance"
            : $"https://api.unsplash.com/photos?page={Math.Max(1, req.Page)}&per_page={per}&order_by=popular";
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.TryAddWithoutValidation("Authorization", $"Client-ID {cfg.UnsplashKey!.Trim()}");
            using var resp = await SourceHttp.Get().SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"unsplash http {(int)resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            List<UnsplashPhoto>? arr = searching
                ? System.Text.Json.JsonSerializer.Deserialize<UnsplashSearchResponse>(json)?.Results
                : System.Text.Json.JsonSerializer.Deserialize<List<UnsplashPhoto>>(json);
            if (arr == null || arr.Count == 0) return Array.Empty<WallpaperItem>();

            var list = new List<WallpaperItem>();
            foreach (var p in arr)
            {
                if (string.IsNullOrEmpty(p.Id)) continue;
                var (w, h) = ParseResolution(cfg.Resolution);
                var raw = p.Urls?.Raw;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                list.Add(new WallpaperItem
                {
                    SourceKey = Key,
                    Id = p.Id,
                    // Unsplash 支持在 raw 后追加 &w=&h=&fit= 直接裁剪到目标尺寸
                    Path = AppendSize(raw!, w, h),
                    Thumb = AppendSize(p.Urls?.Thumb ?? raw!, 400, 0),
                    Resolution = $"{p.Width}x{p.Height}",
                    Category = searching ? kw : "精选",
                    Purity = "sfw",
                    PageUrl = p.Links?.Html ?? "https://unsplash.com/"
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn($"unsplash fetch failed: {ex.Message}");
            return null;
        }
    }

    private static string AppendSize(string url, int w, int h)
    {
        var sep = url.Contains('?') ? '&' : '?';
        return h > 0
            ? $"{url}{sep}w={w}&h={h}&fit=crop&crop=entropy&q=85"
            : $"{url}{sep}w={w}&q=80";
    }

    private static (int W, int H) ParseResolution(string res)
    {
        var parts = (res ?? "").Split('x', '×', 'X');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
            return (w, h);
        return (2560, 1440);
    }

    private sealed class UnsplashPhoto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("urls")] public UnsplashUrls? Urls { get; set; }
        [JsonPropertyName("links")] public UnsplashLinks? Links { get; set; }
    }

    private sealed class UnsplashUrls
    {
        [JsonPropertyName("raw")] public string? Raw { get; set; }
        [JsonPropertyName("full")] public string? Full { get; set; }
        [JsonPropertyName("regular")] public string? Regular { get; set; }
        [JsonPropertyName("small")] public string? Small { get; set; }
        [JsonPropertyName("thumb")] public string? Thumb { get; set; }
    }

    private sealed class UnsplashSearchResponse
    {
        [JsonPropertyName("results")] public List<UnsplashPhoto>? Results { get; set; }
        [JsonPropertyName("total_pages")] public int TotalPages { get; set; }
    }

    private sealed class UnsplashLinks
    {
        [JsonPropertyName("html")] public string? Html { get; set; }
    }
}
