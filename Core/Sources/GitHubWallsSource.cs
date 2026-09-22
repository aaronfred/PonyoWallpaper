using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PonyoWallpaper;

/// <summary>
/// GitHub 图库源（v1.5.1）—— 把 GitHub 上的开源壁纸仓库当图库用。
///
/// 原理：
///   1) 用 GitHub Trees API 一次性列出仓库全部文件（结果<b>本地缓存 24 小时</b>，
///      因为未认证的 GitHub API 只有 60 次/小时，不能让每次翻页都去打接口）；
///   2) 只保留 <b>.jpg/.jpeg/.png</b> —— 与程序现有 GDI+ 管线完全兼容，
///      且严格符合「轻量化」原则：不引入任何 webp/avif 解码依赖（本机 WIC 也无 webp codec）；
///   3) 走 jsDelivr 免费 CDN 取图（国内直连可达，实测 cdn/gcore 双域名可用），
///      不消耗 GitHub 的 raw 流量，也不需要 Token。
///
/// 频道映射：仓库的<b>目录名</b>与频道关键词做包含匹配（如 anime/nature/city/space/minimal…），
/// 命中则只出该目录的图；无命中时退回该仓库全部图片。
/// </summary>
internal sealed class GitHubWallsSource : IWallpaperSource
{
    /// <summary>入库的仓库（repo, 分支）。均为开源壁纸集合，注意各自许可证。</summary>
    private static readonly (string Repo, string Branch, string License)[] Repos =
    {
        ("Tcode-Motion/os-wallpapers",        "main", "未声明"),   // 2107 张，按 wallpapers/{ai,anime_girls,macro,minimal,aurora,cyberpunk_girl…} 分类，每类约 200 张
        ("dharmx/walls",                      "main", "未声明"),   // 1637 张，按 anime/nord/flowers/centered/architecture… 分类
        ("Joao2Pereira1/Wallpapers",          "main", "未声明"),   // 443 张，Animes/Fantasy/Logos/Landscapes/Space/Relaxing
        ("D3Ext/aesthetic-wallpapers",        "main", "MIT"),      // 380 张，许可最干净
        ("vimlinuz/wall-archive",             "main", "未声明"),   // 335 张
    };

    /// <summary>允许的图片扩展名（不接 webp/avif —— 保持零解码依赖）。</summary>
    private static readonly string[] Allowed = { ".jpg", ".jpeg", ".png" };

    /// <summary>清单缓存有效期。</summary>
    private static readonly TimeSpan ManifestTtl = TimeSpan.FromHours(24);

    // repo|branch -> (时间戳, 文件路径列表)。进程内缓存，避免同一次运行重复读盘。
    private static readonly Dictionary<string, (DateTime At, List<string> Files)> Cache = new();
    private static readonly object CacheLock = new();

