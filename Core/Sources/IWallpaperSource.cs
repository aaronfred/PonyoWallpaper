namespace PonyoWallpaper;

/// <summary>多源取图请求。新源不提供关键词检索，只按各自「默认/精选流」翻页。</summary>
internal sealed class SourceFetchRequest
{
    public int Page { get; init; } = 1;
    public int PerPage { get; init; } = 24;

    /// <summary>随机种子：换一批时变化，让支持随机的源给出不同结果。</summary>
    public string? Seed { get; init; }

    // 以下是 wallhaven 专用（频道树只对它生效）；其他源忽略这些字段。
    public string WhCategory { get; init; } = "100";
    public string WhQuery { get; init; } = "";
    public string Purity { get; init; } = "100";
    public string Sorting { get; init; } = "random";
}

/// <summary>
/// 壁纸源统一接口。
/// 设计要点：各站分类体系不同（wallhaven 有 23 频道，360 有 18 类，Unsplash/Pexels 无分类，
/// Bing 连查询都没有），因此<b>不做分类对齐</b>——每个源只按自己的默认/精选流翻页，
/// 首页的「壁纸源」多选决定参与哪些源，频道树继续只对 wallhaven 生效。
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
