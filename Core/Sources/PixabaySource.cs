using System.Text.Json.Serialization;

namespace PonyoWallpaper;

/// <summary>
/// Pixabay（需 API Key，免费额度 100 次/60 秒，素材总量最大，支持中文检索）。
/// 走编辑器精选流（editors_choice=true），不提供查询。
/// </summary>
internal sealed class PixabaySource : IWallpaperSource
{
    public string Key => "pixabay";
    public string DisplayName => "Pixabay";
    public bool NeedsApiKey => true;
    public bool IsReady(AppConfig cfg) => !string.IsNullOrWhiteSpace(cfg.PixabayKey);
    public string StatusText(AppConfig cfg) => IsReady(cfg) ? "已配置" : "未配置 Key";

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        if (!IsReady(cfg)) return null;
        var per = Math.Clamp(req.PerPage, 3, 200);
        var url = $"https://pixabay.com/api/?key={Uri.EscapeDataString(cfg.PixabayKey!.Trim())}"
                + $"&page={Math.Max(1, req.Page)}&per_page={per}&editors_choice=true&image_type=photo&order=popular";
        try
        {
            using var resp = await SourceHttp.Get().GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"pixabay http {(int)resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var data = System.Text.Json.JsonSerializer.Deserialize<PixabayResponse>(json);
            if (data?.Hits == null || data.Hits.Count == 0) return Array.Empty<WallpaperItem>();

            var list = new List<WallpaperItem>();
            foreach (var h in data.Hits)
            {
                if (string.IsNullOrWhiteSpace(h.LargeImageUrl)) continue;
                list.Add(new WallpaperItem
                {
                    SourceKey = Key,
                    Id = (h.Id ?? 0).ToString(),
                    Path = h.LargeImageUrl!,
                    Thumb = string.IsNullOrWhiteSpace(h.WebFormatUrl) ? h.LargeImageUrl! : h.WebFormatUrl!,
                    Resolution = $"{h.ImageWidth}x{h.ImageHeight}",
                    Category = "精选",
                    Purity = "sfw",
                    FileSize = h.ImageSize,
                    PageUrl = h.PageUrl ?? "https://pixabay.com/"
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn($"pixabay fetch failed: {ex.Message}");
            return null;
        }
    }

    private sealed class PixabayResponse
    {
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("totalHits")] public int TotalHits { get; set; }
        [JsonPropertyName("hits")] public List<PixabayHit>? Hits { get; set; }
    }

    private sealed class PixabayHit
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("pageURL")] public string? PageUrl { get; set; }
        [JsonPropertyName("largeImageURL")] public string? LargeImageUrl { get; set; }
        [JsonPropertyName("webformatURL")] public string? WebFormatUrl { get; set; }
        [JsonPropertyName("imageWidth")] public int ImageWidth { get; set; }
        [JsonPropertyName("imageHeight")] public int ImageHeight { get; set; }
        [JsonPropertyName("imageSize")] public long ImageSize { get; set; }
    }
}
