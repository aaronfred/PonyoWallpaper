using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace PonyoWallpaper;

/// <summary>
/// hosts 更新器：
/// - 从 oopsunix/hosts 拉取 wallhaven 专用映射
/// - 解析 # wallhaven Hosts Start / End 标记段
/// - 合并到系统 hosts（替换旧段，保留其他内容）
/// - 写 hosts 需要管理员权限，非管理员时由调用方用 runas 提权
/// </summary>
internal static class HostsUpdater
{
    public const string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
    /// <summary>主源（保留常量以兼容旧配置迁移）。</summary>
    public const string DefaultSourceUrl = "https://raw.githubusercontent.com/oopsunix/hosts/main/hosts_wallhaven";
    public const string StartMarker = "# wallhaven Hosts Start";
    public const string EndMarker = "# wallhaven Hosts End";

    /// <summary>单个请求的超时（秒）。源/加速组合较多，超时不宜过长。</summary>
    private const int FetchTimeoutSeconds = 8;

    /// <summary>
    /// 拉取 hosts 源内容。两阶段：
    /// ① 依次直连每个源地址；
    /// ② 全部不通时，依次用加速地址前缀拼接源地址重试（加速在外层，命中即返回）。
    /// 全部失败抛出聚合异常。
    /// </summary>
    public static async Task<FetchResult> FetchAsync(
        IEnumerable<string>? customSources,
        IEnumerable<string>? customAccels,
        CancellationToken ct = default)
    {
        var sources = NormalizeList(customSources, DefaultSourceUrls);
        var accels = NormalizeList(customAccels, DefaultAccelUrls);

        Exception? last = null;

        // 阶段一：源地址直连
        foreach (var s in sources)
        {
            try
            {
                var content = await GetStringAsync(s, ct);
                if (!string.IsNullOrWhiteSpace(content)) return new FetchResult(content, s, false);
                Logger.Warn($"hosts fetch empty (direct): {s}");
            }
            catch (Exception ex)
            {
                last = ex;
                Logger.Warn($"hosts fetch failed (direct {s}): {ex.Message}");
            }
        }

        // 阶段二：源地址不通 → 自动套加速地址
        foreach (var a in accels)
        {
            var prefix = a.TrimEnd('/') + "/";
            foreach (var s in sources)
            {
                var url = prefix + s;
                try
                {
                    var content = await GetStringAsync(url, ct);
                    if (!string.IsNullOrWhiteSpace(content)) return new FetchResult(content, url, true);
                    Logger.Warn($"hosts fetch empty (accel): {url}");
                }
                catch (Exception ex)
                {
                    last = ex;
                    Logger.Warn($"hosts fetch failed (accel {url}): {ex.Message}");
                }
            }
        }

        throw new InvalidOperationException(
            $"所有源地址（{sources.Count} 个）直连与 {accels.Count} 个加速地址组合均拉取失败", last);
    }

    /// <summary>
    /// 内置默认源地址（均每日自动更新，Content 含 Start/End 标记段）。
    /// ① oopsunix 主源：GitHub Actions 每日更新
    /// ② jocay 镜像仓库：独立维护，IP 与主源不同，互为兜底
    /// ③ jsDelivr CDN：独立 CDN 同步同一仓库，国内通常可直连（无需加速）
    /// </summary>
    public static readonly string[] DefaultSourceUrls =
    {
        "https://raw.githubusercontent.com/oopsunix/hosts/main/hosts_wallhaven",
        "https://raw.githubusercontent.com/jocay/hosts/main/hosts_wallhaven",
        "https://cdn.jsdelivr.net/gh/oopsunix/hosts@main/hosts_wallhaven",
    };

    /// <summary>
    /// 内置默认加速地址（前缀，逐个拼到源地址前重试）。
    /// 按实测可用率排序：ghproxy.net 与 gh-proxy.com 在国内实测可达。
    /// </summary>
    public static readonly string[] DefaultAccelUrls =
    {
        "https://ghproxy.net/",
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://mirror.ghproxy.com/",
    };

