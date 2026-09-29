namespace PonyoWallpaper;

/// <summary>
/// 轮换引擎：
/// - 定时器驱动按间隔切换壁纸
/// - 三级回退：缓存命中 → 搜索下载 → 缓存随机 → 保持不变
/// - 预取：每次轮换后后台静默下载下一张到缓存，切换瞬间零等待
/// - v1.3.1：范围新增「收藏」（key=fav）—— 从收藏夹随机取图，
///   原图不在缓存则按 wallhaven 直链规则重建（jpg 失败回退 png）
/// </summary>
internal sealed class RotationEngine : IDisposable
{
    private readonly WallhavenClient _api;
    private readonly CacheManager _cache;
    private readonly AppConfig _cfg;
    private readonly ListStore _favs;
    private readonly System.Timers.Timer _timer;
    private WallpaperItem? _current;
    private bool _busy;

    public event Action<WallpaperItem>? OnRotated;
    public WallpaperItem? Current => _current;

    /// <summary>
    /// 外部同步「当前壁纸」——手动设为壁纸（卡片 / 预览 / 托盘）时调用，
    /// 使状态栏右键的「收藏当前壁纸」始终指向桌面上真正在用的那张。
    /// </summary>
    public void SetCurrent(WallpaperItem item) => _current = item;

    public RotationEngine(AppConfig cfg, WallhavenClient api, CacheManager cache, ListStore favorites)
    {
        _cfg = cfg; _api = api; _cache = cache; _favs = favorites;
        _timer = new System.Timers.Timer { AutoReset = true };
        _timer.Elapsed += async (_, _) => await NextAsync();
        UpdateInterval(cfg.IntervalMinutes);
    }

    public void UpdateInterval(int minutes)
    {
        _cfg.IntervalMinutes = minutes;
        _timer.Interval = Math.Max(60_000, minutes * 60_000.0);
    }

    public void Start()
    {
        UpdateInterval(_cfg.IntervalMinutes);
        _timer.Start();
        Logger.Info($"rotation started, interval={_cfg.IntervalMinutes}min");
        _ = PrefetchAsync();
    }

    public void Stop() => _timer.Stop();

    /// <summary>最近一次轮换实际使用的频道显示名（供状态栏/历史记录）。</summary>
    public string CurrentChannelName { get; private set; } = "";

    /// <summary>
    /// 解析本轮自动更换范围：从启用的频道（二级分类粒度，可多选）中随机取一个。
    /// "nsfw" 为 NSFW 分类整体（purity=111），其余频道固定仅 SFW。浏览选择不影响轮换。
    /// v1.3.1："fav" = 收藏夹（随机取一张收藏；收藏夹为空时回退随机频道）。
    /// </summary>
    private (ChannelDef Channel, string Purity)? ResolveRotation()
    {
        var keys = _cfg.RotationChannels;
        if (keys == null || keys.Count == 0)
            keys = Channels.All.Select(c => c.Key).ToList();

        var key = keys[Random.Shared.Next(keys.Count)];
        if (key == "nsfw")
            return (new ChannelDef("nsfw", "NSFW", "111", _cfg.HiddenQuery), "111");
        if (key == "fav")
        {
            if (_favs.All().Count > 0)
                return (new ChannelDef("fav", "收藏", "100", ""), "100");
            key = Channels.All[Random.Shared.Next(Channels.All.Length)].Key;   // 收藏夹为空 → 随机频道
        }

        var ch = Channels.Find(key)
                 ?? Channels.All[Random.Shared.Next(Channels.All.Length)];
        return (ch, "100");
    }

    /// <summary>
    /// 收藏轮换的候选集：把收藏记录还原成 WallpaperItem（wallhaven id）。
    /// 原图直链按 wallhaven 规则推导（收藏记录 v1.3 不落盘直链）。
    /// </summary>
    private List<WallpaperItem> FavCandidates()
    {
        var items = new List<WallpaperItem>();
        foreach (var rec in _favs.All())
        {
            if (rec.Id.Length < 2) continue;
            items.Add(new WallpaperItem
            {
                Id = rec.Id,
                Path = $"https://w.wallhaven.cc/full/{rec.Id[..2]}/wallhaven-{rec.Id}.jpg",
                Resolution = rec.Resolution,
                PageUrl = rec.PageUrl
            });
        }
        return items;
    }

    /// <summary>立即以当前配置（填充/多屏）重新应用当前壁纸，用于设置即时生效。</summary>
    public void ApplyCurrent()
    {
        try
        {
            var id = _current?.Id;
            if (id != null && File.Exists(_cache.FullPath(id)))
                ApplyWallpaper(_cache.FullPath(id));
        }
        catch (Exception ex) { Logger.Warn($"apply current: {ex.Message}"); }
    }

    public async Task<WallpaperItem?> NextAsync()
    {
        if (_busy) return _current;
        _busy = true;
        try { return await RotateAsync(); }
        finally { _busy = false; }
    }

    /// <summary>上一张：简化实现为从缓存随机取已下载图应用。</summary>
    public async Task<WallpaperItem?> PrevAsync()
    {
        var ids = _cache.RandomIds(1).ToList();
        if (ids.Count == 0) return null;
        var path = _cache.FullPath(ids[0]);
        if (!File.Exists(path)) return null;
        if (ApplyWallpaper(path))
        {
            _current = new WallpaperItem { Id = ids[0] };
            _cache.Touch(ids[0]);
            Logger.Info($"prev (random cached): {ids[0]}");
            OnRotated?.Invoke(_current);
            return _current;
        }
        return null;
    }

