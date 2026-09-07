using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PonyoWallpaper;

/// <summary>
/// 桌面壁纸设置。
/// 优先走 IDesktopWallpaper COM（多显示器 + 精确填充模式，Win8+），
/// 失败时回退到 SystemParametersInfoW + 注册表（兼容路径）。
/// </summary>
internal static class WallpaperSetter
{
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, string? file, uint flags);

    private static readonly Dictionary<string, (string style, string tile)> _modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fill"] = ("10", "0"),
        ["fit"] = ("6", "0"),
        ["stretch"] = ("2", "0"),
        ["tile"] = ("0", "1"),
        ["center"] = ("0", "0"),
    };

    /// <summary>设置壁纸到所有显示器（同图）。优先 COM，降级 SPI。</summary>
    public static bool Set(string imagePath, string fillMode = "fill")
    {
        if (!File.Exists(imagePath))
        {
            Logger.Error($"set wallpaper: file not found {imagePath}");
            return false;
        }
        if (MultiMonitorWallpaper.SetAll(imagePath, fillMode))
            return true;
        return SetViaSpi(imagePath, fillMode);
    }

    /// <summary>每个显示器设置独立壁纸。</summary>
    public static bool SetIndependent(Dictionary<string, string> monitorMap, string fillMode = "fill")
    {
        if (MultiMonitorWallpaper.SetIndependent(monitorMap, fillMode))
            return true;
        // 回退：取第一张应用到所有显示器
        var first = monitorMap.Values.FirstOrDefault();
        return first != null && Set(first, fillMode);
    }

    private static bool SetViaSpi(string imagePath, string fillMode)
    {
        try
        {
            if (!_modes.TryGetValue(fillMode, out var mode)) mode = _modes["fill"];
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true);
            if (key != null)
            {
                key.SetValue("WallpaperStyle", mode.style, RegistryValueKind.String);
                key.SetValue("TileWallpaper", mode.tile, RegistryValueKind.String);
            }
            var ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            if (!ok)
                Logger.Error($"SystemParametersInfo failed err={Marshal.GetLastWin32Error()}");
            return ok;
        }
        catch (Exception ex)
        {
            Logger.Error("set wallpaper (SPI) failed", ex);
            return false;
        }
    }
}