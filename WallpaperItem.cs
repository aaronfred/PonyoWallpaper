namespace PonyoWallpaper;

/// <summary>
/// 壁纸元数据。仅记录必要字段，避免把整个 API 响应塞进历史。
/// Path 是图片直链（各源域名不同，wallhaven 为 w.wallhaven.cc/full/xx/wallhaven-xxxxx.{ext}）。
/// </summary>
internal sealed class WallpaperItem
{
    /// <summary>来源标识（"wallhaven" / "bing" / "unsplash" …）。不同源 id 会撞，故与 Id 组成复合主键。</summary>
    public string SourceKey { get; init; } = "wallhaven";

    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public string Thumb { get; init; } = "";
    public string Resolution { get; init; } = "";
    public string Category { get; init; } = "";
    public string Purity { get; init; } = "sfw";
    public long FileSize { get; init; }
    public string PageUrl { get; init; } = "";

    /// <summary>卡片角标用的来源缩写。</summary>
    public string SourceLabel =>
        SourceKey switch
        {
            "wallhaven" => "WH",
            "bing"      => "Bing",
            "unsplash"  => "UN",
            "pexels"    => "PX",
            "pixabay"   => "PB",
            "qh360"     => "360",
            "picsum"    => "LP",
            _           => SourceKey
        };

    /// <summary>复合主键（含冒号，仅用于内部比较/日志，不要做文件名）。</summary>
    public string CompositeId => $"{SourceKey}:{Id}";

    /// <summary>
    /// 落盘键：收藏记录与缓存文件名都用它，避免跨源 id 冲突。
    /// wallhaven 保持裸 Id（与旧版本兼容，已有缓存不会失效）；其余源加源前缀。
    /// 不含冒号，可安全用作 Windows 文件名。
    /// </summary>
    public string StoreId => SourceKey == "wallhaven" ? Id : $"{SourceKey}_{Id}";
}
