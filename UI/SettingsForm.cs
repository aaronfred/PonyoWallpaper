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
    // v1.5.8：手填代理框（代理管理）已移除 —— 自定义代理改在配置文件 ManualProxy 字段填写
    // （见「代理填写指南」），设置页只保留状态展示 + 恢复默认代理 + 指南入口。
    private readonly Label _lblProxyInfo = new();
    // v1.4.0 壁纸源 API Key 已随需 Key 源一并移除（v1.5.0）
    private int? _lastLatency;          // 最近一次实测延迟（null = 未测，-1 = 不可达）
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
    private const int BaseHeight = 436;
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
        _uiTimer.Tick += (_, _) => UpdateProxyUi();
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

        // 当前代理（v1.5.8 重排）：手填代理框已移除，本区只剩展示与两个入口。
        //   第一行：状态条（类型 + 延迟，占满左列）+「恢复默认代理」
        //   第二行：「代理填写指南」（代理示例 / 反代搭建 / 代码，打开内嵌 txt）
        // 内置默认反代（隐藏）在用时，状态条只报「默认代理」，不暴露地址。
        var proxyPanel = new Panel { Width = 340, Height = 52 };

        // 代理填写指南：各种代理填写示例（HTTP / SOCKS5 / 配置文件字段）+ 反代搭建指南 + 示例代码
        var btnGuide = new Button
        {
            Text = "代理填写指南（示例 · 反代搭建 · 代码）",
            Bounds = new Rectangle(0, 28, 240, 24),
            Font = new Font("Microsoft YaHei UI", 8.25f),
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleCenter
        };
        btnGuide.FlatAppearance.BorderSize = 1;
        btnGuide.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnGuide.Click += (_, _) => OpenProxyGuide();

        // 状态条：与文本框原位等宽，显示代理类型与实测延迟；超阈值时追加提示。
        // Tag="self"：颜色由 UpdateProxyUi 按状态+主题自管，ThemeManager 不接管（否则深色下白底刺眼）
        _lblProxyInfo.SetBounds(0, 0, 240, 26);
        _lblProxyInfo.AutoSize = false;
        _lblProxyInfo.TextAlign = ContentAlignment.MiddleLeft;
        _lblProxyInfo.Font = new Font("Microsoft YaHei UI", 8);
        _lblProxyInfo.Tag = "self";
        _lblProxyInfo.ForeColor = Color.FromArgb(105, 105, 105);
        _lblProxyInfo.BackColor = Color.FromArgb(245, 245, 245);

        // 恢复默认代理：清掉自定义反代与配置文件里的手填代理，回到内置默认代理
        var btnRestoreDefault = new Button
        {
            Text = "恢复默认代理",
            Bounds = new Rectangle(248, 0, 92, 26),
            Font = new Font("Microsoft YaHei UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        btnRestoreDefault.FlatAppearance.BorderSize = 1;
        btnRestoreDefault.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnRestoreDefault.Click += (_, _) => RestoreDefaultProxy();

        proxyPanel.Controls.Add(btnGuide);
        proxyPanel.Controls.Add(_lblProxyInfo);
        proxyPanel.Controls.Add(btnRestoreDefault);
        y = PlaceRow(rows, y, "当前代理", proxyPanel, 52);
        proxyPanel.Size = new Size(340, 52);
        _tips.SetToolTip(btnGuide, "各种代理填写示例（含配置文件字段写法）、Cloudflare Worker / Caddy / Nginx 反代搭建指南与代码");
        _tips.SetToolTip(btnRestoreDefault, "清除自定义反代与手填代理（配置文件 ManualProxy），回到内置默认代理");

        // v1.5.0：需要 API Key 的壁纸源（Unsplash / Pexels / Pixabay）已移除，
        // 现源为 wallhaven / 360 壁纸 / WallpaperCave，均免注册，故不再有「壁纸源 Key」行。

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
            UpdateProxyUi();   // 状态条颜色自管（Tag="self"），切主题后立即按新主题刷新
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
            Size = new Size(80, 28),
            BackColor = Color.FromArgb(216, 90, 48),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        // 不可用 Anchor：footer 尚未入父时宽为默认 200px，锚点基线按 200px 计算，
        // footer 被 Dock 撑到实际宽度后锚点重算会把按钮推出窗外（v1.4.2 实测「完成」不可见）。
        // 改为跟随 footer 宽度定位，任何时候都贴右缘。
        void PlaceSave() => btnSave.Location = new Point(Math.Max(0, footer.Width - 96), 14);
        footer.Resize += (_, _) => PlaceSave();
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (_, _) => Close();
        var btnOpenLogs = new Button
        {
            Text = "打开日志",
            Location = new Point(124, 13),   // 版本号标签 AutoSize 实宽到 x≈112，留 12px 间距（原 110 重叠 2px）
            Size = new Size(86, 26),
            Font = new Font("Microsoft YaHei UI", 8),
            FlatStyle = FlatStyle.Flat
        };
        btnOpenLogs.FlatAppearance.BorderSize = 1;
        btnOpenLogs.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btnOpenLogs.Click += (_, _) => OpenLogsFolder();
        footer.Controls.Add(versionLbl);
        footer.Controls.Add(btnOpenLogs);
        footer.Controls.Add(btnSave);

        root.Controls.Add(_hiddenPanel);
        root.Controls.Add(rows);
        Controls.Add(root);
        Controls.Add(footer);
        PlaceSave();   // footer 已 Dock 到实际宽度，立即贴右缘（Resize 事件只兜后续变化）

        // 悬停提示：功能 + 快捷键
        _tips.SetToolTip(btnOpenLogs, "打开日志文件夹（轮换等操作的详细日志）");
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
            _theme.SelectedIndex = Math.Clamp(_cfg.Theme, 0, 2);
            _apiKey.Text = _cfg.ApiKey;
            _chkShowNsfw.Checked = _cfg.ShowNsfw;
        }
        finally { _loadingValues = false; }
    }

    /// <summary>保存三个壁纸源 API Key（停手 800ms 或失焦时触发），即时生效。</summary>
    /// <summary>打开日志文件夹（查看 hosts 更新等操作的详细日志）。</summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _uiTimer.Stop();
        _uiTimer.Dispose();
    }

    /// <summary>
    /// 刷新代理区状态条：显示「类型 + 延迟」，超阈值时追加提示。
    /// v1.5.8：手填代理框已移除，本方法只剩状态展示。
    /// </summary>
    private void UpdateProxyUi()
    {
        var lat = _lastLatency switch
        {
            null => "未测",
            -1 => "不可达",
            int v => v + " ms"
        };
        var bad = _lastLatency == null || _lastLatency == -1 || _lastLatency > LatencyThresholdMs;
        // 状态条宽 240px：提示语用短版，实测最长组合（默认代理 + 4 位延迟 + 提示）≈ 231px 不溢出
        _lblProxyInfo.Text = $"类型：{_api.ActiveTier}    延迟：{lat}"
            + (bad ? "    ⚠ 恢复默认" : "");
        // 颜色按主题 + 状态自管（Tag="self"，ThemeManager 不接管）；Tag="self" 的标签自带状态色逻辑
        var dark = ThemeManager.LastDark;
        _lblProxyInfo.BackColor = dark ? Color.FromArgb(37, 37, 38) : Color.FromArgb(245, 245, 245);
        _lblProxyInfo.ForeColor = bad
            ? (dark ? Color.FromArgb(235, 140, 90) : Color.FromArgb(196, 90, 48))   // 提示需恢复：橙
            : (dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(105, 105, 105)); // 正常：次级灰
    }

    /// <summary>
    /// 恢复默认代理：清掉自定义反代 + 配置文件里的手填代理，回到内置默认反代（隐藏资源）。
    /// v1.5.8：手填代理框已移除，此按钮是清除 ManualProxy 的唯一 UI 入口。
    /// </summary>
    private void RestoreDefaultProxy()
    {
        _cfg.ManualProxy = "";
        _cfg.ManualProxyLocked = false;
        _cfg.CfProxyUrl = "";
        _cfg.CfProxyUrls = null;   // 空 = 用内置默认反代
        _cfg.Save();

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
    /// 打开「代理填写指南」（各种代理填写示例、反代搭建指南与示例代码）。
    /// 依次尝试 exe 同目录 → docs 子目录；都没有则从内嵌资源释放后再打开。
    /// </summary>
    /// <summary>
    /// v1.5.4：把设置窗**贴着主窗口**显示（在主窗口区域内居中），
    /// 避免出现「弹出框跑到屏幕别处 / 每次位置都不一样」；主窗口隐藏或最小化时退回屏幕工作区居中。
    /// </summary>
    public void ShowAttachedTo(Form owner)
    {
        var b = owner.WindowState == FormWindowState.Normal ? owner.Bounds : owner.RestoreBounds;
        if (!owner.Visible || b.Width <= 0 || b.Height <= 0)
            b = Screen.FromControl(owner).WorkingArea;

        StartPosition = FormStartPosition.Manual;   // 位置由下面每次显式计算（CenterParent 只在首次显示时生效）
        Location = new Point(
            b.Left + Math.Max(0, (b.Width - Width) / 2),
            b.Top + Math.Max(0, (b.Height - Height) / 2));

        Show(owner);
    }

    private void OpenProxyGuide()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "代理指引.txt"),
                Path.Combine(AppContext.BaseDirectory, "docs", "代理指引.txt"),
            };
            var path = candidates.FirstOrDefault(File.Exists);
            if (path != null)
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            // 外部 txt 都不在 → 从内嵌资源释放到 exe 同目录（不可写则退到临时目录），再弹出
            var text = ReadEmbeddedGuide();
            if (text.Length == 0)
            {
                MessageBox.Show("未找到指引：外部 代理指引.txt 与内嵌资源均不可用", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            path = candidates[0];
            try
            {
                File.WriteAllText(path, text, System.Text.Encoding.UTF8);
            }
            catch
            {
                // exe 目录可能不可写（如装在 Program Files）→ 释放到临时目录
                path = Path.Combine(Path.GetTempPath(), "代理指引.txt");
                File.WriteAllText(path, text, System.Text.Encoding.UTF8);
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开指引失败：" + ex.Message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>读取内嵌的代理指引（csproj 里 LogicalName = ponyo-proxy-guide.txt）。</summary>
    private static string ReadEmbeddedGuide()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("ponyo-proxy-guide.txt");
            if (s == null) return "";
            using var r = new StreamReader(s, System.Text.Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch { return ""; }
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



}