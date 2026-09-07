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
    private readonly ToolTip _tips = new();

    // 底部设置条控件
    private readonly NumericUpDown _numInterval = new();
    private readonly ComboBox _cboResolution = new();
    private readonly ComboBox _cboFill = new();
    private readonly ComboBox _cboSorting = new();
    private readonly ComboBox _cboMonitors = new();
    private Button _btnRotScope = new();
    private ToolStripDropDown _rotMenu = new();

    private string _currentChannelKey = "";
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
        if (_api.ActiveTier is "直连" or "反代")
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
            else
            {
                var ch = Channels.Find(tag);
                if (ch == null) return;
                _favMode = false; _nsfwMode = false;
                _currentChannelKey = tag;
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
        _cboResolution.Items.AddRange(new object[] { "1080p", "2K", "4K" });
        _cboResolution.SelectedIndex = _cfg.Resolution switch { "1920x1080" => 0, "3840x2160" => 2, _ => 1 };
        _cboResolution.SelectedIndexChanged += (_, _) =>
        {
            _cfg.Resolution = _cboResolution.SelectedIndex switch
            {
                0 => "1920x1080", 2 => "3840x2160", _ => "2560x1440"
            };
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
        return row;
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
                    SetParentCheckState(parent, chs);
                };
                parent.DropDownItems.Add(item);
            }
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

        dd.Items.Add(new ToolStripSeparator());
        var done = new ToolStripMenuItem("完成（已自动保存）");
        done.Click += (_, _) => dd.Close();
        dd.Items.Add(done);

        // 勾选（ItemClicked）时不关闭菜单；点「完成」、菜单外区域或 Esc 自动保存并关闭
        dd.Closing += MenuClosingHandler;

        // 深色主题适配菜单
        if (ThemeManager.ShouldUseDark(_cfg.Theme))
        {
            var darkBg = Color.FromArgb(37, 37, 38);
            var darkFg = Color.FromArgb(235, 235, 235);
            dd.BackColor = darkBg;
            dd.ForeColor = darkFg;
            foreach (ToolStripItem it in dd.Items)
            {
                it.BackColor = darkBg;
                it.ForeColor = darkFg;
                if (it is ToolStripMenuItem mi)
                    foreach (ToolStripItem sub in mi.DropDownItems)
                    {
                        sub.BackColor = darkBg;
                        sub.ForeColor = darkFg;
                    }
            }
        }

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

    private void UpdateRotationChannel(string key, bool onSelect)
    {
        var list = new List<string>(_cfg.RotationChannels ?? new List<string>());
        if (onSelect && !list.Contains(key)) list.Add(key);
        if (!onSelect) list.Remove(key);
        _cfg.RotationChannels = list;
        _cfg.Save();
        UpdateRotScopeText();
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
        var old = _rotMenu;
        _rotMenu = BuildRotationMenu();
        UpdateRotScopeText();
        if (old is IDisposable d) d.Dispose();
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

        // 分组顺序：风景 → 摄影 → 人物 → 动漫。一级节点 Tag = grp:<分类码>:<名称>，
        // 点击一级分类显示该分类全部壁纸
        foreach (var (title, keys) in Channels.TreeGroups)
        {
            var root = new TreeNode(title);
            foreach (var k in keys)
            {
                var ch = Channels.Find(k);
                if (ch != null) root.Nodes.Add(new TreeNode(ch.Name) { Tag = ch.Key });
            }
            var cat = Channels.Find(keys[0])?.Category ?? "100";
            root.Tag = $"grp:{cat}:{title}";
            _tree.Nodes.Add(root);
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

        // 打开首页默认显示「风景 · 自然风光」（不恢复上次节点，NSFW 永不自动恢复）
        var firstGroup = _tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Nodes.Count > 0);
        if (firstGroup != null) _tree.SelectedNode = firstGroup.Nodes[0];
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
        // 最小化到任务栏：结束 NSFW 会话（切回普通频道 + 锁定 + 移除节点）
        if (WindowState == FormWindowState.Minimized) LeaveNsfwSession();
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
            AddCard(new WallpaperItem
            {
                Id = rec.Id,
                Path = FullUrlFor(rec.Id),
                Thumb = ThumbUrlFor(rec.Id),
                Resolution = rec.Resolution,
                PageUrl = rec.PageUrl
            });
        }
        _status.Text = all.Count == 0
            ? "收藏夹为空，浏览时点卡片上的「收藏」即可加入"
            : $"收藏 {all.Count} 张 · 双击卡片设为壁纸" + (shuffle ? "（已换一批）" : "");
    }

    /// <summary>wallhaven 缩略图直链（lg 档，无需 API）。</summary>
    private static string ThumbUrlFor(string id) => $"https://th.wallhaven.cc/lg/{id[..2]}/{id}.jpg";

    /// <summary>wallhaven 原图直链（默认 jpg，下载失败时回退 png）。
    /// 注意文件名带 wallhaven- 前缀（此前缺前缀导致收藏重建 404）。</summary>
    private static string FullUrlFor(string id) => $"https://w.wallhaven.cc/full/{id[..2]}/wallhaven-{id}.jpg";

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
            Logger.Info($"load: key={_currentChannelKey} cat={ch.Category} purity={purity} q=\"{ch.Query}\" page={_page} seed=\"{useSeed}\"");
            var list = await _api.SearchAsync(ch.Category, purity, sorting, ch.Query, resolution, _page, useSeed);
            if (list == null) Logger.Warn("load: search returned null (network fail)");
            else Logger.Info($"load: search ok, {list.Count} items");

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
            foreach (var card in _flow.Cards) seen.Add(card.Item.Id);
            foreach (var item in list)
            {
                if (_blacklist.Contains(item.Id) || seen.Contains(item.Id)) continue;
                AddCard(item);
                added++;
            }
            _page++;
            if (added > 0) _emptyStreak = 0;

            var sortName = sorting switch
            {
                "random" => "随机", "hot" => "热门", "date_added" => "最新",
                "views" => "浏览最多", "favorites" => "收藏最多", _ => sorting
            };
            _status.Text = $"已加载 {_flow.CardCount} 张 · {ch.Name} · {sortName} · 第 {_page - 1} 页";

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
        var cat = it.Category switch
        {
            "anime" => "动漫", "people" => "人物", "general" => "综合", _ => it.Category
        };
        var pur = it.Purity switch
        {
            "sketchy" => "Sketchy", "nsfw" => "NSFW", _ => "SFW"
        };
        var size = it.FileSize > 0 ? $" · {it.FileSize / 1048576.0:F1}MB" : "";
        var url = string.IsNullOrEmpty(it.PageUrl) ? "" : $" · {it.PageUrl}";
        return $"{it.Id} · {it.Resolution} · {cat} · {pur}{size}{url}";
    }

    private void AddCard(WallpaperItem item)
    {
        var card = new WallpaperCard(item);
        if (_favorites.Contains(item.Id)) card.MarkFavorited();
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
                _favorites.Remove(it.Id);
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
            _blacklist.AddId(it.Id);
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
                if (_favorites.Contains(it.Id)) _favorites.Remove(it.Id);
                else _favorites.Add(it);
            });
        dlg.ShowDialog(this);
        // 关闭预览后同步当前瀑布流的收藏状态显示
        foreach (var c in _flow.Cards)
        {
            if (_favorites.Contains(c.Item.Id)) c.MarkFavorited();
        }
    }

    private async Task SetAsWallpaperAsync(WallpaperItem item)
    {
        var fullPath = _cache.FullPath(item.Id);
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
                _cache.Touch(item.Id);
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

    private async Task LoadThumbAsync(WallpaperCard card)
    {
        try
        {
            var thumbPath = _cache.ThumbPath(card.Item.Id);
            if (!File.Exists(thumbPath))
            {
                var url = card.Item.Thumb;
                if (string.IsNullOrEmpty(url)) return;
                url = WallhavenClient.RewriteForThumbs(url);
                var dir = Path.GetDirectoryName(thumbPath)!;
                Directory.CreateDirectory(dir);
                using var resp = await _thumbHttp.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(thumbPath);
                await resp.Content.CopyToAsync(fs);
            }
            // 用字节流构造图片：避免 Image.FromFile 长期锁定缓存文件（否则 LRU 淘汰时删不掉）
            byte[] bytes = await File.ReadAllBytesAsync(thumbPath);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);

            // 关键：必须在 img 释放前克隆。BeginInvoke 是异步投递的，
            // 若在 lambda 内 Clone，届时 img 已被 using 释放 → ArgumentException
            var copy = (Image)img.Clone();

            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(() =>
                {
                    if (!card.IsDisposed) card.SetThumb(copy);
                    else copy.Dispose();
                });
            }
            else
            {
                copy.Dispose();
            }
        }
        catch
        {
            // 缩略图失败静默，不阻塞浏览
        }
    }

    public void InvokeSetStatus(string text) => Invoke(() => _status.Text = text);

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