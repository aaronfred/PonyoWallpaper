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

    // —— 壁纸源（v1.4.0 多源）——
    /// <summary>
    /// 已启用的壁纸源 key 列表（"wallhaven" / "bing" / "qh360" / "picsum" / "unsplash" / "pexels" / "pixabay"）。
    /// 空或 null = 使用内置默认组合（wallhaven + qh360 + wallpapercave）。
    /// v1.5.0：需 Key 的源（Unsplash / Pexels / Pixabay）已移除，其配置字段一并删除；
    /// 旧配置文件里残留的键会被反序列化时自动忽略，不影响启动。
    /// </summary>
    public List<string>? EnabledSources { get; set; }

    /// <summary>
    /// 「浏览源」锁定（v1.5.5）：空 = 全部源混排；填了某个源 key 则<b>只从该源取图</b>，
    /// 并且左侧分类树改为显示<b>该源自己的分类</b>（如 360 的「风景大片 / 小清新」）。
    ///
    /// 与 EnabledSources 的区别：EnabledSources 是"哪些源参与混排"（多选），
    /// 本字段是"当前只看哪一个源"（单选）。用户报「筛选单个源还会混入其他源的图」，
    /// 根因就是"只看此源"藏在源菜单的二级子菜单里、容易误点父项 —— 现在提到一级入口并硬锁定。
    /// </summary>
    public string BrowseSource { get; set; } = "";

    /// <summary>
    /// 上次验证有效的链路标识（v1.5.5）：<c>direct</c> / <c>mirror:default</c> / <c>mirror:&lt;用户自填反代&gt;</c> /
    /// <c>proxy:&lt;地址&gt;</c>。
    ///
    /// 用途（用户要求「当前代理有效时默认用它，连不上才开始轮换」）：启动时直接把链路指针放到这一条，
    /// 而不是每次都从"直连"重新试一遍。默认反代只写 <c>mirror:default</c> 这个占位，<b>不落地址</b>。
    /// </summary>
    public string PreferredChain { get; set; } = "";

    // —— 通用 ——
    public bool StartMinimized { get; set; } = true;
    public int CacheLimitMb { get; set; } = 2048;
    public int PrefetchMinutes { get; set; } = 10;
    public int Theme { get; set; } = 0;
    public string ProxyUrl { get; set; } = "";

    /// <summary>
    /// 用户代理池（每行一个：scheme://host:port，支持行内嵌 user:pass@；空 = 未配置）。
    /// v1.4.2 起 UI 已拆分至独立工具 ProxyToolkit；此字段保留以兼容已有配置（旧值继续生效）。
    /// </summary>
    public List<string>? ProxyUrls { get; set; }

    /// <summary>
    /// （v1.4.2 拆分至 ProxyToolkit，仅保留字段兼容旧 JSON；本程序不再读写）
    /// </summary>
    public List<string>? ProxySourceUrls { get; set; }

    /// <summary>
    /// 每个代理源的连续失败次数（v1.2.0）。成功一次即清零；达到 3 次则从 ProxySourceUrls 中剔除。
    /// （v1.4.2 拆分至 ProxyToolkit，仅保留字段兼容旧 JSON；本程序不再读写）
    /// </summary>
    public Dictionary<string, int>? ProxySourceFails { get; set; }

    /// <summary>公共代理池（自动探测产出）。
    /// （v1.4.2 拆分至 ProxyToolkit，仅保留字段兼容旧 JSON；本程序不再读写）</summary>
    public List<string>? PublicProxyUrls { get; set; }

    /// <summary>公共代理池上次更新时间。
    /// （v1.4.2 拆分至 ProxyToolkit，仅保留字段兼容旧 JSON；本程序不再读写）</summary>
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

    /// <summary>设置页手填的代理地址（如 socks5://127.0.0.1:7890）。锁定后链路只使用它。</summary>
    public string ManualProxy { get; set; } = "";

    /// <summary>true = 锁定使用 ManualProxy：不参与自动优选、不静默降级到其他链路。</summary>
    public bool ManualProxyLocked { get; set; } = false;

    /// <summary>
    /// 公共代理池扫描模式（v1.4.2 拆分至 ProxyToolkit，仅保留字段兼容旧 JSON；本程序不再读写）。
    /// </summary>
    public string ProxyScanMode { get; set; } = "quiet";

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

        // v1.5.7 迁移：「浏览源单选锁定」已随其 UI 一并移除——旧配置里若锁着某个源，
        // 而菜单上已没有解除入口，会把用户永久锁死在单源。
        // 锁定的源就是用户实际在浏览的源（EnabledSources 当时被锁定覆盖、处于休眠），
        // → 迁移为「只勾这一个源」，升级后浏览体验无缝衔接（树仍显示该源自己的分类）。
        if (!string.IsNullOrEmpty(cfg.BrowseSource))
        {
            cfg.EnabledSources = new List<string> { cfg.BrowseSource };
            cfg.BrowseSource = "";
            cfg.Save();
        }

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
        // （v1.4.2 hosts 管理已拆分至 ProxyToolkit，此处仅做旧配置一次性清理）
        if (cfg.HostsSourceUrls is { Count: 1 } &&
            cfg.HostsSourceUrls[0] == "https://raw.githubusercontent.com/oopsunix/hosts/main/hosts_wallhaven")
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