    public string Key => "github";
    public string DisplayName => "GitHub 图库";
    public bool NeedsApiKey => false;
    public bool IsReady(AppConfig cfg) => true;
    public string StatusText(AppConfig cfg) => "需国际线路 · 仅 jpg/png";

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        try
        {
            var keywords = BuildKeywords(req);
            var pool = new List<(string Repo, string Branch, string Path, string Dir)>();

            foreach (var (repo, branch, _) in Repos)
            {
                var files = await GetManifestAsync(repo, branch, ct);
                if (files == null || files.Count == 0) continue;

                var picked = 0;
                foreach (var f in files)
                {
                    if (!Allowed.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                    var dir = DirOf(f);
                    pool.Add((repo, branch, f, dir));
                    if (++picked >= 3000) break;   // 安全上限（单仓库通常 ≤ 2200 张）
                }
            }

            if (pool.Count == 0)
            {
                Logger.Warn("github: no manifest available (可能触及 GitHub API 限流 60/h，稍后自动重试)");
                return Array.Empty<WallpaperItem>();
            }

            // 目录名匹配频道关键词；全部落空时退回全库（这些仓库本身就是壁纸集合，兜底无害），
            // 保证任何分类都不会因为目录命名差异而空白
            var total = pool.Count;
            if (keywords.Count > 0)
            {
                var matched = pool
                    .Where(x => keywords.Any(k => x.Dir.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (matched.Count > 0) pool = matched;
                else Logger.Info($"github: 目录未命中 [{string.Join(",", keywords)}]，退回全库 {total} 张");
            }

            // 用 seed/page 做确定性打散：翻页不重复、换一批换 seed 即换图
            var seed = string.IsNullOrEmpty(req.Seed) ? "gh" : req.Seed!;
            var shuffled = pool
                .OrderBy(x => StableHash(x.Repo + x.Path + seed))
                .ToList();

            var per = Math.Clamp(req.PerPage, 1, 60);
            var skip = (Math.Max(1, req.Page) - 1) * per;
            if (skip >= shuffled.Count) return Array.Empty<WallpaperItem>();
            var slice = shuffled.Skip(skip).Take(per).ToList();

            var list = new List<WallpaperItem>();
            foreach (var (repo, branch, path, dir) in slice)
            {
                var url = RawCdn(repo, branch, path);
                list.Add(new WallpaperItem
                {
                    SourceKey = Key,
                    Id = $"{repo.Split('/')[1]}/{path}",      // 稳定唯一（含仓库名，避免跨仓同名）
                    Path = url,
                    Thumb = url,                               // jsDelivr 不做缩放，缩略图复用原图（首次即缓存，点“设为壁纸”时无需再下）
                    Resolution = "",
                    Category = dir,
                    Purity = "sfw",
                    PageUrl = $"https://github.com/{repo}",
                });
            }

            Logger.Info($"github: keywords=[{string.Join(",", keywords)}] pool={pool.Count} page={req.Page} -> {list.Count} items");
            return list;
        }
        catch (Exception ex)
        {
            Logger.Warn($"github fetch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>把频道信息转成用于匹配目录名的关键词集合。</summary>
    private static List<string> BuildKeywords(SourceFetchRequest req)
    {
        // 关键词 = 各仓库<b>实际存在的目录名</b>（已实地核对）：
        //   Tcode-Motion/os-wallpapers: ai / anime / anime_girls / aurora / macro / minimal / vaporwave /
        //                              surreal / abstract / cars / cyberpunk / cyberpunk_girl / marvel /
        //                              robot / sci-fi / space / windows_11 / waifu / coding / hacking / hud …
        //   dharmx/walls: anime / nord / flowers / centered / architecture / abstract / unsorted …
        //   Joao2Pereira1/Wallpapers: Animes / Fantasy / Logos / Landscapes / Space / Relaxing …
        // 命中则只出该目录的图；全部落空会退回全库（见 FetchAsync）。
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["nature_landscape"] = new[] { "landscape", "nature", "scenery", "relaxing", "flowers" },
            ["nature_sea"]       = new[] { "sea", "ocean", "beach", "water", "vaporwave" },
            ["nature_mountain"]  = new[] { "mountain", "forest", "nature", "landscape" },
            ["nature_flower"]    = new[] { "flower", "flowers", "macro", "plant" },
            ["nature_sunset"]    = new[] { "sunset", "sunrise", "sky", "aurora" },
            ["nature_snow"]      = new[] { "snow", "winter", "ice" },
            ["photo_city"]       = new[] { "city", "architecture", "urban", "cyberpunk", "vaporwave" },
            ["photo_space"]      = new[] { "space", "aurora", "sci-fi", "star", "galaxy", "cosmic" },
            ["photo_minimalism"] = new[] { "minimal", "abstract", "surreal", "vaporwave", "nord", "centered" },
            ["photo_animals"]    = new[] { "animal", "cat", "bird", "macro" },
            ["photo_cars"]       = new[] { "cars", "car", "vehicle" },
            ["people_portrait"]  = new[] { "portrait", "people", "girl", "waifu" },
            ["people_fashion"]   = new[] { "fashion", "model", "girl" },
            ["people_sports"]    = new[] { "sport", "athlet" },
            ["people_movies"]    = new[] { "marvel", "movie", "cinema", "film" },
            ["people_street"]    = new[] { "street", "urban", "city" },
            ["people_art"]       = new[] { "art", "abstract", "surreal", "fantasy" },
            ["anime_girls"]      = new[] { "anime", "animes", "waifu", "cyberpunk_girl", "girl" },
            ["anime_shonen"]     = new[] { "anime", "animes", "marvel" },
            ["anime_mecha"]      = new[] { "robot", "mecha", "sci-fi", "cyberpunk" },
            ["anime_games"]      = new[] { "game", "coding", "anime" },
            ["anime_scenery"]    = new[] { "anime", "scenery", "aurora", "fantasy" },
            ["anime_animals"]    = new[] { "anime", "animal", "cat" },
        };

        if (!string.IsNullOrEmpty(req.ChannelKey) && map.TryGetValue(req.ChannelKey, out var hits))
            return hits.ToList();

        // 一级分类/无频道：用频道关键词的首词，再退回“全都要”
        if (!string.IsNullOrWhiteSpace(req.Keywords))
        {
            var first = req.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) return new List<string> { first! };
        }
        return new List<string>();
    }

    private static string DirOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i > 0 ? path[..i] : "";
    }

    /// <summary>jsDelivr CDN 直链（免费、无需 Token、国内可达）。</summary>
    private static string RawCdn(string repo, string branch, string path)
    {
        var segs = path.Split('/').Select(Uri.EscapeDataString);
        return $"https://cdn.jsdelivr.net/gh/{repo}@{branch}/{string.Join("/", segs)}";
    }

    /// <summary>
    /// 取仓库文件清单。优先用本地缓存（24h TTL），再退进程内缓存，最后才打 GitHub API
    /// —— 未认证只有 60 次/小时，绝不能每次翻页都请求。
    /// </summary>
    private static async Task<List<string>?> GetManifestAsync(string repo, string branch, CancellationToken ct)
    {
        var key = $"{repo}@{branch}";
        lock (CacheLock)
            if (Cache.TryGetValue(key, out var hit)) return hit.Files;

        var cacheFile = Path.Combine(AppPaths.DataDir,
            "gh-" + repo.Replace('/', '_') + ".json");
        try
        {
            if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < ManifestTtl)
            {
                var cached = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cacheFile, Encoding.UTF8));
                if (cached is { Count: > 0 })
                {
                    lock (CacheLock) Cache[key] = (DateTime.UtcNow, cached);
                    Logger.Info($"github: manifest cache hit {repo} ({cached.Count} files)");
                    return cached;
                }
            }
        }
        catch (Exception ex) { Logger.Warn($"github: manifest cache read failed {repo}: {ex.Message}"); }

