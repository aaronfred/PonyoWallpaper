using System.ComponentModel;

namespace PonyoWallpaper;

/// <summary>
/// 主界面（M3）：左侧中文分类树（含收藏 / NSFW）+ 顶部搜索 + 右侧缩略图瀑布流
/// + 底部「自动更换分类多选 + 间隔 | 分辨率/填充/排序/多屏」设置条。
/// 关闭按钮 = 隐藏到托盘，不退出进程。所有网络/图片解码均异步，主线程零阻塞。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppConfig _cfg;
    private readonly WallhavenClient _api;
    private readonly SourceRegistry _sources;
    private readonly CacheManager _cache;
    private readonly RotationEngine _engine;
    private readonly HistoryStore _history;
    private readonly ListStore _favorites;
    private readonly ListStore _blacklist;

    private readonly TreeView _tree = new();
    private readonly MasonryPanel _flow = new();
    private bool _treeVisible = true;      // 左侧分类栏当前是否展开（向左靠边折叠）
    private Button _btnTree = new();
    private readonly Label _status = new();
    // 缩略图下载独立客户端：必须与 _api 同样走代理，否则配了代理也仍直连（此前是裸 new HttpClient）
    private HttpClient _thumbHttp;

    // —— 缩略图内存/流量控制（v1.5.3）——
    /// <summary>浏览专用的缩略图宽度（卡片显示 190px，240 留高分屏余量）。</summary>
    private const int ThumbWidth = 240;

    /// <summary>缓存文件宽度超过它就认为存的是原图（旧版本遗留），读到时重新编码成小图并就地覆写（自愈）。</summary>
    private const int ThumbRawThreshold = 480;

    /// <summary>缩略图<b>网络</b>并发（纯 IO，内存占用小）：一屏十几张可以并行拉，别让首屏排队。</summary>
    private readonly SemaphoreSlim _thumbNet = new(6, 6);

    /// <summary>
    /// 缩略图<b>解码/缩放</b>并发（CPU 与内存重）：收紧到 2，避免同时解码多张大图。
    /// v1.5.3 曾用单一闸门 3，结果首屏 24 张要分 8 批串行下载 → 用户反馈「加载慢」；
    /// v1.5.4 拆成「网络宽 + 解码窄」两段，兼顾速度与内存峰值。
    /// </summary>
    private readonly SemaphoreSlim _thumbCpu = new(2, 2);

    private readonly ToolTip _tips = new();

    // 底部设置条控件
    private readonly NumericUpDown _numInterval = new();
    private readonly ComboBox _cboResolution = new();

    /// <summary>
    /// 分辨率档位表（v1.2.0：3 档 → 6 档）。wallhaven 各档实测可用量见 docs/v1.2.0-优化方案.md 2.1
    /// （1080p 278k / 2K 113k / 4K 59k / 5K 17k / 8K 3.7k / 10K 258）。
    /// </summary>
    private static readonly (string Label, string Value)[] ResTiers =
    {
        ("1080p", "1920x1080"), ("2K", "2560x1440"), ("4K", "3840x2160"),
        ("5K", "5120x2880"), ("8K", "7680x4320"), ("10K", "10240x4320"),
    };
    private readonly ComboBox _cboFill = new();
    private readonly ComboBox _cboSorting = new();
    private readonly ComboBox _cboMonitors = new();
    private Button _btnRotScope = new();
    private ToolStripDropDown _rotMenu = new();
    // v1.4.0 壁纸源
    private Button _btnSources = new();
    private ContextMenuStrip _srcMenu = new();
    private bool _buildingSourceMenu;
    private int _buildingRotationMenu;   // RebuildRotationMenu 期间抑制「调整范围触发换壁纸」
    private bool _syncingSelectAll;      // SyncSelectAll 回写「全选」勾选期间，抑制其 CheckedChanged 批处理

    private string _currentChannelKey = "";
    /// <summary>锁定单源浏览时选中的「源自己的分类」id（v1.5.5）；多源混排时为空。</summary>
    private string _currentLocalChannel = "";
    private string _currentCategory = "";
    private bool _nsfwMode;   // NSFW 分类浏览（purity=111，需密码 + API Key）
    private bool _favMode;    // 收藏浏览（本地，无分页）
    private int _page = 1;
    private string _seed = "";            // random 排序的随机种子（换一批用）
    private bool _batchFirstPage = true;  // 当前批次是否刚取完第一页（用于首屏自动续页）
    private bool _loading;
    private bool _ended;
    private int _emptyStreak;   // 连续"无新增"页数：无限下拉翻到头后连续多页全是重复图才停止

    /// <summary>由 Program 注入：打开设置中心（主界面「设置」按钮与托盘共用）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action? OnOpenSettings { get; set; }

    public MainForm(AppConfig cfg, WallhavenClient api, CacheManager cache, RotationEngine engine,
        HistoryStore history, ListStore favorites, ListStore blacklist)
    {
        _cfg = cfg;
        _api = api;
        _sources = new SourceRegistry(api);
        _cache = cache;
        _engine = engine;
        _history = history;
        _favorites = favorites;
        _blacklist = blacklist;

        Text = "Ponyo壁纸";
        Width = 1000;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = IconHelper.LoadAppIcon();

        // 缩略图必须与主 API 走同一套代理配置（含 SOCKS5）；CF 反代启用时直连反代；构造失败降级直连
        HttpMessageHandler thumbHandler;
        if (WallhavenClient.MirrorActive)
        {
            thumbHandler = new HttpClientHandler();
        }
        else try
        {
            thumbHandler = ProxyFactory.Create(cfg.ProxyUrl, cfg.ProxyUser, cfg.ProxyPassword)
                           ?? new HttpClientHandler();
        }
        catch (Exception ex)
        {
            Logger.Warn($"thumb proxy invalid, fallback to direct: {ex.Message}");
            thumbHandler = new HttpClientHandler();
        }
        _thumbHttp = new HttpClient(thumbHandler) { Timeout = TimeSpan.FromSeconds(30) };
        _thumbHttp.DefaultRequestHeaders.Add("User-Agent", "PonyoWallpaper/1.0");

        BuildUi();
        BuildTree();
    }

    /// <summary>代理/反代变更后由设置页回调：重建缩略图客户端，免重启即时生效。
    /// 当前活动链路为直连/反代时缩略图直连即可；为代理时套该代理。</summary>
    public void RebuildThumbProxy()
    {
        HttpMessageHandler handler;
        if (_api.ManualProxyLocked && !string.IsNullOrWhiteSpace(_cfg.ManualProxy))
        {
            // 手填代理锁定：缩略图必须走同一条代理，否则 API 能通但图加载不出来
            try
            {
                handler = ProxyFactory.Create(_cfg.ManualProxy, _cfg.ProxyUser, _cfg.ProxyPassword)
                          ?? new HttpClientHandler();
            }
            catch (Exception ex)
            {
                Logger.Warn($"manual proxy invalid for thumbs, fallback to direct: {ex.Message}");
                handler = new HttpClientHandler();
            }
        }
        else if (_api.ActiveTier is "直连" or "反代")
        {
            handler = new HttpClientHandler();
        }
        else try
        {
            handler = ProxyFactory.Create(_cfg.ProxyUrl, _cfg.ProxyUser, _cfg.ProxyPassword)
                          ?? new HttpClientHandler();
        }
        catch (Exception ex)
        {
            Logger.Warn($"thumb proxy invalid, fallback to direct: {ex.Message}");
            handler = new HttpClientHandler();
        }
        var old = _thumbHttp;
        _thumbHttp = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _thumbHttp.DefaultRequestHeaders.Add("User-Agent", "PonyoWallpaper/1.0");
        Logger.Info(WallhavenClient.MirrorActive
            ? "thumb client rebuilt (cf mirror direct)"
            : "thumb client rebuilt (proxy changed)");
        try { old.Dispose(); } catch { /* 忽略 */ }
    }

    private void BuildUi()
    {
        // 顶部搜索条已移除（按分类词搜索与左侧分类栏重复）。
        // 全部功能按钮集中在底部设置行；「收起分类」可把左侧栏向左折叠，右侧图片区自动扩大。
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(10, 4, 10, 2) };
        _status.Dock = DockStyle.Fill;
        _status.Font = new Font("Microsoft YaHei UI", 9);
        _status.ForeColor = Color.FromArgb(120, 120, 120);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        bottom.Controls.Add(_status);
        bottom.Controls.Add(BuildSettingsRow());

        // 贴边小箭头：常驻分类栏右缘（树收起后贴最左缘），点击向左收起/展开分类栏
        var handle = new Panel { Dock = DockStyle.Left, Width = 16, Padding = new Padding(1, 10, 1, 0) };
        _btnTree = new Button
        {
            Dock = DockStyle.Top,
            Height = 56,
            Text = "◀",
            Font = new Font("Segoe UI Symbol", 8.5f),
            FlatStyle = FlatStyle.Flat
        };
        _btnTree.FlatAppearance.BorderSize = 1;
        _btnTree.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        _btnTree.Click += (_, _) => ToggleTree();
        _tips.SetToolTip(_btnTree, "向左收起/展开左侧分类栏（收起后右侧图片展示区更大）");
        handle.Controls.Add(_btnTree);

        // 左侧分类树
        _tree.Dock = DockStyle.Left;
        _tree.Width = 170;
        _tree.Font = new Font("Microsoft YaHei UI", 9.5f);
        _tree.HideSelection = false;

        // NSFW 分类密码门禁：验证失败或取消则保持原选中节点不变
        _tree.BeforeSelect += (_, e) =>
        {
            if (e.Node?.Tag is not string t || !t.StartsWith("nsfw:")) return;
            if (HiddenAuth.Unlocked) return;

            using var dlg = new PasswordDialog();
            var result = dlg.ShowDialog(this);
            if (result == DialogResult.OK && HiddenAuth.Verify(dlg.Password, _cfg.HiddenPasswordHash))
            {
                HiddenAuth.Unlock();
                return;
            }
            e.Cancel = true;
            _status.Text = result == DialogResult.OK ? "密码错误，无法打开 NSFW 分类" : "已取消打开 NSFW 分类";
        };

        // 浏览选择只影响浏览，不再影响自动更换范围（由底部多选决定）
        _tree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is not string tag) return;
            _currentLocalChannel = "";   // 统一先清空；local: 分支会再赋上源私有分类 id

            if (tag == "fav:all")
            {
                _favMode = true; _nsfwMode = false;
                _currentChannelKey = "fav";
                _currentCategory = "";
                _cfg.Sub = "fav";
            }
            else if (tag.StartsWith("nsfw:", StringComparison.Ordinal))
            {
                // 子分类标签：nsfw:<purity>:<名称>（010=擦边 001=NSFW）
                _favMode = false; _nsfwMode = true;
                _currentChannelKey = tag;
                _cfg.Sub = tag;
            }
            else if (tag.StartsWith("grp:", StringComparison.Ordinal))
            {
                // 一级分类：显示该分类下的全部壁纸（不加 tag 限定）
                _favMode = false; _nsfwMode = false;
                _currentChannelKey = tag;
                _currentCategory = tag[4..].Split(':')[0];
                _cfg.Sub = tag;
            }
            else if (tag.StartsWith("cat:"))
            {
                _favMode = false; _nsfwMode = false;
                _currentChannelKey = "all";
                _currentCategory = tag[4..];
                _cfg.Sub = "all";
            }
            else if (tag.StartsWith("local:", StringComparison.Ordinal))
            {
                // 锁定单源浏览：树上是「源自己的分类」（360 的 cid / WCV 的检索词 / GitHub 的目录词）
                _favMode = false; _nsfwMode = false;
                _currentChannelKey = "local";
                _currentLocalChannel = tag[6..];
                _currentCategory = "100";
                _cfg.Sub = tag;
            }
            else
            {
                var ch = Channels.Find(tag);
                if (ch == null) return;
                _favMode = false; _nsfwMode = false;
                _currentChannelKey = tag;
                _currentLocalChannel = "";     // 走进通用频道 → 清掉源私有分类
                _currentCategory = ch.Category;
                _cfg.Sub = tag;
            }
            _cfg.Save();
            if (_favMode) LoadFavorites();
            else Reload();
            Logger.Info($"browse -> key={_currentChannelKey} nsfw={_nsfwMode} fav={_favMode}");
        };

        // 右侧多列瀑布流（自适应列数 + 不等高 + 缩略图懒加载 + 无限下拉）
        _flow.Dock = DockStyle.Fill;
        _flow.BackColor = Color.FromArgb(245, 245, 247);
        _flow.OnCardVisible += c => _ = LoadThumbAsync(c);
        _flow.OnNearBottom += () => _ = LoadPageAsync();

        // 停靠处理为逆序：bottom(底) → tree(最左) → handle(树右缘) → flow(填充)
        Controls.Add(_flow);
        Controls.Add(handle);
        Controls.Add(_tree);
        Controls.Add(bottom);

        // 滚轮路由：面板无焦点时 WinForms 不会把滚轮消息给它（表现为必须点滚动条才能滚），
        // 用消息过滤器把落在瀑布流区域上的滚轮消息转发给面板
        Application.AddMessageFilter(new WheelRouter { Flow = _flow });
    }

    /// <summary>把瀑布流区域上的鼠标滚轮消息转发给 MasonryPanel（WinForms 无焦点不滚动的通病修复）。</summary>
    private sealed class WheelRouter : IMessageFilter
    {
        public MasonryPanel? Flow;
        private const int WM_MOUSEWHEEL = 0x020A;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL || Flow == null || !Flow.IsHandleCreated) return false;
            var screen = Flow.RectangleToScreen(Flow.ClientRectangle);
            if (!screen.Contains(Cursor.Position)) return false;
            var delta = (short)((long)m.WParam >> 16);
            Flow.ScrollWheel(delta);
            return true; // 已消费，避免重复滚动
        }
    }

    /// <summary>底部设置行（单行）：自动更换范围下拉 + 间隔 + 分辨率/填充/排序/多屏。</summary>
    private Control BuildSettingsRow()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0)
        };

        _btnRotScope = new Button
        {
            Width = 175,
            Height = 26,
            Margin = new Padding(0, 2, 8, 0),
            Font = new Font("Microsoft YaHei UI", 9),
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat
        };
        _btnRotScope.FlatAppearance.BorderSize = 1;
        _btnRotScope.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        _btnRotScope.Click += (_, _) =>
        {
            if (_rotMenu.Visible) _rotMenu.Close();
            else _rotMenu.Show(_btnRotScope, new Point(0, _btnRotScope.Height));
        };
        row.Controls.Add(_btnRotScope);

        // v1.4.0 壁纸源：多选菜单，决定图墙从哪些源取图（频道树只对 wallhaven 生效）
        _btnSources = new Button
        {
            Width = 108,
            Height = 26,
            Margin = new Padding(0, 2, 8, 0),
            Font = new Font("Microsoft YaHei UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        _btnSources.FlatAppearance.BorderSize = 1;
        _btnSources.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        // v1.4.0 壁纸源：多选菜单，决定图墙从哪些源取图。
        // v1.4.1 修复：ContextMenuStrip 默认点任意项就关闭 → 实际只能勾一个。
        // v1.5.3 修复「菜单永久置顶粘屏」：v1.5.0 曾用 AutoClose=false 来实现连续勾选，
        //   但它的语义是「失去焦点也不关闭」→ 菜单会一直浮在所有窗口最上层。
        //   改为 AutoClose=true（点外部/切程序/失焦即收起）+ Closing 事件里取消
        //   「因点击条目而关闭」的情况 → 既能连续勾选，又不会粘屏。
        // 注意：不能设 ShowImageMargin=false —— 勾选标记就画在图像边距列里，
        // 关掉后菜单上看不到任何勾选状态，用户会以为"多选没生效"（v1.5.0 修）
        _srcMenu = new ContextMenuStrip { AutoClose = true };
        _srcMenu.Closing += (_, e) =>
        {
            // 点条目（勾选/取消）时不收起菜单，便于连续多选；其他原因（点外部、Esc、切程序）照常关闭。
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
        };
        _srcMenu.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) _srcMenu.Close(); };
        _srcMenu.Opening += (_, _) => RebuildSourceMenu();
        _btnSources.Click += (_, _) =>
        {
            RebuildSourceMenu();
            _srcMenu.Show(_btnSources, new Point(0, _btnSources.Height));
        };
        row.Controls.Add(_btnSources);

        row.Controls.Add(new Label
        {
            Text = "间隔",
            AutoSize = true,
            Margin = new Padding(4, 6, 2, 0),
            Font = new Font("Microsoft YaHei UI", 9)
        });
        _numInterval.Minimum = 5;
        _numInterval.Maximum = 1440;
        _numInterval.Width = 52;
        _numInterval.Margin = new Padding(0, 3, 2, 0);
        _numInterval.Value = Math.Clamp(_cfg.IntervalMinutes, 5, 1440);
        _numInterval.ValueChanged += (_, _) =>
        {
            _cfg.IntervalMinutes = (int)_numInterval.Value;
            _engine.UpdateInterval(_cfg.IntervalMinutes);
            _cfg.Save();
        };
        row.Controls.Add(_numInterval);
        row.Controls.Add(new Label
        {
            Text = "分",
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 0),
            Font = new Font("Microsoft YaHei UI", 9)
        });

        // 分辨率
        _cboResolution.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboResolution.Width = 72;
        _cboResolution.Margin = new Padding(0, 3, 8, 0);
        _cboResolution.Font = new Font("Microsoft YaHei UI", 9);
        _cboResolution.Items.AddRange(ResTiers.Select(t => (object)t.Label).ToArray());
        var resIdx = Array.FindIndex(ResTiers, t => t.Value == _cfg.Resolution);
        _cboResolution.SelectedIndex = resIdx >= 0 ? resIdx : 1; // 未知值回落 2K
        _cboResolution.SelectedIndexChanged += (_, _) =>
        {
            var i = Math.Clamp(_cboResolution.SelectedIndex, 0, ResTiers.Length - 1);
            _cfg.Resolution = ResTiers[i].Value;
            _cfg.Save();
            if (!_favMode) Reload();
        };
        row.Controls.Add(_cboResolution);

        // 填充
        _cboFill.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboFill.Width = 68;
        _cboFill.Margin = new Padding(0, 3, 8, 0);
        _cboFill.Font = new Font("Microsoft YaHei UI", 9);
        _cboFill.Items.AddRange(new object[] { "填充", "适应", "拉伸", "平铺", "居中" });
        _cboFill.SelectedIndex = _cfg.FillMode switch
        {
            "fit" => 1, "stretch" => 2, "tile" => 3, "center" => 4, _ => 0
        };
        _cboFill.SelectedIndexChanged += (_, _) =>
        {
            _cfg.FillMode = _cboFill.SelectedIndex switch
            {
                1 => "fit", 2 => "stretch", 3 => "tile", 4 => "center", _ => "fill"
            };
            _cfg.Save();
            _engine.ApplyCurrent(); // 填充模式立即生效
        };
        row.Controls.Add(_cboFill);

        // 排序
        _cboSorting.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboSorting.Width = 88;
        _cboSorting.Margin = new Padding(0, 3, 8, 0);
        _cboSorting.Font = new Font("Microsoft YaHei UI", 9);
        _cboSorting.Items.AddRange(new object[] { "随机", "热门", "最新", "浏览最多", "收藏最多" });
        _cboSorting.SelectedIndex = _cfg.Sorting switch
        {
            "toplist" => 1, "date_added" => 2, "views" => 3, "favorites" => 4, _ => 0
        };
        _cboSorting.SelectedIndexChanged += (_, _) =>
        {
            _cfg.Sorting = _cboSorting.SelectedIndex switch
            {
                1 => "toplist", 2 => "date_added", 3 => "views", 4 => "favorites", _ => "random"
            };
            _cfg.Save();
            if (!_favMode) Reload();
        };
        row.Controls.Add(_cboSorting);

        // 多屏
        _cboMonitors.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboMonitors.Width = 104;
        _cboMonitors.Margin = new Padding(0, 3, 0, 0);
        _cboMonitors.Font = new Font("Microsoft YaHei UI", 9);
        _cboMonitors.Items.AddRange(new object[] { "所有屏幕同图", "每屏独立壁纸" });
        _cboMonitors.SelectedIndex = _cfg.Monitors == "independent" ? 1 : 0;
        _cboMonitors.SelectedIndexChanged += (_, _) =>
        {
            _cfg.Monitors = _cboMonitors.SelectedIndex == 1 ? "independent" : "all";
            _cfg.Save();
            _engine.ApplyCurrent(); // 多屏模式立即生效
        };
        row.Controls.Add(_cboMonitors);

        // 功能按钮（原顶部工具条移入）：换一批 / 设置。换一批与多屏之间留出间隔
        var btnShuffle = AddRowButton(row, "换一批", 64, ShuffleBatch, leftMargin: 12);
        _tips.SetToolTip(btnShuffle, "换一批：保证刷出不同的一批图（随机换种子，确定性排序随机跳页）");
        var btnSettings = AddRowButton(row, "设置", 56, () => OnOpenSettings?.Invoke());
        _tips.SetToolTip(btnSettings, "打开设置中心（托盘右键也可进入）");

        // 悬停提示
        _tips.SetToolTip(_btnRotScope, "点击展开频道多选列表（可精确到二级频道），勾选即时生效\n全局快捷键：Ctrl+Alt+N 下一张 / Ctrl+Alt+P 上一张");
        _tips.SetToolTip(_numInterval, "自动更换间隔（5–1440 分钟），保存后立即生效");
        _tips.SetToolTip(_cboResolution, "浏览与自动更换的最低分辨率（1080p / 2K / 4K）");
        _tips.SetToolTip(_cboFill, "桌面壁纸填充方式，切换立即生效");
        _tips.SetToolTip(_cboSorting, "浏览结果的排序方式（切换后自动刷新）");
        _tips.SetToolTip(_cboMonitors, "多显示器壁纸模式，切换立即生效");

        RebuildRotationMenu();
        UpdateSourceButtonText();
        _tips.SetToolTip(_btnSources, "点击展开壁纸源多选列表，可同时勾选多个；勾选即时生效并重新加载\n360 壁纸：国内免注册，按频道映射到自家 18 个分类\nWallpaperCave：国际免注册，按频道关键词检索专辑，只出电脑横屏壁纸\nGitHub 图库：开源壁纸仓库（约 4 千张，按目录分类），取图自动走多条公共镜像/反代\nwallhaven：需代理，频道分类最全（23 频道）");
        return row;
    }

    /// <summary>按钮文字显示已启用源数量，如「壁纸源 3」。</summary>
    private void UpdateSourceButtonText()
    {
        // v1.5.5：锁定浏览源时直接显示源名，让"现在只看哪个源"一眼可见
        if (!string.IsNullOrWhiteSpace(_cfg.BrowseSource)
            && _sources.Find(_cfg.BrowseSource) is { } only)
        {
            _btnSources.Text = $"壁纸源 {only.DisplayName}";
            return;
        }
        _btnSources.Text = $"壁纸源 {_sources.Enabled(_cfg).Count}";
    }

    /// <summary>
    /// 重建壁纸源菜单（v1.5.5）：
    /// 顶部「浏览源」单选（全部 / 各源）→ 决定左侧分类树显示什么；
    /// 分隔线以下是「全部模式下参与取图的源」多选勾选。
    /// </summary>
    private void RebuildSourceMenu()
    {
        _buildingSourceMenu = true;
        _srcMenu.Items.Clear();

        // ── v1.5.5 浏览源（单选）：锁定只看某一个源；「全部」= 多源混排 ──
        //   放在菜单最上面且互斥，替代原先藏在二级子菜单里的「只看此源」——
        //   用户报「筛选单个源还会混入其他源的图」，很大程度就是旧入口容易误点父项。
        //   选中某项后左侧分类树会切换成该源自己的分类。
        var browseOptions = new List<(string Key, string Name)> { ("", "全部（多源混排）") };
        foreach (var s in _sources.All) browseOptions.Add((s.Key, s.DisplayName));

        foreach (var (key, name) in browseOptions)
        {
            var it = new ToolStripMenuItem(name)
            {
                CheckOnClick = true,
                Checked = string.Equals(_cfg.BrowseSource ?? "", key, StringComparison.OrdinalIgnoreCase),
                Tag = "browse:" + key
            };
            var captured = key;
            it.Click += (_, _) =>
            {
                if (string.Equals(_cfg.BrowseSource ?? "", captured, StringComparison.OrdinalIgnoreCase))
                    return;   // 点的是当前项：保持不变（浏览源不允许被取消）

                _cfg.BrowseSource = captured;
                _cfg.Save();
                _currentChannelKey = "";
                _currentLocalChannel = "";
                UpdateSourceButtonText();
                RebuildTree();          // 树按新浏览源重建（单源 → 该源自己的分类）
                _srcMenu.Close();
                if (!_favMode) { Reload(); return; }
                LoadFavorites();
            };
            _srcMenu.Items.Add(it);
        }

        _srcMenu.Items.Add(new ToolStripSeparator());
        _srcMenu.Items.Add(new ToolStripMenuItem("以下勾选项在「全部」模式下参与取图")
        {
            Enabled = false,   // 纯说明行，不可点
            ForeColor = Color.FromArgb(140, 140, 140)
        });

        // 当前生效的源集合。注意必须与 SourceRegistry.Enabled 用同一套归一化：
        // 配置为 null 或空列表都视为「默认组合」——否则会出「菜单一个勾都没有、按钮却显示
        // 壁纸源 3」的自相矛盾状态（v1.5.0 修）
        var enabled = new HashSet<string>(CurrentSourceKeys(), StringComparer.OrdinalIgnoreCase);

        foreach (var s in _sources.All)
        {
            var ready = s.IsReady(_cfg);
            // v1.5.3：菜单项只显示站点名。原先拼了「免注册 · 国内源」这类状态文字，
            // 菜单又长又杂；可用性改用灰字表达（未就绪 = 灰）。
            var item = new ToolStripMenuItem(s.DisplayName)
            {
                CheckOnClick = true,
                Checked = enabled.Contains(s.Key),
                Enabled = true,
                ForeColor = ready ? SystemColors.ControlText : Color.FromArgb(150, 150, 150),
                Tag = s
            };
            item.CheckedChanged += (_, _) =>
            {
                if (_buildingSourceMenu) return;
                var src = (IWallpaperSource)item.Tag!;
                var keys = CurrentSourceKeys().ToList();
                if (item.Checked)
                {
                    if (!keys.Contains(src.Key, StringComparer.OrdinalIgnoreCase))
                        keys.Add(src.Key);
                }
                else
                {
                    keys.RemoveAll(k => string.Equals(k, src.Key, StringComparison.OrdinalIgnoreCase));
                    // 至少保留一个源，否则图墙永远空白。
                    // 必须判断「本次改动后」的列表：此前判断的是尚未更新的 _cfg（旧值），
                    // 于是最后一个源也能被取消 → 配置存成空列表（v1.5.0 修）
                    if (keys.Count == 0)
                    {
                        _buildingSourceMenu = true;   // 回滚勾选状态但不触发再次进入
                        item.Checked = true;
                        _buildingSourceMenu = false;
                        _status.Text = "至少需保留一个壁纸源";
                        return;
                    }
                }
                _cfg.EnabledSources = keys;
                _cfg.Save();
                UpdateSourceButtonText();
                _page = 1;
                _ended = false;
                if (!_favMode) Reload();
            };

            // v1.5.5：不再挂「只看此源」子菜单 —— 顶层已有「浏览源（单选）」，
            // 保留子菜单只会让父项带上展开箭头、增加误点概率（用户反馈的"混入其他源"来源之一）。
            _srcMenu.Items.Add(item);
        }

        // 菜单每次打开都整体重建 → 主题必须在重建后补涂（否则深色下是白底菜单）
        ThemeManager.ApplyMenu(_srcMenu, ThemeManager.LastDark);
        // 未就绪源的灰字提示在主题化后重新覆盖（ApplyMenu 会统一设前景色）
        foreach (ToolStripItem it in _srcMenu.Items)
            if (it.Tag is IWallpaperSource s && !s.IsReady(_cfg))
                it.ForeColor = Color.FromArgb(150, 150, 150);

        _buildingSourceMenu = false;
    }

    /// <summary>
    /// 当前生效的源 key 列表（配置为 null/空 → 默认组合），与 SourceRegistry.Enabled 的归一化保持一致。
    /// </summary>
    private List<string> CurrentSourceKeys()
    {
        var keys = _cfg.EnabledSources;
        return keys == null || keys.Count == 0
            ? SourceRegistry.DefaultEnabled.ToList()
            : keys.ToList();
    }

    private static Button AddRowButton(FlowLayoutPanel row, string text, int width, Action onClick, int leftMargin = 0)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 26,
            Margin = new Padding(leftMargin, 2, 6, 0),
            Font = new Font("Microsoft YaHei UI", 9),
            FlatStyle = FlatStyle.Flat
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        b.Click += (_, _) => onClick();
        row.Controls.Add(b);
        return b;
    }

    /// <summary>左侧分类栏向左靠边折叠/展开。贴边小箭头按钮常驻分类栏右缘：
    /// 展开时箭头 ◀（在树右侧），收起后树隐藏、箭头 ▶（贴最左缘），右侧图片区自动扩大。</summary>
    private void ToggleTree()
    {
        _treeVisible = !_treeVisible;
        _tree.Visible = _treeVisible;
        _btnTree.Text = _treeVisible ? "◀" : "▶";
        _tips.SetToolTip(_btnTree, _treeVisible ? "向左收起分类栏" : "展开分类栏");
    }

    /// <summary>构建自动更换范围多选菜单（分类 → 二级频道两级勾选，分组与树一致）。</summary>
    private ToolStripDropDown BuildRotationMenu()
    {
        var dd = new ToolStripDropDown { Font = new Font("Microsoft YaHei UI", 9) };

        foreach (var (title, keys) in Channels.TreeGroups)
        {
            var chs = keys.Select(Channels.Find).Where(c => c != null).Cast<ChannelDef>().ToList();
            // 父级为三态指示：全选✓ / 部分■ / 未选空；点击展开子频道列表
            var parent = new ToolStripMenuItem(title);
            // 子菜单顶部的「全选」项：勾选=选满本类，取消勾选=清空本类
            var selAll = new ToolStripMenuItem("全选") { CheckOnClick = true };
            selAll.Checked = chs.Count > 0 && chs.All(c => _cfg.RotationChannels?.Contains(c.Key) == true);
            foreach (var ch in chs)
            {
                var item = new ToolStripMenuItem(ch.Name)
                {
                    CheckOnClick = true,
                    Checked = _cfg.RotationChannels?.Contains(ch.Key) == true,
                    Tag = ch.Key
                };
                item.CheckedChanged += (_, _) =>
                {
                    UpdateRotationChannel(ch.Key, item.Checked);
                    // 批处理（点「全选」）期间不回写「全选」自身，等整批结束后统一校正，
                    // 避免中途把 selAll 拉来拉去
                    if (_buildingRotationMenu == 0) SyncSelectAll(selAll, parent, chs);
                    SetParentCheckState(parent, chs);
                };
                parent.DropDownItems.Add(item);
            }

            // 「全选」：勾选=选满本类，取消勾选=清空本类 —— 同一个项来回切换（v1.2.0 需求 2）
            selAll.CheckedChanged += (_, _) =>
            {
                // SyncSelectAll 反向同步引起的勾选变化不是用户操作，直接忽略；
                // 否则批处理中途「尚未全部选中」的中间态会被回写成未勾选，导致整批被撤销
                if (_syncingSelectAll) return;

                // 关键：先把本次操作的目标状态捕获下来。循环里绝不能再读 selAll.Checked ——
                // 子项逐个变化时会触发 SyncSelectAll 回写 selAll，读到中间态就会漏勾后面的子项。
                var want = selAll.Checked;
                bool changed = false;
                _buildingRotationMenu++;
                try
                {
                    foreach (ToolStripItem sub in parent.DropDownItems)
                    {
                        // 只动带 Tag 的频道项（全选项自身与分隔线无 Tag）
                        if (sub is ToolStripMenuItem mi && mi.Tag is string && mi.Checked != want)
                        {
                            mi.Checked = want;   // 触发各自的 CheckedChanged 完成写入
                            changed = true;
                        }
                    }
                }
                finally { _buildingRotationMenu--; }

                SyncSelectAll(selAll, parent, chs);   // 批处理结束，按最终状态校正「全选」自身
                if (!changed) return;   // 反向同步（子项变化引起）无需额外动作，避免递归与重复换壁纸
                SetParentCheckState(parent, chs);
                UpdateRotScopeText();
                _ = _engine.NextAsync();  // 与单条勾选行为一致：整批改完后再换一张
            };
            // 「全选」置顶 + 分隔线（此处分隔线有语义：区分操作项与具体频道）
            parent.DropDownItems.Insert(0, selAll);
            parent.DropDownItems.Insert(1, new ToolStripSeparator());
            SetParentCheckState(parent, chs);
            // 子级级联菜单同样拦截"点击即关闭"，保证可连续勾选
            parent.DropDown.Closing += MenuClosingHandler;
            dd.Items.Add(parent);
        }

        if (_cfg.ShowNsfw && HiddenAuth.Unlocked)
        {
            var nsfw = new ToolStripMenuItem("NSFW（需 API Key）")
            {
                CheckOnClick = true,
                Checked = _cfg.RotationChannels?.Contains("nsfw") == true
            };
            nsfw.CheckedChanged += (_, _) =>
            {
                UpdateRotationChannel("nsfw", nsfw.Checked);
            };
            dd.Items.Add(nsfw);
        }

        // v1.2.0：去掉底部多余的分隔线与「完成（已自动保存）」项
        // —— 关闭即保存已由 MenuClosingHandler 处理（点菜单外区域或 Esc 同样保存并关闭）
        dd.Closing += MenuClosingHandler;

        // 深色主题适配菜单（统一走 ThemeManager：递归全部层级，与托盘/源菜单同色）
        ThemeManager.ApplyMenu(dd, ThemeManager.LastDark);

        return dd;
    }

    /// <summary>菜单关闭拦截：ItemClicked 取消（可连续勾选），其余（完成/外部/Esc）自动保存并关闭。</summary>
    private void MenuClosingHandler(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
        {
            e.Cancel = true;
            return;
        }
        UpdateRotScopeText();
    }
    private void SetParentCheckState(ToolStripMenuItem parent, List<ChannelDef> chs)
    {
        var selected = chs.Count(ch => _cfg.RotationChannels?.Contains(ch.Key) == true);
        parent.CheckState = selected == chs.Count ? CheckState.Checked
            : selected > 0 ? CheckState.Indeterminate
            : CheckState.Unchecked;
    }

    /// <summary>
    /// 反向同步：子项逐个变化时，更新「全选」项自身的勾选状态（全选✓ / 否则空）。
    /// 只改 selAll.Checked，不直接改子项；写入期间置 _syncingSelectAll 抑制 selAll 的批处理，
    /// 从而避免「点全选 → 回写 → 反向撤销」的递归。
    /// </summary>
    private void SyncSelectAll(ToolStripMenuItem selAll, ToolStripMenuItem parent, List<ChannelDef> chs)
    {
        if (chs.Count == 0) return;
        var all = chs.All(c => parent.DropDownItems
            .Cast<ToolStripItem>()
            .Any(i => i is ToolStripMenuItem mi && (string?)mi.Tag == c.Key && mi.Checked));
        if (selAll.Checked == all) return;
        _syncingSelectAll = true;
        try { selAll.Checked = all; }
        finally { _syncingSelectAll = false; }
    }

    private void UpdateRotationChannel(string key, bool onSelect)
    {
        var list = new List<string>(_cfg.RotationChannels ?? new List<string>());
        if (onSelect && !list.Contains(key)) list.Add(key);
        if (!onSelect) list.Remove(key);
        _cfg.RotationChannels = list;
        _cfg.Save();
        UpdateRotScopeText();
        // 用户主动调整范围：立即换一次壁纸（从新范围里抽一张）。
        // 初始化/重建菜单期间的 Checked 赋值不触发，避免启动时连环换壁纸。
        if (_buildingRotationMenu == 0) _ = _engine.NextAsync();
    }

    /// <summary>按钮上显示当前自动更换范围摘要。</summary>
    private void UpdateRotScopeText()
    {
        var selected = _cfg.RotationChannels ?? new List<string>();
        var parts = new List<string>();
        foreach (var (title, keys) in Channels.TreeGroups)
        {
            var n = keys.Count(selected.Contains);
            if (n == keys.Length) parts.Add(title);
            else if (n > 0) parts.Add($"{title}{n}");
        }
        if (selected.Contains("nsfw")) parts.Add("NSFW");
        _btnRotScope.Text = parts.Count > 0 ? string.Join(" + ", parts) : "点击选择频道…";
    }

    private void RebuildRotationMenu()
    {
        _buildingRotationMenu++;
        try
        {
            var old = _rotMenu;
            _rotMenu = BuildRotationMenu();
            UpdateRotScopeText();
            if (old is IDisposable d) d.Dispose();
        }
        finally { _buildingRotationMenu--; }
    }

    private void BuildTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        ClearCards();
        _nsfwMode = false;
        _favMode = false;
        _currentChannelKey = "";
        _page = 1;
        _seed = "";
        _batchFirstPage = true;
        _ended = false;

        // 收藏固定最前
        var favNode = new TreeNode("收藏") { Tag = "fav:all" };
        _tree.Nodes.Add(favNode);

        // ── v1.5.5：分类树随「浏览源」变化 ──
        //   多源混排 → 只显示几个大类（风景/摄影/人物/动漫），不再摆一堆各源能力对不上的子频道；
        //   锁定单个源 → 显示<b>该源自己的分类</b>（360 的自家分类名 / WallpaperCave 的检索词 / GitHub 的目录词）。
        var browse = _cfg.BrowseSource;
        if (string.IsNullOrWhiteSpace(browse))
        {
            foreach (var (title, keys) in Channels.TreeGroups)
            {
                var cat = Channels.Find(keys[0])?.Category ?? "100";
                _tree.Nodes.Add(new TreeNode(title) { Tag = $"grp:{cat}:{title}" });
            }
        }
        else
        {
            var src = _sources.Find(browse);
            var local = src?.SupportedChannels(_cfg) ?? Array.Empty<(string Id, string Name)>();
            foreach (var (id, name) in local)
                _tree.Nodes.Add(new TreeNode(name) { Tag = $"local:{id}" });

            if (local.Count == 0)   // 兜底：该源没有独立分类体系时仍给通用大类
                foreach (var (title, keys) in Channels.TreeGroups)
                {
                    var cat = Channels.Find(keys[0])?.Category ?? "100";
                    _tree.Nodes.Add(new TreeNode(title) { Tag = $"grp:{cat}:{title}" });
                }
        }

        // NSFW 分类（需 API Key）：拆「Sketchy / NSFW」两个子分类，仅在隐藏设置开启且本会话已通过密码解锁时出现；
        // 收起窗口/最小化/重启都会锁定并移除，主页面默认永远没有 NSFW 分类
        if (_cfg.ShowNsfw && HiddenAuth.Unlocked)
        {
            var nsfwRoot = new TreeNode("NSFW") { Tag = "nsfwroot" };
            nsfwRoot.Nodes.Add(new TreeNode("Sketchy") { Tag = "nsfw:010:Sketchy" });
            nsfwRoot.Nodes.Add(new TreeNode("NSFW") { Tag = "nsfw:001:NSFW" });
            _tree.Nodes.Add(nsfwRoot);
        }

        _tree.ExpandAll();

        // 默认选中第一个可选分类（收藏之后的第一项）
        var first = _tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is string t && t != "fav:all");
        if (first != null) _tree.SelectedNode = first;
        _tree.EndUpdate();
    }

    /// <summary>结束 NSFW 会话：切回普通频道 + 锁定 + 重建树（移除 NSFW 节点）。</summary>
    private void LeaveNsfwSession()
    {
        EnsureSafeView();
        HiddenAuth.Lock();
        RebuildTree();
    }

    /// <summary>若正浏览 NSFW，切回「风景」首个频道。</summary>
    private void EnsureSafeView()
    {
        if (!_nsfwMode) return;
        var firstGroup = _tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Nodes.Count > 0);
        if (firstGroup != null) _tree.SelectedNode = firstGroup.Nodes[0];
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 最小化：结束 NSFW 会话（切回普通频道 + 锁定 + 移除节点）并释放缩略图
        if (WindowState == FormWindowState.Minimized)
        {
            LeaveNsfwSession();
            ReleaseCardsForBackground();
        }
        else if (WindowState == FormWindowState.Normal && Visible && _releasedForBackground)
        {
            // 从托盘恢复：缩略图已释放，重新拉回当前频道
            _releasedForBackground = false;
            Reload();
        }
    }

    private bool _releasedForBackground;

    /// <summary>缩到托盘后释放全部卡片与缩略图，让后台常驻内存回到基线。
    /// 此前最小化只是 Hide 窗口，缩略图仍全部常驻，后台占用因此居高不下。</summary>
    private void ReleaseCardsForBackground()
    {
        if (_flow.CardCount == 0) return;
        _releasedForBackground = true;
        _page = 1;
        _ended = false;
        ClearCards();
    }

    /// <summary>设置保存后由外部调用：NSFW 入口显示状态可能变化，重建分类树与轮换菜单。</summary>
    public void RebuildTree()
    {
        BuildTree();
        RebuildRotationMenu();
    }

    private void Reload()
    {
        if (_favMode)
        {
            _status.Text = "收藏模式下不支持搜索";
            return;
        }
        _page = 1;
        _seed = "";
        _batchFirstPage = true;
        _ended = false;
        ClearCards();
        _ = LoadPageAsync();
    }

    /// <summary>换一批：保证取到与当前不同的一批图（此前只会重取第 1 页，确定性排序下永远同一批）。
    /// 随机排序 → 换 seed；确定性排序（收藏最多/热门等）→ 随机跳页；
    /// 收藏模式 → 打乱本地顺序。随机页翻空时自动回退第 1 页。</summary>
    private void ShuffleBatch()
    {
        if (_favMode) { LoadFavorites(shuffle: true); return; }

        if (_cfg.Sorting == "random")
        {
            _seed = Guid.NewGuid().ToString("N")[..8];
            _page = 1;
        }
        else
        {
            _seed = "";
            _page = Random.Shared.Next(1, 51);   // 确定性排序随机跳页
        }
        _batchFirstPage = true;
        _ended = false;
        _emptyStreak = 0;
        ClearCards();
        _ = LoadPageAsync();
    }

    /// <summary>清空并释放所有卡片（含缩略图），避免反复切换频道累积内存。</summary>
    private void ClearCards()
        => _flow.ClearCards();

    /// <summary>收藏视图：直接渲染本地收藏（无分页，缩略图优先取缓存）。shuffle=true 时打乱顺序（换一批）。</summary>
    private void LoadFavorites(bool shuffle = false)
    {
        ClearCards();
        _page = 1;
        _batchFirstPage = false;
        _ended = true;
        var all = _favorites.All();
        if (shuffle) all = all.OrderBy(_ => Random.Shared.Next()).ToList();
        foreach (var rec in all)
        {
            // v1.4.0：收藏记录自带源标识与直链。旧记录（无 SourceKey/Path）按 wallhaven 规则回退。
            var (src, rawId) = SplitStoreId(rec.Id, rec.SourceKey);
            AddCard(new WallpaperItem
            {
                SourceKey = src,
                Id = rawId,
                Path = string.IsNullOrEmpty(rec.Path) ? FullUrlFor(rawId) : rec.Path,
                Thumb = string.IsNullOrEmpty(rec.Thumb) ? ThumbUrlFor(rawId) : rec.Thumb,
                Resolution = rec.Resolution,
                PageUrl = rec.PageUrl
            });
        }
        _status.Text = all.Count == 0
            ? "收藏夹为空，浏览时点卡片上的「收藏」即可加入"
            : $"收藏 {all.Count} 张 · 双击卡片设为壁纸" + (shuffle ? "（已换一批）" : "");
    }

    /// <summary>
    /// 把收藏落盘键拆回 (源, 原始 id)。落盘键规则见 <see cref="WallpaperItem.StoreId"/>：
    /// wallhaven 是裸 id，其余源为 "源_原始id"。
    /// </summary>
    private static (string Source, string RawId) SplitStoreId(string storeId, string? sourceKey)
    {
        var src = string.IsNullOrWhiteSpace(sourceKey) ? "wallhaven" : sourceKey;
        if (src == "wallhaven") return ("wallhaven", storeId);
        var prefix = src + "_";
        return storeId.StartsWith(prefix, StringComparison.Ordinal)
            ? (src, storeId[prefix.Length..])
            : (src, storeId);
    }

    /// <summary>wallhaven 缩略图直链（lg 档，无需 API）。</summary>
    private static string ThumbUrlFor(string id) =>
        id.Length >= 2 ? $"https://th.wallhaven.cc/lg/{id[..2]}/{id}.jpg" : "";

    /// <summary>wallhaven 原图直链（默认 jpg，下载失败时回退 png）。
    /// 注意文件名带 wallhaven- 前缀（此前缺前缀导致收藏重建 404）。</summary>
    private static string FullUrlFor(string id) =>
        id.Length >= 2 ? $"https://w.wallhaven.cc/full/{id[..2]}/wallhaven-{id}.jpg" : "";

    private async Task LoadPageAsync()
    {
        if (_favMode || _loading || _ended || string.IsNullOrEmpty(_currentChannelKey)) return;
        _loading = true;
        try
        {
            _status.Text = "加载中…";
            // 普通频道 / 一级分类：仅 SFW；
            // NSFW 子分类（擦边 010 / NSFW 001，Tag=nsfw:<purity>:<名称>）：需 API Key
            ChannelDef ch;
            string purity, sorting, resolution;
            if (_currentChannelKey.StartsWith("nsfw:", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
                {
                    _ended = true;
                    _status.Text = "NSFW 分类需要在隐藏设置中填写 API Key";
                    return;
                }
                var p = _currentChannelKey[5..].Split(':');
                ch = new ChannelDef("nsfw", p.Length > 1 ? p[1] : "NSFW", "111", _cfg.HiddenQuery);
                purity = p[0]; // 010=擦边 001=NSFW
                sorting = _cfg.Sorting;
                resolution = _cfg.Resolution;
            }
            else if (_currentChannelKey.StartsWith("grp:", StringComparison.Ordinal))
            {
                // 一级分类：显示该分类下的全部壁纸（不加 tag 限定）
                var p = _currentChannelKey[4..];
                var sep = p.IndexOf(':');
                ch = new ChannelDef("grp", sep > 0 ? p[(sep + 1)..] : "分类", sep > 0 ? p[..sep] : p, "");
                purity = "100";
                sorting = _cfg.Sorting;
                resolution = _cfg.Resolution;
            }
            else if (_currentChannelKey == "local")
            {
                // v1.5.5：锁定单源浏览时选中的是「源自己的分类」，语义完全由该源解释（LocalChannelId）。
                // 这里只需要一个占位定义 —— 旧代码没有这个分支，会被下面的 Channels.Find("local") == null
                // 直接 return，导致整页永远停在"加载中…"。
                ch = new ChannelDef("local", "源分类", "100", "");
                purity = "100";
                sorting = _cfg.Sorting;
                resolution = _cfg.Resolution;
            }
            else
            {
                var found = Channels.Find(_currentChannelKey);
                if (found == null) return;
                ch = found;
                purity = "100";
                sorting = _cfg.Sorting;
                resolution = _cfg.Resolution;
            }

            var useSeed = sorting == "random" ? _seed : null;
            var kw = (ch.Query ?? "").Replace("+", " ").Trim();
            Logger.Info($"load: key={_currentChannelKey} cat={ch.Category} purity={purity} q=\"{ch.Query}\" kw=\"{kw}\" page={_page} seed=\"{useSeed}\"");

            // v1.4.0：多源聚合。壁纸源决定「从哪来」；v1.4.1 起频道对非 wallhaven 源也生效：
            // WhQuery 保留 +tag 语法给 wallhaven，Keywords（去 + 号）传给支持检索的源。
            // v1.5.4：一级分类（grp:100:风景）要带上该组的全部子频道 key ——
            // 否则 360 会退化成按页轮换自家分类、GitHub 关键词落空后退回全库，导致"点分类出来很乱"。
            var groupKeys = _currentChannelKey.StartsWith("grp:", StringComparison.Ordinal)
                ? Channels.KeysOfGroup(_currentChannelKey[4..].Split(':')[0])
                : null;

            var list = await _sources.FetchMergedAsync(_cfg, new SourceFetchRequest
            {
                Page = _page,
                PerPage = 24,
                Seed = useSeed,
                WhCategory = ch.Category,
                WhQuery = ch.Query,
                Keywords = kw,
                ChannelKey = _currentChannelKey ?? "",
                GroupKeys = groupKeys,
                LocalChannelId = _currentLocalChannel,
                Purity = purity,
                Sorting = sorting
            });
            if (list == null || list.Count == 0) Logger.Warn("load: merged sources returned empty");
            else Logger.Info($"load: merged ok, {list.Count} items");

            if (list == null || list.Count == 0)
            {
                // 无限下拉：翻到头自动从头继续——随机排序换新 seed（内容全新），
                // 确定性排序回第 1 页（重复图会被下方去重跳过）。
                // 连续多页都没有新增（频道确实耗尽）才停止。
                if (_page > 1 && _emptyStreak < 6)
                {
                    _emptyStreak++;
                    _page = 1;
                    _batchFirstPage = false;
                    if (_cfg.Sorting == "random") _seed = Guid.NewGuid().ToString("N")[..8];
                    _status.Text = "已到底，从头继续加载…";
                    BeginInvoke(() => _ = LoadPageAsync());
                    return;
                }
                _ended = true;
                _status.Text = "没有更多结果";
                return;
            }

            int added = 0;
            var seen = new HashSet<string>();
            var srcLabels = new List<string>();
            foreach (var card in _flow.Cards) seen.Add(card.Item.Id);
            _flow.BeginBatch();      // v1.5.5：整页卡片一次性重排，避免逐张重排造成的卡顿
            try
            {
                foreach (var item in list)
                {
                    if (_blacklist.Contains(item.StoreId) || seen.Contains(item.StoreId)) continue;
                    AddCard(item);
                    added++;
                    if (!srcLabels.Contains(item.SourceLabel)) srcLabels.Add(item.SourceLabel);
                }
            }
            finally { _flow.EndBatch(); }
            _page++;
            if (added > 0) _emptyStreak = 0;

            var sortName = sorting switch
            {
                "random" => "随机", "hot" => "热门", "date_added" => "最新",
                "views" => "浏览最多", "favorites" => "收藏最多", _ => sorting
            };
            // 末尾标出本页图片来自哪些源：多源混排时用户能一眼看出混合是预期行为
            var srcNote = srcLabels.Count > 0 ? " · 源 " + string.Join("/", srcLabels) : "";
            _status.Text = $"已加载 {_flow.CardCount} 张 · {ch.Name} · {sortName} · 第 {_page - 1} 页{srcNote}";

            // 首屏未撑满则自动续一页。必须 BeginInvoke 延后执行：本方法 finally 还没跑、
            // _loading 仍为 true，直接递归调用会被自身的重入保护拦掉（此前自动续页从未生效）
            bool firstPage = _batchFirstPage;
            _batchFirstPage = false;
            if (added > 0 && firstPage && !_flow.VerticalScroll.Visible)
                BeginInvoke(() => _ = LoadPageAsync());
        }
        catch (Exception ex)
        {
            _status.Text = "加载失败：" + ex.Message;
            Logger.Warn($"mainform load: {ex.Message}");
        }
        finally { _loading = false; }
    }

    /// <summary>图片基础信息（单行）：ID · 分辨率 · 分类 · 分级 · 大小 · 详情链接。</summary>
    private static string BuildItemInfo(WallpaperItem it)
    {
        var size = it.FileSize > 0 ? $" · {it.FileSize / 1048576.0:F1}MB" : "";
        var url = string.IsNullOrEmpty(it.PageUrl) ? "" : $" · {it.PageUrl}";
        return $"{it.Id} · {it.Resolution}{size}{url}";
    }

    private void AddCard(WallpaperItem item)
    {
        var card = new WallpaperCard(item);
        if (_favorites.Contains(item.StoreId)) card.MarkFavorited();
        if (_favMode) card.SetRemovableFavorite();

        // 悬停 Tooltip + 单击/悬停状态栏展示基础信息
        _tips.SetToolTip(card, BuildItemInfo(item));
        card.OnInfo += it => _status.Text = BuildItemInfo(it);

        card.OnPreview += it => OpenPreview(it);
        card.OnSetWallpaper += async it => await SetAsWallpaperAsync(it);
        card.OnFavorite += it =>
        {
            if (_favMode)
            {
                // 收藏视图：按钮 = 取消收藏
                _favorites.Remove(it.StoreId);
                _flow.RemoveCard(card);
                _status.Text = $"已取消收藏 · 剩余 {_favorites.All().Count} 张";
            }
            else
            {
                _favorites.Add(it);
                card.MarkFavorited();
            }
        };
        card.OnBlock += it =>
        {
            _blacklist.AddId(it.StoreId);
            _flow.RemoveCard(card);
        };

        _flow.AddCard(card);
    }

    /// <summary>大图预览浮层：以当前瀑布流可见卡片为切换序列，当前卡片为起点。</summary>
    private void OpenPreview(WallpaperItem start)
    {
        var items = _flow.Cards.Select(c => c.Item).ToList();
        var idx = items.FindIndex(i => i.Id == start.Id);
        if (idx < 0)
        {
            items = new List<WallpaperItem> { start };
            idx = 0;
        }
        var dlg = new PreviewForm(items, idx, _api, _cache, _favorites,
            it => SetAsWallpaperAsync(it),
            it =>
            {
                // 预览窗内的收藏开关与卡片行为一致
                if (_favorites.Contains(it.StoreId)) _favorites.Remove(it.StoreId);
                else _favorites.Add(it);
            });
        dlg.ShowDialog(this);
        // 关闭预览后同步当前瀑布流的收藏状态显示
        foreach (var c in _flow.Cards)
        {
            if (_favorites.Contains(c.Item.StoreId)) c.MarkFavorited();
        }
    }

    private async Task SetAsWallpaperAsync(WallpaperItem item)
    {
        var fullPath = _cache.FullPath(item.StoreId);
        try
        {
            if (!File.Exists(fullPath))
            {
                _status.Text = $"下载中 {item.Id} …";
                try
                {
                    await _api.DownloadAsync(item.Path, fullPath);
                }
                catch when (item.Path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                            && item.Path.Contains("/full/", StringComparison.OrdinalIgnoreCase))
                {
                    // 收藏重建的直链默认 jpg，部分图是 png，回退一次
                    await _api.DownloadAsync(item.Path[..^4] + ".png", fullPath);
                }
            }
            if (WallpaperSetter.Set(fullPath, _cfg.FillMode))
            {
                _cache.Touch(item.StoreId);
                _engine.SetCurrent(item);   // 同步「当前壁纸」，供状态栏右键收藏
                var chName = _favMode ? "收藏"
                    : _nsfwMode ? "NSFW"
                    : Channels.Find(_currentChannelKey)?.Name ?? "";
                _history.Append(item, chName, fullPath);
                _status.Text = $"已设为壁纸：{item.Id} ({item.Resolution})";
            }
            else _status.Text = "壁纸设置失败";
        }
        catch (Exception ex)
        {
            _status.Text = "设置失败：" + ex.Message;
            Logger.Warn($"set wallpaper from card: {ex.Message}");
        }
    }

    /// <summary>
    /// 缩略图加载（v1.5.3 重写：浏览只吃小图，不吃原图）。
    ///
    /// 三条一起才能同时压住内存与流量：
    ///  ① <b>缓存里存的就是 240px 小图</b>（约 15KB）—— 旧版把原图直接塞进缩略图缓存，
    ///     一张 5120×2880 的 GitHub 图是 6MB，读到就要解码成约 59MB 内存；现在下载后立即
    ///     缩放再落盘；历史遗留的大图缓存会在读到时就地重编码（自愈）；
    ///  ② <b>并发闸门 3</b>：可见卡片一多也不会同时解码十几张大图；
    ///  ③ 源侧拦截：GitHub 图库在清单阶段按文件大小过滤超大图（见 GitHubWallsSource）。
    /// </summary>
    private async Task LoadThumbAsync(WallpaperCard card)
    {
        try
        {
            var thumbPath = _cache.ThumbPath(card.Item.StoreId);
            Image? img = null;

            if (File.Exists(thumbPath))
            {
                await _thumbCpu.WaitAsync();
                try { img = TryReadCachedThumb(thumbPath); }
                finally { _thumbCpu.Release(); }
            }

            if (img == null)
            {
                var url = card.Item.Thumb;
                if (string.IsNullOrEmpty(url)) return;

                // 两段式：网络并发放宽（快），解码并发收紧（省内存）
                byte[]? bytes;
                await _thumbNet.WaitAsync();
                try { bytes = await FetchThumbBytesAsync(url); }
                finally { _thumbNet.Release(); }

                // 首选缩略图取不到（如 wallhaven 小图走不通）→ 回退到备用地址（GitHub 原图）
                if ((bytes == null || bytes.Length == 0) && !string.IsNullOrEmpty(card.Item.ThumbFallback))
                {
                    Logger.Info($"thumb: 首选失败，回退备用地址（{card.Item.SourceKey}）");
                    await _thumbNet.WaitAsync();
                    try { bytes = await FetchThumbBytesAsync(card.Item.ThumbFallback); }
                    finally { _thumbNet.Release(); }
                }

                if (bytes == null || bytes.Length == 0) return;

                await _thumbCpu.WaitAsync();
                try
                {
                    img = DecodeScaled(bytes);
                    if (img != null) TryWriteThumbCache(thumbPath, img);   // 缓存小图：之后浏览零流量
                }
                finally { _thumbCpu.Release(); }

                if (img == null) return;
            }

            // 交付后由卡片负责释放：BeginInvoke 是异步投递的，lambda 内不能再依赖已释放的 img
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(() =>
                {
                    if (!card.IsDisposed) card.SetThumb(img);
                    else img.Dispose();
                });
            }
            else
            {
                img.Dispose();
            }
        }
        catch
        {
            // 缩略图失败静默，不阻塞浏览
        }
    }

    /// <summary>取缩略图字节：wallhaven 系一律走反代（含 GitHub 图库里的 wallhaven 小图），其余源走通用链路。</summary>
    private async Task<byte[]?> FetchThumbBytesAsync(string url)
    {
        if (url.Contains("wallhaven", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                // RewriteThumbAlways：不依赖"当前链路是否在反代"，否则会直连被墙的 wallhaven 域名挂到超时
                using var resp = await _thumbHttp.GetAsync(_api.RewriteThumbAlways(url), cts.Token);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsByteArrayAsync(cts.Token);
            }
            catch { return null; }
        }
        return await SourceHttp.GetBytesAsync(url, _cfg);
    }

    /// <summary>
    /// 读缩略图缓存：本身已是小图就直接用；若是旧版留下的原图（宽 &gt; <see cref="ThumbRawThreshold"/>），
    /// 缩放后覆写缓存再交付 —— 浏览一遍即可把历史大图缓存全部换成小图。
    /// </summary>
    private static Image? TryReadCachedThumb(string path)
    {
        try
        {
            // 用字节流构造，避免 Image.FromFile 长期锁定缓存文件（否则 LRU 淘汰时删不掉）
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var src = Image.FromStream(ms);
            if (src.Width <= ThumbRawThreshold) return new Bitmap(src);

            var scaled = ScaleTo(src, ThumbWidth);
            TryWriteThumbCache(path, scaled);
            return scaled;
        }
        catch { return null; }
    }

    private static Image? DecodeScaled(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var src = Image.FromStream(ms);
            return ScaleTo(src, Math.Min(ThumbWidth, src.Width));   // 只缩不放
        }
        catch { return null; }
    }

    /// <summary>
    /// 等比缩放到指定宽度。**大图走两段缩放**：先 Bilinear 粗缩到 2× 目标宽，再 Bicubic 精缩到底。
    /// 直接拿 HighQualityBicubic 把 5120×2880 缩到 240px 是纯 CPU 灾难（首页/换频道时肉眼可见卡顿），
    /// 两段法质量几乎无损、耗时降一个数量级（v1.5.5 性能优化）。
    /// </summary>
    private static Bitmap ScaleTo(Image src, int width)
    {
        if (src.Width > width * 2)
        {
            var midW = width * 2;
            var midH = Math.Max(1, (int)((double)midW / src.Width * src.Height));
            using var mid = new Bitmap(midW, midH);
            using (var g0 = Graphics.FromImage(mid))
            {
                g0.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g0.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                g0.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g0.DrawImage(src, 0, 0, midW, midH);
            }
            return ScaleTo(mid, width);   // 此时源只有 2× 目标宽，Bicubic 很快
        }

        var h = Math.Max(1, (int)((double)width / src.Width * src.Height));
        var bmp = new Bitmap(width, h);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, width, h);
        return bmp;
    }

    /// <summary>原子写缩略图缓存（先写 .tmp 再改名，中断不会留下半个文件）。</summary>
    private static void TryWriteThumbCache(string path, Image img)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            img.Save(tmp, System.Drawing.Imaging.ImageFormat.Jpeg);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* 缓存写失败不影响本次显示 */ }
    }

    public void InvokeSetStatus(string text) => Invoke(() => _status.Text = text);

    /// <summary>
    /// 「当前壁纸」= 桌面上正在使用的那张。优先取本次运行的轮换记录（`_engine.Current`，手动设为壁纸时也会同步）；
    /// 若本次还没换过图，则回退到更换历史的最新一条——历史跨会话持久，覆盖「上次运行设过、本次还没换」的情况。
    /// </summary>
    private WallpaperItem? CurrentWallpaper()
    {
        var cur = _engine.Current;
        if (cur != null && !string.IsNullOrEmpty(cur.Id)) return cur;
        try
        {
            var last = _history.Load(1).FirstOrDefault();
            if (last != null && !string.IsNullOrEmpty(last.Id))
                return new WallpaperItem
                {
                    Id = last.Id, Resolution = last.Resolution,
                    PageUrl = last.PageUrl, FileSize = last.FileSize
                };
        }
        catch (Exception ex) { Logger.Warn($"read history for current wallpaper: {ex.Message}"); }
        return null;
    }

    /// <summary>
    /// 收藏「当前壁纸」（托盘右键菜单调用）。只做收藏、不做取消，已收藏则原样返回提示。
    /// 返回结果文案，由调用方决定展示方式（托盘气泡 / 状态栏）。
    /// </summary>
    public string FavoriteCurrentWallpaper()
    {
        var it = CurrentWallpaper();
        if (it == null) return "暂无当前壁纸可收藏";

        if (_favorites.Contains(it.StoreId)) return $"已在收藏夹：{it.Id}（{it.SourceLabel}）";

        _favorites.Add(it);
        // 瀑布流里若正好有这张，同步心形状态
        foreach (var c in _flow.Cards)
            if (c.Item.Id == it.Id) c.MarkFavorited();
        return $"已收藏当前壁纸：{it.Id}（{it.SourceLabel}） · 共 {_favorites.All().Count} 张";
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            LeaveNsfwSession(); // 收起窗口：结束 NSFW 会话，重开主页面默认没有 NSFW
            Hide();
        }
        else base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbHttp.Dispose();
            _tips.Dispose();
            _rotMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}