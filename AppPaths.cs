namespace PonyoWallpaper;

/// <summary>
/// 应用运行时路径常量。所有写入 LOCALAPPDATA，避免污染用户目录。
/// </summary>
internal static class AppPaths
{
    /// <summary>自定义缓存根目录（设置中修改后重启生效；空 = 默认位置）。</summary>
    public static string? CustomCacheRoot { get; private set; }

    public static void SetCacheRoot(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            CustomCacheRoot = path;
    }

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PonyoWallpaper");

    public static string CacheDir => CustomCacheRoot ?? Path.Combine(Root, "cache");
    public static string CacheFullDir => Path.Combine(CacheDir, "full");
    public static string CacheThumbDir => Path.Combine(CacheDir, "thumb");
    public static string DataDir => Path.Combine(Root, "data");
    public static string LogDir => Path.Combine(Root, "logs");
    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string HistoryDb => Path.Combine(DataDir, "history.db");
    public static string FavoritesFile => Path.Combine(DataDir, "favorites.json");
    public static string BlacklistFile => Path.Combine(DataDir, "blacklist.json");

    /// <summary>确保所有目录存在。</summary>
    public static void EnsureAll()
    {
        Directory.CreateDirectory(CacheFullDir);
        Directory.CreateDirectory(CacheThumbDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }
}