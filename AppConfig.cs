using System.Text.Json;

namespace PonyoWallpaper;

/// <summary>
/// 应用配置。默认 JSON 持久化于 %LOCALAPPDATA%\PonyoWallpaper\data\config.json。
/// ApiKey 与 NSFW 分类入口存放在隐藏设置面板（需密码唤出），默认仅 SFW。
/// 旧字段（Category/Purity/NotifyOnChange/HiddenEntry 等）废弃后从 JSON 中删除也不影响反序列化。
/// </summary>
internal class AppConfig
{
    // —— 自动更换（主界面底部设置）——
    public int IntervalMinutes { get; set; } = 60;
    public string Resolution { get; set; } = "2560x1440";
    public string Sorting { get; set; } = "random";
    public string FillMode { get; set; } = "fill";
    public string Monitors { get; set; } = "all";

    /// <summary>自动更换参与的频道（二级分类 key）列表，"nsfw" 代表 NSFW 分类整体。Load 后必非空。</summary>
    public List<string>? RotationChannels { get; set; }

    /// <summary>旧版分类码选择（010/100/001），仅用于迁移，加载后清空。</summary>
    public List<string>? RotationCategories { get; set; }

    // —— 通用 ——
    public bool StartMinimized { get; set; } = true;
    public int CacheLimitMb { get; set; } = 2048;
    public int PrefetchMinutes { get; set; } = 10;
    public int Theme { get; set; } = 0;
    public string ProxyUrl { get; set; } = "";

    /// <summary>
    /// 用户代理池（每行一个：scheme://host:port，支持行内嵌 user:pass@；空 = 未配置，走公共池）。
    /// 粘性策略：成功的代理保持使用，失败自动切换。用户代理识别成功后优先于公共池。首项与 ProxyUrl 保持一致（兼容旧字段）。
    /// </summary>
    public List<string>? ProxyUrls { get; set; }

    /// <summary>
    /// 公共代理池源地址。行格式：&lt;协议&gt;|&lt;列表URL&gt;（如 socks5|https://…），空 = 内置默认源。
    /// </summary>
    public List<string>? ProxySourceUrls { get; set; }

    /// <summary>公共代理池（自动探测产出，程序自管理，只读展示）。默认首次启动后台探测。</summary>
    public List<string>? PublicProxyUrls { get; set; }

    /// <summary>公共代理池上次更新时间（超过 45 分钟或池内不足 3 个自动刷新）。</summary>
    public DateTime? PublicProxyUpdatedAt { get; set; }

    /// <summary>
    /// CF 反代地址（Cloudflare Worker 反代 wallhaven，如 https://xxx.workers.dev；空 = 不使用）。
    /// 启用后对应链路直连反代。首项与 CfProxyUrl 保持一致（兼容旧字段）。
    /// </summary>
    public string CfProxyUrl { get; set; } = "";

    /// <summary>反代地址列表（可多行，按序尝试）。空 = 不使用反代。</summary>
    public List<string>? CfProxyUrls { get; set; }

    /// <summary>
    /// 需代理的网址（域名，后缀匹配；空 = 使用默认 wallhaven 三域名）。
    /// 代理模块的路由规则：命中域名的请求走代理链路，其余直连。
    /// 默认展示 wallhaven.cc / th.wallhaven.cc / w.wallhaven.cc，可自行增删，便于其他程序集成。
    /// </summary>
    public List<string>? ProxiedHosts { get; set; }

    /// <summary>代理认证用户名（远程 http/socks5 代理，可空）。</summary>
    public string ProxyUser { get; set; } = "";

    /// <summary>代理认证密码（本地配置明文存储，仅本机使用）。</summary>
    public string ProxyPassword { get; set; } = "";

    /// <summary>最后浏览的树节点：频道 key / "fav" / "nsfw"，用于重启恢复选中。</summary>
    public string Sub { get; set; } = "";

    /// <summary>缓存目录（空 = 默认 %LOCALAPPDATA%\PonyoWallpaper\cache；修改后重启生效）。</summary>
    public string CacheDirPath { get; set; } = "";

