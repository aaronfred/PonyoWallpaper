namespace PonyoWallpaper;

/// <summary>
/// 全局单实例锁。基于具名 Mutex（每个 Windows 用户独立命名空间）。
/// 二次启动直接退出，提示已在运行。
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private const string Name = "Global\\PonyoWallpaper.SingleInstance.v1";

    public static bool Acquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, Name, out bool createdNew);
            if (createdNew) return true;
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch
        {
            // 极端情况下（如无权限的全局命名空间）降级到本地
            _mutex = new Mutex(initiallyOwned: true, "Local\\PonyoWallpaper.SingleInstance.v1", out bool createdNew);
            return createdNew;
        }
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { /* ignored */ }
        _mutex?.Dispose();
        _mutex = null;
    }
}