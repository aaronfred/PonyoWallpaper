namespace PonyoWallpaper;

using System.Diagnostics;

/// <summary>
/// 代理管理页（双模块 Tab）：
/// ① 代理管理 —— 用户代理池编辑、公共代理池（自动探测，只读展示）、代理池源地址、
///    「更新代理池」= 从源地址抓取（http/https/socks5 不限）→ 竞速实测 → 保留最优 3 个；
/// ② hosts 管理 —— 沿用原 hosts 管理页全部功能。
/// 代理池变更即时生效（API 客户端 + 缩略图客户端重建）。
/// </summary>
internal sealed class ProxyManagerForm : Form
{
    private readonly AppConfig _cfg;
    private readonly WallhavenClient _api;
    private readonly Action _onPoolChanged;

    private readonly TextBox _pool = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
        Font = new Font("Consolas", 9)
    };
    private readonly TextBox _public = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
        Font = new Font("Consolas", 9), BackColor = Color.FromArgb(245, 245, 247)
    };
    private readonly Label _publicMeta = new()
    {
        AutoSize = true, ForeColor = Color.FromArgb(120, 120, 120),
        Font = new Font("Microsoft YaHei UI", 8)
    };
    private readonly TextBox _sources = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
        Font = new Font("Consolas", 9)
    };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 8.5f), BackColor = Color.FromArgb(250, 250, 250)
    };
    private readonly TextBox _proxiedHosts = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
        Font = new Font("Consolas", 9)
    };
    private readonly Button _btnPickBest = new() { Text = "优选代理" };
    private readonly Button _btnOptimize = new() { Text = "优选代理池" };
    private readonly Button _btnUpdateSources = new() { Text = "更新代理源" };
    private readonly Button _btnResetSources = new() { Text = "恢复默认源" };
    private readonly TextBox _cf = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Font = new Font("Consolas", 9) };
    private readonly Button _btnGuide = new() { Text = "反代搭建指引" };

    // 输入防抖：停止输入 700ms 后自动保存（避免"要切走焦点才保存"的延迟感），失焦时立即保存
    private readonly System.Windows.Forms.Timer _cfTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _poolTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _srcTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _hostsTimer = new() { Interval = 700 };

    public ProxyManagerForm(AppConfig cfg, WallhavenClient api, Action onPoolChanged)
    {
        _cfg = cfg;
        _api = api;
        _onPoolChanged = onPoolChanged;

        Text = "代理管理";
        Width = 680;
        Height = 724;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 9);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tabProxy = new TabPage("代理管理");
        var tabHosts = new TabPage("hosts 管理");

        BuildProxyTab(tabProxy);

        // hosts 模块：原管理页整体嵌入
        var hosts = new HostsForm(cfg)
        {
            TopLevel = false,
            FormBorderStyle = FormBorderStyle.None,
            Dock = DockStyle.Fill,
            Visible = true
        };
        tabHosts.Controls.Add(hosts);

        tabs.TabPages.Add(tabProxy);
        tabs.TabPages.Add(tabHosts);
        Controls.Add(tabs);

        ThemeManager.Apply(this, ThemeManager.LastDark);
    }

    private static Label MakeLabel(string text, int x, int y)
        => new() { Text = text, AutoSize = true, Location = new Point(x, y), Font = new Font("Microsoft YaHei UI", 9) };

    private void BuildProxyTab(TabPage page)
    {
        _proxiedHosts.Text = string.Join("\r\n", ProxyModule.Hosts(_cfg));
        _pool.Text = string.Join("\r\n", _cfg.ProxyUrls ?? new List<string>());
        _pool.PlaceholderText = "填写参考（输入时隐藏）：\r\nsocks5://user:pass@1.2.3.4:1080\r\nhttp://1.2.3.4:8080\r\nsocks5://1.2.3.4:1080";
        _public.Text = string.Join("\r\n", _cfg.PublicProxyUrls ?? new List<string>());
        _sources.Text = string.Join("\r\n",
            _cfg.ProxySourceUrls is { Count: > 0 } ? _cfg.ProxySourceUrls : ProxyTester.DefaultSourceUrls.ToList());
        _cf.Text = string.Join("\r\n", _cfg.CfProxyUrls ?? (string.IsNullOrEmpty(_cfg.CfProxyUrl) ? new List<string>() : new List<string> { _cfg.CfProxyUrl }));
        UpdatePublicMeta();

        // 展示顺序（自上而下）：需代理的网址 → 反代 → 用户代理 → 公共代理 → 代理源；
        // 五个文本框统一高度 64
        _btnPickBest.SetBounds(12, 486, 96, 30);
        _btnOptimize.SetBounds(112, 486, 110, 30);
        _btnUpdateSources.SetBounds(226, 486, 110, 30);
        _btnResetSources.SetBounds(340, 486, 110, 30);
        _btnGuide.SetBounds(454, 486, 140, 30);

        page.Controls.AddRange(new Control[]
        {
            MakeLabel("需代理的网址（每行一个域名，后缀匹配；默认 wallhaven 三站点，可增删供其他程序集成）", 12, 14),
            _proxiedHosts,
            MakeLabel("反代地址（可填多行按序尝试，直连可达、优先于一切代理；留空不使用。搭建见「反代搭建指引」）", 12, 104),
            _cf,
            MakeLabel("用户代理池（每行一个，可含 user:pass@；优先级高于公共代理池）", 12, 194),
            _pool,
            MakeLabel("公共代理池（自动探测产出 · 只读 · 超 6 小时或池内不足 2 个自动刷新）", 12, 284),
            _public,
            _publicMeta,
            MakeLabel("代理池源地址（每行：协议|URL，如 socks5|https://… ；留空用内置默认源）", 12, 394),
            _sources,
            _btnPickBest,
            _btnOptimize,
            _btnUpdateSources,
            _btnResetSources,
            _btnGuide,
            MakeLabel("执行日志", 12, 528),
            _log
        });

        _proxiedHosts.SetBounds(12, 34, 624, 64);
        _cf.SetBounds(12, 124, 624, 64);
        _pool.SetBounds(12, 214, 624, 64);
        _public.SetBounds(12, 304, 624, 64);
        _publicMeta.SetBounds(12, 372, 600, 18);
        _sources.SetBounds(12, 414, 624, 64);
        _log.SetBounds(12, 548, 624, 104);

        // 防抖自动保存：输入停止 700ms 即保存；失焦/离开立即保存并即时生效
        _proxiedHosts.TextChanged += (_, _) => { _hostsTimer.Stop(); _hostsTimer.Start(); };
        _cf.TextChanged += (_, _) => { _cfTimer.Stop(); _cfTimer.Start(); };
        _pool.TextChanged += (_, _) => { _poolTimer.Stop(); _poolTimer.Start(); };
        _sources.TextChanged += (_, _) => { _srcTimer.Stop(); _srcTimer.Start(); };
        _hostsTimer.Tick += (_, _) => { _hostsTimer.Stop(); SaveProxiedHosts(); };
        _cfTimer.Tick += (_, _) => { _cfTimer.Stop(); SaveCf(); };
        _poolTimer.Tick += (_, _) => { _poolTimer.Stop(); SavePool(); };
        _srcTimer.Tick += (_, _) => { _srcTimer.Stop(); SaveSources(); };
        _proxiedHosts.Leave += (_, _) => { _hostsTimer.Stop(); SaveProxiedHosts(); };
        _cf.Leave += (_, _) => { _cfTimer.Stop(); SaveCf(); };
        _pool.Leave += (_, _) => { _poolTimer.Stop(); SavePool(); };
        _sources.Leave += (_, _) => { _srcTimer.Stop(); SaveSources(); };

        _btnPickBest.Click += (_, _) => _ = PickBestAsync();
        _btnOptimize.Click += (_, _) => _ = RefreshPublicPoolAsync();
        _btnUpdateSources.Click += async (_, _) =>
        {
            // 更新代理源：重置为内置默认的全网源集合，并立即开始优选
            _sources.Text = string.Join("\r\n", ProxyTester.DefaultSourceUrls);
            SaveSources();
            AppendLog($"代理源已更新为内置默认（{ProxyTester.DefaultSourceUrls.Length} 个源），开始优选…");
            await RefreshPublicPoolAsync();
        };
        _btnResetSources.Click += (_, _) =>
        {
            // 仅重置源列表为内置默认（不自动优选）
            _sources.Text = string.Join("\r\n", ProxyTester.DefaultSourceUrls);
            SaveSources();
            AppendLog($"源地址已恢复为内置默认（{ProxyTester.DefaultSourceUrls.Length} 个源）");
        };
        _btnGuide.Click += (_, _) => OpenGuide();
    }

    /// <summary>保存 CF 反代地址并即时生效（API + 缩略图全部切到反代直连）。</summary>
    private void SaveCf()
    {
        var lines = ProxyTester.SplitLines(_cf.Text);
        var unchanged = _cfg.CfProxyUrls != null
            && _cfg.CfProxyUrls.Count == lines.Count
            && !_cfg.CfProxyUrls.Where((u, i) => u != lines[i]).Any();
        if (unchanged) return;
        _cfg.CfProxyUrls = lines.Count > 0 ? lines : null;
        _cfg.CfProxyUrl = lines.Count > 0 ? lines[0] : "";
        _cfg.Save();
        _onPoolChanged();
        AppendLog(lines.Count > 0 ? $"反代地址已保存（{lines.Count} 个，按序尝试）" : "反代地址已清空");
    }
    /// <summary>打开随程序附带的反代搭建指引（release 目录下 反代搭建指引.txt）。</summary>
    private void OpenGuide()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "反代搭建指引.txt");
            if (!File.Exists(path))
            {
                AppendLog("未找到指引文件 反代搭建指引.txt（随发布包附带）");
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog("打开指引失败：" + ex.Message); }
    }

    private void UpdatePublicMeta()
        => _publicMeta.Text = "上次探测：" +
            (_cfg.PublicProxyUpdatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "尚未探测") +
            "；超过 6 小时或池内不足 2 个时会自动刷新";

    private void SavePool()
    {
        var lines = ProxyTester.SplitLines(_pool.Text);
        _cfg.ProxyUrls = lines.Count > 0 ? lines : null;
        _cfg.ProxyUrl = lines.Count > 0 ? lines[0] : "";
        _cfg.Save();
        _onPoolChanged();
        AppendLog($"用户代理池已保存（{lines.Count} 个）");
    }

    private void SaveSources()
    {
        var lines = _sources.Text.Replace("\r", "")
            .Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        _cfg.ProxySourceUrls = lines.Count > 0 ? lines : null;
        _cfg.Save();
    }

    /// <summary>保存需代理的网址规则（空 = 回落默认 wallhaven 三域名）。</summary>
    private void SaveProxiedHosts()
    {
        var lines = ProxyTester.SplitLines(_proxiedHosts.Text)
            .Select(s => s.Trim().TrimEnd('/').ToLowerInvariant())
            .Where(s => s.Length > 0).Distinct().ToList();
        var unchanged = _cfg.ProxiedHosts != null
            && _cfg.ProxiedHosts.Count == lines.Count
            && !_cfg.ProxiedHosts.Where((h, i) => h != lines[i]).Any();
        if (unchanged) return;

        _cfg.ProxiedHosts = lines.Count > 0 ? lines : null;
        _cfg.Save();
        AppendLog(lines.Count > 0
            ? $"需代理的网址已保存（{lines.Count} 条规则，后缀匹配）"
            : "需代理的网址已清空，回落默认 wallhaven 三站点");
    }

    /// <summary>
    /// 优选代理：不抓新源，仅从现有链路（直连 / 反代 / 用户代理 / 公共池）并发实测，
    /// 切换为最快且能访问 wallhaven 的一级。新增反代地址后点它即可立即生效。
    /// </summary>
    private async Task PickBestAsync()
    {
        _cfTimer.Stop(); _poolTimer.Stop(); _srcTimer.Stop();
        SaveCf();
        SavePool();
        SaveSources();

        _btnPickBest.Enabled = false;
        var old = _btnPickBest.Text;
        _btnPickBest.Text = "优选中…";
        try
        {
            AppendLog("开始优选：实测现有全部链路（直连 / 反代 / 用户代理 / 公共池）…");
            var r = await _api.PickBestAsync(AppendLog);
            if (r == null)
            {
                AppendLog("优选完成：所有链路均不可用（可填入反代地址后重试）");
                return;
            }
            AppendLog($"优选完成：已切换为 {r.Value.Name}（{r.Value.Ms}ms）");
            _onPoolChanged();
        }
        finally
        {
            _btnPickBest.Enabled = true;
            _btnPickBest.Text = old;
        }
    }

    /// <summary>更新代理池：从源地址抓取（http/https/socks5 不限）→ 竞速实测 → 保留最优 3 个。</summary>
    private async Task RefreshPublicPoolAsync()
    {
        SaveSources();
        _btnOptimize.Enabled = false;
        _btnPickBest.Enabled = false;
        try
        {
            var keep = await ProxyTester.EnsurePublicPoolAsync(_cfg, _api, force: true, log: AppendLog);
            _public.Text = string.Join("\r\n", keep);
            UpdatePublicMeta();
            _onPoolChanged();
            AppendLog("完成。用户代理识别成功后仍优先于公共池。");
        }
        catch (Exception ex)
        {
            AppendLog("更新失败：" + ex.Message);
        }
        finally
        {
            _btnOptimize.Enabled = true;
            _btnPickBest.Enabled = true;
        }
    }

    /// <summary>测试当前全部代理（用户层 + 公共层），仅报告不修改。</summary>
    private async Task TestPoolAsync()
    {
        var urls = (_cfg.ProxyUrls ?? new List<string>())
            .Concat(_cfg.PublicProxyUrls ?? new List<string>())
            .Distinct().ToList();
        if (urls.Count == 0)
        {
            AppendLog("当前没有任何代理可测（用户池与公共池均为空）");
            return;
        }
        _btnPickBest.Enabled = false;
        try
        {
            AppendLog($"开始测试 {urls.Count} 个代理（8 并发 × 12s 超时）…");
            var best = await ProxyTester.ProbeAsync(urls, 8, 12, AppendLog);
            AppendLog(best.Count > 0
                ? $"测试完成：可用 {best.Count} 个，最优 {best[0].Url}（{best[0].Ms}ms）"
                : "测试完成：全部不可用");
        }
        finally
        {
            _btnPickBest.Enabled = true;
        }
    }

    private void AppendLog(string msg)
        => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
}
