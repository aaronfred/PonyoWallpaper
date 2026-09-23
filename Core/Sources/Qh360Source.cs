using System.Text.Json.Serialization;

namespace PonyoWallpaper;

/// <summary>
/// 360 壁纸（免注册，纯国内源，18 个分类）。
/// v1.4.1：频道 key 映射到 360 自家分类（语义相近），映射不上的频道按页轮换；
/// 分辨率档位生效——2K 及以上取原图（Url），低档取 1600x900 变体省流量。
/// </summary>
internal sealed class Qh360Source : IWallpaperSource
{
    /// <summary>分类轮转顺序（无频道映射时使用）：风景大片 → 小清新 → 萌宠动物 → 炫酷时尚 → 动漫卡通 → 4K专区。</summary>
    public static readonly (string Cid, string Name)[] Categories =
    {
        ("9",  "风景大片"),
        ("15", "小清新"),
        ("14", "萌宠动物"),
        ("10", "炫酷时尚"),
        ("26", "动漫卡通"),
        ("36", "4K专区"),
    };

    /// <summary>360 全部 18 类的 cid → 名称。</summary>
    private static readonly Dictionary<string, string> CidNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["36"] = "4K专区",   ["6"] = "美女模特", ["30"] = "爱情美图", ["9"]  = "风景大片",
        ["15"] = "小清新",   ["26"] = "动漫卡通", ["11"] = "明星风尚", ["14"] = "萌宠动物",
        ["5"]  = "游戏壁纸", ["12"] = "汽车天下", ["10"] = "炫酷时尚", ["29"] = "月历壁纸",
        ["7"]  = "影视剧照", ["13"] = "节日美图", ["22"] = "军事天地", ["16"] = "劲爆体育",
        ["18"] = "BABY秀",   ["35"] = "文字控",
    };

    /// <summary>应用频道 key → 360 分类 cid（语义相近的映射；360 分类有限，尽量对上）。</summary>
    private static readonly Dictionary<string, string> ChannelCid = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nature_landscape"] = "9",   // 风景大片
        ["nature_sea"]       = "9",
        ["nature_mountain"]  = "9",
        ["nature_sunset"]    = "9",
        ["nature_snow"]      = "9",
        ["nature_flower"]    = "15",  // 小清新
        ["photo_city"]       = "10",  // 炫酷时尚（最接近都市感）
        ["photo_space"]      = "9",
        ["photo_minimalism"] = "15",
        ["photo_animals"]    = "14",  // 萌宠动物
        ["photo_cars"]       = "12",  // 汽车天下
        ["people_portrait"]  = "6",   // 美女模特
        ["people_fashion"]   = "11",  // 明星风尚
        ["people_sports"]    = "16",  // 劲爆体育
        ["people_movies"]    = "7",   // 影视剧照
        ["people_street"]    = "10",
        ["people_art"]       = "30",  // 爱情美图
        ["anime_girls"]      = "26",  // 动漫卡通
        ["anime_shonen"]     = "26",
        ["anime_mecha"]      = "5",   // 游戏壁纸
        ["anime_games"]      = "5",
        ["anime_scenery"]    = "26",
        ["anime_animals"]    = "26",
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

        // v1.4.1：频道映射优先；没有映射的频道才按页轮换分类
        string cidStr, catName;
        if (!string.IsNullOrEmpty(req.ChannelKey) && ChannelCid.TryGetValue(req.ChannelKey, out var mapped))
        {
            cidStr = mapped;
            catName = CidNames.TryGetValue(mapped, out var n) ? n : "360 壁纸";
        }
        else
        {
            var cat = Categories[(req.Page - 1) % Categories.Length];
            cidStr = cat.Cid;
            catName = cat.Name;
        }

        // 接口是 http（该站不支持 https），保持原样
        var url = $"http://wallpaper.apc.360.cn/index.php?c=WallPaper&a=getAppsByCategory&cid={cidStr}&start={start}&count={limit}&from=360chrome";
        try
        {
            var json = await SourceHttp.GetStringAsync(url, cfg, ct);
            if (string.IsNullOrEmpty(json)) return null;
            var data = System.Text.Json.JsonSerializer.Deserialize<Qh360Response>(json);
            var items = data?.Data;
            if (items == null || items.Count == 0) return Array.Empty<WallpaperItem>();

            // v1.4.1：分辨率档位生效——2K 及以上取原图，低档取 1600x900 变体省流量
            var (tw, th) = ParseResolution(cfg.Resolution);
            var hiRes = tw >= 2560 || th >= 1440;

            var list = new List<WallpaperItem>();
            foreach (var it in items)
            {
                var full = NormalizeUrl(hiRes ? (it.Url ?? it.Img1600x900) : (it.Img1600x900 ?? it.Url));
                var thumb = NormalizeUrl(it.UrlThumb ?? it.UrlMid) ?? full;
                if (string.IsNullOrEmpty(full) || string.IsNullOrEmpty(it.Id)) continue;
                list.Add(new WallpaperItem
                {
                    SourceKey = Key,
                    Id = it.Id ?? "",
                    Path = full,
                    Thumb = thumb ?? full,
                    Resolution = string.IsNullOrWhiteSpace(it.Resolution) ? "1920x1080" : it.Resolution,
                    Category = catName,
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

    private static (int W, int H) ParseResolution(string res)
    {
        var parts = (res ?? "").Split('x', '×', 'X');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
            return (w, h);
        return (2560, 1440);
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
