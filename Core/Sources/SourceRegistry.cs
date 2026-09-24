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
    /// v1.5.2：github 重新纳入默认 —— 取图已改为「镜像候选 + 手填代理」多链路失败降级
    /// （<see cref="CdnMirror"/> + <see cref="SourceHttp"/>），不再出现 v1.5.1 的整片空白；
    /// wallhaven 仍需代理（国内直连不可达），保留在源菜单由用户按需勾选。
    /// </summary>
    public static readonly string[] DefaultEnabled = { "qh360", "wallpapercave", "github" };

    /// <summary>
    /// 单源超时：避免某个慢源/不可达源拖垮整页加载（合并是等所有源返回的）。
    ///
    /// v1.5.5 从 12s 放宽到 25s —— 12s 太紧：wallhaven 的链路是「直连 → 反代」，
    /// 直连超时（5s）+ 限流间隔（1.5s）+ 重试延迟（2s）已经逼近 12s，导致它**永远没机会走反代**
    /// 就被外层砍掉（日志特征：`failover -> 默认代理` 与 `source timeout after 12s` 同时出现），
    /// 表现就是用户说的「反代怎么都无法访问」。
    /// </summary>
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(25);

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
        // v1.5.7：「浏览源单选锁定」随其 UI 一并移除——单源浏览 = 只勾一个源，
        // 这里恒按 EnabledSources 取（归一化 + 可用性过滤 + 兜底）。
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
                    GroupKeys = req.GroupKeys,
                    LocalChannelId = req.LocalChannelId,
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
