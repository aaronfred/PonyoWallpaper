using System.Text.Json;

namespace PonyoWallpaper;

internal sealed class HistoryRecord
{
    public string Id { get; set; } = "";
    public DateTime AppliedAt { get; set; }
    public string Channel { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string Purity { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public long FileSize { get; set; }
}

/// <summary>
/// 更换记录（JSONL，append-only）—— 避免引入 SQLite 包（境外 NuGet 源不可达）。
/// 追加一条 JSON 一行，读取时倒序取最近 N 条。
/// </summary>
internal sealed class HistoryStore
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = false };
    private readonly object _lock = new();
    private const int MaxRecords = 500;

    public void Append(WallpaperItem item, string channelName, string localPath)
    {
        var rec = new HistoryRecord
        {
            Id = item.Id,
            AppliedAt = DateTime.Now,
            Channel = channelName,
            Resolution = item.Resolution,
            Purity = item.Purity,
            PageUrl = item.PageUrl,
            LocalPath = localPath,
            FileSize = item.FileSize
        };
        try
        {
            lock (_lock)
            {
                AppPaths.EnsureAll();
                File.AppendAllText(AppPaths.HistoryDb,
                    JsonSerializer.Serialize(rec, _opts) + Environment.NewLine);
            }
            Trim();
        }
        catch (Exception ex) { Logger.Warn($"history append failed: {ex.Message}"); }
    }

    public List<HistoryRecord> Load(int limit = 200)
    {
        try
        {
            if (!File.Exists(AppPaths.HistoryDb)) return new();
            var lines = File.ReadAllLines(AppPaths.HistoryDb);
            var list = new List<HistoryRecord>();
            for (int i = lines.Length - 1; i >= 0 && list.Count < limit; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { list.Add(JsonSerializer.Deserialize<HistoryRecord>(line)!); }
                catch { /* skip corrupted line */ }
            }
            return list;
        }
        catch (Exception ex) { Logger.Warn($"history load failed: {ex.Message}"); return new(); }
    }

    private void Trim()
    {
        lock (_lock)
        {
            if (!File.Exists(AppPaths.HistoryDb)) return;
            var lines = File.ReadAllLines(AppPaths.HistoryDb);
            if (lines.Length <= MaxRecords) return;
            File.WriteAllLines(AppPaths.HistoryDb, lines.Skip(lines.Length - MaxRecords));
        }
    }
}