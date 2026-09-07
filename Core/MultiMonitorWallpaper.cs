using System.Runtime.InteropServices;

namespace PonyoWallpaper;

/// <summary>
/// 多显示器壁纸支持，基于 IDesktopWallpaper COM（Windows 8+）。
/// 相比 SystemParametersInfoW，可逐屏设置、精确控制填充模式。
/// 不支持时自动回退到 SystemParametersInfoW。
/// </summary>
internal static class MultiMonitorWallpaper
{
    // 填充模式 → IDesktopWallpaper position
    private static int MapPosition(string fillMode) => fillMode switch
    {
        "fit" => 3,       // Fit
        "stretch" => 2,   // Stretch
        "tile" => 1,      // Tile
        "center" => 0,    // Center
        "span" => 5,      // Span（跨屏）
        _ => 4            // Fill（默认）
    };

    private static IDesktopWallpaper? Create()
    {
        try { return (IDesktopWallpaper)new DesktopWallpaperClass(); }
        catch (Exception ex) { Logger.Warn($"IDesktopWallpaper not available: {ex.Message}"); return null; }
    }

    /// <summary>枚举所有显示器 ID。</summary>
    public static List<string> GetMonitorIds()
    {
        var list = new List<string>();
        var dw = Create();
        if (dw == null) return list;
        try
        {
            dw.GetMonitorDevicePathCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                dw.GetMonitorDevicePathAt(i, out string id);
                if (!string.IsNullOrEmpty(id)) list.Add(id);
            }
        }
        catch (Exception ex) { Logger.Warn($"GetMonitorIds: {ex.Message}"); }
        return list;
    }

    /// <summary>给所有显示器设置同一张壁纸，并应用填充模式。</summary>
    public static bool SetAll(string wallpaperPath, string fillMode = "fill")
    {
        var dw = Create();
        if (dw == null) return false;
        try
        {
            dw.GetMonitorDevicePathCount(out uint count);
            if (count == 0) return false;

            // 设置填充模式（失败不致命）
            try { dw.SetPosition(MapPosition(fillMode)); } catch { }

            int ok = 0;
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    dw.GetMonitorDevicePathAt(i, out string id);
                    if (dw.SetWallpaper(id, wallpaperPath) >= 0) ok++;
                }
                catch { /* 单屏失败继续 */ }
            }
            return ok == count;
        }
        catch (Exception ex)
        {
            Logger.Warn($"SetAll: {ex.Message}");
            return false;
        }
    }

    /// <summary>每个显示器设置独立壁纸（按 monitorId → path 映射）。</summary>
    public static bool SetIndependent(Dictionary<string, string> map, string fillMode = "fill")
    {
        var dw = Create();
        if (dw == null) return false;
        try
        {
            try { dw.SetPosition(MapPosition(fillMode)); } catch { }
            int ok = 0;
            foreach (var (monitorId, path) in map)
            {
                try { if (dw.SetWallpaper(monitorId, path) >= 0) ok++; }
                catch { }
            }
            return ok == map.Count;
        }
        catch (Exception ex) { Logger.Warn($"SetIndependent: {ex.Message}"); return false; }
    }
}

[ComImport]
[Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
internal class DesktopWallpaperClass { }

[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    [PreserveSig]
    int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

    [PreserveSig]
    int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);

    [PreserveSig]
    int GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorID);

    [PreserveSig]
    int GetMonitorDevicePathCount(out uint count);

    [PreserveSig]
    int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT rect);

    [PreserveSig]
    int SetBackgroundColor([MarshalAs(UnmanagedType.U4)] uint color);

    [PreserveSig]
    int GetBackgroundColor([MarshalAs(UnmanagedType.U4)] out uint color);

    [PreserveSig]
    int SetPosition([MarshalAs(UnmanagedType.I4)] int position);

    [PreserveSig]
    int GetPosition([MarshalAs(UnmanagedType.I4)] out int position);

    // 以下 slideshow 方法对本应用无用，但 vtable 顺序需完整，用占位签名
    [PreserveSig]
    int SetSlideshow(IntPtr items);

    [PreserveSig]
    int GetSlideshow(IntPtr items);

    [PreserveSig]
    int SetSlideshowOptions(uint options, uint slideshowTick);

    [PreserveSig]
    int GetSlideshowOptions(out uint options, out uint slideshowTick);

    [PreserveSig]
    int AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, uint direction);

    [PreserveSig]
    int GetStatus(out uint status);

    [PreserveSig]
    int Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}