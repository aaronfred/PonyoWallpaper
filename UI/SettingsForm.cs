using System.Diagnostics;
using System.Net;
using System.Reflection;

namespace PonyoWallpaper;

/// <summary>
/// 设置中心。仅保留通用项；轮换/浏览相关设置已移至主界面底部。
/// 所有选项改动即时保存并生效，无需点「保存」（保存按钮仅用于关闭窗口）。
/// 隐藏式面板（API Key / NSFW 分类开关 / 修改密码）：底部版本号连点 5 次或
/// Ctrl+Shift+K 唤出，唤出前需先通过访问密码验证。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly WallhavenClient _api;
    private readonly Action _onSaved;
    private readonly Action? _onProxyChanged;
    private readonly CacheManager _cache;

    private readonly NumericUpDown _cacheLimit = new();
    private readonly CheckBox _autostart = new();
    private readonly CheckBox _startMinimized = new();
    // v1.2.0：当前代理 = 一个可编辑文本框（显示当前生效代理，也可直接手填）+ 下方等宽状态条
    private readonly TextBox _txtProxy = new();
    private readonly Label _lblProxyInfo = new();
    // v1.4.0 壁纸源 API Key（留空 = 不启用对应源）
    private readonly TextBox _txtUnsplash = new();
    private readonly TextBox _txtPexels = new();
    private readonly TextBox _txtPixabay = new();
    private int? _lastLatency;          // 最近一次实测延迟（null = 未测，-1 = 不可达）
    private readonly System.Windows.Forms.Timer _proxyTimer = new() { Interval = 800 };
    private readonly System.Windows.Forms.Timer _srcKeyTimer = new() { Interval = 800 };
    private bool _applyingProxy;        // 程序回填文本框时抑制其 TextChanged 自动应用
    private const int LatencyThresholdMs = 2000;   // 延迟阈值（实测正常值约 840ms）
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 5000 };
    private readonly ComboBox _theme = new();

    private readonly Panel _hiddenPanel = new();
    private readonly TextBox _apiKey = new();
    private readonly CheckBox _chkShowNsfw = new();
    private readonly Button _btnEye = new();
    private int _versionClicks;
    private bool _loadingValues;   // LoadValues 回填期间抑制控件事件，防止打开设置就触发主窗口重建
    private readonly ToolTip _tips = new();
    private const int BaseHeight = 532;
    private const int HiddenPanelHeight = 116;

    public SettingsForm(AppConfig cfg, WallhavenClient api, CacheManager cache, Action onSaved,
        Action? onProxyChanged = null)
    {
        _cfg = cfg; _api = api; _cache = cache; _onSaved = onSaved; _onProxyChanged = onProxyChanged;

        Text = "Ponyo壁纸 · 设置";
        Width = 560;
        Height = BaseHeight;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        Build();
        LoadValues();
        _uiTimer.Tick += (_, _) => { if (!_txtProxy.Focused) UpdateProxyUi(); };
        _uiTimer.Start();
        // 打开设置页即实测一次当前链路延迟（v1.2.0 需求 4）
        Shown += (_, _) => _ = RefreshLatencyAsync();
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.K) TryRevealHidden();
        };
    }

    private void Build()
    {
        var root = new Panel { Dock = DockStyle.Fill };
        var rows = new Panel { Dock = DockStyle.Fill };

        // 固定坐标行布局，避免 TableLayoutPanel 行错位
        var y = 16;
        y = PlaceRow(rows, y, "缓存配额（MB）", _cacheLimit);

        // 当前代理（v1.2.0 / v1.3.0）：两行，右列各一个按钮。
        //   第一行：可编辑文本框（显示当前生效代理，也可直接手填）+「优选代理」
        //   第二行：状态条（类型 + 延迟）+「恢复默认代理」
        // 内置默认反代（隐藏）在用时，文本框只显示提示语、不暴露地址。
        var proxyPanel = new Panel { Width = 340, Height = 52 };
        _txtProxy.SetBounds(0, 0, 240, 26);   // 与上方「缓存配额」等宽，右缘对齐
        _txtProxy.Font = new Font("Microsoft YaHei UI", 9);
        _txtProxy.PlaceholderText = "留空=自动；可手填 socks5://127.0.0.1:7890";
        _txtProxy.TextChanged += (_, _) =>
        {
            if (_applyingProxy || _loadingValues) return;
            _proxyTimer.Stop(); _proxyTimer.Start();   // 停手 800ms 自动应用
        };
        _txtProxy.Leave += (_, _) => { _proxyTimer.Stop(); ApplyProxyInput(); };
        _proxyTimer.Tick += (_, _) => { _proxyTimer.Stop(); ApplyProxyInput(); };

        var btnPickBest = new Button
        {
            Text = "优选代理",
            Bounds = new Rectangle(248, 0, 92, 26),
            Font = new Font("Microsoft YaHei UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        btnPickBest.FlatAppearance.BorderSize = 1;
        btnPickBest.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnPickBest.Click += async (_, _) => await PickBestProxyAsync(btnPickBest);

        // 状态条：与文本框等宽，显示代理类型与实测延迟；超阈值时追加提示
        _lblProxyInfo.SetBounds(0, 28, 240, 24);
        _lblProxyInfo.AutoSize = false;
        _lblProxyInfo.TextAlign = ContentAlignment.MiddleLeft;
        _lblProxyInfo.Font = new Font("Microsoft YaHei UI", 8);
        _lblProxyInfo.ForeColor = Color.FromArgb(105, 105, 105);
        _lblProxyInfo.BackColor = Color.FromArgb(245, 245, 245);

        // 恢复默认代理：清掉自定义反代与手填代理，回到内置默认代理
        var btnRestoreDefault = new Button
        {
            Text = "恢复默认代理",
            Bounds = new Rectangle(248, 28, 92, 24),
            Font = new Font("Microsoft YaHei UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        btnRestoreDefault.FlatAppearance.BorderSize = 1;
        btnRestoreDefault.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnRestoreDefault.Click += (_, _) => RestoreDefaultProxy();

        proxyPanel.Controls.Add(_txtProxy);
        proxyPanel.Controls.Add(btnPickBest);
        proxyPanel.Controls.Add(_lblProxyInfo);
        proxyPanel.Controls.Add(btnRestoreDefault);
        y = PlaceRow(rows, y, "当前代理", proxyPanel, 52);
        proxyPanel.Size = new Size(340, 52);
        _tips.SetToolTip(btnPickBest, "实测各条链路并自动切换到最快的一级（内置默认代理不参与优选）");
        _tips.SetToolTip(_txtProxy, "显示当前生效的代理；可直接手填（socks5:// 或 http://，可含 user:pass@）并自动锁定使用；清空则回到自动链路");
        _tips.SetToolTip(btnRestoreDefault, "清除自定义反代与手填代理，回到内置默认代理（原配置将被覆盖）");

        // v1.4.0 壁纸源 API Key：三个需注册的源，填了才会在首页源菜单中可用。
        // 免注册源（Bing 每日壁纸 / 360 壁纸 / Picsum）无需配置，始终可用。
        var srcPanel = new Panel { Width = 340, Height = 96 };
        var srcKeys = new (string Label, TextBox Box, Action<string> Set, Func<string> Get)[]
        {
            ("Unsplash", _txtUnsplash, v => _cfg.UnsplashKey = v, () => _cfg.UnsplashKey),
            ("Pexels",   _txtPexels,   v => _cfg.PexelsKey   = v, () => _cfg.PexelsKey),
            ("Pixabay",  _txtPixabay,  v => _cfg.PixabayKey  = v, () => _cfg.PixabayKey),
        };
        for (var i = 0; i < srcKeys.Length; i++)
        {
            var (label, box, set, get) = srcKeys[i];
            var yy = i * 32;
            var lb = new Label
            {
                Text = label,
                Font = new Font("Microsoft YaHei UI", 9),
                ForeColor = Color.FromArgb(105, 105, 105),
                Bounds = new Rectangle(0, yy + 4, 72, 22),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            box.SetBounds(76, yy, 264, 26);
            box.Font = new Font("Microsoft YaHei UI", 9);
            box.UseSystemPasswordChar = true;
            box.PlaceholderText = "留空 = 不启用该源";
            box.TextChanged += (_, _) =>
            {
                if (_loadingValues) return;
                _srcKeyTimer.Stop(); _srcKeyTimer.Start();
            };
            srcPanel.Controls.Add(lb);
            srcPanel.Controls.Add(box);
        }
        _srcKeyTimer.Tick += (_, _) => { _srcKeyTimer.Stop(); SaveSourceKeys(); };
        foreach (var (_, box, _, _) in srcKeys)
            box.Leave += (_, _) => { _srcKeyTimer.Stop(); SaveSourceKeys(); };
        y = PlaceRow(rows, y, "壁纸源 Key", srcPanel, 96);
        srcPanel.Size = new Size(340, 96);
        _tips.SetToolTip(_txtUnsplash, "Unsplash Access Key；留空则在首页源菜单中不可用（免费额度 50 次/小时）");
        _tips.SetToolTip(_txtPexels, "Pexels API Key；留空则不可用（免费额度 200 次/小时）");
        _tips.SetToolTip(_txtPixabay, "Pixabay API Key；留空则不可用（免费额度 100 次/60 秒）");

        y = PlaceRow(rows, y, "开机自启", _autostart);
        y = PlaceRow(rows, y, "启动最小化到托盘", _startMinimized);
        y = PlaceRow(rows, y, "界面主题", _theme);

        // 缓存清理 + 完全清理（同一行两个按钮）
        var btnClearCache = new Button
        {
            Text = "清理缓存…",
            Dock = DockStyle.Left,
            Width = 110,
            Height = 28,
            Font = new Font("Microsoft YaHei UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        btnClearCache.FlatAppearance.BorderSize = 1;
        btnClearCache.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnClearCache.Click += (_, _) => ClearCache();
        var btnWipe = new Button
        {
            Text = "完全清理…",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        btnWipe.FlatAppearance.BorderSize = 1;
        btnWipe.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnWipe.Click += (_, _) => CompleteWipe();
        var clearPanel = new Panel { Width = 240, Height = 28 };
        clearPanel.Controls.Add(btnWipe);
        clearPanel.Controls.Add(btnClearCache);
        y = PlaceRow(rows, y, "清理", clearPanel);

        // 缓存地址：打开文件夹 / 更换文件夹
        var cacheBtnPanel = new Panel { Width = 240, Height = 28 };
        var btnOpenCache = new Button
        {
            Text = "打开文件夹",
            Dock = DockStyle.Left,
            Width = 116,
            Font = new Font("Microsoft YaHei UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        btnOpenCache.FlatAppearance.BorderSize = 1;
        btnOpenCache.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnOpenCache.Click += (_, _) => OpenCacheFolder();
        var btnCachePath = new Button
        {
            Text = "更换文件夹…",
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        btnCachePath.FlatAppearance.BorderSize = 1;
        btnCachePath.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnCachePath.Click += (_, _) => ChangeCachePath();
        cacheBtnPanel.Controls.Add(btnCachePath);
        cacheBtnPanel.Controls.Add(btnOpenCache);
        y = PlaceRow(rows, y, "缓存地址", cacheBtnPanel);

        // 即时保存并生效
        _cacheLimit.Minimum = 100; _cacheLimit.Maximum = 10240;
        _cacheLimit.ValueChanged += (_, _) =>
        {
            _cfg.CacheLimitMb = (int)_cacheLimit.Value;
            _cache.SetLimitMb(_cfg.CacheLimitMb);
            _cfg.Save();
        };
        _autostart.CheckedChanged += (_, _) =>
        {
            if (_autostart.Checked) AutostartHelper.Enable();
            else AutostartHelper.Disable();
        };
        _startMinimized.CheckedChanged += (_, _) =>
        {
            _cfg.StartMinimized = _startMinimized.Checked;
            _cfg.Save();
        };
        _theme.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingValues) return; // 回填不算用户操作
            _cfg.Theme = _theme.SelectedIndex < 0 ? 0 : _theme.SelectedIndex;
            _cfg.Save();
            ThemeManager.Apply(this, ThemeManager.ShouldUseDark(_cfg.Theme));
            _onSaved(); // 主窗口主题 + 托盘提示同步
        };

        // 隐藏面板（默认不可见，密码验证通过后展开）
        _hiddenPanel.Dock = DockStyle.Bottom;
        _hiddenPanel.Height = 0;
        _hiddenPanel.Visible = false;

        var lblApiKey = new Label { Text = "API Key", AutoSize = true, Location = new Point(16, 12), Font = new Font("Microsoft YaHei UI", 9) };
        _apiKey.Location = new Point(90, 8);
        _apiKey.Size = new Size(250, 26);
        _apiKey.UseSystemPasswordChar = true;
        _apiKey.Leave += (_, _) =>
        {
            if (_cfg.ApiKey == _apiKey.Text.Trim()) return;
            _cfg.ApiKey = _apiKey.Text.Trim();
            _api.SetApiKey(_cfg.ApiKey);
            _cfg.Save();
        };
        _btnEye.Text = "显示";
        _btnEye.Location = new Point(348, 7);
        _btnEye.Size = new Size(60, 26);
        _btnEye.Font = new Font("Microsoft YaHei UI", 8);
        _btnEye.FlatStyle = FlatStyle.Flat;
        _btnEye.FlatAppearance.BorderSize = 1;
        _btnEye.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        _btnEye.Click += (_, _) =>
        {
            _apiKey.UseSystemPasswordChar = !_apiKey.UseSystemPasswordChar;
            _btnEye.Text = _apiKey.UseSystemPasswordChar ? "显示" : "隐藏";
        };

        _chkShowNsfw.Text = "首页显示 NSFW 分类（Sketchy / NSFW 两个子分类，需 API Key）";
        _chkShowNsfw.Location = new Point(16, 44);
        _chkShowNsfw.AutoSize = true;
        _chkShowNsfw.Font = new Font("Microsoft YaHei UI", 9);
        _chkShowNsfw.CheckedChanged += (_, _) =>
        {
            if (_loadingValues) return; // 回填不算用户操作，否则打开设置就会重建主窗口
            _cfg.ShowNsfw = _chkShowNsfw.Checked;
            _cfg.Save();
            _onSaved(); // 主窗口分类树立即显示/隐藏 NSFW 入口
        };

        var lblPwd = new Label { Text = "入口密码", AutoSize = true, Location = new Point(16, 80), Font = new Font("Microsoft YaHei UI", 9) };
        var btnChangePwd = new Button
        {
            Text = "修改密码…",
            Location = new Point(90, 74),
            Size = new Size(96, 28),
            Font = new Font("Microsoft YaHei UI", 8.5f),
            FlatStyle = FlatStyle.Flat
        };
        btnChangePwd.FlatAppearance.BorderSize = 1;
        btnChangePwd.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnChangePwd.Click += (_, _) => ChangeHiddenPassword();

        _hiddenPanel.Controls.AddRange(new Control[]
        {
            lblApiKey, _apiKey, _btnEye, _chkShowNsfw, lblPwd, btnChangePwd
        });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52 };
        var versionLbl = new Label
        {
            // 版本号必须读 InformationalVersion（来自 csproj 的 <Version>）；
            // Assembly.GetName().Version 是 AssemblyVersion（默认恒为 1.0.0.0），此前读错显示 v1.0.0
            Text = $"Ponyo壁纸 v{VersionText()}",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8),
            ForeColor = Color.FromArgb(170, 170, 170),
            Location = new Point(16, 20)
        };
        versionLbl.Click += (_, _) =>
        {
            _versionClicks++;
            if (_versionClicks >= 5) { _versionClicks = 0; TryRevealHidden(); }
        };
        var btnSave = new Button
        {
            Text = "完成",
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            Location = new Point(Width - 96, 14),
            Size = new Size(80, 28),
            BackColor = Color.FromArgb(216, 90, 48),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (_, _) => Close();
        var btnHosts = new Button
        {
            Text = "代理管理…",
            Location = new Point(110, 13),
            Size = new Size(96, 26),
            Font = new Font("Microsoft YaHei UI", 8),
            FlatStyle = FlatStyle.Flat
        };
        btnHosts.FlatAppearance.BorderSize = 1;
        btnHosts.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnHosts.Click += (_, _) => new ProxyManagerForm(_cfg, _api, () =>
        {
            // 代理池在管理页被修改：即时生效（API 客户端 + 缩略图客户端）
            _api.SetProxies(_cfg.ProxyUrls, _cfg.ProxyUser, _cfg.ProxyPassword);
            _onProxyChanged?.Invoke();
        }).ShowDialog(this);
        var btnOpenLogs = new Button
        {
            Text = "打开日志",
            Location = new Point(216, 13),
            Size = new Size(86, 26),
            Font = new Font("Microsoft YaHei UI", 8),
            FlatStyle = FlatStyle.Flat
        };
        btnOpenLogs.FlatAppearance.BorderSize = 1;
        btnOpenLogs.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnOpenLogs.Click += (_, _) => OpenLogsFolder();
        footer.Controls.Add(versionLbl);
        footer.Controls.Add(btnHosts);
        footer.Controls.Add(btnOpenLogs);
        footer.Controls.Add(btnSave);

        root.Controls.Add(_hiddenPanel);
        root.Controls.Add(rows);
        Controls.Add(root);
        Controls.Add(footer);

        // 悬停提示：功能 + 快捷键
        _tips.SetToolTip(btnHosts, "代理管理（代理池 / 更新 / 源地址）与 hosts 管理");
        _tips.SetToolTip(btnOpenLogs, "打开日志文件夹（ hosts 更新、轮换等操作的详细日志）");
        _tips.SetToolTip(btnSave, "所有改动已即时保存，此按钮仅关闭窗口");
        _tips.SetToolTip(_cacheLimit, "本地缓存配额，超出按最近最少使用淘汰，改动立即生效");
        _tips.SetToolTip(btnClearCache, "清空全部原图与缩略图缓存，收藏不受影响");
        _tips.SetToolTip(btnCachePath, "打开并修改壁纸缓存目录，改动重启后生效");
        _tips.SetToolTip(_theme, "切换后立即应用到全部窗口");
        _tips.SetToolTip(_apiKey, "wallhaven API Key，NSFW 分类必需；离开输入框自动保存");
        _tips.SetToolTip(_btnEye, "点击显示/隐藏 API Key 明文");
        _tips.SetToolTip(_chkShowNsfw, "勾选后左侧分类树立即显示 NSFW 入口（打开需密码）");
    }

    /// <summary>清空全部缓存（确认后执行，报告释放空间）。</summary>
    private void ClearCache()
    {
        var mb = _cache.TotalBytes() / 1024.0 / 1024.0;
        var msg = $"清空全部缓存（原图 + 缩略图，当前约 {mb:F0} MB）？\n收藏记录不受影响，缩略图浏览时会自动重新下载。";
        if (MessageBox.Show(msg, "清理缓存", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK) return;

        var freed = _cache.ClearAll();
        MessageBox.Show($"已释放 {freed / 1024.0 / 1024.0:F1} MB", "完成",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 完全清理：删除全部数据并恢复出厂状态（配置 / 历史 / 收藏 / 黑名单 / 缓存 / 日志），
    /// 完成后自动重启（新配置按默认值重新生成）。两次确认，不可恢复。
    /// </summary>
    private void CompleteWipe()
    {
        var r = MessageBox.Show(
            "完全清理将删除本程序的全部数据并恢复出厂状态：\n\n" +
            "· 全部设置（代理、hosts 地址、API Key、访问密码等）\n" +
            "· 更换历史、收藏、黑名单\n" +
            "· 本地缓存（原图 + 缩略图）\n" +
            "· 日志文件\n\n" +
            $"数据目录：{AppPaths.Root}\n" +
            (string.IsNullOrWhiteSpace(_cfg.CacheDirPath)
                ? ""
                : $"自定义缓存目录：{_cfg.CacheDirPath}\n") +
            "\n删除后程序将自动重启。此操作不可恢复，确定继续吗？",
            "完全清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;

        if (MessageBox.Show("再次确认：真的要删除全部数据吗？", "完全清理",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        // 尽力删除：个别被占用的文件（如当前日志）自动跳过
        TryDeleteDir(AppPaths.Root);
        if (!string.IsNullOrWhiteSpace(_cfg.CacheDirPath))
            TryDeleteDir(_cfg.CacheDirPath);

        MessageBox.Show("已清空全部数据，程序即将重启。", "完成",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        Application.Restart();
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* 被占用的文件（如当前日志）跳过 */ }
    }

    /// <summary>固定坐标行：标签 (16, y+8)，控件 (170, y, 240×28)。</summary>
    /// <summary>读取版本号：取程序集 InformationalVersion（csproj &lt;Version&gt; 的单一来源）。
    /// 不能用 Assembly.GetName().Version——那是 AssemblyVersion（默认恒 1.0.0.0）。</summary>
    internal static string VersionText()
    {
        var info = typeof(Program).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "1.0";
        var plus = info.IndexOf('+');
        if (plus > 0) info = info[..plus];
        return info;
    }

    /// <summary>固定坐标行布局（避免 TableLayoutPanel 行错位）。返回下一行的 y，支持变高行。</summary>
    private static int PlaceRow(Panel root, int y, string label, Control ctrl, int height = 28)
    {
        root.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Location = new Point(16, y + 8),
            Font = new Font("Microsoft YaHei UI", 9)
        });
        ctrl.Location = new Point(170, y);
        ctrl.Size = new Size(240, height);
        ctrl.Font = new Font("Microsoft YaHei UI", 9);
        root.Controls.Add(ctrl);
        return y + height + 12;
    }

    private void LoadValues()
    {
        _loadingValues = true;
        try
        {
            _theme.Items.AddRange(new object[] { "跟随系统", "浅色", "深色" });

            _cacheLimit.Value = Math.Clamp(_cfg.CacheLimitMb, 100, 10240);
            _autostart.Checked = AutostartHelper.IsEnabled();
            _startMinimized.Checked = _cfg.StartMinimized;
            _txtProxy.Text = _cfg.ManualProxy ?? "";
            _theme.SelectedIndex = Math.Clamp(_cfg.Theme, 0, 2);
            _apiKey.Text = _cfg.ApiKey;
            _chkShowNsfw.Checked = _cfg.ShowNsfw;
            _txtUnsplash.Text = _cfg.UnsplashKey ?? "";
            _txtPexels.Text = _cfg.PexelsKey ?? "";
            _txtPixabay.Text = _cfg.PixabayKey ?? "";
        }
        finally { _loadingValues = false; }
    }

    /// <summary>保存三个壁纸源 API Key（停手 800ms 或失焦时触发），即时生效。</summary>
    private void SaveSourceKeys()
    {
        if (_loadingValues) return;
        _cfg.UnsplashKey = _txtUnsplash.Text.Trim();
        _cfg.PexelsKey = _txtPexels.Text.Trim();
        _cfg.PixabayKey = _txtPixabay.Text.Trim();
        _cfg.Save();
    }

    /// <summary>打开日志文件夹（查看 hosts 更新等操作的详细日志）。</summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _uiTimer.Stop();
        _uiTimer.Dispose();
    }

    /// <summary>
    /// 优选代理：从现有链路（直连 / 反代 / 用户代理 / 公共池）并发实测，切换到最快且可用的一级。
    /// 不抓取任何新代理，速度远快于「更新代理池」。
    /// </summary>
    /// <summary>刷新「当前代理」行文字：层级 · 名称 · 延迟（延迟为空时只显示前两项）。</summary>
    /// <summary>
    /// 刷新代理区：文本框显示当前生效的代理地址（用户正在输入时不覆盖），
    /// 状态条显示「类型 + 延迟」，超阈值时追加提示。
    /// </summary>
    private void UpdateProxyUi()
    {
        // 内置默认反代在用时，文本框留空只显示提示语 —— 不把地址摆到界面上（避免被抄走滥用）
        _txtProxy.PlaceholderText = _api.UsingDefaultMirror
            ? "当前使用默认代理 · 填自己的代理可覆盖"
            : "留空=自动；可手填 socks5://127.0.0.1:7890";
        if (!_txtProxy.Focused)
        {
            _applyingProxy = true;
            var addr = _api.ActiveProxyAddress;   // 默认反代返回空串 → 走占位提示
            if (_txtProxy.Text != addr) _txtProxy.Text = addr;
            _applyingProxy = false;
        }
        var lat = _lastLatency switch
        {
            null => "未测",
            -1 => "不可达",
            int v => v + " ms"
        };
        var bad = _lastLatency == null || _lastLatency == -1 || _lastLatency > LatencyThresholdMs;
        // 状态条宽 240px：提示语用短版，实测最长组合（默认代理 + 4 位延迟 + 提示）≈ 231px 不溢出
        _lblProxyInfo.Text = $"类型：{_api.ActiveTier}    延迟：{lat}"
            + (bad ? "    ⚠ 建议优选" : "");
        _lblProxyInfo.ForeColor = bad ? Color.FromArgb(196, 90, 48) : Color.FromArgb(105, 105, 105);
    }

    /// <summary>
    /// 恢复默认代理：清掉自定义反代 + 手填代理，回到内置默认反代（隐藏资源）。
    /// </summary>
    private void RestoreDefaultProxy()
    {
        _applyingProxy = true;
        try
        {
            _txtProxy.Text = "";
            _cfg.ManualProxy = "";
            _cfg.ManualProxyLocked = false;
            _cfg.CfProxyUrl = "";
            _cfg.CfProxyUrls = null;   // 空 = 用内置默认反代
            _cfg.Save();
        }
        finally { _applyingProxy = false; }

        _api.SetManualProxy("", false);
        _api.SetMirrors(null);
        _onProxyChanged?.Invoke();
        _lastLatency = null;
        _ = RefreshLatencyAsync();
        _lblProxyInfo.Text = "已恢复默认代理";
    }

    /// <summary>实测当前链路延迟（内部连测 2 次取较小值，抗偶发尖峰）并刷新状态条。</summary>
    private async Task RefreshLatencyAsync()
    {
        UpdateProxyUi();
        var ms = await _api.MeasureActiveLatencyAsync();
        if (IsDisposed) return;
        _lastLatency = ms ?? -1;   // -1 = 不可达
        UpdateProxyUi();
    }

    /// <summary>
    /// 应用文本框里的代理（手填后自动触发：停手 800ms 或失焦）。
    /// 有值 → 实测后锁定使用；不可用 → 不静默降级，状态条提示可去获取公共代理；
    /// 空值 → 退出锁定、回到自动链路（替代原「恢复自动」按钮）。
    /// </summary>
    private async Task ApplyProxyInput()
    {
        if (_applyingProxy || _loadingValues) return;
        var url = _txtProxy.Text.Trim();

        if (url.Length == 0)
        {
            if (_cfg.ManualProxyLocked || (_cfg.ManualProxy ?? "").Length > 0)
            {
                _cfg.ManualProxy = "";
                _cfg.ManualProxyLocked = false;
                _cfg.Save();
                _api.SetManualProxy("", false);
                _onProxyChanged?.Invoke();
            }
            await RefreshLatencyAsync();
            return;
        }

        _lblProxyInfo.Text = "正在测试该代理…";
        var r = await ProxyTester.ProbeAsync(new[] { url }, 1, 6);
        if (IsDisposed) return;

        _cfg.ManualProxy = url;
        if (r.Count == 0)
        {
            _cfg.ManualProxyLocked = false;
            _cfg.Save();
            _api.SetManualProxy(url, false);
            _lastLatency = -1;
            UpdateProxyUi();
            _lblProxyInfo.Text = "类型：手填代理    延迟：不可用    ⚠ 换默认";
            _lblProxyInfo.ForeColor = Color.FromArgb(196, 90, 48);
            return;
        }

        _cfg.ManualProxyLocked = true;
        _cfg.Save();
        _api.SetManualProxy(url, true);
        _onProxyChanged?.Invoke();
        await RefreshLatencyAsync();
    }

    private async Task PickBestProxyAsync(Button btn)
    {
        var old = btn.Text;
        btn.Enabled = false;
        btn.Text = "优选中…";
        _lblProxyInfo.Text = "正在实测各条链路…";
        try
        {
            var r = await _api.PickBestAsync();
            if (r == null)
            {
                if (_api.UsingDefaultMirror)
                {
                    // 默认代理不参与优选：此时"优选不到"= 其他链路实测都不可用，继续用默认代理即可
                    await RefreshLatencyAsync();
                    MessageBox.Show(
                        "内置默认代理不参与优选。\n\n" +
                        "其他链路（直连 / 自有反代 / 用户代理 / 公共池）实测均不可用，" +
                        "将继续使用默认代理，无需处理。",
                        "优选代理", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _lastLatency = -1;
                UpdateProxyUi();
                MessageBox.Show(
                    "当前所有链路（直连 / 反代 / 用户代理 / 公共池）都无法访问 wallhaven。\n\n" +
                    "建议：点「恢复默认代理」使用内置默认代理，或在「代理管理」中填入自己的反代地址。",
                    "优选代理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 优选 = 自动功能：切到最快一级后把结果回填文本框（用户仍可再手改）
            _cfg.ManualProxyLocked = false;
            _cfg.ManualProxy = "";
            _cfg.Save();
            _lastLatency = r.Value.Ms;
            UpdateProxyUi();
            _onProxyChanged?.Invoke();
        }
        finally
        {
            btn.Enabled = true;
            btn.Text = old;
        }
    }

    private void OpenLogsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDir}\""));
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开日志文件夹失败：" + ex.Message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>打开当前缓存目录。</summary>
    private void OpenCacheFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.CacheDir}\""));
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开缓存目录失败：" + ex.Message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>修改缓存目录（FolderBrowserDialog；改动重启后生效）。</summary>
    private void ChangeCachePath()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择壁纸缓存目录（原图 + 缩略图存放位置）。\n改动将在重启程序后生效。",
            SelectedPath = AppPaths.CacheDir,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var newPath = dlg.SelectedPath.TrimEnd('\\', '/');
        var current = Path.GetFullPath(AppPaths.CacheDir).TrimEnd('\\', '/');
        if (string.Equals(newPath, current, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("新目录与当前目录相同", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cfg.CacheDirPath = newPath;
        _cfg.Save();
        MessageBox.Show($"缓存地址已保存：{newPath}\n重启程序后生效。", "完成",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>隐藏面板唤出入口：无密码时先引导设置密码，已设置则验证后展开。</summary>
    private void TryRevealHidden()
    {
        if (_hiddenPanel.Visible) return;

        // 首次使用（未设置过密码）：引导用户自行设置，无出厂密码
        if (!HiddenAuth.HasPassword(_cfg.HiddenPasswordHash))
        {
            using var setDlg = new SetPasswordDialog();
            if (setDlg.ShowDialog(this) != DialogResult.OK)
                return;
            if (string.IsNullOrWhiteSpace(setDlg.NewPassword))
            {
                MessageBox.Show("密码不能为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (setDlg.NewPassword != setDlg.ConfirmPassword)
            {
                MessageBox.Show("两次输入的密码不一致", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _cfg.HiddenPasswordHash = HiddenAuth.Hash(setDlg.NewPassword);
            _cfg.Save();
            HiddenAuth.Unlock();
            RevealHiddenPanel();
            MessageBox.Show("密码已设置（可在隐藏面板中修改）", "完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new PasswordDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        if (!HiddenAuth.Verify(dlg.Password, _cfg.HiddenPasswordHash))
        {
            MessageBox.Show("密码错误", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 关键修复：此前密码验证通过后漏了 Unlock，导致树的条件 (ShowNsfw && Unlocked)
        // 永远不满足 → 勾选「首页显示 NSFW」首页也不出现。密码通过即视为本会话解锁；
        // 最小化/收起主窗口/重启仍会重新上锁（保持原有的防误触三重保障）。
        HiddenAuth.Unlock();
        _onSaved(); // 若 ShowNsfw 此前已勾选，立即重建分类树显示 NSFW 入口

        RevealHiddenPanel();
    }

    /// <summary>展开隐藏面板（密码验证/首次设置通过后调用）。</summary>
    private void RevealHiddenPanel()
    {
        _hiddenPanel.Visible = true;
        _hiddenPanel.Height = HiddenPanelHeight;
        Height = BaseHeight + HiddenPanelHeight;
        _apiKey.Focus();
    }

    /// <summary>修改隐藏访问密码：验证旧密码 → 新密码两次一致 → 写入哈希并立即保存。</summary>
    private void ChangeHiddenPassword()
    {
        using var dlg = new ChangePasswordDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (!HiddenAuth.Verify(dlg.OldPassword, _cfg.HiddenPasswordHash))
        {
            MessageBox.Show("旧密码不正确", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(dlg.NewPassword))
        {
            MessageBox.Show("新密码不能为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (dlg.NewPassword != dlg.ConfirmPassword)
        {
            MessageBox.Show("两次输入的新密码不一致", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cfg.HiddenPasswordHash = HiddenAuth.Hash(dlg.NewPassword);
        _cfg.Save();
        // 改密者本人刚通过旧密码验证、且新密码是其本人设置 —— 保持本会话解锁。
        // 若此处 Lock，已勾选的 NSFW 入口会当场消失且需再次输密码，体验割裂。
        HiddenAuth.Unlock();
        MessageBox.Show("密码已修改", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }



    private static string Short(string msg)
    {
        msg = msg.Replace("\r", " ").Replace("\n", " ");
        return msg.Length <= 160 ? msg : msg[..157] + "…";
    }

    private Cursor? _cursor;
}