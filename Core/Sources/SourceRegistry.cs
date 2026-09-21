namespace PonyoWallpaper;

/// <summary>
/// 壁纸源注册表 + 多源聚合。
///
/// 聚合策略：勾选 N 个源时，各源<b>并发</b>取 <c>PerPage/N</c> 条，合并后按 <b>round-robin 交错</b>
/// 排列，避免"前一半全是 A 源"。单个源失败（返回 null）直接跳过，不影响其他源；
/// 全部失败才返回空列表。
/// </summary>
internal sealed class SourceRegistry
{
    private readonly IWallpaperSource[] _all;
    private readonly Dictionary<string, IWallpaperSource> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>内置且免注册、国内直连可达的源 —— 升级后的默认启用组合。</summary>
    public static readonly string[] DefaultEnabled = { "bing", "qh360", "picsum" };

    public SourceRegistry(WallhavenClient client)
    {
        _all = new IWallpaperSource[]
        {
            new WallhavenSource(client),
            new BingSource(),
            new Qh360Source(),
            new PicsumSource(),
            new UnsplashSource(),
            new PexelsSource(),
            new PixabaySource(),
        };
        foreach (var s in _all) _byKey[s.Key] = s;
    }

    public IReadOnlyList<IWallpaperSource> All => _all;

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
        var tasks = sources
            .Select(s => s.FetchAsync(cfg, new SourceFetchRequest
            {
                Page = req.Page,
                PerPage = per,
                Seed = req.Seed,
                WhCategory = req.WhCategory,
                WhQuery = req.WhQuery,
                Purity = req.Purity,
                Sorting = req.Sorting
            }, ct))
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
