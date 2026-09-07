using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace PonyoWallpaper;

/// <summary>
/// 代理池工具：从源地址抓取公共免费代理（http / https / socks5 不限协议），
/// 并行竞速实测对 wallhaven 的可用性与延迟，输出按延迟排序的最优集合。
/// 判定标准：能经该代理访问 wallhaven API（HTTP 200）。
/// 层级：用户代理（识别成功后最优先）→ 公共代理池（自动探测）→ 直连。
/// </summary>
internal static partial class ProxyTester
{
    /// <summary>内置代理池源地址。行格式：&lt;协议&gt;|&lt;列表URL&gt;（无协议前缀时按 URL 关键词推断）。
    /// 覆盖全网维护较活跃的免费代理列表仓库；个别源失效只影响候选数量，不影响流程。</summary>
    public static readonly string[] DefaultSourceUrls =
    {
        "socks5|https://ghproxy.net/https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt",
        "socks5|https://ghproxy.net/https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/socks5.txt",
        "http|https://ghproxy.net/https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt",
        "http|https://ghproxy.net/https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt",
        "https|https://ghproxy.net/https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/protocols/https/data.txt",
        "http|https://ghproxy.net/https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/protocols/http/data.txt",
        "socks5|https://ghproxy.net/https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/protocols/socks5/data.txt",
        "http|https://ghproxy.net/https://raw.githubusercontent.com/jetkai/proxy-list/main/regular-updates/proxy-list/http.txt",
        "socks5|https://ghproxy.net/https://raw.githubusercontent.com/jetkai/proxy-list/main/regular-updates/proxy-list/socks5.txt",
        "socks5|https://ghproxy.net/https://raw.githubusercontent.com/roosterkid/openproxylist/main/SOCKS5_RAW.txt",
        "https|https://ghproxy.net/https://raw.githubusercontent.com/roosterkid/openproxylist/main/HTTPS_RAW.txt",
        "http|https://ghproxy.net/https://raw.githubusercontent.com/sunny9577/proxy-scraper/master/generated/http_proxies.txt",
        "socks5|https://ghproxy.net/https://raw.githubusercontent.com/sunny9577/proxy-scraper/master/generated/socks_proxies.txt",
        "http|https://ghproxy.net/https://raw.githubusercontent.com/mmpx12/proxy-list/master/http.txt",
        "socks5|https://ghproxy.net/https://raw.githubusercontent.com/mmpx12/proxy-list/master/socks5.txt",
        "http|https://ghproxy.net/https://raw.githubusercontent.com/zevtyardt/proxy-list/main/all.txt",
    };