    /// <summary>拉取结果：内容 + 实际生效的地址（便于界面回显）。</summary>
    public sealed record FetchResult(string Content, string UsedUrl, bool ViaAccel);

    public static bool IsAdmin() =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    /// <summary>去空、去重、去回车；空列表则回落到内置默认值。</summary>
    private static List<string> NormalizeList(IEnumerable<string>? custom, string[] defaults)
    {
        var list = new List<string>();
        if (custom != null)
            foreach (var raw in custom)
            {
                var s = raw.Trim().TrimEnd('\r');
                if (s.Length > 0 && !list.Contains(s)) list.Add(s);
            }
        return list.Count > 0 ? list : defaults.ToList();
    }

    // ——— 源地址优选（测速取最优 N 个）———

    /// <summary>候选源地址 = 当前源 + 内置默认源 + 「加速前缀 × 源」组合，去重保持顺序。</summary>
    public static List<string> BuildCandidates(
        IEnumerable<string>? customSources, IEnumerable<string>? customAccels)
    {
        var sources = NormalizeList(customSources, DefaultSourceUrls);
        var accels = NormalizeList(customAccels, DefaultAccelUrls);
        var list = new List<string>();
        void Add(string u)
        {
            if (!string.IsNullOrWhiteSpace(u) && !list.Contains(u)) list.Add(u);
        }
        foreach (var s in sources) Add(s);
        foreach (var a in accels)
        {
            var prefix = a.TrimEnd('/') + "/";
            foreach (var s in sources) Add(prefix + s);
        }
        return list;
    }

