namespace PonyoWallpaper;

/// <summary>
/// 隐藏分类访问密码输入框。输入正确由调用方用 HiddenAuth.Verify 校验。
/// </summary>
internal sealed class PasswordDialog : Form
{
    private readonly TextBox _pwd = new();

    public string Password => _pwd.Text;

    public PasswordDialog()
    {
        Text = "访问验证";
        Width = 340;
        Height = 150;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var lbl = new Label { Text = "请输入隐藏分类密码：", Location = new Point(16, 16), AutoSize = true };
        _pwd.SetBounds(16, 42, 290, 26);
        _pwd.UseSystemPasswordChar = true;

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(148, 76), Size = new Size(76, 28) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(230, 76), Size = new Size(76, 28) };
        ok.BackColor = Color.FromArgb(216, 90, 48);
        ok.ForeColor = Color.White;
        ok.FlatStyle = FlatStyle.Flat;
        ok.FlatAppearance.BorderSize = 0;

        Controls.AddRange(new Control[] { lbl, _pwd, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
        ThemeManager.Apply(this, ThemeManager.LastDark); // 跟随当前深浅色（深色下此前是突兀的白框）
    }
}

/// <summary>
/// 修改隐藏分类密码：需验证旧密码，新密码需两次一致。
/// 校验通过后由调用方写入配置哈希。
/// </summary>
internal sealed class ChangePasswordDialog : Form
{
    private readonly TextBox _old = new();
    private readonly TextBox _new = new();
    private readonly TextBox _confirm = new();

    public string OldPassword => _old.Text;
    public string NewPassword => _new.Text;
    public string ConfirmPassword => _confirm.Text;

    public ChangePasswordDialog()
    {
        Text = "修改隐藏分类密码";
        Width = 360;
        Height = 220;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        AddRow("旧密码：", _old, 16);
        AddRow("新密码：", _new, 50);
        AddRow("确认新密码：", _confirm, 84);
        foreach (var t in new[] { _old, _new, _confirm }) t.UseSystemPasswordChar = true;

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(164, 130), Size = new Size(76, 28) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(248, 130), Size = new Size(76, 28) };
        ok.BackColor = Color.FromArgb(216, 90, 48);
        ok.ForeColor = Color.White;
        ok.FlatStyle = FlatStyle.Flat;
        ok.FlatAppearance.BorderSize = 0;

        Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
        ThemeManager.Apply(this, ThemeManager.LastDark); // 跟随当前深浅色
    }

    private void AddRow(string label, TextBox box, int y)
    {
        var lbl = new Label { Text = label, Location = new Point(16, y + 3), AutoSize = true };
        box.SetBounds(110, y, 214, 26);
        Controls.Add(lbl);
        Controls.Add(box);
    }
}
/// <summary>
/// 首次设置访问密码对话框（无出厂密码：初次唤出隐藏面板时由用户自行设定）。
/// </summary>
internal sealed class SetPasswordDialog : Form
{
    private readonly TextBox _new = new();
    private readonly TextBox _confirm = new();

    public string NewPassword => _new.Text;
    public string ConfirmPassword => _confirm.Text;

    public SetPasswordDialog()
    {
        Text = "设置隐藏分类密码";
        Width = 360;
        Height = 200;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var tip = new Label
        {
            Text = "首次使用：请先设置隐藏分类的访问密码",
            Location = new Point(16, 14),
            AutoSize = true,
            ForeColor = Color.FromArgb(216, 90, 48)
        };

        void AddRow(string label, TextBox box, int y)
        {
            Controls.Add(new Label { Text = label, Location = new Point(16, y + 4), AutoSize = true });
            box.Location = new Point(110, y);
            box.Width = 214;
            Controls.Add(box);
        }
        AddRow("新密码：", _new, 46);
        AddRow("确认新密码：", _confirm, 80);
        foreach (var t in new[] { _new, _confirm }) t.UseSystemPasswordChar = true;

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(164, 126), Size = new Size(76, 28) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(248, 126), Size = new Size(76, 28) };
        ok.BackColor = Color.FromArgb(216, 90, 48);
        ok.ForeColor = Color.White;
        ok.FlatStyle = FlatStyle.Flat;
        Controls.Add(ok);
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}
