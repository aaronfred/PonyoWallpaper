namespace PonyoWallpaper;

/// <summary>
/// 多列瀑布流面板（自绘布局，替代 FlowLayoutPanel 固定网格）：
/// · 列数随窗口宽度自适应（2~6 列，每列理想宽约 240px）
/// · 卡片不等高：按图片宽高比换算列高，放入当前最矮的列（真瀑布流）
/// · 懒加载：卡片进入可视区（含下边预备区）才触发 OnCardVisible，由主窗口加载缩略图，
///   翻页不再产生一次性网络风暴，未滚到的卡片不占内存
/// </summary>
internal sealed class MasonryPanel : Panel
{
    private const int Pad = 10;     // 面板内边距
    private const int Gap = 10;     // 卡片间距
    private const int IdealColWidth = 240;

    private readonly List<WallpaperCard> _cards = new();
    private readonly HashSet<WallpaperCard> _thumbRequested = new();
    private bool _layouting;

    /// <summary>卡片进入可视区（只对每张卡片触发一次）。</summary>
    public event Action<WallpaperCard>? OnCardVisible;

    /// <summary>滚动到距底部 60px 内（主窗口据此无限加载下一页）。
    /// 触发点：OnScroll / 滚轮 / 尺寸变化 / 布局完成。</summary>
    public event Action? OnNearBottom;

    public MasonryPanel()
    {
        AutoScroll = true;
        DoubleBuffered = true;
    }

    public int CardCount => _cards.Count;
    public IReadOnlyList<WallpaperCard> Cards => _cards;

    /// <summary>常驻卡片上限。超出后释放最早加入的卡片（含其缩略图），
    /// 避免同一频道无限下拉时缩略图持续堆积——这是后台内存偏高的主因。</summary>
    private const int MaxCards = 240;

    public void AddCard(WallpaperCard card)
    {
        _cards.Add(card);
        Controls.Add(card);
        TrimExcess();
        Relayout();
        RequestVisibleThumbs();
    }

    /// <summary>从最早加入的卡片开始释放，直到数量回到上限内。</summary>
    private void TrimExcess()
    {
        while (_cards.Count > MaxCards)
        {
            var oldest = _cards[0];
            _cards.RemoveAt(0);
            _thumbRequested.Remove(oldest);
            Controls.Remove(oldest);
            oldest.Dispose();
        }
    }

    public void RemoveCard(WallpaperCard card)
    {
        _cards.Remove(card);
        _thumbRequested.Remove(card);
        Controls.Remove(card);
        card.Dispose();
        Relayout();
    }

    /// <summary>清空并释放全部卡片（切换频道/换一批时调用）。</summary>
    public void ClearCards()
    {
        foreach (var c in _cards) c.Dispose();
        _cards.Clear();
        _thumbRequested.Clear();
        Controls.Clear();
        AutoScrollPosition = new Point(0, 0);
    }

    /// <summary>按窗口宽度重排列：列数自适应，逐卡片放入最矮列。</summary>
    public void Relayout()
    {
        if (_layouting) return;
        _layouting = true;
        try
        {
            var avail = ClientSize.Width - Pad * 2;
            if (avail < 300 || _cards.Count == 0) return;

            var columns = Math.Clamp(avail / IdealColWidth, 2, 6);
            var colW = (avail - (columns - 1) * Gap) / columns;
            var heights = new int[columns];

            foreach (var card in _cards)
            {
                if (card.IsDisposed) continue;
                var h = card.DesiredHeight(colW);
                var col = 0;
                for (var i = 1; i < columns; i++)
                    if (heights[i] < heights[col]) col = i;
                card.SetBounds(Pad + col * (colW + Gap), Pad + heights[col], colW, h);
                heights[col] += h + Gap;
            }
            AutoScrollMinSize = new Size(0, heights.Max() + Pad);
        }
        finally { _layouting = false; }
    }

    /// <summary>滚轮滚动（无焦点时由主窗口 MessageFilter 转发调用）：按滚轮量移动滚动位置。</summary>
    public void ScrollWheel(int delta)
    {
        var maxY = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
        var y = Math.Clamp(-AutoScrollPosition.Y - Math.Sign(delta) * 120, 0, maxY);
        AutoScrollPosition = new Point(0, y);
        CheckNearBottom();
        RequestVisibleThumbs();
    }

    /// <summary>距底部 60px 内判定（无限下拉触发）。</summary>
    private void CheckNearBottom()
    {
        var vs = VerticalScroll;
        if (vs.Visible && vs.Value + ClientSize.Height >= vs.Maximum - 60)
            OnNearBottom?.Invoke();
    }

    /// <summary>可视区（向下多预备半屏）内未加载过缩略图的卡片触发懒加载。</summary>
    public void RequestVisibleThumbs()
    {
        if (_layouting) return;
        var scrollY = -AutoScrollPosition.Y;
        var viewport = new Rectangle(0, scrollY - 120, ClientSize.Width, ClientSize.Height + 360);
        foreach (var card in _cards)
        {
            if (card.IsDisposed || _thumbRequested.Contains(card)) continue;
            if (card.Bounds.IntersectsWith(viewport))
            {
                _thumbRequested.Add(card);
                OnCardVisible?.Invoke(card);
            }
        }
        CheckNearBottom();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
        RequestVisibleThumbs();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        CheckNearBottom();
        RequestVisibleThumbs();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        CheckNearBottom();
        RequestVisibleThumbs();
    }
}
