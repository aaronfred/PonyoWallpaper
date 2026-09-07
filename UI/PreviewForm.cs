namespace PonyoWallpaper;

/// <summary>
/// 大图预览浮层：黑底窗口预览原图。←/→ 切换（循环）、Esc 关闭、
/// 双击图片或「设为壁纸」按钮应用（走主窗口同一下载/记录逻辑）。
/// 原图优先取缓存，未命中时经当前代理下载（同时写入缓存，之后设壁纸零等待）。
/// </summary>
internal sealed class PreviewForm : Form
{
    private readonly List<WallpaperItem> _items;
    private int _index;
    private readonly WallhavenClient _api;
    private readonly CacheManager _cache;
    private readonly ListStore _favorites;
    private readonly Func<WallpaperItem, Task> _onSet;
    private readonly Action<WallpaperItem> _onToggleFav;

    private readonly PictureBox _pic = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.FromArgb(16, 16, 16)
    };
    private readonly Panel _bottom = new() { Dock = DockStyle.Bottom, Height = 40, BackColor = Color.FromArgb(24, 24, 24), Padding = new Padding(10, 4, 8, 4) };
    private readonly Label _info = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(220, 220, 220),
        Font = new Font("Microsoft YaHei UI", 9),
        AutoEllipsis = true
    };
    private readonly Label _loading = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(200, 200, 200),
        Font = new Font("Microsoft YaHei UI", 10),
        BackColor = Color.FromArgb(16, 16, 16),
        Text = "原图加载中…"
    };
    private readonly Button _btnSet = new()
    {
        Text = "设为壁纸",
        Dock = DockStyle.Right,
        Width = 82,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(216, 90, 48),
        ForeColor = Color.White,
        Font = new Font("Microsoft YaHei UI", 9)
    };
    private readonly Button _btnFav = new()
    {
        Text = "收藏",
        Dock = DockStyle.Right,
        Width = 64,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(24, 24, 24),
        ForeColor = Color.FromArgb(220, 220, 220),
        Font = new Font("Microsoft YaHei UI", 9)
    };
    private readonly Button _btnClose = new()
    {
        Text = "✕",
        Size = new Size(32, 30),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(24, 24, 24),
        ForeColor = Color.FromArgb(220, 220, 220),
        Font = new Font("Segoe UI Symbol", 10)
    };
    private CancellationTokenSource? _cts;

    public PreviewForm(List<WallpaperItem> items, int index, WallhavenClient api, CacheManager cache,
        ListStore favorites, Func<WallpaperItem, Task> onSet, Action<WallpaperItem> onToggleFav)
    {
        _items = items;
        _index = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
        _api = api;
        _cache = cache;
        _favorites = favorites;
        _onSet = onSet;
        _onToggleFav = onToggleFav;

        Text = "大图预览";
        Width = 980;
        Height = 680;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(16, 16, 16);
        KeyPreview = true;
        Font = new Font("Microsoft YaHei UI", 9);

        _pic.MouseEnter += (_, _) => Cursor = Cursors.Hand;
        _pic.MouseLeave += (_, _) => Cursor = Cursors.Default;
        _pic.DoubleClick += (_, _) => _ = SetWallpaperAsync();

        _btnSet.Click += (_, _) => _ = SetWallpaperAsync();
        _btnFav.Click += (_, _) =>
        {
            var item = _items[_index];
            _onToggleFav(item);
            UpdateFavButton(item);
        };

        _bottom.Controls.Add(_info);
        _bottom.Controls.Add(_btnFav);
        _bottom.Controls.Add(_btnSet);

        _btnClose.Location = new Point(Width - 46, 8);
        _btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnClose.Click += (_, _) => Close();

        Controls.Add(_pic);
        Controls.Add(_bottom);
        Controls.Add(_loading);
        Controls.Add(_btnClose);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Left) Navigate(-1);
            else if (e.KeyCode == Keys.Right) Navigate(1);
        };
        FormClosed += (_, _) => Cleanup();

        _ = LoadCurrentAsync();
    }

    private void Navigate(int dir)
    {
        if (_items.Count == 0) return;
        _index = (_index + dir + _items.Count) % _items.Count;
        _ = LoadCurrentAsync();
    }

    private async Task LoadCurrentAsync()
    {
        var item = _items[_index];
        _info.Text = $"{item.Id} · {item.Resolution} · 第 {_index + 1}/{_items.Count} 张 · ←→ 切换，Esc 关闭";
        UpdateFavButton(item);
        _loading.Visible = true;
        _loading.Text = "原图加载中…";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var fullPath = _cache.FullPath(item.Id);
            if (!File.Exists(fullPath))
            {
                try { await _api.DownloadAsync(item.Path, fullPath, ct); }
                catch when (item.Path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                            && item.Path.Contains("/full/", StringComparison.OrdinalIgnoreCase))
                {
                    // 部分图为 png：jpg 404 时回退一次
                    await _api.DownloadAsync(item.Path[..^4] + ".png", fullPath, ct);
                }
            }
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            using var ms = new MemoryStream(bytes);
            using var decoded = Image.FromStream(ms);
            var img = new Bitmap(decoded); // 脱离流生命周期，ms 可安全释放
            if (ct.IsCancellationRequested) { img.Dispose(); return; }

            var old = _pic.Image;
            _pic.Image = img;
            old?.Dispose();
            _loading.Visible = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _loading.Text = "加载失败：" + ex.Message + "（←→ 可切换其他图）";
            Logger.Warn($"preview load {item.Id}: {ex.Message}");
        }
    }

    private async Task SetWallpaperAsync()
    {
        var item = _items[_index];
        _btnSet.Enabled = false;
        var prev = _btnSet.Text;
        _btnSet.Text = "应用中…";
        try
        {
            await _onSet(item);
            _info.Text = $"已设为壁纸：{item.Id}";
        }
        catch (Exception ex)
        {
            _info.Text = "设置失败：" + ex.Message;
        }
        finally
        {
            _btnSet.Enabled = true;
            _btnSet.Text = prev;
        }
    }

    private void UpdateFavButton(WallpaperItem item)
    {
        _btnFav.Text = _favorites.Contains(item.Id) ? "已收藏" : "收藏";
    }

    private void Cleanup()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        var img = _pic.Image;
        _pic.Image = null;
        img?.Dispose();
    }
}
