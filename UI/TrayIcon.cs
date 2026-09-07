namespace PonyoWallpaper;

/// <summary>
/// 系统托盘图标 + 右键菜单。
/// - 左键双击 / 单击打开主界面
/// - 右键菜单：下一张 / 上一张 / 打开主界面 / 设置 / 退出
/// - 托盘标题（Tooltip）显示当前频道 + 下一张倒计时
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _ni;
    private readonly ContextMenuStrip _menu;

    public event Action? OnShowMain;
    public event Action? OnSettings;
    public event Action? OnExit;

    public TrayIcon()
    {
        _menu = new ContextMenuStrip();

        var next = new ToolStripMenuItem("下一张壁纸");
        next.Click += (_, _) => OnNext?.Invoke();

        var prev = new ToolStripMenuItem("上一张（缓存随机）");
        prev.Click += (_, _) => OnPrev?.Invoke();

        var show = new ToolStripMenuItem("打开主界面");
        show.Click += (_, _) => OnShowMain?.Invoke();

        var settings = new ToolStripMenuItem("设置");
        settings.Click += (_, _) => OnSettings?.Invoke();

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => OnExit?.Invoke();

        _menu.Items.AddRange(new ToolStripItem[]
        {
            next, prev, new ToolStripSeparator(), show, settings, new ToolStripSeparator(), exit
        });

        _ni = new NotifyIcon
        {
            Icon = IconHelper.LoadAppIcon(),
            Text = "Ponyo壁纸",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _ni.DoubleClick += (_, _) => OnShowMain?.Invoke();
        ApplyTheme(ThemeManager.LastDark); // 右键菜单跟随当前深浅色（此前深色模式下是突兀的白底菜单）
    }

    /// <summary>托盘右键菜单深浅色适配（主题切换时由 Program 回调）。</summary>
    public void ApplyTheme(bool dark) => ThemeManager.Apply(_menu, dark);

    public event Action? OnNext;
    public event Action? OnPrev;

    public void SetTooltip(string text)
    {
        // WinForms NotifyIcon 提示上限 63 字符，超长截断
        _ni.Text = text.Length > 63 ? text[..63] : text;
        _ni.Visible = true;
    }

    public void ShowBalloon(string title, string text)
    {
        _ni.BalloonTipTitle = title;
        _ni.BalloonTipText = text;
        _ni.BalloonTipIcon = ToolTipIcon.Info;
        _ni.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _ni.Visible = false;
        _ni.Dispose();
        _menu.Dispose();
    }
}