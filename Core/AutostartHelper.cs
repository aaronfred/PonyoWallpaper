using Microsoft.Win32;

namespace PonyoWallpaper;

/// <summary>
/// 开机自启：写入/删除 HKCU\...\Run 注册表键。
/// 相比启动文件夹更可控（可带参数、可静默）。
/// </summary>
internal static class AutostartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PonyoWallpaper";

    /// <summary>自启路径：优先当前进程真实路径（发布时 exe 带版本号/lite 后缀），
    /// 单文件自包含场景 ProcessPath 同样可靠；都没有再回落到同目录同名 exe。</summary>
    private static string ExePath
    {
        get
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self) && File.Exists(self)) return self;
            return Path.Combine(AppContext.BaseDirectory, "PonyoWallpaper.exe");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static bool Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return false;
            var exe = ExePath;
            if (!File.Exists(exe))
            {
                // dev 环境：指向 dotnet run 不可靠，改用当前进程路径兜底
                exe = Environment.ProcessPath ?? exe;
            }
            key.SetValue(ValueName, $"\"{exe}\" --minimized", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) { Logger.Error("autostart enable failed", ex); return false; }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) { Logger.Error("autostart disable failed", ex); return false; }
    }
}