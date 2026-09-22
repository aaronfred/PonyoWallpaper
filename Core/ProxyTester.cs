using System.Diagnostics;

namespace PonyoWallpaper;

/// <summary>
/// 代理测速工具（v1.4.2 精简版）。
/// 代理池管理 / 公共代理抓取 / hosts 管理已拆分为独立通用工具 ProxyToolkit
/// （C:\tools\SOFT\ProxyToolkit\）；本程序只保留「手填代理实测」能力：
/// 在设置页填入代理后，实测其能否经代理访问 wallhaven API 及延迟。
/// </summary>
internal static class ProxyTester
{
    private const string ProbeUrl = "https://wallhaven.cc/api/v1/search?q=cat&purity=100&page=1";

    /// <summary>
    /// 并行实测代理对 wallhaven 的可用性与延迟，返回可用代理按延迟升序（完整 url, 毫秒）。
    /// </summary>
    public static async Task<List<(string Url, int Ms)>> ProbeAsync(
        IEnumerable<string> urls, int maxConcurrency, int timeoutSec, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var results = new List<(string Url, int Ms)>();
        var list = urls.Distinct().ToList();
        if (list.Count == 0) return results;

        // 手填代理通常只有 1 条，直接顺序测即可；保留并发框架以兼容多条
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var tasks = list.Select(async url =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var handler = ProxyFactory.Create(url, null, null);
                    if (handler == null) return;
                    using var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
                    c.DefaultRequestHeaders.Add("User-Agent", "PonyoWallpaper/1.0");
                    using var resp = await c.GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    sw.Stop();
                    if (resp.IsSuccessStatusCode)
                    {
                        lock (results) { results.Add((url, (int)sw.ElapsedMilliseconds)); }
                        log?.Invoke($"可用 {url} · {sw.ElapsedMilliseconds}ms");
                    }
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch { /* 单个候选失败不影响整体 */ }
        }).ToList();
        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.Ms).ToList();
    }
}
