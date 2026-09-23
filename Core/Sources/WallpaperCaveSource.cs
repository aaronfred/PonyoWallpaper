using System.Text.RegularExpressions;

namespace PonyoWallpaper;

/// <summary>
/// WallpaperCave（免注册，国际源，国内直连可用）。
///
/// 结构：搜索/分类页列出「专辑」（形如 /golden-nature-wallpapers），
/// 专辑页服务端渲染出全部壁纸条目，每张带 <c>&lt;img src="/wp/wpNNN.jpg" width= height= alt=</c>。
/// 因此无需 API Key，也不需要浏览器渲染，纯 HTML 解析即可。
///
/// 本实现按用户要求<b>只取横屏（width &gt; height）的电脑壁纸</b>：
/// 专辑页的 width/height 属性可直接判定，手机/竖屏专辑（slug 含 phone/mobile/vertical 等）整张跳过。
/// </summary>
internal sealed class WallpaperCaveSource : IWallpaperSource
{
    private const string Base = "https://wallpapercave.com";

    /// <summary>竖屏 / 手机方向的专辑 slug 标记（命中即整张专辑跳过）。</summary>
    private static readonly string[] PortraitMarkers =
    {
        "phone", "mobile", "vertical", "tablet", "iphone", "android", "watch",
        "square", "dual-monitor-vertical", "tall",
    };

