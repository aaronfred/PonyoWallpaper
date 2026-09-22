using Microsoft.Win32;

namespace PonyoWallpaper;

/// <summary>
/// 主题管理。支持浅色 / 深色 / 跟随系统。
/// WinForms 无原生深色，采用递归着色容器 + 文字 + 输入控件；
/// 按钮保持原色（含珊瑚色强调按钮），避免破坏品牌色。
/// </summary>
internal static class ThemeManager
{
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (int?)key?.GetValue("AppsUseLightTheme") == 0;
        }
        catch { return false; }
    }

    /// <summary>根据配置（0=auto/1=light/2=dark）判断是否用深色。</summary>
    public static bool ShouldUseDark(int themeSetting) => themeSetting switch
    {
        2 => true,
        1 => false,
        _ => IsSystemDark()
    };

    /// <summary>最近一次应用的主题。供没有 cfg 的弹窗（密码框）与托盘菜单跟随当前深浅色。</summary>
    public static bool LastDark { get; private set; }

    public static void Apply(Control root, bool dark)
    {
        LastDark = dark;
        var bg = dark ? Color.FromArgb(30, 30, 30) : Color.White;
        var panel = dark ? Color.FromArgb(37, 37, 38) : Color.FromArgb(245, 245, 247);
        var fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(30, 30, 30);
        // 深色下次级文字同样保持高亮度（用户要求设置窗字体全白）
        var fg2 = dark ? Color.FromArgb(230, 230, 230) : Color.FromArgb(120, 120, 120);
        var inputBg = dark ? Color.FromArgb(45, 45, 48) : Color.White;
        var border = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(200, 200, 200);
        var accent = Color.FromArgb(216, 90, 48);

        // ToolStrip 系（托盘右键菜单等）：菜单项不在 Control 树里，需单独着色
        if (root is ToolStrip ts) ApplyToolStrip(ts, dark, panel, fg);

        ApplyRecursive(root, dark, bg, panel, fg, fg2, inputBg, border, accent);
    }

    /// <summary>
    /// 下拉菜单（ContextMenuStrip / ToolStripDropDown）深浅色适配，递归全部层级的子菜单。
    /// 调用方如需保留个别项的特殊前景色（如未就绪源的灰字），在本方法之后覆盖即可。
    /// </summary>
    public static void ApplyMenu(ToolStripDropDown menu, bool dark)
    {
        var bg = dark ? Color.FromArgb(37, 37, 38) : Color.White;
        var fg = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(30, 30, 30);
        ApplyToolStrip(menu, dark, bg, fg);
    }

    private static void ApplyToolStrip(ToolStrip ts, bool dark, Color bg, Color fg)
    {
        // 保持默认 Professional 渲染器：System 渲染器会忽略自定义 BackColor
        ts.BackColor = dark ? bg : Color.White;
        ts.ForeColor = fg;
        WalkMenuItems(ts.Items, dark, bg, fg);
    }

    private static void WalkMenuItems(ToolStripItemCollection items, bool dark, Color bg, Color fg)
    {
        foreach (ToolStripItem item in items)
        {
            item.BackColor = dark ? bg : Color.White;
            item.ForeColor = fg;
            // 级联子菜单：容器与子项都要着色（此前轮换菜单只铺了两层）
            if (item is ToolStripMenuItem mi && mi.HasDropDownItems)
            {
                mi.DropDown.BackColor = dark ? bg : Color.White;
                mi.DropDown.ForeColor = fg;
                WalkMenuItems(mi.DropDownItems, dark, bg, fg);
            }
        }
    }

    private static void ApplyRecursive(Control c, bool dark, Color bg, Color panel, Color fg, Color fg2,
        Color inputBg, Color border, Color accent)
    {
        switch (c)
        {
            case Form f:
                f.BackColor = bg;
                break;
            case FlowLayoutPanel _:
            case TableLayoutPanel _:
            case TreeView _:
            case ListView _:
                c.BackColor = panel;
                c.ForeColor = fg;
                break;
            case WallpaperCard wc:
                wc.ApplyTheme(dark, panel, fg, fg2, inputBg, border);
                break;
            case Panel _:
                c.BackColor = panel;
                break;
            case Label l when l.Tag is not "self":
                // Tag="self" 的标签（如代理状态条）自带按状态/主题着色逻辑，这里不接管，
                // 否则主题切换时会把状态色（橙=需恢复）覆盖成 fg2
                l.ForeColor = fg2;
                break;
            case CheckBox chk:
                chk.ForeColor = fg;
                break;
            case TextBox t:
                t.BackColor = inputBg;
                t.ForeColor = fg;
                break;
            case ComboBox cb:
                cb.BackColor = inputBg;
                cb.ForeColor = fg;
                // DropDownList 下 Standard 样式的下拉框面色不吃 BackColor（实测仍是白脸），
                // 深色下切 Flat 才能让深色生效；浅色保持系统原生观感
                cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                break;
            case NumericUpDown n:
                n.BackColor = inputBg;
                n.ForeColor = fg;
                break;
            case PictureBox pb:
                pb.BackColor = inputBg;
                break;
            case Button b:
                // 珊瑚色强调按钮保持原样，其余按钮深色化
                if (b.BackColor == accent) break;
                b.BackColor = inputBg;
                b.ForeColor = fg;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = border;
                break;
        }
        foreach (Control child in c.Controls)
            ApplyRecursive(child, dark, bg, panel, fg, fg2, inputBg, border, accent);
    }
}