        try
        {
            var url = $"https://api.github.com/repos/{repo}/git/trees/{branch}?recursive=1";
            using var resp = await SourceHttp.Get().GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Warn($"github: tree {repo} -> {(int)resp.StatusCode} (可能触及 API 限流 60/h)");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var tree = JsonSerializer.Deserialize<GitTree>(json);
            var files = tree?.Tree?
                .Where(t => string.Equals(t.Type, "blob", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Path ?? "")
                .Where(p => p.Length > 0)
                .ToList() ?? new List<string>();

            if (files.Count > 0)
            {
                // 不带 BOM 写盘（避免被其它工具读成 JSONDecodeError）
                try { File.WriteAllText(cacheFile, JsonSerializer.Serialize(files), new UTF8Encoding(false)); }
                catch (Exception ex) { Logger.Warn($"github: manifest cache write failed: {ex.Message}"); }
                lock (CacheLock) Cache[key] = (DateTime.UtcNow, files);
                Logger.Info($"github: manifest fetched {repo} ({files.Count} files)");
            }
            return files;
        }
        catch (Exception ex)
        {
            Logger.Warn($"github: manifest fetch failed {repo}: {ex.Message}");
            return null;
        }
    }

    /// <summary>稳定哈希（跨运行一致，用于确定性打散）。</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }

    private sealed class GitTree
    {
        [JsonPropertyName("tree")] public List<GitTreeEntry>? Tree { get; set; }
    }

    private sealed class GitTreeEntry
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }
}
