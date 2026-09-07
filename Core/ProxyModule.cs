using System.Text.Json;

namespace PonyoWallpaper;

/// <summary>
/// 代理独立模块（无 UI 依赖，可被其他程序直接复用/集成）：
/// - 规则层：需代理的网址（域名后缀匹配），默认 wallhaven 三域名（主站 + 缩略图/原图子域），可配置
/// - 链路层：直连 → 反代 → 用户代理 → 公共池（运行状态在 WallhavenClient，配置来源 AppConfig）
/// - 集成入口（其他程序只需引用本文件 + AppConfig 的代理字段即可）：
///     ProxyModule.NeedProxy(url, cfg)   —— 判断某 URL 是否命中代理规则（域名后缀匹配）
///     ProxyModule.Route(url, cfg)       —— 路由决策（是否走代理链路 + 说明）
///     ProxyModule.Snapshot(cfg)         —— 导出当前代理配置快照（JSON），供外部程序读取/同步
/// 本程序内所有请求默认都来自 wallhaven（全部命中规则）；规则表的意义在于：
/// 将来接入其他站点流量时，按域名决定是否套用代理链路，而链路本身零改动。
/// </summary>
internal static class ProxyModule
{
    /// <summary>默认需代理的站点：wallhaven 主站 + 缩略图子域 + 原图子域。</summary>
    public static readonly string[] DefaultProxiedHosts =
    {
        "wallhaven.cc",
        "th.wallhaven.cc",
        "w.wallhaven.cc",
    };

    /// <summary>当前规则列表：未配置或为空时回落默认站点。</summary>
    public static List<string> Hosts(AppConfig cfg)
        => cfg.ProxiedHosts is { Count: > 0 }
            ? cfg.ProxiedHosts
            : DefaultProxiedHosts.ToList();

    /// <summary>URL 是否命中需代理规则（域名后缀匹配，忽略大小写与端口）。</summary>
    public static bool NeedProxy(string url, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var u))
            return false;

        var host = u.Host.ToLowerInvariant();
        foreach (var raw in Hosts(cfg))
        {
            var rule = raw.Trim().ToLowerInvariant();
            if (rule.Length == 0) continue;
            if (host == rule ||
                host.EndsWith("." + rule, StringComparison.Ordinal) ||
                host.EndsWith(rule, StringComparison.Ordinal)) // 允许直接写二级域（如 wallhaven.cc 匹配 www.wallhaven.cc 由前两条覆盖，此处兜底）
                return true;
        }
        return false;
    }

    /// <summary>路由决策：返回（是否走代理链路, 说明文字）。</summary>
    public static (bool Need, string Reason) Route(string url, AppConfig cfg)
        => NeedProxy(url, cfg)
            ? (true, "命中代理规则 → 链路（直连/反代/用户代理/公共池）")
            : (false, "未命中代理规则 → 直连");

    /// <summary>当前代理配置快照（JSON）：规则 + 反代 + 用户池 + 公共池，供其他程序集成时读取。</summary>
    public static string Snapshot(AppConfig cfg)
    {
        var snap = new
        {
            chain = new[] { "direct", "mirror", "user-proxy", "public-pool" },
            proxiedHosts = Hosts(cfg),
            mirrors = cfg.CfProxyUrls ?? new List<string>(),
            userProxies = cfg.ProxyUrls ?? new List<string>(),
            publicPool = cfg.PublicProxyUrls ?? new List<string>(),
            publicPoolUpdatedAt = cfg.PublicProxyUpdatedAt,
        };
        return JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true });
    }
}
