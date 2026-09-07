using System.Diagnostics;

namespace PonyoWallpaper;

/// <summary>
/// hosts 管理页（通用 hosts 管理能力）：
/// 顶部只读框 = 最新 hosts（系统 hosts 中本程序的映射段；拉取成功后同步刷新）。
/// ① 需要加速的网站 —— 写入 hosts 的目标域名（默认 wallhaven 三站点）
/// ② 源地址         —— 定期更新的 hosts 内容网址，右侧「更新」探测并保留最优 1~3 个
/// ③ git 加速地址   —— 可加速 GitHub 的前缀，右侧「更新」探测并保留最优 1~3 个
/// 底部操作：更新 hosts（拉取并写入系统 hosts，需管理员）/ 移除 hosts / 打开 hosts。
/// hosts 仅用于访问 wallhaven 官网，本程序主要依赖代理（直连/反代/代理池）。
/// </summary>
internal sealed class HostsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly TextBox _currentHosts = new();
    private readonly TextBox _sites = new();
    private readonly TextBox _sources = new();
    private readonly TextBox _accels = new();
    private readonly Label _status = new();
    private readonly Button _btnUpdate = new();
    private readonly Button _btnRemove = new();
    private readonly Button _btnOpen = new();
    private readonly Button _btnPickSources = new();
    private readonly Button _btnPickAccels = new();
    private readonly System.Windows.Forms.Timer _sitesTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _srcTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _accTimer = new() { Interval = 700 };
    private bool _busy;

    private const int BoxW = 624;
    private static readonly Font Mono = new("Consolas", 9);

    public HostsForm(AppConfig cfg)
    {
        _cfg = cfg;

        Text = "hosts 管理";
        Width = 664;
        Height = 500;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9);

        Build();
        LoadLists();
        RefreshCurrentHosts();
        ThemeManager.Apply(this, ThemeManager.ShouldUseDark(_cfg.Theme));
    }

    private void Build()
    {
        // —— 顶部提示 ——
        var hint = new Label
        {
            Text = "hosts 仅用于访问 wallhaven 官网（wallhaven.cc / th.wallhaven.cc / w.wallhaven.cc）。" +
                   "本程序主要依赖代理（直连 / 反代 / 代理池）；hosts 是最后的备用手段，默认不会自动替换系统 hosts。",
            Location = new Point(12, 8),
            Size = new Size(BoxW, 42),
            ForeColor = Color.FromArgb(150, 110, 40),
            Font = new Font("Microsoft YaHei UI", 8.5f)
        };

        // —— 最新 hosts（只读展示）——
        _currentHosts.SetBounds(12, 60, BoxW, 84);
        _currentHosts.Multiline = true;
        _currentHosts.ReadOnly = true;
        _currentHosts.ScrollBars = ScrollBars.Vertical;
        _currentHosts.WordWrap = false;
        _currentHosts.Font = Mono;
        _currentHosts.BackColor = Color.FromArgb(250, 250, 250);

        // —— ① 需要加速的网站 ——
        Controls.Add(new Label
        {
            Text = "① 需要加速的网站（每行一个域名，写入 hosts 的目标；默认 wallhaven 三站点）",
            Location = new Point(12, 150),
            AutoSize = true
        });
        _sites.SetBounds(12, 172, BoxW, 52);
        _sites.Multiline = true;
        _sites.ScrollBars = ScrollBars.Vertical;
        _sites.WordWrap = false;
        _sites.Font = Mono;
        _sites.AcceptsReturn = true;
        _sites.TextChanged += (_, _) => { _sitesTimer.Stop(); _sitesTimer.Start(); };
        _sitesTimer.Tick += (_, _) => { _sitesTimer.Stop(); SaveLists(); };
        _sites.Leave += (_, _) => SaveLists();

        // —— ② 源地址 + 更新 ——
        Controls.Add(new Label
        {
            Text = "② 源地址（定期更新的 hosts 内容网址，一行一个）",
            Location = new Point(12, 228),
            AutoSize = true
        });
        MakeFlatButton(_btnPickSources, "更新", 544, 224);
        _btnPickSources.Width = 92;
        _sources.SetBounds(12, 250, BoxW, 52);
        _sources.Multiline = true;
        _sources.ScrollBars = ScrollBars.Vertical;
        _sources.WordWrap = false;
        _sources.Font = Mono;
        _sources.AcceptsReturn = true;
        _sources.TextChanged += (_, _) => { _srcTimer.Stop(); _srcTimer.Start(); };
        _srcTimer.Tick += (_, _) => { _srcTimer.Stop(); SaveLists(); };
        _sources.Leave += (_, _) => SaveLists();

        // —— ③ git 加速地址 + 更新 ——
        Controls.Add(new Label
        {
            Text = "③ git 加速地址（可加速 GitHub 的前缀，一行一个，如 https://ghproxy.net/）",
            Location = new Point(12, 306),
            AutoSize = true
        });
        MakeFlatButton(_btnPickAccels, "更新", 544, 302);
        _btnPickAccels.Width = 92;
        _accels.SetBounds(12, 328, BoxW, 52);
        _accels.Multiline = true;
        _accels.ScrollBars = ScrollBars.Vertical;
        _accels.WordWrap = false;
        _accels.Font = Mono;
        _accels.AcceptsReturn = true;
        _accels.TextChanged += (_, _) => { _accTimer.Stop(); _accTimer.Start(); };
        _accTimer.Tick += (_, _) => { _accTimer.Stop(); SaveLists(); };
        _accels.Leave += (_, _) => SaveLists();

        // —— 底部操作行 ——
        _btnUpdate.Text = "更新 hosts";
        _btnUpdate.SetBounds(12, 392, 110, 30);
        _btnUpdate.FlatStyle = FlatStyle.Flat;
        _btnUpdate.FlatAppearance.BorderSize = 0;
        _btnUpdate.BackColor = Color.FromArgb(216, 90, 48);
        _btnUpdate.ForeColor = Color.White;
        _btnUpdate.Click += async (_, _) => await UpdateHostsAsync();

        MakeFlatButton(_btnRemove, "移除 hosts", 130, 392);
        _btnRemove.Click += async (_, _) => await RemoveHostsAsync();
        MakeFlatButton(_btnOpen, "打开 hosts", 248, 392);
        _btnOpen.Click += (_, _) => OpenHostsFile();

        // —— 状态栏 ——
        _status.SetBounds(12, 430, BoxW, 22);
        _status.ForeColor = Color.FromArgb(120, 120, 120);
        _status.AutoEllipsis = true;

        var tips = new ToolTip();
        tips.SetToolTip(_currentHosts, "系统 hosts 中本程序写入的映射段；「更新 hosts」拉取成功后同步刷新");
        tips.SetToolTip(_sites, "需要加速的网站域名（后缀）；源地址拉到的条目对这些域名生效");
        tips.SetToolTip(_sources, "定期更新的 hosts 内容网址；「更新」实测可达性并保留最优 1~3 个");
        tips.SetToolTip(_accels, "git 加速前缀（建议以 / 结尾）；源地址直连全部失败时套用；「更新」实测并保留最优 1~3 个");
        tips.SetToolTip(_btnUpdate, "从源地址拉取最新 hosts 映射并写入系统 hosts（需管理员权限）");
        tips.SetToolTip(_btnRemove, "仅移除本程序写入的标记段，其他 hosts 条目不受影响");
        tips.SetToolTip(_btnOpen, "用记事本打开系统 hosts 文件");
        tips.SetToolTip(_btnPickSources, "实测源地址（含 git 加速组合），保留最优 1~3 个并回填");
        tips.SetToolTip(_btnPickAccels, "实测 git 加速地址，保留最优 1~3 个并回填");

        Controls.AddRange(new Control[]
        {
            hint, _currentHosts,
            _sites, _sources, _accels,
            _btnPickSources, _btnPickAccels,
            _btnUpdate, _btnRemove, _btnOpen,
            _status
        });
    }

    private static void MakeFlatButton(Button b, string text, int x, int y)
    {
        b.Text = text;
        b.SetBounds(x, y, 110, 30);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
    }

    /// <summary>刷新「最新 hosts」展示框：系统 hosts 中本程序的映射段。</summary>
    private void RefreshCurrentHosts()
    {
        var block = HostsUpdater.ReadBlock();
        _currentHosts.Text = string.IsNullOrWhiteSpace(block)
            ? "（系统 hosts 中暂无本程序写入的映射段）"
            : block;
    }

    /// <summary>载入三个文本框；未配置过时回填内置默认值。</summary>
    private void LoadLists()
    {
        _sites.Text = string.Join(Environment.NewLine,
            _cfg.HostsSites is { Count: > 0 } ? _cfg.HostsSites : ProxyModule.DefaultProxiedHosts);
        _sources.Text = string.Join(Environment.NewLine,
            _cfg.HostsSourceUrls is { Count: > 0 } ? _cfg.HostsSourceUrls : HostsUpdater.DefaultSourceUrls);
        _accels.Text = string.Join(Environment.NewLine,
            _cfg.HostsAccelUrls is { Count: > 0 } ? _cfg.HostsAccelUrls : HostsUpdater.DefaultAccelUrls);
    }

    private static List<string> ParseLines(string text) =>
        text.Split('\n')
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Distinct()
            .ToList();

    private static bool SameList(List<string>? a, List<string> b) =>
        a != null && a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);

    /// <summary>任一文本框有改动即落盘（去重、去空行）。</summary>
    private void SaveLists()
    {
        var sites = ParseLines(_sites.Text).Select(s => s.ToLowerInvariant()).ToList();
        var src = ParseLines(_sources.Text);
        var acc = ParseLines(_accels.Text);
        if (SameList(_cfg.HostsSites, sites) && SameList(_cfg.HostsSourceUrls, src) && SameList(_cfg.HostsAccelUrls, acc))
            return;

        _cfg.HostsSites = sites;
        _cfg.HostsSourceUrls = src;
        _cfg.HostsAccelUrls = acc;
        _cfg.Save();
        SetStatus("地址已保存");
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>状态栏宽度有限，超长 URL 截断显示。</summary>
    private static string Brief(string url) => url.Length <= 54 ? url : url[..51] + "…";

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnUpdate.Enabled = !_busy;
        _btnRemove.Enabled = !_busy;
        _btnOpen.Enabled = !_busy;
        _btnPickSources.Enabled = !_busy;
        _btnPickAccels.Enabled = !_busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    /// <summary>优选源地址：从候选（当前源 + 内置源 + git 加速组合）实测保留最优 1~3 个并回填。</summary>
    private async Task PickSourcesAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            SaveLists();
            SetStatus("正在优选源地址…");
            var best = await HostsUpdater.PickBestSourcesAsync(
                _cfg.HostsSourceUrls, _cfg.HostsAccelUrls, keep: 3, log: s => SetStatus(s));
            if (best.Count == 0)
            {
                SetStatus("优选失败：候选源地址均不可达，请检查网络或 git 加速地址");
                return;
            }
            _sources.Text = string.Join(Environment.NewLine, best);
            SaveLists();
            SetStatus($"已优选 {best.Count} 个源地址并保存");
        }
        catch (Exception ex)
        {
            SetStatus("优选失败：" + ex.Message);
        }
        finally { SetBusy(false); }
    }

    /// <summary>优选 git 加速地址：实测各前缀保留最优 1~3 个并回填。</summary>
    private async Task PickAccelsAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            SaveLists();
            SetStatus("正在优选 git 加速地址…");
            var best = await HostsUpdater.PickBestAccelsAsync(_cfg.HostsAccelUrls, keep: 3, log: s => SetStatus(s));
            if (best.Count == 0)
            {
                SetStatus("优选失败：所有 git 加速地址均不可达");
                return;
            }
            _accels.Text = string.Join(Environment.NewLine, best);
            SaveLists();
            SetStatus($"已优选 {best.Count} 个 git 加速地址并保存");
        }
        catch (Exception ex)
        {
            SetStatus("优选失败：" + ex.Message);
        }
        finally { SetBusy(false); }
    }

    /// <summary>更新 hosts：拉取最新内容 → 展示 → 写入系统 hosts（需管理员）。</summary>
    private async Task UpdateHostsAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            SaveLists();
            SetStatus("正在拉取：先直连源地址，不通自动套 git 加速地址…");
            var result = await HostsUpdater.FetchAsync(_cfg.HostsSourceUrls, _cfg.HostsAccelUrls);
            var block = HostsUpdater.ExtractBlock(result.Content);
            SetStatus($"已获取（{(result.ViaAccel ? "git 加速" : "直连")}）：{Brief(result.UsedUrl)}");
            if (string.IsNullOrWhiteSpace(block))
            {
                SetStatus("未获取到有效 hosts 内容");
                MessageBox.Show("未获取到有效 hosts 内容", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 展示最新拉取到的 hosts 内容
            _currentHosts.Text = block;

            if (!HostsUpdater.IsAdmin())
            {
                var tmp = Path.Combine(Path.GetTempPath(), "ponyo_hosts_block.txt");
                File.WriteAllText(tmp, block);
                var exe = Environment.ProcessPath ?? HostsResolveExe();

                if (MessageBox.Show(
                        "更新 hosts 需要管理员权限。\n点击「确定」后请在弹出的 UAC 窗口中确认；取消则不做任何修改。",
                        "需要管理员权限",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                {
                    SetStatus("已取消");
                    return;
                }

                try
                {
                    Process.Start(new ProcessStartInfo(exe, $"--apply-hosts \"{tmp}\"")
                    {
                        Verb = "runas",
                        UseShellExecute = true
                    });
                    SetStatus("已发起提权请求，结果见 UAC 后的弹窗");
                }
                catch (Exception)
                {
                    SetStatus("已取消提权，hosts 未修改");
                }
                return;
            }

            if (HostsUpdater.Apply(block))
            {
                SetStatus("hosts 已更新并刷新 DNS 缓存");
                RefreshCurrentHosts();
            }
            else
            {
                SetStatus("hosts 更新失败，详见日志");
            }
        }
        catch (Exception ex)
        {
            SetStatus("拉取失败");
            MessageBox.Show(
                $"拉取 hosts 源失败：{ex.Message}\n\n" +
                "已尝试：全部源地址直连 → 每个加速地址逐个拼接源地址重试。\n" +
                "可点源地址旁的「更新」优选可达地址后重试。",
                "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { SetBusy(false); }
    }

    /// <summary>定位本程序 exe：优先当前进程路径；否则在同目录找 PonyoWallpaper*.exe。</summary>
    private static string HostsResolveExe()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "PonyoWallpaper.exe");
        if (File.Exists(direct)) return direct;
        try
        {
            var found = Directory.GetFiles(AppContext.BaseDirectory, "PonyoWallpaper*.exe")
                .OrderBy(f => f.Length).FirstOrDefault();
            if (found != null) return found;
        }
        catch { /* 忽略 */ }
        return direct;
    }

    private async Task RemoveHostsAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            if (!HostsUpdater.IsAdmin())
            {
                var exe = Environment.ProcessPath ?? HostsResolveExe();

                if (MessageBox.Show(
                        "移除 hosts 映射需要管理员权限。\n仅删除本程序写入的标记段（标记之间的内容），其他条目不受影响。\n点击「确定」后请在弹出的 UAC 窗口中确认。",
                        "需要管理员权限",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                {
                    SetStatus("已取消");
                    return;
                }

                try
                {
                    Process.Start(new ProcessStartInfo(exe, "--remove-hosts")
                    {
                        Verb = "runas",
                        UseShellExecute = true
                    });
                    SetStatus("已发起提权请求，结果见 UAC 后的弹窗");
                }
                catch (Exception)
                {
                    SetStatus("已取消提权，hosts 未修改");
                }
                return;
            }

            if (HostsUpdater.Remove())
            {
                SetStatus("已移除本程序写入的 wallhaven 段");
                RefreshCurrentHosts();
            }
            else
            {
                SetStatus("移除失败，详见日志");
            }
        }
        finally { SetBusy(false); }
    }

    private void OpenHostsFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{HostsUpdater.HostsPath}\""));
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开 hosts 失败：" + ex.Message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
