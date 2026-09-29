using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PonyoWallpaper;

/// <summary>
/// wallhaven API v1 客户端。
/// - 令牌桶限流：40/分钟（API 限制 45，安全水位 88%）
/// - 429 自动指数退避
/// - 所有 IO 异步，主线程零阻塞
/// </summary>
internal sealed class WallhavenClient : IDisposable
{
    private HttpClient _http = null!;
    private string _apiKey = "";
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1500); // 40/min

    // —— 统一请求链路（粘性轮换，按优先级排列）：直连 → 反代 → 用户代理 ——
    // （v1.4.2 公共代理池管理已拆分至独立工具 ProxyToolkit，本链路不再包含公共池）
    // 每个条目是一级；失败切换到下一级，成功后保持（回绕时从头重试，按用户要求直连最优先）。
    private enum EntryKind { Direct, Mirror, DefaultMirror, Proxy }
    private readonly List<(EntryKind Kind, string Url, string? User, string? Pass)> _chain = new();
    private List<string> _userRaw = new();
    private List<string> _mirrorRaw = new();
    private bool _usingDefaultMirror;   // 用户未填反代 → 链路用内置默认反代（隐藏地址）
    private string? _poolUser;
    private string? _poolPass;
    private int _chainIdx;
    /// <summary>直连在本会话已确认不可用（v1.5.5）→ 重建链路时不再放进它，避免每次冷启动都白等一次超时。</summary>
    private bool _directDead;
    // 手填代理（v1.2.0）：锁定后链路只用它，不自动优选、不静默降级
    private string _manualProxy = "";
    private bool _manualLocked;
    private static string? _activeMirror; // 当前活动条目为反代时记录其地址（缩略图改写用）

    /// <summary>缩略图走反代：当前活动条目为反代时改写 th./w. 子域 URL。</summary>
    public static bool MirrorActive => _activeMirror != null;

    public static string RewriteForThumbs(string url)
        => _activeMirror == null ? url : RewriteWith(url, _activeMirror);

    /// <summary>
    /// 把 wallhaven 系 URL <b>必定</b>改写到反代上（用户自填反代优先，否则内置默认反代），
    /// 与"当前链路是否正好在反代"无关。
    ///
    /// 为什么需要它：wallhaven 域名国内直连不可达，缩略图若依赖链路状态，一旦链路在直连
    /// 就会去连 th.wallhaven.cc 并挂到超时（实测 15s），整批缩略图被拖死。
    /// v1.5.4：GitHub 图库里的 wallhaven 图改用它的 300×200 小图（实测经反代 27KB/0.7s，
    /// 而 GitHub 原图是 6.3MB/3.0s）—— 这条路径必须强制走反代。
    /// </summary>
    public string RewriteThumbAlways(string url)
    {
        // 内置默认反代有多条时取第一条（缩略图改写需要一个确定的目标，不做链路状态判断）；
        // 该条若不可达，调用方会回退到 ThumbFallback（GitHub 原图）。
        var mirror = _mirrorRaw.Count > 0 ? _mirrorRaw[0] : DefaultMirror.Url;
        return string.IsNullOrWhiteSpace(mirror) ? url : RewriteWith(url, mirror.TrimEnd('/'));
    }

    private static string RewriteWith(string url, string mirror)
    {
        if (url.StartsWith("https://th.wallhaven.cc/", StringComparison.Ordinal))
            return mirror + "/th/" + url["https://th.wallhaven.cc/".Length..];
        if (url.StartsWith("https://w.wallhaven.cc/", StringComparison.Ordinal))
            return mirror + "/w/" + url["https://w.wallhaven.cc/".Length..];
        if (url.StartsWith("https://wallhaven.cc/", StringComparison.Ordinal))
            return mirror + "/" + url["https://wallhaven.cc/".Length..];
        return url;
    }

    private HttpClient? _directHttp;

    /// <summary>反代模式下用直连客户端（反代本身境内可达，不应再套代理）。</summary>
    private HttpClient CurrentHttp()
        => Current().Kind == EntryKind.Proxy ? _http : (_directHttp ??= NewDirectClient());

    private static HttpClient NewDirectClient()
    {
        var c = new HttpClient(new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.Add("User-Agent", "PonyoWallpaper/1.0");
        return c;
    }

    /// <summary>当前实际在用的链路描述（随失败切换/池重建实时变化）。默认反代不外泄地址，统一称「默认代理」。</summary>
    public string ActiveProxyName => Current().Kind switch
    {
        EntryKind.Direct => "直连",
        // v1.5.6：内置默认反代有多条，用序号区分主/备（只报序号，不报地址）
        EntryKind.DefaultMirror => DefaultMirrorRank(Current().Url) is { } r
            ? $"默认代理{DefaultMirrorSuffix(r)}"
            : "默认代理",
        EntryKind.Mirror => Current().Url + "（反代直连）",
        _ => Current().Url
    };

    /// <summary>该地址在内置默认反代列表中的序号（0 起）；不是内置地址则返回 null。</summary>
    private static int? DefaultMirrorRank(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var norm = url.Trim().TrimEnd('/');
        for (var i = 0; i < DefaultMirror.Urls.Count; i++)
            if (string.Equals(norm, DefaultMirror.Urls[i], StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>0 → ""（主）；1 → "（备1）"；2 → "（备2）" …（仅用于界面/日志文案）</summary>
    private static string DefaultMirrorSuffix(int rank) => rank == 0 ? "" : $"（备{rank}）";

    /// <summary>当前代理地址；直连与内置默认反代都返回空串（默认反代不对外暴露地址）。</summary>
    public string ActiveProxyAddress => Current().Kind switch
    {
        EntryKind.Direct => "",
        EntryKind.DefaultMirror => "",
        _ => Current().Url
    };

    /// <summary>当前链路是否使用内置默认反代（设置页据此显示「当前使用默认代理」提示）。</summary>
    public bool UsingDefaultMirror => Current().Kind == EntryKind.DefaultMirror;

    /// <summary>当前在用层级：直连 / 反代 / 默认代理 / 用户代理 / 手填代理（锁定）。</summary>
    public string ActiveTier
    {
        get
        {
            if (_manualLocked && _manualProxy.Length > 0) return "手填代理";
            return Current().Kind switch
            {
                EntryKind.Direct => "直连",
                EntryKind.DefaultMirror => "默认代理",
                EntryKind.Mirror => "反代",
                _ => "用户代理"
            };
        }
    }

    private (EntryKind Kind, string Url, string? User, string? Pass) Current()
        => _chain.Count == 0 ? (EntryKind.Direct, "", null, null) : _chain[Math.Clamp(_chainIdx, 0, _chain.Count - 1)];

    /// <summary>
    /// 该条目是否为「反代」（用户自填反代 或 内置默认反代）。
    /// 新增反代类型时只需改这里一处 —— 曾经因为漏判 DefaultMirror 导致测速探针打到被墙的直连地址。
    /// </summary>
    private static bool IsMirror(in (EntryKind Kind, string Url, string? User, string? Pass) e)
        => e.Kind is EntryKind.Mirror or EntryKind.DefaultMirror;

    /// <summary>
    /// 设置反代地址（可多行，按序尝试；来自 代理管理 → 反代地址）。
    /// <b>留空 = 使用内置默认反代</b>（隐藏资源，见 <see cref="DefaultMirror"/>）；填了则只用用户自己的。
    /// </summary>
    public void SetMirrors(IReadOnlyList<string>? urls)
    {
        _mirrorRaw = (urls ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().TrimEnd('/')).ToList();
        _usingDefaultMirror = _mirrorRaw.Count == 0;
        // v1.5.6：内置默认反代现在是多条（主 + 备用）→ 全部入链，某条被墙/限流时自动滑到下一跳
        if (_usingDefaultMirror) _mirrorRaw = DefaultMirror.Urls.ToList();
        RebuildChain();
    }

    /// <summary>设置用户代理池（设置变更时调用）。空 = 未配置用户代理。</summary>
    public void SetProxies(IReadOnlyList<string>? urls, string? user, string? pass)
    {
        _userRaw = (urls ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        _poolUser = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        _poolPass = pass;
        RebuildChain();
    }

    /// <summary>
    /// 设置/清除「手填代理」并决定是否锁定。锁定后 RebuildChain 只生成该代理一条链路：
    /// 不自动优选、不静默降级（用户手填即视为明确意图；失效时由 UI 提示，见 v1.2.0 方案 2.3）。
    /// </summary>
    public void SetManualProxy(string? url, bool locked)
    {
        _manualProxy = (url ?? "").Trim();
        _manualLocked = locked && _manualProxy.Length > 0;
        RebuildChain();
    }

    /// <summary>当前是否处于「手填代理锁定」状态（供 UI 显示与缩略图客户端同步）。</summary>
    public bool ManualProxyLocked => _manualLocked;

    /// <summary>
    /// 实测当前活动链路的延迟（毫秒）：连测 2 次取较小值，用于抗偶发尖峰
    /// （实测反代中位 840ms 但出现过 11.8s 尖峰，单次测量不可作判据）。
    /// 返回 null = 不可达。每次请求独立 5s 超时。
    /// </summary>
    public async Task<int?> MeasureActiveLatencyAsync(CancellationToken ct = default)
    {
        // v1.5.5：先把链路指针放到「上次验证有效」的那条再测。
        // 否则在直连不可达的环境里，状态条会一直显示"不可用"，用户点「恢复默认代理」也看不到好转
        // —— 这正是「默认反代怎么都无法访问」的观感来源。
        ApplyPreferredChain();

        int? best = null;
        for (int i = 0; i < 2; i++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var e = Current();
                var target = IsMirror(e)
                    ? e.Url.TrimEnd('/') + "/api/v1/search?q=cat&purity=100&page=1"
                    : "https://wallhaven.cc/api/v1/search?q=cat&purity=100&page=1";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var resp = await _http.GetAsync(target, cts.Token);
                sw.Stop();
                if (resp.IsSuccessStatusCode)
                {
                    var ms = (int)sw.ElapsedMilliseconds;
                    best = best == null ? ms : Math.Min(best.Value, ms);
                    RememberChain();   // v1.5.5：测速成功 = 这条链路有效 → 记住它，下次直接用
                }
            }
            catch { /* 单次失败忽略，两次都失败则返回 null */ }
        }
        return best;
    }

    /// <summary>重建统一链路：直连 → 反代 → 用户代理（行内嵌账密优先，其次全局账密）。</summary>
    private void RebuildChain()
    {
        _chain.Clear();

        // 手填代理锁定模式：链路只有这一条，失败即失败（不降级、不优选）
        if (_manualLocked && _manualProxy.Length > 0)
        {
            try
            {
                var (url, u, p) = ParseProxyCredentials(_manualProxy, null, null);
                _chain.Add((EntryKind.Proxy, url, u ?? _poolUser, p ?? _poolPass));
            }
            catch (Exception ex)
            {
                Logger.Warn($"manual proxy invalid '{_manualProxy}': {ex.Message}");
                _chain.Add((EntryKind.Direct, "", null, null));
            }
            if (_chainIdx >= _chain.Count) _chainIdx = 0;
            var oldM = _http;
            _http = BuildClient();
            Logger.Info($"request chain rebuilt: manual locked = {_manualProxy}; active = {ActiveProxyName}");
            try { oldM.Dispose(); } catch { }
            return;
        }

        // v1.5.5：直连被确认不可用后就不再放进链路 —— 国内直连 wallhaven 必挂，
        // 留着它只会让每次重建链路后的首个请求先白等一次超时（这正是"反代访问不了"的观感来源）
        if (!_directDead) _chain.Add((EntryKind.Direct, "", null, null)); // 直连最优先（按用户要求）
        foreach (var m in _mirrorRaw)
            _chain.Add((DefaultMirror.IsDefault(m) ? EntryKind.DefaultMirror : EntryKind.Mirror, m, null, null));
        foreach (var line in _userRaw)
        {
            try
            {
                var (url, u, p) = ParseProxyCredentials(line, null, null);
                _chain.Add((EntryKind.Proxy, url, u ?? _poolUser, p ?? _poolPass));
            }
            catch (Exception ex) { Logger.Warn($"proxy line invalid '{line}': {ex.Message}"); }
        }
        if (_chainIdx >= _chain.Count) _chainIdx = 0;
        var old = _http;
        _http = BuildClient();
        // 注意：不要打印 _mirrorRaw 内容——默认反代地址不外泄，只报是否在用默认
        Logger.Info($"request chain rebuilt: direct + mirror {_mirrorRaw.Count}{( _usingDefaultMirror ? "(默认)" : "")} + user {_userRaw.Count}; active = {ActiveProxyName}");
        try { old.Dispose(); } catch { }
    }

    /// <summary>按当前链路条目构造 HttpClient：直连/反代 → 直连客户端；代理 → 该代理的客户端。</summary>
    /// <summary>
    /// v1.5.8 占用优化（P5）：连接池条目 10 分钟周期回收 —— 常驻托盘的长连接会被中间设备
    /// 静默断开，下一次请求撞死连接只能靠 failover 兜底；周期回收让空闲连接自然过期，
    /// 同时避免 Socket 句柄长期堆积。
    /// </summary>
    private static SocketsHttpHandler NewSocketsHandler(TimeSpan? connectTimeout) => new()
    {
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    private HttpClient BuildClient()
    {
        var e = Current();
        var name = e.Kind switch
        {
            EntryKind.Direct => "(直连)",
            EntryKind.DefaultMirror => "默认代理（反代直连）",
            EntryKind.Mirror => e.Url + "（反代直连）",
            _ => e.Url
        };
        HttpMessageHandler handler;
        try
        {
            handler = e.Kind == EntryKind.Proxy
                ? ProxyFactory.Create(e.Url, e.User, e.Pass) ?? NewSocketsHandler(null)
                : e.Kind == EntryKind.Direct
                    // 直连：连接阶段 3s 就放弃（国内直连 wallhaven 必然超时，不能让它拖住首屏）
                    ? NewSocketsHandler(TimeSpan.FromSeconds(3))
                    : NewSocketsHandler(null);
        }
        catch (Exception ex)
        {
            Logger.Warn($"invalid proxy, fallback to direct: {ex.Message}");
            handler = NewSocketsHandler(null);
        }
        _activeMirror = IsMirror(e) ? e.Url : null;
        // v1.5.5：直连整体超时从 30s 收紧到 8s。实测反代完全可用（API 1.7s / 缩略图 0.8s），
        // 但链路把「直连」排在第一位，每次冷启动都要先白等一次超时才切到反代 —— 用户感知就是
        // 「反代怎么都无法访问」。现在直连 8s 快速失败，且失败一次后本会话直接跳过直连（见 _directDead）。
        var timeout = e.Kind == EntryKind.Direct
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromSeconds(30);
        var c = new HttpClient(handler) { Timeout = timeout };
        c.DefaultRequestHeaders.Add("User-Agent", "PonyoWallpaper/1.0");
        if (_apiKey.Length > 0)
            c.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
        return c;
    }

    /// <summary>
    /// 请求失败时切换到链路下一级（粘性成功：成功后保持不变）。
    ///
    /// v1.5.5 关键修复：**只在「链路仍停在我失败的那一条」时才推进**。
    /// 此前并发请求各自无条件推进 `_chainIdx`，于是一个请求刚切到反代、另一个请求的失败又把它切回直连
    /// （日志特征：`failover -> 默认代理` 紧跟 `failover -> 直连`），导致 wallhaven 永远用不对链路
    /// —— 这正是用户报「默认反代怎么都无法访问」的直接原因。
    /// </summary>
    private void RotateProxy(in (EntryKind Kind, string Url, string? User, string? Pass) used)
    {
        var cur = Current();
        if (cur.Kind != used.Kind ||
            !string.Equals(cur.Url, used.Url, StringComparison.OrdinalIgnoreCase))
        {
            return;   // 别人已经切换过了：我的重试直接用新链路即可，不要再推进（否则互相踩）
        }

        if (cur.Kind == EntryKind.Direct) _directDead = true;
        if (_chain.Count <= 1) return;
        _chainIdx = (_chainIdx + 1) % _chain.Count;
        var old = _http;
        _http = BuildClient();
        try { old.Dispose(); } catch { }
        Logger.Info($"request chain failover -> {ActiveProxyName}");
    }

    public WallhavenClient(string apiKey = "", string? proxyUrl = null,
        string? proxyUser = null, string? proxyPassword = null,
        IReadOnlyList<string>? proxyUrls = null)
    {
        _apiKey = apiKey ?? "";
        SetProxies(proxyUrls ?? (string.IsNullOrWhiteSpace(proxyUrl)
            ? Array.Empty<string>() : new[] { proxyUrl }), proxyUser, proxyPassword);
    }

    // —— v1.5.2：非 wallhaven 资源的下载改走通用链路（镜像候选 + 手填代理），需要读配置 ——
    private AppConfig? _cfg;

    // —— v1.5.5：链路粘性记忆（上次有效的条目跨会话沿用，连不上才轮换）——
    private string _preferredChain = "";

    /// <summary>
    /// 链路条目的稳定标识（用于记忆；内置默认反代只记占位名 + 序号，<b>不落地址</b>）。
    ///
    /// v1.5.6：内置默认反代有多条时必须带序号（<c>mirror:default:N</c>），
    /// 否则主/备都会记成同一个 <c>mirror:default</c> → ApplyPreferredChain 永远命中主地址，
    /// 备用地址被记住后重启恢复不了。
    /// </summary>
    private static string TierKey(in (EntryKind Kind, string Url, string? User, string? Pass) e)
        => e.Kind switch
        {
            EntryKind.Direct => "direct",
            EntryKind.DefaultMirror => DefaultMirrorRank(e.Url) is { } r
                ? $"mirror:default:{r}"
                : "mirror:default",
            EntryKind.Mirror => "mirror:" + e.Url,
            _ => "proxy:" + e.Url
        };

    /// <summary>注入配置：非 wallhaven 源（360 / WallpaperCave / GitHub 图库）的取图需要它读代理设置。</summary>
    public void AttachConfig(AppConfig cfg)
    {
        _cfg = cfg;
        _preferredChain = cfg.PreferredChain ?? "";
        ApplyPreferredChain();
    }

    /// <summary>
    /// 把链路指针移到「上次验证有效」的那一条（用户要求：有效就默认用它，连不上才轮换）。
    /// 这样启动后不会再每次都先打一遍必然失败的直连。
    /// </summary>
    private void ApplyPreferredChain()
    {
        if (_preferredChain.Length == 0 || _chain.Count == 0) return;

        var idx = -1;
        for (var i = 0; i < _chain.Count; i++)
            if (TierKey(_chain[i]) == _preferredChain) { idx = i; break; }

        // 向后兼容：v1.5.5 及更早只在配置里存了无序号占位 "mirror:default"
        //（当时内置反代只有一条）→ 视为「主地址」。
        if (idx < 0 && _preferredChain == "mirror:default")
            for (var i = 0; i < _chain.Count; i++)
                if (_chain[i].Kind == EntryKind.DefaultMirror) { idx = i; break; }

        if (idx < 0) return;

        _chainIdx = idx;
        var old = _http;
        _http = BuildClient();
        try { old.Dispose(); } catch { }
        Logger.Info($"request chain: 沿用上次有效链路 -> {ActiveProxyName}");
    }

    /// <summary>请求成功后记住当前链路（下次启动直接用它；它连不上时 RotateProxy 会自动换下一级）。</summary>
    private void RememberChain()
    {
        var key = TierKey(Current());
        if (key == _preferredChain) return;
        _preferredChain = key;
        if (_cfg == null) return;
        _cfg.PreferredChain = key;
        try { _cfg.Save(); } catch { /* 落盘失败不影响本次运行 */ }
    }

    /// <summary>是否 wallhaven 系资源（决定走它自己的反代链路还是通用链路）。</summary>
    private static bool IsWallhavenUrl(string url)
        => url.Contains("wallhaven", StringComparison.OrdinalIgnoreCase);

    /// <summary>按当前链路条目改写 URL：反代条目（含内置默认反代）→ 反代地址；其余原样。</summary>
    private string PrepareUrl(string raw)
        => IsMirror(Current()) ? RewriteWith(raw, Current().Url) : raw;

    /// <summary>
    /// 解析代理地址中内嵌的 user:pass@ 凭据（支持 URL 转义）。
    /// 显式填写的用户名/密码优先于 URL 内嵌凭据。
    /// </summary>
    public static (string Url, string? User, string? Pass) ParseProxyCredentials(
        string? proxyUrl, string? user, string? pass)
    {
        var url = proxyUrl?.Trim() ?? "";
        if (url.Length == 0) return ("", null, null);

        string? u = null, p = null;
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var at = url.LastIndexOf('@');
        if (schemeEnd >= 0 && at > schemeEnd + 3)
        {
            var userinfo = url.Substring(schemeEnd + 3, at - schemeEnd - 3);
            url = url.Substring(0, schemeEnd + 3) + url[(at + 1)..];
            var colon = userinfo.IndexOf(':');
            if (colon >= 0)
            {
                u = Uri.UnescapeDataString(userinfo[..colon]);
                p = Uri.UnescapeDataString(userinfo[(colon + 1)..]);
            }
            else
            {
                u = Uri.UnescapeDataString(userinfo);
            }
        }
        if (!string.IsNullOrWhiteSpace(user)) u = user.Trim();
        if (!string.IsNullOrWhiteSpace(pass)) p = pass;
        return (url, u, p);
    }

    public void SetApiKey(string key)
    {
        _apiKey = key ?? "";
        if (_http.DefaultRequestHeaders.Contains("X-API-Key"))
            _http.DefaultRequestHeaders.Remove("X-API-Key");
        if (_apiKey.Length > 0)
            _http.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
    }

    /// <summary>代理设置变更后即时生效：重建底层 HttpClient（无需重启程序）。
    /// 正在进行的请求会被取消，调用方（轮换引擎）自带重试。</summary>
    public void RebuildProxy(string? proxyUrl, string? proxyUser, string? proxyPassword)
        => SetProxies(string.IsNullOrWhiteSpace(proxyUrl) ? Array.Empty<string>() : new[] { proxyUrl },
            proxyUser, proxyPassword);

    /// <summary>串行化 + 最小间隔，确保实际不超过 40 次/分钟。</summary>
    private async Task AcquireAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < MinInterval)
                await Task.Delay(MinInterval - elapsed, ct);
            _lastRequest = DateTime.UtcNow;
        }
        finally { _throttle.Release(); }
    }

    public async Task<List<WallpaperItem>?> SearchAsync(
        string categories, string purity, string sorting, string? query, string resolution,
        int page = 1, string? seed = null, CancellationToken ct = default)
    {
        var qs = new List<string>
        {
            $"categories={categories}",
            $"purity={purity}",
            $"sorting={sorting}",
            "order=desc",
            $"page={page}"
        };
        if (!string.IsNullOrWhiteSpace(resolution)) qs.Add($"atleast={resolution}");
        if (!string.IsNullOrWhiteSpace(query)) qs.Add($"q={Uri.EscapeDataString(query)}");
        // random 排序支持 seed：换一批时换 seed 即可拿到确定的另一批随机图
        if (!string.IsNullOrWhiteSpace(seed)) qs.Add($"seed={Uri.EscapeDataString(seed)}");
        // API Key 同时放进查询串：CF 反代模式下请求头不会被转发，查询串最可靠
        if (_apiKey.Length > 0) qs.Add($"apikey={Uri.EscapeDataString(_apiKey)}");

        var qsUrl = "https://wallhaven.cc/api/v1/search?" + string.Join("&", qs);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var used = Current();   // 本次尝试用的链路快照（失败时据此判断是否该推进链路）
            try
            {
                await AcquireAsync(ct);
                // URL 与客户端必须在每次尝试时重新按"当前链路条目"取：
                // 失败切换链路后（直连→反代→代理），重试要用新的地址和客户端
                var url = PrepareUrl(qsUrl);
                var http = CurrentHttp();
                // 诊断（不含地址）：确认这一跳到底用的是哪条链路、URL 有没有被改写成反代
                Logger.Info($"search try {attempt}: tier={ActiveProxyName} rewritten={(url != qsUrl ? "Y" : "N")} chain={_chainIdx}/{_chain.Count} client={(ReferenceEquals(http, _http) ? "chain" : "direct")}");
                using var resp = await http.GetAsync(url, ct);
                if ((int)resp.StatusCode == 429)
                {
                    // 429 按「出口 IP」限流：换一条链路通常立刻可用（不同 IP 不同配额），
                    // 死等同一个 IP 既慢又白烧配额 —— 实测共享反代 burst 后 retry-after=22s，
                    // 而原来的 2/4/8s 退避会白等 14s+ 再失败。
                    // 首次 429 先换链路；没有别的链路或换了还是 429，才按服务端 Retry-After 等一次（上限 20s）。
                    if (_chain.Count > 1 && attempt == 0)
                    {
                        Logger.Warn("429 rate-limited, 切换链路重试");
                        RotateProxy(used);
                        await Task.Delay(400, ct);
                        continue;
                    }
                    var ra = resp.Headers.RetryAfter?.Delta;
                    var wait = ra is { TotalSeconds: > 0 and <= 20 }
                        ? ra.Value
                        : TimeSpan.FromSeconds(Math.Min(20, Math.Pow(2, attempt + 1)));
                    Logger.Warn($"429 rate-limited, retry in {wait.TotalSeconds:0.0}s");
                    await Task.Delay(wait, ct);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var data = await resp.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: ct);
                if (data?.Data == null) return new List<WallpaperItem>();
                RememberChain();   // v1.5.5：这条链路确实取到了数据 → 记住，下次启动直接用它
                return data.Data.Select(d => new WallpaperItem
                {
                    Id = d.Id ?? "",
                    Path = d.Path ?? "",
                    Thumb = d.Thumbs?.Small ?? "",
                    Resolution = d.Resolution ?? "",
                    Category = d.Category ?? "",
                    Purity = d.Purity ?? "sfw",
                    FileSize = d.FileSize,
                    PageUrl = d.Url ?? ""
                }).ToList();
            }
            catch (Exception ex) when (attempt < 2)
            {
                Logger.Warn($"search attempt {attempt + 1}: {ex.Message}");
                RotateProxy(used);
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
        }
        return null;
    }

    public async Task DownloadAsync(string url, string destPath, CancellationToken ct = default)
    {
        // v1.5.2：非 wallhaven 资源（360 / WallpaperCave / GitHub 图库）走通用链路 ——
        // CdnMirror 展开多条等价镜像 + 手填代理，逐条硬超时快速失败；
        // 关键是<b>不动 wallhaven 的链路状态</b>（不再让非 wallhaven 的失败去 RotateProxy 污染全局链路索引）。
        if (!IsWallhavenUrl(url) && _cfg != null)
        {
            if (await SourceHttp.DownloadToAsync(url, destPath, _cfg, ct)) return;
            // 全链路失败：继续走下面的直连重试，保持对旧行为的兜底
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var used = Current();   // 同上：链路快照
            try
            {
                await AcquireAsync(ct);
                // 每次尝试重新按"当前链路条目"改写地址与客户端（失败切换后重试要用新链路）
                var tryUrl = PrepareUrl(url);
                using var resp = await CurrentHttp().GetAsync(tryUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode == 429)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var dir = Path.GetDirectoryName(destPath)!;
                Directory.CreateDirectory(dir);
                await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                await resp.Content.CopyToAsync(fs, ct);
                RememberChain();   // v1.5.5：下载成功 → 记住这条链路
                return;
            }
            catch (Exception ex) when (attempt < 2)
            {
                Logger.Warn($"download attempt {attempt + 1}: {ex.Message}");
                RotateProxy(used);
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
        }
        throw new IOException($"download failed after 3 retries: {url}");
    }

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("data")] public List<Item>? Data { get; set; }
    }
    private sealed class Item
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("thumbs")] public Thumbs? Thumbs { get; set; }
        [JsonPropertyName("resolution")] public string? Resolution { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("purity")] public string? Purity { get; set; }
        [JsonPropertyName("file_size")] public long FileSize { get; set; }
    }
    private sealed class Thumbs
    {
        [JsonPropertyName("small")] public string? Small { get; set; }
    }
}