    /// <summary>
    /// 频道 → WallpaperCave 检索词。
    /// 实测（2026-09-22）：该站搜索对<b>单词</b>召回最好（每个词 20~47 个专辑），
    /// 双词组合大量返回 0（如 "city architecture" / "cars vehicles" 均为 0），故逐频道指定单关键词。
    /// </summary>
    private static readonly Dictionary<string, string> ChannelTerm = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nature_landscape"] = "landscape",
        ["nature_sea"]       = "beach",
        ["nature_mountain"]  = "mountain",
        ["nature_flower"]    = "flowers",
        ["nature_sunset"]    = "sunset",
        ["nature_snow"]      = "snow",
        ["photo_city"]       = "city",
        ["photo_space"]      = "space",
        ["photo_minimalism"] = "minimalist",
        ["photo_animals"]    = "animals",
        ["photo_cars"]       = "car",
        ["people_portrait"]  = "portrait",
        ["people_fashion"]   = "fashion",
        ["people_sports"]    = "sports",
        ["people_movies"]    = "movie",
        ["people_street"]    = "street",
        ["people_art"]       = "art",
        ["anime_girls"]      = "anime girl",
        ["anime_shonen"]     = "shonen",
        ["anime_mecha"]      = "mecha",
        ["anime_games"]      = "game",
        ["anime_scenery"]    = "anime landscape",
        ["anime_animals"]    = "cute animal",
    };

    public string Key => "wallpapercave";
    public string DisplayName => "WallpaperCave";
    public bool NeedsApiKey => false;
    public bool IsReady(AppConfig cfg) => true;
    public string StatusText(AppConfig cfg) => "免注册 · 国际源";

    /// <summary>解析检索词：频道映射优先，其次取频道关键词的首个词，最后退回通用词。</summary>
    private static string ResolveTerm(SourceFetchRequest req)
    {
        if (!string.IsNullOrEmpty(req.ChannelKey) && ChannelTerm.TryGetValue(req.ChannelKey, out var mapped))
            return mapped;
        if (!string.IsNullOrWhiteSpace(req.Keywords))
        {
            var first = req.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) return first!;
        }
        return "wallpaper";
    }

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        try
        {
            // 频道 → 检索词（逐频道单关键词表；实测单词召回远好于双词组合）
            var term = ResolveTerm(req);

            var albums = await SearchAlbumsAsync(term, cfg, ct);
            if (albums.Count == 0)
            {
                Logger.Warn($"wallpapercave: no album for '{term}'");
                return Array.Empty<WallpaperItem>();
            }

            // 每页取一个专辑（按页轮换），翻页即换专辑 —— 内容持续更新且不会反复取同一张
            var page = Math.Max(1, req.Page);
            var album = albums[(page - 1) % albums.Count];

            var items = await FetchAlbumAsync(album.Slug, album.Title, cfg, ct);
            if (items == null) return null;

            var landscape = items.Where(i => i.Landscape).ToList();
            Logger.Info($"wallpapercave: album={album.Slug} items={items.Count} landscape={landscape.Count} term='{term}' page={page}");
            if (landscape.Count == 0) return Array.Empty<WallpaperItem>();

            // 每页最多给 PerPage 张：同一专辑内按页切片，翻页可继续深入该专辑
            var per = Math.Clamp(req.PerPage, 1, 60);
            var skip = ((page - 1) / albums.Count) * per;
            var slice = landscape.Skip(skip).Take(per).ToList();
            return slice.Count > 0 ? slice : landscape.Take(per).ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn($"wallpapercave fetch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>搜索专辑列表（服务端渲染 HTML）。</summary>
    private static async Task<List<(string Slug, string Title)>> SearchAlbumsAsync(string term, AppConfig cfg,
        CancellationToken ct)
    {
        var url = $"{Base}/search?q={Uri.EscapeDataString(term)}";
        var html = await SourceHttp.GetStringAsync(url, cfg, ct);
        if (string.IsNullOrEmpty(html)) return new List<(string, string)>();

        var albums = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html,
                     "<a href=\"(/[a-z0-9\\-]+)\" title=\"(\\d+) wallpapers in ([^\"]+)\"",
                     RegexOptions.IgnoreCase))
        {
            var slug = m.Groups[1].Value;
            var title = m.Groups[3].Value;
            var probe = (slug + " " + title).ToLowerInvariant();
            if (PortraitMarkers.Any(p => probe.Contains(p))) continue;   // 竖屏/手机专辑整张跳过
            if (!seen.Add(slug)) continue;
            albums.Add((slug, title));
        }
        return albums;
    }

    /// <summary>解析专辑页的全部条目（含宽高，用于横竖屏判定）。</summary>
    private async Task<List<WallpaperItem>?> FetchAlbumAsync(string slug, string albumTitle, AppConfig cfg,
        CancellationToken ct)
    {
        var html = await SourceHttp.GetStringAsync(Base + slug, cfg, ct);
        if (string.IsNullOrEmpty(html)) return null;

        // 以 <div class="wallpaper" id="wpNNN"> 为分块边界，块内首个 /wp/wpNNN.jpg 即该图的原图与尺寸
        var marks = Regex.Matches(html, "<div class=\"wallpaper\" id=\"(wp\\d+)\">", RegexOptions.IgnoreCase);
        var list = new List<WallpaperItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < marks.Count; i++)
        {
            var start = marks[i].Index;
            var end = i + 1 < marks.Count ? marks[i + 1].Index : Math.Min(html.Length, start + 4000);
            var block = html.Substring(start, end - start);

            var img = Regex.Match(block,
                "<img src=\"(/wp/wp\\d+\\.jpg)\" width=\"(\\d+)\" height=\"(\\d+)\" alt=\"([^\"]*)\"",
                RegexOptions.IgnoreCase);
            if (!img.Success) continue;

            var rel = img.Groups[1].Value;                      // /wp/wp16116145.jpg
            var w = int.Parse(img.Groups[2].Value);
            var h = int.Parse(img.Groups[3].Value);
            var alt = img.Groups[4].Value;
            var id = Path.GetFileNameWithoutExtension(rel);     // wp16116145
            if (!seen.Add(id)) continue;

            list.Add(new WallpaperItem
            {
                SourceKey = Key,
                Id = id,
                Path = Base + rel,                                            // /wp/ 原图（截图实测 1920x1200 ~ 3840x2400）
                Thumb = $"{Base}/wpt1x/{id}.jpg",                             // 小图缩略（jpeg，约 2KB）
                Resolution = $"{w}x{h}",
                Category = string.IsNullOrWhiteSpace(alt) ? albumTitle : alt,
                Purity = "sfw",
                PageUrl = Base + slug,
                Landscape = w > h,                                            // 仅横屏
            });
        }
        return list;
    }
}
