namespace PonyoWallpaper;

/// <summary>
/// 轻量滚动日志。每天一个文件，UTF-8，FileShare.Read 允许多读。
/// 无外部依赖（Serilog 会在常驻进程增加 ~10MB 内存）。
/// </summary>
internal static class Logger
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static string? _currentDate;

    public static void Init()
    {
        AppPaths.EnsureAll();
        CleanupOldLogs(days: 30);
        OpenForToday();
    }

    /// <summary>清理过期日志（保留最近 N 天），防止常驻进程日志无限增长占磁盘。</summary>
    private static void CleanupOldLogs(int days)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var f in Directory.GetFiles(AppPaths.LogDir, "app-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* 清理失败不影响主流程 */ }
    }

    private static void OpenForToday()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        var path = Path.Combine(AppPaths.LogDir, $"app-{today}.log");
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
            // FileShare.ReadWrite：允许 test-rotate 等多进程同时写同一天日志
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = false
            };
            _currentDate = today;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"logger init failed: {ex.Message}");
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) =>
        Write("ERROR", ex is null ? msg : $"{msg}\n{ex}");

    private static void Write(string level, string msg)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        if (today != _currentDate) OpenForToday();

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        lock (_lock)
        {
            try { _writer?.WriteLine(line); _writer?.Flush(); } catch { }
            Console.WriteLine(line);
        }
    }

    public static void Close()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}