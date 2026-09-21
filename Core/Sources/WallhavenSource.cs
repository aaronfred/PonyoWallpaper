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

    public async Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct)
    {
        var list = await _client.SearchAsync(
            req.WhCategory, req.Purity, req.Sorting, req.WhQuery,
            cfg.Resolution, req.Page, req.Seed, ct);

        if (list == null) return null;
        // WallhavenClient 返回的对象没有 SourceKey（默认即 wallhaven），直接返回即可
        return list;
    }
}
