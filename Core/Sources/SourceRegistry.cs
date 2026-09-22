namespace PonyoWallpaper;

/// <summary>
/// 壁纸源注册表 + 多源聚合。
///
/// 聚合策略：勾选 N 个源时，各源<b>并发</b>取 <c>PerPage/N</c> 条，合并后按 <b>round-robin 交错</b>
/// 排列，避免"前一半全是 A 源"。单个源失败（返回 null）直接跳过，不影响其他源；
/// 全部失败才返回空列表。
///
/// v1.5.0：只保留提供电脑横屏壁纸的源 —— wallhaven / 360 壁纸 / WallpaperCave。
/// </summary>
internal sealed class SourceRegistry
{
    private readonly IWallpaperSource[] _all;
    private readonly Dictionary<string, IWallpaperSource> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 默认启用的源。
    /// v1.5.1：只默认启用<b>免注册且国内直连稳定</b>的两个源，保证开箱即有图；
    /// wallhaven（需代理）与 github（图库在 GitHub，取图走 jsDelivr/代理链路，
    /// 实测国内时快时断）都保留在源菜单里由用户按需勾选。
    /// </summary>
    public static readonly string[] DefaultEnabled = { "qh360", "wallpapercave" };

    /// <summary>单源超时：避免某个慢源/不可达源拖垮整页加载（合并是等所有源返回的）。</summary>
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(12);

    public SourceRegistry(WallhavenClient client)
    {
        _all = new IWallpaperSource[]
        {
            new WallhavenSource(client),
            new Qh360Source(),
            new WallpaperCaveSource(),
            new GitHubWallsSource(),
        };
        foreach (var s in _all) _byKey[s.Key] = s;
    }

    public IReadOnlyList<IWallpaperSource> All => _all;

    /// <summary>全部已注册源 key（静态表，供配置迁移用）。</summary>
    private static readonly string[] AllKeys = { "wallhaven", "qh360", "wallpapercave", "github" };

    /// <summary>
    /// 配置迁移（启动时调用一次）：
    /// 1) 剔除已不存在的源 key（如 v1.5.0 移除的 bing/picsum/unsplash/pexels/pixabay）；
    /// 2) 若旧配置里存在已移除的源，说明是升级用户 —— 自动补上新默认源，避免升级后可用源变少；
    /// 3) 过滤后为空则回落到默认组合。
    /// 变更时立即落盘，保证界面（源菜单勾选状态）与配置一致。
    /// </summary>
    public static void NormalizeEnabled(AppConfig cfg)
    {
        var keys = cfg.EnabledSources;
        if (keys == null || keys.Count == 0)
        {
            cfg.EnabledSources = DefaultEnabled.ToList();
            cfg.Save();
            return;
        }

        static bool Known(string k) => AllKeys.Contains(k, StringComparer.OrdinalIgnoreCase);
        var hadRemoved = keys.Any(k => !Known(k));
        var kept = keys.Where(Known).ToList();

        if (kept.Count == 0)
        {
            kept = DefaultEnabled.ToList();
        }
        else if (hadRemoved)
        {
            foreach (var d in DefaultEnabled)
                if (!kept.Contains(d, StringComparer.OrdinalIgnoreCase))
                    kept.Add(d);
        }

        if (kept.Count != keys.Count || !kept.SequenceEqual(keys, StringComparer.OrdinalIgnoreCase))
        {
            Logger.Info($"sources migrated: [{string.Join(",", keys)}] -> [{string.Join(",", kept)}]");
            cfg.EnabledSources = kept;
            cfg.Save();
        }
    }

    public IWallpaperSource? Find(string key) => _byKey.TryGetValue(key, out var s) ? s : null;

    /// <summary>已启用且当前可用的源（未配置 Key 的需 Key 源自动排除）。</summary>
    public IReadOnlyList<IWallpaperSource> Enabled(AppConfig cfg)
    {
        var keys = cfg.EnabledSources;
        if (keys == null || keys.Count == 0) keys = DefaultEnabled.ToList();
        var list = new List<IWallpaperSource>();
        foreach (var k in keys)
        {
            var s = Find(k);
            if (s != null && s.IsReady(cfg)) list.Add(s);
        }
        // 兜底：万一一个可用源都没有，至少退回默认组合，避免界面永远空白
        if (list.Count == 0)
            foreach (var k in DefaultEnabled)
                if (Find(k) is { } s && s.IsReady(cfg)) list.Add(s);
        return list;
    }

    /// <summary>并发取所有启用源，交错合并。</summary>
    public async Task<List<WallpaperItem>> FetchMergedAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct = default)
    {
        var sources = Enabled(cfg);
        if (sources.Count == 0) return new List<WallpaperItem>();

        var per = Math.Max(6, req.PerPage / sources.Count);
        // 每源一个独立的超时窗口：慢源/不可达源到点即判失败，不拖累整页
        var tasks = sources
            .Select(async s =>
            {
                var one = new SourceFetchRequest
                {
                    Page = req.Page,
                    PerPage = per,
                    Seed = req.Seed,
                    WhCategory = req.WhCategory,
                    WhQuery = req.WhQuery,
                    Keywords = req.Keywords,
                    ChannelKey = req.ChannelKey,
                    Purity = req.Purity,
                    Sorting = req.Sorting
                };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(PerSourceTimeout);
                try
                {
                    return await s.FetchAsync(cfg, one, cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Logger.Warn($"source '{s.Key}' timeout after {PerSourceTimeout.TotalSeconds:F0}s, skipped");
                    return null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"source '{s.Key}' error: {ex.Message}");
                    return null;
                }
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var ok = 0;
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] != null) ok++;
            else Logger.Warn($"source '{sources[i].Key}' failed on page {req.Page}, skipped");
        }
        if (ok == 0) return new List<WallpaperItem>();

        // round-robin 交错
        var merged = new List<WallpaperItem>();
        var maxLen = results.Where(r => r != null).Max(r => r!.Count);
        for (var idx = 0; idx < maxLen; idx++)
            for (var s = 0; s < results.Length; s++)
            {
                var r = results[s];
                if (r != null && idx < r.Count) merged.Add(r[idx]);
            }

        Logger.Info($"sources merged: {ok}/{sources.Count} ok ({string.Join(",", sources.Select(s => s.Key))}) -> {merged.Count} items");
        return merged;
    }
}