    /// <summary>hosts 更新源地址列表（一行一个；空 = 使用内置默认源）。先直连，全部失败再套加速地址。</summary>
    public List<string>? HostsSourceUrls { get; set; }

    /// <summary>
    /// 需要加速的网站（写入 hosts 的目标域名，一行一个；空 = 默认 wallhaven 三站点）。
    /// 通用 hosts 管理：源地址拉到的条目即针对这些域名生效。
    /// </summary>
    public List<string>? HostsSites { get; set; }

    /// <summary>
    /// hosts 加速地址列表（一行一个前缀，如 https://ghproxy.net/；空 = 使用内置默认）。
    /// 仅在源地址直连全部失败时使用，逐个前缀拼接到源地址前重试。
    /// </summary>
    public List<string>? HostsAccelUrls { get; set; }

    /// <summary>旧版单条 hosts 源地址，仅用于迁移。</summary>
    public string HostsSourceUrl { get; set; } = "";

    // —— 隐藏设置（密码门禁）——
    public string ApiKey { get; set; } = "";

    /// <summary>是否在首页显示 NSFW 分类（sketchy + NSFW 合并，需 API Key）。</summary>
    public bool ShowNsfw { get; set; } = false;

    /// <summary>NSFW 分类的默认搜索词（wallhaven 搜索语法，可空 = 不限）。</summary>
    public string HiddenQuery { get; set; } = "";

    /// <summary>隐藏设置密码的 SHA256 哈希（含固定盐），配置中不保存明文。</summary>
    public string HiddenPasswordHash { get; set; } = "";

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        AppConfig? cfg = null;
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var txt = File.ReadAllText(AppPaths.ConfigFile);
                cfg = JsonSerializer.Deserialize<AppConfig>(txt);
            }
        }
        catch (Exception ex) { Logger.Warn($"config load failed: {ex.Message}"); }

        cfg ??= new AppConfig();

        // 迁移：旧版分类码（010/100/001/nsfw）→ 二级频道粒度；无记录则默认全选
        if (cfg.RotationChannels == null || cfg.RotationChannels.Count == 0)
        {
            var expanded = new List<string>();
            foreach (var code in cfg.RotationCategories ?? new List<string>())
            {
                if (code == "nsfw") expanded.Add("nsfw");
                else expanded.AddRange(Channels.All.Where(c => c.Category == code).Select(c => c.Key));
            }
            cfg.RotationChannels = expanded.Count > 0
                ? expanded
                : Channels.All.Select(c => c.Key).ToList();
            cfg.Save();
        }
        cfg.RotationCategories = null;

        // 迁移：旧版单代理 → 代理池（首项与 ProxyUrl 一致）
        if ((cfg.ProxyUrls == null || cfg.ProxyUrls.Count == 0) && !string.IsNullOrWhiteSpace(cfg.ProxyUrl))
        {
            cfg.ProxyUrls = new List<string> { cfg.ProxyUrl.Trim() };
            cfg.Save();
        }

        // 迁移：旧版单反代地址 → 反代地址列表（首项与 CfProxyUrl 一致）
        if ((cfg.CfProxyUrls == null || cfg.CfProxyUrls.Count == 0) && !string.IsNullOrWhiteSpace(cfg.CfProxyUrl))
        {
            cfg.CfProxyUrls = new List<string> { cfg.CfProxyUrl.Trim() };
            cfg.Save();
        }

        // 迁移：旧版只保存了单条内置主源 → 清空，回落内置多源（3 个）+ 内置加速地址
        if (cfg.HostsSourceUrls is { Count: 1 } && cfg.HostsSourceUrls[0] == HostsUpdater.DefaultSourceUrl)
        {
            cfg.HostsSourceUrls = null;
            cfg.HostsAccelUrls = null;
            cfg.Save();
        }

        return cfg;
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureAll();
            File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(this, _jsonOpts));
        }
        catch (Exception ex) { Logger.Error("config save failed", ex); }
    }
}