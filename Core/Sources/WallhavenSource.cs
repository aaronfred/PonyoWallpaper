namespace PonyoWallpaper;

/// <summary>
/// wallhaven（原唯一数据源）。它是唯一有完整频道树与查询语法的源，
/// 因此沿用现有 WallhavenClient 的全部逻辑（含四级代理链路、429 换链路等）。
/// 国内直连不可达，需依赖反代/代理；默认仍保留但菜单中标注不可达。
/// </summary>
internal sealed class WallhavenSource : IWallpaperSource
{
    private readonly WallhavenClient _client;

    public WallhavenSource(WallhavenClient client) => _client = client;

    public string Key => "wallhaven";
    public string DisplayName => "Wallhaven";
    public bool NeedsApiKey => false;
    public bool IsReady(AppConfig cfg) => true;

    public string StatusText(AppConfig cfg)
    {
        var hasMirror = (cfg.CfProxyUrls?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(cfg.CfProxyUrl);
        return hasMirror ? "需反代/代理" : "国内不可达";
    }

    /// <summary>wallhaven 就是通用频道体系本身 → 直接给全部 23 个频道（id 即频道 key）。</summary>
    public IReadOnlyList<(string Id, string Name)> SupportedChannels(AppConfig cfg)
        => Channels.All.Select(c => (c.Key, c.Name)).ToList();

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        // 锁定单源浏览时，树上是 wallhaven 自己的频道（id = 频道 key）→ 就地解析出查询语法
        var category = req.WhCategory;
        var query = req.WhQuery;
        if (!string.IsNullOrEmpty(req.LocalChannelId))
        {
            var ch = Channels.Find(req.LocalChannelId);
            if (ch != null)
            {
                category = ch.Category;
                query = ch.Query;
            }
        }

        var list = await _client.SearchAsync(
            category, req.Purity, req.Sorting, query,
            cfg.Resolution, req.Page, req.Seed, ct);

        if (list == null) return null;
        // WallhavenClient 返回的对象没有 SourceKey（默认即 wallhaven），直接返回即可
        return list;
    }
}
