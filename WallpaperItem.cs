namespace PonyoWallpaper;

/// <summary>
/// 壁纸元数据。仅记录必要字段，避免把整个 API 响应塞进历史。
/// Path 是 wallhaven 直链：w.wallhaven.cc/full/xx/wallhaven-xxxxx.{ext}
/// </summary>
internal sealed class WallpaperItem
{
    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public string Thumb { get; init; } = "";
    public string Resolution { get; init; } = "";
    public string Category { get; init; } = "";
    public string Purity { get; init; } = "sfw";
    public long FileSize { get; init; }
    public string PageUrl { get; init; } = "";
}