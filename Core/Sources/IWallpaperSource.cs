namespace PonyoWallpaper;

/// <summary>多源取图请求。v1.4.1：Keywords 让频道对支持检索的源也生效。</summary>
internal sealed class SourceFetchRequest
{
    public int Page { get; init; } = 1;
    public int PerPage { get; init; } = 24;

    /// <summary>随机种子：换一批时变化，让支持随机的源给出不同结果。</summary>
    public string? Seed { get; init; }

    // wallhaven 专用（+tag 语法）。
    public string WhCategory { get; init; } = "100";
    public string WhQuery { get; init; } = "";
    public string Purity { get; init; } = "100";
    public string Sorting { get; init; } = "random";

    /// <summary>当前频道 key（如 nature_landscape）。360 用它映射到自家分类。</summary>
    public string ChannelKey { get; init; } = "";

    /// <summary>
    /// 通用关键词（频道 Query 去掉 + 号，如 "landscape nature"）。
    /// v1.5.0 现役三个源都支持检索：wallhaven 用 +tag 语法（WhQuery），
    /// WallpaperCave 用它检索专辑，360 按 ChannelKey 映射到自家分类。
    /// </summary>
    public string Keywords { get; init; } = "";
}

/// <summary>
/// 壁纸源统一接口。
/// 设计要点：各站分类体系不同（wallhaven 23 频道 / 360 18 类 / WallpaperCave 专辑检索），
/// 因此不硬性对齐分类 ID——wallhaven 用 +tag 语法，WallpaperCave 用关键词检索专辑，
/// 360 按频道映射到自家分类。
/// v1.5.0：仅保留提供电脑横屏壁纸的源；竖屏/手机壁纸源不接入。
/// </summary>
internal interface IWallpaperSource
{
    /// <summary>唯一标识，用于配置与卡片角标。</summary>
    string Key { get; }

    /// <summary>菜单显示名。</summary>
    string DisplayName { get; }

    /// <summary>是否需要 API Key。</summary>
    bool NeedsApiKey { get; }

    /// <summary>当前配置下是否可用（免注册源恒为 true，需 Key 的源看 Key 是否已填）。</summary>
    bool IsReady(AppConfig cfg);

    /// <summary>菜单右侧的状态提示（如「免注册」「未配置 Key」）。</summary>
    string StatusText(AppConfig cfg);

    /// <summary>取一页。返回 null = 该源本次失败（调用方跳过，不影响其他源）；空列表 = 没有更多。</summary>
    Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct);
}

/// <summary>各源共用的轻量 HTTP 客户端（静态复用，避免每源一个 HttpClient 造成的端口与句柄压力）。</summary>
internal static class SourceHttp
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return h;
    }

    public static HttpClient Get() => Client;
}