    /// <summary>应用壁纸（同图或独立模式）。独立模式下主屏用指定图，其余屏从缓存取不同图。</summary>
    private bool ApplyWallpaper(string fullPath)
    {
        if (_cfg.Monitors == "independent")
        {
            var monitorIds = MultiMonitorWallpaper.GetMonitorIds();
            if (monitorIds.Count > 1)
            {
                var map = new Dictionary<string, string> { [monitorIds[0]] = fullPath };
                var cached = _cache.RandomIds(monitorIds.Count * 2).ToList();
                int ci = 0;
                for (int i = 1; i < monitorIds.Count; i++)
                {
                    string p = fullPath;
                    while (ci < cached.Count)
                    {
                        var cp = _cache.FullPath(cached[ci++]);
                        if (File.Exists(cp)) { p = cp; break; }
                    }
                    map[monitorIds[i]] = p;
                }
                if (WallpaperSetter.SetIndependent(map, _cfg.FillMode))
                {
                    Logger.Info($"independent: {monitorIds.Count} monitors");
                    return true;
                }
            }
        }
        return WallpaperSetter.Set(fullPath, _cfg.FillMode);
    }

    private async Task<WallpaperItem?> RotateAsync()
    {
        try
        {
            // 1. 搜索拉一张（含 AND 降级可后续加入；M1 阶段直接按 channel 全 OR）
            var item = await FetchOneAsync();
            // 2. 搜索失败 → 缓存随机
            if (item == null)
            {
                var ids = _cache.RandomIds(1).ToList();
                if (ids.Count > 0)
                {
                    var path = _cache.FullPath(ids[0]);
                    if (File.Exists(path))
                    {
                        _current = new WallpaperItem { Id = ids[0] };
                        _cache.Touch(ids[0]);
                        if (ApplyWallpaper(path))
                        {
                            Logger.Info($"rotated from cache: {ids[0]}");
                            OnRotated?.Invoke(_current);
                            _ = PrefetchAsync();
                            return _current;
                        }
                    }
                }
                Logger.Warn("rotate: no search result and no cached fallback");
                return null;
            }
            // 3. 应用
            _current = item;
            var fullPath = _cache.FullPath(item.Id);
            if (File.Exists(fullPath) && ApplyWallpaper(fullPath))
            {
                Logger.Info($"rotated: {item.Id} {item.Resolution} ({item.Purity})");
                _cache.Touch(item.Id);
                _cache.EnforceLimit();
                OnRotated?.Invoke(item);
                _ = PrefetchAsync();
                return item;
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error("rotate failed", ex);
            return null;
        }
    }

    private async Task<WallpaperItem?> FetchOneAsync()
    {
        var (ch, purity) = ResolveRotation() ?? default;
        if (ch == null) return null;
        CurrentChannelName = ch.Name;

        // v1.3.1 收藏轮换：本地候选，无需搜索
        List<WallpaperItem>? favList = null;
        if (ch.Key == "fav")
        {
            favList = FavCandidates();
            if (favList.Count == 0) return null;
        }

        IReadOnlyList<WallpaperItem> list;
        if (favList != null)
        {
            list = favList;
        }
        else
        {
            try
            {
                var page = Random.Shared.Next(1, 6);
                var searched = await _api.SearchAsync(ch.Category, purity, _cfg.Sorting, ch.Query, _cfg.Resolution, page);
                if (searched == null || searched.Count == 0) return null;
                list = searched;
            }
            catch (Exception ex)
            {
                // 搜索异常（如代理不可达）不能向外抛 —— 否则上层 catch 会连缓存兜底一起跳过
                Logger.Warn($"search {ch.Key}: {ex.Message}");
                return null;
            }
        }

        // 尝试 5 张直到有一张能成功下载（避开偶尔 404 的图）；收藏直链默认 jpg，失败回退 png
        foreach (var cand in list.OrderBy(_ => Random.Shared.Next()).Take(5))
        {
            var dst = _cache.FullPath(cand.Id);
            try
            {
                if (!File.Exists(dst))
                    await _api.DownloadAsync(cand.Path, dst);
                _cache.Touch(cand.Id);
                return cand;
            }
            catch when (cand.Path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        && cand.Path.Contains("/full/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _api.DownloadAsync(cand.Path[..^4] + ".png", dst);
                    _cache.Touch(cand.Id);
                    return cand;
                }
                catch { /* png 也失败，试下一张 */ }
            }
            catch (Exception ex)
            {
                Logger.Warn($"fetch {cand.Id}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>后台预取一张到缓存（不阻塞主流程）。</summary>
    public async Task PrefetchAsync()
    {
        try
        {
            var (ch, purity) = ResolveRotation() ?? default;
            if (ch == null) return;
            CurrentChannelName = ch.Name;

            IReadOnlyList<WallpaperItem> list;
            if (ch.Key == "fav")
            {
                var favs = FavCandidates();
                if (favs.Count == 0) return;
                list = favs;
            }
            else
            {
                var page = Random.Shared.Next(1, 6);
                var searched = await _api.SearchAsync(ch.Category, purity, _cfg.Sorting, ch.Query, _cfg.Resolution, page);
                if (searched == null || searched.Count == 0) return;
                list = searched;
            }

            foreach (var cand in list.OrderBy(_ => Random.Shared.Next()).Take(3))
            {
                if (_cache.HasFull(cand.Id)) continue;
                var dst = _cache.FullPath(cand.Id);
                try
                {
                    await _api.DownloadAsync(cand.Path, dst);
                    _cache.Touch(cand.Id);
                    Logger.Info($"prefetched: {cand.Id}");
                    return;
                }
                catch { /* try next */ }
            }
        }
        catch (Exception ex) { Logger.Warn($"prefetch: {ex.Message}"); }
    }

    public void Dispose() { _timer.Dispose(); }
}