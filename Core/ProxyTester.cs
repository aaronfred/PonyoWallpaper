using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Net.Sockets;

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
    /// <summary>兼容原签名：只返回候选列表。</summary>
    public static async Task<List<string>> FetchSourcesAsync(IEnumerable<string> sources, Action<string>? log = null)
        => (await FetchSourcesWithReportAsync(sources, log)).Candidates;

    /// <summary>
    /// 抓取代理源，并回报每个源的结果（是否成功、新增条数），供上层做失败记账与剔除（v1.2.0 需求 5）。
    /// </summary>
    public static async Task<(List<string> Candidates, List<(string Url, bool Ok, int Added)> Results)>
        FetchSourcesWithReportAsync(IEnumerable<string> sources, Action<string>? log = null)
    {
        var set = new HashSet<string>();
        var report = new List<(string Url, bool Ok, int Added)>();
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
                    if (proto == "https") set.Add($"https://{l}");
                    else if (proto == "socks5") set.Add($"socks5://{l}");
                    else set.Add($"http://{l}");
                }
                var added = set.Count - before;
                report.Add((url, true, added));
                log?.Invoke($"源[{proto}] 新增 {added} 条");
            }
            catch (Exception ex)
            {
                report.Add((url, false, 0));
                log?.Invoke($"源失败[{proto}] {url}: {ex.Message}");
            }
        }
        return (set.OrderBy(_ => Random.Shared.Next()).ToList(), report);
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

    /// <summary>
    /// TCP 连通性快筛：仅建立 Socket 连接，不建 TLS、不发 HTTP。
    /// 免费代理绝大多数是死端口，这一步可用极低成本淘汰 90% 以上候选
    /// （单条约 0.3KB，对比完整 HTTPS 约 20KB，见方案 2.5.1）。
    /// </summary>
    public static async Task<List<string>> ProbeTcpAsync(
        IEnumerable<string> urls, int maxConcurrency, int timeoutMs, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var results = new ConcurrentBag<string>();
        var list = urls.Distinct().ToList();
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var tasks = list.Select(async url =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (!TryParseHostPort(url, out var host, out var port)) return;
                    using var client = new TcpClient();
                    var connect = client.ConnectAsync(host, port);
                    var timeout = Task.Delay(timeoutMs, ct);
                    var finished = await Task.WhenAny(connect, timeout);
                    if (finished == timeout) return;   // 超时 = 不可达
                    await connect;                      // 抛出真实异常同样视为不可达
                    if (client.Connected) results.Add(url);
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch { /* 单条失败不影响整体 */ }
        }).ToList();
        await Task.WhenAll(tasks);
        var r = results.ToList();
        log?.Invoke($"TCP 快筛：{list.Count} → 存活 {r.Count}");
        return r;
    }

    /// <summary>从代理 URL 解析 host/port（支持 scheme://、user:pass@ 前缀）。</summary>
    private static bool TryParseHostPort(string url, out string host, out int port)
    {
        host = ""; port = 0;
        try
        {
            var s = url.Trim();
            var i = s.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) s = s[(i + 3)..];
            var at = s.LastIndexOf('@');
            if (at >= 0) s = s[(at + 1)..];
            var colon = s.LastIndexOf(':');
            if (colon < 0) return false;
            host = s[..colon];
            if (!int.TryParse(s[(colon + 1)..], out port)) return false;
            return host.Length > 0 && port > 0;
        }
        catch { return false; }
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
                    using var resp = await c.GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
                        using var resp = await c.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
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

        // —— 扫描模式（v1.2.0 需求 5 / 方案 2.5.2）——
        // off=不扫描；quiet=默认（16 并发、抽样 50，企业网络友好）；normal=64；aggressive=128 全量
        var mode = (cfg.ProxyScanMode ?? "quiet").ToLowerInvariant();
        var (tcpConc, httpConc, sample, verifyCap) = mode switch
        {
            "aggressive" => (128, 96, 0, 120),   // 0 = 不抽样
            "normal" => (64, 64, 200, 80),
            _ => (16, 16, 50, 30),               // quiet（默认）
        };
        if (mode == "off" && !force)
        {
            log?.Invoke("代理扫描模式为 off，跳过（如需启用请到设置里改为 quiet/normal）");
            return cfg.PublicProxyUrls ?? new List<string>();
        }
        log?.Invoke($"扫描模式：{mode}（TCP {tcpConc} 并发 / 抽样 {sample}）");

        var sources = cfg.ProxySourceUrls is { Count: > 0 } ? cfg.ProxySourceUrls : DefaultSourceUrls.ToList();
        log?.Invoke("抓取公共代理源…");
        var (candidates0, srcReport) = await FetchSourcesWithReportAsync(sources, log);

        // 源失败记账：成功清零、连续失败 3 次剔除（v1.2.0 决策 6）
        var fails = cfg.ProxySourceFails ?? new Dictionary<string, int>();
        foreach (var (url, ok, _) in srcReport)
            fails[url] = ok ? 0 : (fails.TryGetValue(url, out var n) ? n : 0) + 1;
        var dropped = fails.Where(kv => kv.Value >= 3).Select(kv => kv.Key).ToList();
        if (dropped.Count > 0)
        {
            var keptSources = sources.Where(s => !dropped.Contains(SourceUrl(s))).ToList();
            if (keptSources.Count > 0) cfg.ProxySourceUrls = keptSources;
            foreach (var d in dropped)
            {
                fails.Remove(d);
                log?.Invoke($"剔除失效源（连续失败 3 次）：{d}");
            }
        }
        cfg.ProxySourceFails = fails;

        if (candidates0.Count == 0)
        {
            log?.Invoke("所有源均未取得代理");
            cfg.Save();
            return cfg.PublicProxyUrls ?? new List<string>();
        }

        // 抽样（quiet/normal 控制连接基数，降低企业网络观感）
        var candidates = sample > 0 ? candidates0.Take(sample).ToList() : candidates0;
        // 历史优先：上次池内成功的代理置顶先测，复用率显著高于随机候选
        var known = cfg.PublicProxyUrls ?? new List<string>();
        candidates = candidates.OrderBy(c => known.Contains(c) ? 0 : 1).ToList();
        if (sample > 0 && candidates0.Count > sample)
            log?.Invoke($"候选 {candidates0.Count} 条，抽样 {candidates.Count} 条");

        // ① TCP 连通快筛：仅建 Socket 连接（不建 TLS、不发 HTTP），低成本淘汰死端口
        var alive = await ProbeTcpAsync(candidates, tcpConc, 800, log);
        if (alive.Count == 0)
        {
            log?.Invoke("TCP 快筛后无存活代理，稍后可重试或更换源地址");
            cfg.Save();
            return cfg.PublicProxyUrls ?? new List<string>();
        }

        // ② HTTP 验证：只取响应头（不下载 body），按模式限制验证条数
        var verify = alive.Take(verifyCap).ToList();
        var survivors = await ProbeAsync(verify, httpConc, 8, log);
        if (survivors.Count == 0)
        {
            log?.Invoke("HTTP 验证后无可用代理");
            cfg.Save();
            return cfg.PublicProxyUrls ?? new List<string>();
        }
        log?.Invoke($"存活 {survivors.Count} 个，延迟复验（12 并发 × 12s）…");

        // ③ 精确复测存活者（低并发长超时），得到可比较的延迟
        var best = await ProbeAsync(survivors.Take(40).Select(b => b.Url), 12, 12, log);
        if (best.Count == 0)
        {
            // 复验全挂说明结果已过期（免费代理秒级死亡），回退用上一步结果
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
