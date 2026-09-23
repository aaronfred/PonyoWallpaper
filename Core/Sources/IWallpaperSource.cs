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
    /// 一级分类（树上的「风景 / 摄影 / 人物 / 动漫」）对应的<b>全部子频道 key</b>；点二级频道时为空。
    ///
    /// 为什么需要它：一级分类没有具体 tag（wallhaven 靠 category 码就够了），但其它源需要「检索线索」，
    /// 否则 360 会退化成按页轮换自家分类（点"风景"出来"萌宠动物"）、GitHub 关键词落空后退回全库
    /// —— 这正是「点分类出来很乱」的主因。各源用它把"一个组"翻译成自己的分类/检索词，按页轮换取其中一个。
    /// </summary>
    public IReadOnlyList<string>? GroupKeys { get; init; }

    /// <summary>
    /// 源<b>自己</b>的分类 id（v1.5.5）。锁定单个源浏览时，左侧树显示的是该源自己的分类
    /// （360 的 cid、WallpaperCave 的检索词、GitHub 的目录关键词），点选后用它直接定位，
    /// 不再走通用频道映射。
    /// </summary>
    public string LocalChannelId { get; init; } = "";

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

    /// <summary>
    /// 本源<b>自己的分类清单</b>（v1.5.5）：锁定该源浏览时，左侧分类树就显示这些项。
    /// 返回 (id, 显示名) —— id 会原样进 <see cref="SourceFetchRequest.LocalChannelId"/>，
    /// 由各源自己解释（360 是 cid、WallpaperCave 是检索词、GitHub 是目录关键词）。
    /// 返回空列表表示该源没有独立分类体系（此时沿用 wallhaven 的通用 23 频道）。
    /// </summary>
    IReadOnlyList<(string Id, string Name)> SupportedChannels(AppConfig cfg);

    /// <summary>取一页。返回 null = 该源本次失败（调用方跳过，不影响其他源）；空列表 = 没有更多。</summary>
    Task<IReadOnlyList<WallpaperItem>?> FetchAsync(AppConfig cfg, SourceFetchRequest req, CancellationToken ct);
}
