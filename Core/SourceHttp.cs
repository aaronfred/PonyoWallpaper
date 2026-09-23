using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;

namespace PonyoWallpaper;

/// <summary>
/// 源通用取数器（v1.5.2 起取代原来的「直连静态 HttpClient」）。
///
/// 链路（逐条快速失败、自动降级，任何一跳通了就返回）：
///   直连 × 各镜像候选（<see cref="CdnMirror"/> 展开）→ 手填代理 × 各镜像候选
///
/// 关键设计：
///  • <b>每次尝试都有独立硬超时</b>（文本 8s / 图片 20s，CTS.CancelAfter 精确控制）
///    —— v1.5.1 的问题正是「HttpClient 超时对挂起的连接不生效」，30~60s 挂着拖垮整页；
///  • 连接层 <c>ConnectTimeout = 5s</c>，DNS/TCP/TLS 卡住也能立刻换下一条链路；
///  • 成功走非直连链路时记一次 Info（可观测「现在用的是哪条链路」），直连成功不记（防日志刷屏）；
///  • 手填代理（设置页代理框 / 代理池）对<b>所有源</b>生效，不再只服务 wallhaven。
/// </summary>
internal static class SourceHttp
{
    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>文本类请求（HTML / JSON）单次尝试超时。</summary>
    private static readonly TimeSpan TextTry = TimeSpan.FromSeconds(8);

    /// <summary>二进制类请求（图片，最大几 MB）单次尝试超时。</summary>
    private static readonly TimeSpan BinTry = TimeSpan.FromSeconds(20);

    /// <summary>最多尝试几条手填代理（避免 N 代理 × M 候选 的组合爆炸）。</summary>
    private const int MaxProxiesTried = 2;

    private static readonly HttpClient Direct = NewClient(null);
    private static readonly ConcurrentDictionary<string, HttpClient> ProxyPool = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);

    /// <summary>取文本（列表页 / JSON）。全部链路失败返回 null。</summary>
    public static async Task<string?> GetStringAsync(string url, AppConfig? cfg, CancellationToken ct = default)
    {
        var candidates = CdnMirror.Candidates(url);
        foreach (var (client, tier) in Tiers(cfg))
            for (var i = 0; i < candidates.Count; i++)
            {
                var sw = Stopwatch.StartNew();
                var text = await TryAsync(client, candidates[i], TextTry,
                    static (c, t) => c.ReadAsStringAsync(t), ct);
                if (!string.IsNullOrEmpty(text))
                {
                    Note(tier, candidates[i], i, sw.ElapsedMilliseconds);
                    return text;
                }
            }

        Logger.Warn($"source http failed: {Host(url)} ({candidates.Count} 条链路全败)");
        return null;
    }

    /// <summary>取字节（图片）。全部链路失败返回 null。</summary>
    public static async Task<byte[]?> GetBytesAsync(string url, AppConfig? cfg, CancellationToken ct = default)
    {
        var candidates = CdnMirror.Candidates(url);
        foreach (var (client, tier) in Tiers(cfg))
            for (var i = 0; i < candidates.Count; i++)
            {
                var sw = Stopwatch.StartNew();
                var data = await TryAsync(client, candidates[i], BinTry,
                    static (c, t) => c.ReadAsByteArrayAsync(t), ct);
                if (data is { Length: > 0 })
                {
                    Note(tier, candidates[i], i, sw.ElapsedMilliseconds);
                    return data;
                }
            }

        Logger.Warn($"source bytes failed: {Host(url)} ({candidates.Count} 条链路全败)");
        return null;
    }

    /// <summary>下载到文件（原图缓存 / 设为壁纸用）。成功返回 true。</summary>
    public static async Task<bool> DownloadToAsync(string url, string destPath, AppConfig? cfg,
        CancellationToken ct = default)
    {
        var candidates = CdnMirror.Candidates(url);
        foreach (var (client, tier) in Tiers(cfg))
            for (var i = 0; i < candidates.Count; i++)
            {
                var sw = Stopwatch.StartNew();
                if (await TryDownloadAsync(client, candidates[i], destPath, BinTry, ct))
                {
                    Note(tier, candidates[i], i, sw.ElapsedMilliseconds);
                    return true;
                }
            }

        Logger.Warn($"source download failed: {Host(url)}");
        return false;
    }

    // —————————————————————————— 内部 ——————————————————————————

    /// <summary>链路枚举：直连优先，其后最多 N 条手填代理。</summary>
    private static IEnumerable<(HttpClient Client, string Tier)> Tiers(AppConfig? cfg)
    {
        yield return (Direct, "直连");

        var tried = 0;
        foreach (var proxy in Proxies(cfg))
        {
            yield return (ClientFor(proxy), "代理");
            if (++tried >= MaxProxiesTried) break;
        }
    }

    /// <summary>手填代理列表：设置页代理框（ManualProxy）优先，其次代理池（ProxyUrls）。</summary>
    private static List<string> Proxies(AppConfig? cfg)
    {
        var list = new List<string>();
        void Add(string? s)
        {
            var v = s?.Trim();
            if (!string.IsNullOrEmpty(v) && !list.Contains(v!, StringComparer.OrdinalIgnoreCase))
                list.Add(v!);
        }

        Add(cfg?.ManualProxy);
        if (cfg?.ProxyUrls != null)
            foreach (var p in cfg.ProxyUrls) Add(p);
        return list;
    }

    private static HttpClient ClientFor(string proxy)
        => ProxyPool.GetOrAdd(proxy, p =>
        {
            HttpMessageHandler? handler = null;
            try { handler = ProxyFactory.Create(p, null, null); }
            catch (Exception ex) { Logger.Warn($"source proxy invalid, direct fallback: {ex.Message}"); }
            return NewClient(handler);
        });

    private static HttpClient NewClient(HttpMessageHandler? handler)
    {
        var h = handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),           // DNS/TCP/TLS 阶段就快速失败
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 6,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };  // 超时一律由 CTS 精确控制
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return c;
    }

    private static async Task<T?> TryAsync<T>(HttpClient client, string url, TimeSpan perTry,
        Func<HttpContent, CancellationToken, Task<T>> read, CancellationToken ct) where T : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(perTry);
        try
        {
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return await read(resp.Content, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;   // 本条链路超时 → 交给下一条
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TryDownloadAsync(HttpClient client, string url, string destPath,
        TimeSpan perTry, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(perTry);
        var tmp = destPath + ".part";
        try
        {
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return false;

            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                await resp.Content.CopyToAsync(fs, cts.Token);

            File.Move(tmp, destPath, overwrite: true);   // 原子落盘：失败不会留下半个文件
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
            return false;
        }
    }

    /// <summary>
    /// 链路可观测：只在「非直连链路」或「发生了镜像降级（不是第 1 条候选）」时记一次，
    /// 每个「链路 × 站点」只记一次，避免刷屏。
    /// </summary>
    private static void Note(string tier, string url, int index, long ms)
    {
        if (tier == "直连" && index == 0) return;   // 常态（直连首选候选成功）不记
        if (!Reported.TryAdd(tier + "|" + Host(url), 0)) return;
        var via = tier == "直连" ? $"镜像 #{index + 1}" : tier;
        Logger.Info($"source http: {Host(url)} via {via} {ms}ms");
    }

    private static string Host(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}
