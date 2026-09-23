using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PonyoWallpaper;

/// <summary>
/// GitHub 图库源（v1.5.1）—— 把 GitHub 上的开源壁纸仓库当图库用。
///
/// 原理：
///   1) 用 GitHub Trees API 一次性列出仓库全部文件（结果<b>本地缓存 24 小时</b>，
///      因为未认证的 GitHub API 只有 60 次/小时，不能让每次翻页都去打接口）；
///   2) 只保留 <b>.jpg/.jpeg/.png</b>，且 <b>blob ≤ 1MB</b>（见 <see cref="MaxBlobBytes"/>）——
///      与程序现有 GDI+ 管线完全兼容，不引入任何 webp/avif 解码依赖（本机 WIC 也无 webp codec）；
///      体积上限是为了「浏览只吃小图」：仓库里中位 2.33MB、最大 44MB，直接当缩略图会顶爆流量与内存（v1.5.3）；
///   3) 取图走 <see cref="CdnMirror"/> 展开的多条等价链路（raw / ghproxy / jsDelivr / gh-proxy），
///      任一跳通了即用 —— v1.5.2 起单条链路抖动不再导致整片卡片空白。
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

    /// <summary>
    /// 浏览用文件的体积上限（v1.5.3）。实测这 5 个仓库的图片：中位 2.33MB、P90 10.7MB、最大 44MB ——
    /// 用原图当缩略图，首屏十几张就要下几十 MB，解码后内存更高（单张 5120×2880 ≈ 59MB）。
    /// 因此在<b>清单阶段</b>就按 blob size 过滤，只收录小图；若某仓库全部被滤空则自动放宽（见 FetchAsync）。
    /// </summary>
    private const long MaxBlobBytes = 1024 * 1024;   // 1.0 MB

    /// <summary>清单缓存有效期。</summary>
    private static readonly TimeSpan ManifestTtl = TimeSpan.FromHours(24);

    // repo|branch -> (时间戳, 文件列表)。进程内缓存，避免同一次运行重复读盘。
    private static readonly Dictionary<string, (DateTime At, List<GhFile> Files)> Cache = new();
    private static readonly object CacheLock = new();

    public string Key => "github";
    public string DisplayName => "GitHub 图库";
    public bool NeedsApiKey => false;
    public bool IsReady(AppConfig cfg) => true;
    public string StatusText(AppConfig cfg) => "免注册 · 多链路取图 · 仅 jpg/png";

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        try
        {
            var keywords = BuildKeywords(req);
            var pool = new List<(string Repo, string Branch, string Path, string Dir)>();
            var allPool = new List<(string Repo, string Branch, string Path, string Dir)>();

            foreach (var (repo, branch, _) in Repos)
            {
                var files = await GetManifestAsync(repo, branch, cfg, ct);
                if (files == null || files.Count == 0) continue;

                var picked = 0;
                foreach (var f in files)
                {
                    if (string.IsNullOrEmpty(f.Path)) continue;
                    if (!Allowed.Any(e => f.Path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                    var dir = DirOf(f.Path);
                    var entry = (repo, branch, f.Path, dir);
                    allPool.Add(entry);
                    if (f.Size <= MaxBlobBytes) pool.Add(entry);   // 浏览只收录小图（见 MaxBlobBytes）
                    if (++picked >= 3000) break;   // 安全上限（单仓库通常 ≤ 2200 张）
                }
            }

            // 兜底：万一某个仓库/整天全是巨图，过滤后为空就直接放宽，保证分类不空
            if (pool.Count == 0 && allPool.Count > 0)
            {
                Logger.Warn($"github: 无 ≤{MaxBlobBytes / 1024 / 1024}MB 的图，放宽到全量 {allPool.Count} 张");
                pool = allPool;
            }

            if (pool.Count == 0)
            {
                Logger.Warn("github: no manifest available (可能触及 GitHub API 限流 60/h，稍后自动重试)");
                return Array.Empty<WallpaperItem>();
            }

            var total = pool.Count;
            // 目录名匹配频道关键词；命中则只出该目录的图。
            // v1.5.4：<b>取消"全部落空就退回全库"的兜底</b> —— 那会让"点风景"混进动漫/人物，
            // 是「点分类出来很乱」的主要来源之一。现在匹配不到就不出图，由其它有能力的源供图。
            if (keywords.Count > 0)
            {
                var matched = pool
                    .Where(x => keywords.Any(k => x.Dir.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (matched.Count > 0)
                {
                    pool = matched;
                }
                else
                {
                    Logger.Info($"github: 目录未命中 [{string.Join(",", keywords)}]，本页不出图（共 {total} 张候选）");
                    return Array.Empty<WallpaperItem>();
                }
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
                var url = RawUrl(repo, branch, path);
                // 这些仓库里大量文件本身就是 wallhaven 的图（文件名 `wallhaven-<id>.jpg`）——
                // 浏览时直接用 wallhaven 的缩略图服务（几 KB、走内置默认代理），
                // 比下 1MB 原图快一个数量级；万一取不到会自动回退到 GitHub 原图。
                var wallThumb = WallhavenThumbFor(path);
                list.Add(new WallpaperItem
                {
                    SourceKey = Key,
                    Id = $"{repo.Split('/')[1]}/{path}",      // 稳定唯一（含仓库名，避免跨仓同名）
                    Path = url,
                    Thumb = wallThumb ?? url,
                    ThumbFallback = wallThumb == null ? "" : url,
                    Resolution = "",
                    Category = dir,
                    Purity = "sfw",
                    PageUrl = $"https://github.com/{repo}",
                });
            }

            Logger.Info($"github: keywords=[{string.Join(",", keywords)}] " +
                        $"pool={pool.Count}/{allPool.Count}(≤{MaxBlobBytes / 1024 / 1024}MB) page={req.Page} -> {list.Count} items");
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
        // v1.5.4 收紧：删掉明显不相干的词（vaporwave 是合成波美学不是风景、relaxing 目录内容不确定、
        // surreal 与"极简"不符）—— 它们会让"点风景"混进科技感电路板之类的图。
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["nature_landscape"] = new[] { "landscape", "nature", "scenery" },
            ["nature_sea"]       = new[] { "sea", "ocean", "beach", "water" },
            ["nature_mountain"]  = new[] { "mountain", "forest" },
            ["nature_flower"]    = new[] { "flower", "flowers", "macro" },
            ["nature_sunset"]    = new[] { "sunset", "sunrise", "sky", "aurora" },
            ["nature_snow"]      = new[] { "snow", "winter", "ice" },
            ["photo_city"]       = new[] { "city", "architecture", "urban" },
            ["photo_space"]      = new[] { "space", "galaxy", "star", "cosmic", "sci-fi" },
            ["photo_minimalism"] = new[] { "minimal", "abstract", "nord", "centered" },
            ["photo_animals"]    = new[] { "animal", "cat", "bird" },
            ["photo_cars"]       = new[] { "cars", "car", "vehicle" },
            ["people_portrait"]  = new[] { "portrait", "people", "girl", "waifu" },
            ["people_fashion"]   = new[] { "fashion", "model" },
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

        // v1.5.4：一级分类（grp）—— 取该组全部子频道关键词的并集，
        // 这样"点风景"至少能覆盖 landscape/sea/mountain/flower/sunset/snow 这些目录，而不是退回全库
        if (req.GroupKeys is { Count: > 0 })
        {
            var union = req.GroupKeys
                .Where(map.ContainsKey)
                .SelectMany(k => map[k])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (union.Count > 0) return union;
        }

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

    /// <summary>
    /// 文件名形如 <c>wallhaven-135529.jpg</c> / <c>wallhaven-21zryg.png</c> 时返回 wallhaven 的小图地址
    /// （<c>https://th.wallhaven.cc/small/&lt;id 前两位&gt;/&lt;id&gt;.jpg</c>，通常几 KB）；否则返回 null。
    /// 浏览只吃这张小图，原图仅在"设为壁纸/收藏下载"时按需取。
    /// </summary>
    private static string? WallhavenThumbFor(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var m = Regex.Match(name, @"^wallhaven-([A-Za-z0-9]+)\.(?:jpg|jpeg|png)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var id = m.Groups[1].Value.ToLowerInvariant();
        if (id.Length < 2) return null;
        return $"https://th.wallhaven.cc/small/{id[..2]}/{id}.jpg";
    }

    /// <summary>
    /// GitHub raw 直链 —— 只作 <b>标识</b> 用：真正下载时由 <see cref="CdnMirror"/> 展开为
    /// raw / ghproxy / jsDelivr / gh-proxy 多条等价链路，逐条快速失败重试（见 <see cref="SourceHttp"/>）。
    /// </summary>
    private static string RawUrl(string repo, string branch, string path)
        => $"https://raw.githubusercontent.com/{repo}/{branch}/{path}";

    /// <summary>
    /// 取仓库文件清单。优先用本地缓存（24h TTL），再退进程内缓存，最后才打 GitHub API
    /// —— 未认证只有 60 次/小时，绝不能每次翻页都请求。
    /// </summary>
    private static async Task<List<GhFile>?> GetManifestAsync(string repo, string branch, AppConfig cfg,
        CancellationToken ct)
    {
        var key = $"{repo}@{branch}";
        lock (CacheLock)
            if (Cache.TryGetValue(key, out var hit)) return hit.Files;

        // v1.5.3：清单元素带 size（用于过滤超大图），缓存文件名加 gh2- 前缀与旧格式区分
        var cacheFile = Path.Combine(AppPaths.DataDir,
            "gh2-" + repo.Replace('/', '_') + ".json");
        try
        {
            if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < ManifestTtl)
            {
                var cached = JsonSerializer.Deserialize<List<GhFile>>(File.ReadAllText(cacheFile, Encoding.UTF8));
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
            var json = await SourceHttp.GetStringAsync(url, cfg, ct);
            if (string.IsNullOrEmpty(json))
            {
                Logger.Warn($"github: tree {repo} 取不到（GitHub API 限流 60/h 或链路不可达）");
                return null;
            }
            var tree = JsonSerializer.Deserialize<GitTree>(json);
            var files = tree?.Tree?
                .Where(t => string.Equals(t.Type, "blob", StringComparison.OrdinalIgnoreCase))
                .Select(t => new GhFile { Path = t.Path ?? "", Size = t.Size })
                .Where(f => f.Path.Length > 0)
                .ToList() ?? new List<GhFile>();

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
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    /// <summary>清单条目：路径 + 文件字节数（字节数用于在清单阶段滤掉超大图，见 <see cref="MaxBlobBytes"/>）。</summary>
    private sealed class GhFile
    {
        [JsonPropertyName("p")] public string Path { get; set; } = "";
        [JsonPropertyName("s")] public long Size { get; set; }
    }
}
