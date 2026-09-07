namespace PonyoWallpaper;

/// <summary>
/// 缩略图卡片：图片满幅展示，鼠标悬停时底部浮现深色操作条（预览 / 设为壁纸 / 收藏）。
/// 双击图片仍直接设为壁纸；右键屏蔽。缩略图由调用方异步加载后通过 SetThumb 回填。
/// </summary>
internal sealed class WallpaperCard : Panel
{
    public WallpaperItem Item { get; }
    private readonly PictureBox _pic;
    private readonly Panel _overlay;
    private readonly Button _btnFav;
    private readonly Button _btnSet;
    private bool _favorited;

    /// <summary>深浅色主题：仅卡片底色随主题（操作条恒为深色半透明风格）。</summary>
    public void ApplyTheme(bool dark, Color panel, Color fg, Color fg2, Color inputBg, Color border)
    {
        BackColor = dark ? panel : Color.White;
    }

    public event Action<WallpaperItem>? OnSetWallpaper;
    public event Action<WallpaperItem>? OnFavorite;
    public event Action<WallpaperItem>? OnBlock;
    /// <summary>悬停操作条上的「预览」或单击图片：打开大图预览浮层。</summary>
    public event Action<WallpaperItem>? OnPreview;
    /// <summary>鼠标悬停时触发，用于在状态栏展示基础信息。</summary>
    public event Action<WallpaperItem>? OnInfo;

    public WallpaperCard(WallpaperItem item)
    {
        Item = item;
        Width = 190;
        Height = 172;
        Margin = new Padding(5);
        BackColor = Color.White;

        _pic = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(244, 244, 244),
            Cursor = Cursors.Hand
        };
        _pic.MouseEnter += (_, _) => { ShowOverlay(); OnInfo?.Invoke(Item); };
        _pic.MouseLeave += CheckHideOverlay;
        // 单击 = 预览大图，双击 = 设为壁纸：用短定时器区分单击/双击，
        // 避免双击时第一次 Click 抢先打开预览浮层
        var clickTimer = new System.Windows.Forms.Timer { Interval = 260 };
        var pendingClick = false;
        _pic.Click += (_, _) =>
        {
            pendingClick = true;
            clickTimer.Start();
        };
        _pic.DoubleClick += (_, _) =>
        {
            pendingClick = false;
            clickTimer.Stop();
            OnSetWallpaper?.Invoke(Item);
        };
        clickTimer.Tick += (_, _) =>
        {
            clickTimer.Stop();
            if (pendingClick) OnPreview?.Invoke(Item);
            pendingClick = false;
        };

        // 悬浮操作条：覆盖在图片底部，悬停时出现（绝对定位，不挤压图片）
        _overlay = new Panel
        {
            Size = new Size(Width, 28),
            Location = new Point(0, Height - 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(224, 22, 22, 22),
            Visible = false,
            Padding = new Padding(2, 0, 2, 0)
        };
        _overlay.MouseEnter += (_, _) => ShowOverlay();
        _overlay.MouseLeave += CheckHideOverlay;

        var btnPreview = MakeOverlayButton("预览", 44);
        btnPreview.Click += (_, _) => OnPreview?.Invoke(Item);
        _overlay.Controls.Add(btnPreview);

        var btnFav = MakeOverlayButton("收藏", 48);
        btnFav.Click += (_, _) => OnFavorite?.Invoke(Item);
        _btnFav = btnFav;
        _overlay.Controls.Add(btnFav);

        var btnSet = MakeOverlayButton("设为壁纸", 66);
        btnSet.BackColor = Color.FromArgb(216, 90, 48);
        btnSet.Click += (_, _) => OnSetWallpaper?.Invoke(Item);
        _btnSet = btnSet;
        _overlay.Controls.Add(btnSet);

        // 右键屏蔽
        var ctx = new ContextMenuStrip();
        ctx.Items.Add("屏蔽此图，不再出现", null, (_, _) => OnBlock?.Invoke(Item));
        ContextMenuStrip = ctx;

        Controls.Add(_pic);
        Controls.Add(_overlay);
    }

    private static Button MakeOverlayButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Dock = DockStyle.Right,
            Width = width,
            Font = new Font("Microsoft YaHei UI", 8),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(224, 22, 22, 22),
            Margin = new Padding(1, 1, 1, 1)
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(224, 62, 62, 62);
        b.MouseEnter += (_, _) => { };
        return b;
    }

    private void ShowOverlay()
    {
        if (!_overlay.Visible)
        {
            _overlay.Visible = true;
            _overlay.BringToFront();
        }
        OnInfo?.Invoke(Item);
    }

    /// <summary>鼠标真正离开整个卡片（含悬浮条）才隐藏操作条：
    /// 进入子控件会触发父级 MouseLeave，必须用光标位置二次确认。</summary>
    private void CheckHideOverlay(object? sender, EventArgs e)
    {
        var pos = PointToClient(Cursor.Position);
        if (!ClientRectangle.Contains(pos)) _overlay.Visible = false;
    }

    public void SetThumb(Image? img)
    {
        if (img == null) return;
        var old = _pic.Image;
        _pic.Image = img;
        old?.Dispose();
    }

    /// <summary>瀑布流列宽 → 卡片高度：按图片宽高比换算（未知分辨率按 3:2，极端比例限幅）。</summary>
    public int DesiredHeight(int colW)
    {
        var aspect = 1.5;
        var m = System.Text.RegularExpressions.Regex.Match(Item.Resolution ?? "", @"^(\d{3,5})x(\d{3,5})$");
        if (m.Success
            && double.TryParse(m.Groups[1].Value, out var w)
            && double.TryParse(m.Groups[2].Value, out var h)
            && w > 0 && h > 0)
            aspect = w / h;
        aspect = Math.Clamp(aspect, 0.85, 2.6);
        return (int)Math.Clamp(colW / aspect, 120, 520);
    }

    public void MarkFavorited()
    {
        _favorited = true;
        _btnFav.Text = "已收藏";
    }

    public void MarkUnfavorited()
    {
        _favorited = false;
        _btnFav.Text = "收藏";
    }

    /// <summary>收藏视图模式：按钮变为「取消收藏」，点击仍触发 OnFavorite（由调用方执行移除）。</summary>
    public void SetRemovableFavorite()
    {
        _btnFav.Text = _favorited ? "取消收藏" : "收藏";
        _btnFav.Width = 66;
    }

    /// <summary>释放卡片占用的缩略图，避免反复切换频道累积内存。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var img = _pic.Image;
            _pic.Image = null;
            img?.Dispose();
        }
        base.Dispose(disposing);
    }
}
