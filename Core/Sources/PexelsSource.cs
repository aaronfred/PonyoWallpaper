using System.Text.Json.Serialization;

namespace PonyoWallpaper;

/// <summary>
/// Pexels（需 API Key，免费额度 200 次/小时、20000 次/月，是几家中最宽的）。
/// v1.4.1：频道有关键词时走 /v1/search 检索，否则 /v1/curated 精选流。
/// </summary>
internal sealed class PexelsSource : IWallpaperSource
{
    public string Key => "pexels";
    public string DisplayName => "Pexels";
    public bool NeedsApiKey => true;
    public bool IsReady(AppConfig cfg) => !string.IsNullOrWhiteSpace(cfg.PexelsKey);
    public string StatusText(AppConfig cfg) => IsReady(cfg) ? "已配置" : "未配置 Key";

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        if (!IsReady(cfg)) return null;
        var per = Math.Clamp(req.PerPage, 1, 80);
        var kw = (req.Keywords ?? "").Trim();
        var url = kw.Length > 0
            ? $"https://api.pexels.com/v1/search?query={Uri.EscapeDataString(kw)}&page={Math.Max(1, req.Page)}&per_page={per}"
            : $"https://api.pexels.com/v1/curated?page={Math.Max(1, req.Page)}&per_page={per}";
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.TryAddWithoutValidation("Authorization", cfg.PexelsKey!.Trim());
            using var resp = await SourceHttp.Get().SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"pexels http {(int)resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var data = System.Text.Json.JsonSerializer.Deserialize<PexelsResponse>(json);
            if (data?.Photos == null || data.Photos.Count == 0) return Array.Empty<WallpaperItem>();

            var (w, h) = ParseResolution(cfg.Resolution);
            var list = new List<WallpaperItem>();
            foreach (var p in data.Photos)
            {
                var orig = p.Src?.Original;
                if (string.IsNullOrWhiteSpace(orig)) continue;
                list.Add(new WallpaperItem
                {
                    SourceKey = Key,
                    Id = p.Id.ToString(),
                    // Pexels 原生支持 ?w=&h= 裁切参数
                    Path = AppendSize(orig!, w, h),
                    Thumb = AppendSize(p.Src?.Medium ?? orig!, 400, 0),
                    Resolution = $"{p.Width}x{p.Height}",
                    Category = kw.Length > 0 ? kw : "精选",
                    Purity = "sfw",
                    PageUrl = p.Url ?? "https://www.pexels.com/"
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn($"pexels fetch failed: {ex.Message}");
            return null;
        }
    }

    private static string AppendSize(string url, int w, int h)
    {
        var sep = url.Contains('?') ? '&' : '?';
        return h > 0 ? $"{url}{sep}auto=compress&cs=tinysrgb&w={w}&h={h}&fit=crop" : $"{url}{sep}auto=compress&cs=tinysrgb&w={w}";
    }

    private static (int W, int H) ParseResolution(string res)
    {
        var parts = (res ?? "").Split('x', '×', 'X');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
            return (w, h);
        return (2560, 1440);
    }

    private sealed class PexelsResponse
    {
        [JsonPropertyName("page")] public int Page { get; set; }
        [JsonPropertyName("per_page")] public int PerPage { get; set; }
        [JsonPropertyName("photos")] public List<PexelsPhoto>? Photos { get; set; }
    }

    private sealed class PexelsPhoto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("src")] public PexelsSrc? Src { get; set; }
    }

    private sealed class PexelsSrc
    {
        [JsonPropertyName("original")] public string? Original { get; set; }
        [JsonPropertyName("large")] public string? Large { get; set; }
        [JsonPropertyName("medium")] public string? Medium { get; set; }
        [JsonPropertyName("small")] public string? Small { get; set; }
    }
}