    private const string ProbeUrl = "https://wallhaven.cc/api/v1/search?q=cat&purity=100&page=1";

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}:\d{2,5}$")]
    private static partial Regex HostPortLine();

    /// <summary>推断源列表协议：显式 "协议|URL" 优先，否则按 URL 关键词（socks5/https）推断，默认 http。</summary>
    private static string InferProtocol(string src)
    {
        var bar = src.IndexOf('|');
        if (bar > 0) return src[..bar].Trim().ToLowerInvariant();
        var u = src.ToLowerInvariant();
        if (u.Contains("socks5")) return "socks5";
        if (u.Contains("socks4")) return "http"; // 不支持 socks4，按 http 处理（通常也抓不到）
        if (u.Contains("https")) return "https";
        return "http";
    }

    private static string SourceUrl(string src)
    {
        var bar = src.IndexOf('|');
        return bar > 0 ? src[(bar + 1)..].Trim() : src.Trim();
    }

    /// <summary>抓取全部源地址（http/https/socks5 不限），去重合并出 scheme://host:port 候选，随机打乱。</summary>
    public static async Task<List<string>> FetchSourcesAsync(IEnumerable<string> sources, Action<string>? log = null)
    {
        var set = new HashSet<string>();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        http.DefaultRequestHeaders.Add("User-Agent", "PonyoWallpaper/1.0");
        foreach (var src in sources)
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            var proto = InferProtocol(src);
            var url = SourceUrl(src);
            try
            {
                var txt = await http.GetStringAsync(url);
                var before = set.Count;
                foreach (var line in txt.Split('\n'))
                {
                    var l = line.Trim();
                    if (!HostPortLine().IsMatch(l)) continue;
                    if (proto == "https")
                        set.Add($"https://{l}");
                    else if (proto == "socks5")
                        set.Add($"socks5://{l}");
                    else
                        set.Add($"http://{l}");
                }
                log?.Invoke($"源[{proto}] 新增 {set.Count - before} 条");
            }
            catch (Exception ex)
            {
                log?.Invoke($"源失败[{proto}] {url}: {ex.Message}");
            }
        }
        return set.OrderBy(_ => Random.Shared.Next()).ToList();
    }

    /// <summary>并行竞速实测：返回可用代理按延迟升序（完整 url, 毫秒）。</summary>
    /// <summary>把多行文本拆成非空行列表（供各表单使用，避免每处都写转义）。</summary>
    public static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length > 0) result.Add(l);
        }
        return result;
    }

    public static async Task<List<(string Url, int Ms)>> ProbeAsync(
        IEnumerable<string> urls, int maxConcurrency, int timeoutSec, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<(string Url, int Ms)>();
        var list = urls.Distinct().ToList();
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
                    using var resp = await c.GetAsync(ProbeUrl, ct);
                    sw.Stop();
                    if (resp.IsSuccessStatusCode)
                    {
                        results.Add((url, (int)sw.ElapsedMilliseconds));
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

    /// <summary>链路条目的显示名：直连 / 反代 / 代理地址。</summary>
    private static string EntryLabel(string kind, string url)
        => kind switch
        {
            "direct" => "直连",
            "mirror" => "反代 " + url,
            _ => url
        };

    /// <summary>
    /// 链路条目竞速：把「直连 / 反代 / 代理」三种形态统一实测，返回可用条目按延迟升序（保留原索引）。
    /// 用于「优选代理」——从已有链路中不抓新源，只挑最快且真能访问 wallhaven 的那一级。
    /// </summary>
    public static async Task<List<(int Idx, string Label, int Ms)>> ProbeChainAsync(
        IReadOnlyList<(int Idx, string Kind, string Url, string? User, string? Pass)> entries,
        int timeoutSec = 12, Action<string>? log = null, CancellationToken ct = default)
    {
        var results = new ConcurrentBag<(int Idx, string Label, int Ms)>();
        using var gate = new SemaphoreSlim(6);
        var tasks = entries.Select(async e =>
        {
            var label = EntryLabel(e.Kind, e.Url);
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var target = e.Kind == "mirror"
                        ? e.Url.TrimEnd('/') + "/api/v1/search?q=cat&purity=100&page=1"
                        : ProbeUrl;
                    HttpClient c;
                    if (e.Kind == "proxy")
                    {
                        var h = ProxyFactory.Create(e.Url, e.User, e.Pass);
                        if (h == null) return;
                        c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
                    }
                    else
                    {
                        c = new HttpClient(new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
                    }
                    using (c)
                    {
                        c.DefaultRequestHeaders.Add("User-Agent", "PonyoWallpaper/1.0");
                        var sw = Stopwatch.StartNew();
                        using var resp = await c.GetAsync(target, ct);
                        sw.Stop();
                        if (resp.IsSuccessStatusCode)
                        {
                            results.Add((e.Idx, label, (int)sw.ElapsedMilliseconds));
                            log?.Invoke($"可用 {label} · {sw.ElapsedMilliseconds}ms");
                        }
                    }
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch { /* 单个条目失败不影响整体 */ }
        }).ToList();
        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.Ms).ToList();
    }

    /// <summary>
    /// 公共代理池自动探测：无用户代理或公共池过期（12 小时）时，抓源实测并更新。
    /// force = 手动触发（代理管理页的更新按钮）。全程后台，不阻塞界面。
    /// </summary>
    public static async Task<List<string>> EnsurePublicPoolAsync(
        AppConfig cfg, WallhavenClient api, bool force = false, Action<string>? log = null)
    {
        // 新鲜判定：6 小时内且池内至少 2 个（按用户要求减少后台动作；免费代理寿命以小时计）
        var fresh = cfg.PublicProxyUpdatedAt.HasValue
            && DateTime.Now - cfg.PublicProxyUpdatedAt.Value < TimeSpan.FromHours(6)
            && cfg.PublicProxyUrls is { Count: >= 2 };
        if (!force && fresh)
        {
            api.SetPublicProxies(cfg.PublicProxyUrls!);
            log?.Invoke($"公共代理池仍新鲜（{cfg.PublicProxyUrls!.Count} 个），跳过");
            return cfg.PublicProxyUrls!;
        }

        var sources = cfg.ProxySourceUrls is { Count: > 0 } ? cfg.ProxySourceUrls : DefaultSourceUrls.ToList();
        log?.Invoke("抓取公共代理源…");
        var candidates = await FetchSourcesAsync(sources, log);
        if (candidates.Count == 0)
        {
            log?.Invoke("所有源均未取得代理");
            return cfg.PublicProxyUrls ?? new List<string>();
        }
        // 两轮筛选：
        // 第一轮——全量快筛（96 并发 × 8s）：免费代理绝大多数是死代理，快筛淘汰 95% 以上
        log?.Invoke($"共 {candidates.Count} 条候选，第一轮全量快筛…");
        var survivors = await ProbeAsync(candidates, 96, 8);
        if (survivors.Count == 0)
        {
            log?.Invoke("全量实测后无可用代理，稍后可重试或更换源地址");
            return cfg.PublicProxyUrls ?? new List<string>();
        }
        log?.Invoke($"第一轮存活 {survivors.Count} 个，第二轮复验（12 并发 × 12s，精确延迟）…");
        // 第二轮——存活者复验：低并发长超时，剔除高并发下的误判，得到精确延迟
        var best = await ProbeAsync(survivors.Take(40).Select(b => b.Url), 12, 12, log);
        if (best.Count == 0)
        {
            // 复验全挂说明第一轮结果已过期（免费代理秒级死亡），直接用第一轮结果
            best = survivors;
        }
        var keep = best.Take(5).Select(b => b.Url).ToList();
        cfg.PublicProxyUrls = keep;
        cfg.PublicProxyUpdatedAt = DateTime.Now;
        cfg.Save();
        api.SetPublicProxies(keep);
        var msg = $"公共代理池已更新（最优 {keep.Count} 个）：{string.Join("、", keep)}";
        log?.Invoke(msg);
        Logger.Info(msg);
        return keep;
    }
}