    /// <summary>并发测速候选源地址：能取到非空内容即视为可用，返回按延迟升序。</summary>
    public static async Task<List<(string Url, int Ms)>> ProbeSourcesAsync(
        IEnumerable<string> candidates, int timeoutSec = 8, CancellationToken ct = default)
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<(string Url, int Ms)>();
        using var gate = new SemaphoreSlim(8);
        var tasks = candidates.Distinct().Select(async url =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    var sw = Stopwatch.StartNew();
                    var content = await GetStringAsync(url, ct);
                    sw.Stop();
                    if (!string.IsNullOrWhiteSpace(content))
                        results.Add((url, (int)sw.ElapsedMilliseconds));
                }
                finally { gate.Release(); }
            }
            catch { /* 单个候选失败忽略 */ }
        }).ToList();
        await Task.WhenAll(tasks);
        return results.OrderBy(r => r.Ms).ToList();
    }

    /// <summary>优选源地址：从候选（含加速组合）中实测取最优 keep 个，供界面回填源地址框。</summary>
    public static async Task<List<string>> PickBestSourcesAsync(
        IEnumerable<string>? customSources, IEnumerable<string>? customAccels,
        int keep = 5, Action<string>? log = null, CancellationToken ct = default)
    {
        var candidates = BuildCandidates(customSources, customAccels);
        log?.Invoke($"开始优选：实测 {candidates.Count} 个候选源地址（含加速组合）…");
        var best = await ProbeSourcesAsync(candidates, FetchTimeoutSeconds, ct);
        if (best.Count == 0)
        {
            log?.Invoke("优选失败：所有候选源地址均不可达");
            return new List<string>();
        }
        var urls = best.Take(keep).Select(b => b.Url).ToList();
        log?.Invoke($"优选完成：保留最优 {urls.Count} 个（最快 {best[0].Ms}ms）");
        foreach (var b in best.Take(keep)) log?.Invoke($"  {b.Ms}ms  {b.Url}");
        return urls;
    }

    /// <summary>
    /// 优选 git 加速地址：并发实测各前缀（拼接已知 GitHub raw 地址探测可达性与延迟），
    /// 返回按延迟升序的可用前缀，最多 keep 个（全失败返回空列表）。
    /// </summary>
    public static async Task<List<string>> PickBestAccelsAsync(
        IEnumerable<string>? customAccels, int keep = 3, Action<string>? log = null, CancellationToken ct = default)
    {
        const string probeTarget = "https://raw.githubusercontent.com/oopsunix/hosts/main/hosts_wallhaven";
        var accels = NormalizeList(customAccels, DefaultAccelUrls);
        var map = new Dictionary<string, string>();
        foreach (var a in accels)
        {
            var prefix = a.TrimEnd('/') + "/";
            map[prefix + probeTarget] = prefix;
        }
        var best = await ProbeSourcesAsync(map.Keys, FetchTimeoutSeconds, ct);
        var result = best.Select(b => map[b.Url]).Take(keep).ToList();
        if (result.Count == 0)
            log?.Invoke("优选失败：所有 git 加速地址均不可达");
        else
            foreach (var (url, ms) in best.Take(keep))
                log?.Invoke($"  {ms}ms  {map[url]}");
        return result;
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(FetchTimeoutSeconds) };
        http.DefaultRequestHeaders.Add("User-Agent", "PonyoWallpaper/1.0");
        return await http.GetStringAsync(url, ct);
    }

    /// <summary>提取 Start/End 标记之间的有效内容（不含标记行本身）。</summary>
    public static string ExtractBlock(string content)
    {
        int s = content.IndexOf(StartMarker, StringComparison.Ordinal);
        int e = content.IndexOf(EndMarker, StringComparison.Ordinal);
        if (s < 0 || e < 0 || e <= s) return content.Trim();
        var inner = content.Substring(s + StartMarker.Length, e - s - StartMarker.Length);
        return string.Join("\n", inner.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    /// <summary>读取系统 hosts 中本程序写入的标记段（未写入时返回空串）。</summary>
    public static string ReadBlock()
    {
        try
        {
            var lines = File.ReadAllLines(HostsPath, Encoding.UTF8).ToList();
            int a = lines.FindIndex(l => l.Contains(StartMarker));
            int b = lines.FindIndex(l => l.Contains(EndMarker));
            if (a < 0 || b < 0 || b <= a) return "";
            var inner = lines.Skip(a + 1).Take(b - a - 1)
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l));
            return string.Join(Environment.NewLine, inner);
        }
        catch (Exception ex) { Logger.Warn($"hosts read block failed: {ex.Message}"); return ""; }
    }

    /// <summary>把 wallhaven 段写入系统 hosts。返回是否成功。</summary>
    public static bool Apply(string block)
    {
        if (!IsAdmin())
        {
            Logger.Warn("hosts apply requires admin");
            return false;
        }
        try
        {
            // 备份 hosts。异常环境兼容：hosts.bak 可能已存在为【目录】（实测某机器出现，
            // File.Copy 会抛 Arg_FileIsDirectory_Name）——删除目录冲突后重试
            var backup = HostsPath + ".bak";
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            File.Copy(HostsPath, backup, overwrite: true);

            var lines = File.ReadAllLines(HostsPath, Encoding.UTF8).ToList();
            int start = lines.FindIndex(l => l.Contains(StartMarker));
            int end = lines.FindIndex(l => l.Contains(EndMarker));
            if (start >= 0 && end >= 0 && end >= start)
                lines.RemoveRange(start, end - start + 1);

            lines.Add(StartMarker);
            lines.AddRange(block.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)));
            lines.Add(EndMarker);

            File.WriteAllLines(HostsPath, lines, new UTF8Encoding(false));
            FlushDns();
            Logger.Info("hosts updated successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("hosts apply failed", ex);
            return false;
        }
    }

    /// <summary>移除已应用的 wallhaven 段。</summary>
    public static bool Remove()
    {
        if (!IsAdmin()) return false;
        try
        {
            var lines = File.ReadAllLines(HostsPath, Encoding.UTF8).ToList();
            int start = lines.FindIndex(l => l.Contains(StartMarker));
            int end = lines.FindIndex(l => l.Contains(EndMarker));
            if (start >= 0 && end >= 0 && end >= start)
                lines.RemoveRange(start, end - start + 1);
            File.WriteAllLines(HostsPath, lines, new UTF8Encoding(false));
            FlushDns();
            return true;
        }
        catch (Exception ex) { Logger.Error("hosts remove failed", ex); return false; }
    }

    public static void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            p?.WaitForExit(3000);
        }
        catch { /* ignored */ }
    }
}