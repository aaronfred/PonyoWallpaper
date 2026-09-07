using System.Reflection;

namespace PonyoWallpaper;

/// <summary>
/// 应用图标加载。优先从嵌入程序集的资源读取（单文件分发友好），
/// 失败时降级到 resources 目录，再降级系统默认图标。
/// </summary>
internal static class IconHelper
{
    public static Icon LoadAppIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ponyo.ico", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream != null) return new Icon(stream);
            }
        }
        catch (Exception ex) { Logger.Warn($"load embedded icon failed: {ex.Message}"); }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "resources", "ponyo.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch { }

        return SystemIcons.Application;
    }
}