using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace UsageTrackerWeb;

internal sealed class CompactStatusForm : Form
{
    private readonly StatusDotControl _statusDot = new();
    private readonly SmoothMarqueeLabel _titleLabel = new();
    private readonly Label _durationLabel = new();
    private readonly Action _showDashboard;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private Point _dragStart;
    private Point _dragStartLocation;
    private bool _movedDuringDrag;

    protected override bool ShowWithoutActivation => true;

    private const int CornerRadius = 10;

    private static GraphicsPath BuildRoundedBounds(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter - 1, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter - 1, bounds.Bottom - diameter - 1, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter - 1, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void ApplyRoundedRegion()
    {
        var region = Region;
        Region = new Region(BuildRoundedBounds(new Rectangle(0, 0, Width, Height), CornerRadius));
        region?.Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedRegion();
        // 小窗固定到所有虚拟桌面（任务视图 / Win+Tab 的每个桌面都可见）
        VirtualDesktopHelper.PinToAllDesktops(Handle);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsHandleCreated) ApplyRoundedRegion();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000;
            return parameters;
        }
    }

    public CompactStatusForm(Action showDashboard)
    {
        _showDashboard = showDashboard;
        Text = "时迹 · 记录状态";
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(340, 34);
        MinimumSize = new Size(300, 34);
        MaximumSize = new Size(340, 34);
        BackColor = NativeTheme.Current.Background;
        Icon = TryLoadIcon();
        Padding = new Padding(8, 0, 6, 0);

        _statusDot.Dock = DockStyle.Left;
        _statusDot.Width = 16;
        _statusDot.BackColor = BackColor;

        // 只保留状态圆点，释放原状态文字和进程名占用的空间给标题滚动。
        _titleLabel.Dock = DockStyle.Fill;
        _titleLabel.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _titleLabel.ForeColor = NativeTheme.Current.TextPrimary;
        _titleLabel.BackColor = BackColor;

        _durationLabel.Dock = DockStyle.Right;
        _durationLabel.Width = 76;
        _durationLabel.Font = new Font("Microsoft YaHei UI", 8.5F);
        _durationLabel.ForeColor = NativeTheme.Current.TextSecondary;
        _durationLabel.TextAlign = ContentAlignment.MiddleRight;
        _durationLabel.AutoEllipsis = true;

        Controls.Add(_titleLabel);
        Controls.Add(_durationLabel);
        Controls.Add(_statusDot);
        MouseUp += (_, e) => HandleMouseUp(e);

        foreach (Control control in Controls)
        {
            control.MouseDown += (_, e) => BeginDrag(e);
            control.MouseMove += (_, e) => MoveDrag(e);
            control.MouseUp += (_, e) => HandleMouseUp(e);
        }
        MouseDown += (_, e) => BeginDrag(e);
        MouseMove += (_, e) => MoveDrag(e);

        _timer.Tick += async (_, _) => await RefreshAsync();
        NativeTheme.Changed += (_, _) => ApplyTheme();
        ApplyTheme();
    }

    private void BeginDrag(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragStart = e.Location;
            _dragStartLocation = Location;
            _movedDuringDrag = false;
        }
    }

    private void MoveDrag(MouseEventArgs e)
    {
        if (_dragStart == Point.Empty || e.Button != MouseButtons.Left) return;
        Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
        if (Math.Abs(Location.X - _dragStartLocation.X) > 3 || Math.Abs(Location.Y - _dragStartLocation.Y) > 3)
            _movedDuringDrag = true;
    }

    private void HandleMouseUp(MouseEventArgs e)
    {
        _dragStart = Point.Empty;
        if (e.Button == MouseButtons.Right)
        {
            // 右键打开网页看板
            _showDashboard();
        }
        else if (e.Button == MouseButtons.Left)
        {
            if (_movedDuringDrag)
            {
                SnapToEdge();
                return;
            }
            // 单击左键立即进入手动空闲
            _ = EnterManualIdleAsync();
        }
    }

    private async Task EnterManualIdleAsync()
    {
        await NativeControlClient.EnsureStartedAndSendAsync("idle");
        await RefreshAsync();
    }

    public void ShowStatus()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        // 默认位置：屏幕正中上方吸顶（贴近工作区顶部，左右居中），与吸边间隙一致不留空隙
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top);
        Show();
        WindowState = FormWindowState.Normal;
        TopMost = true;
        BringToFront();
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>
    /// 松开鼠标时检测窗口是否靠近屏幕边缘（左/右/上），若在阈值内则吸附到该边缘。
    /// 吸附后位置保持在屏幕工作区内，避免被任务栏或其他边缘遮挡。
    /// </summary>
    private void SnapToEdge()
    {
        var area = Screen.FromPoint(Location).WorkingArea;
        const int snapThreshold = 24;
        const int edgeMargin = 0;

        var newX = Location.X;
        var newY = Location.Y;

        // 左边缘吸附
        if (Location.X - area.Left < snapThreshold)
            newX = area.Left + edgeMargin;
        // 右边缘吸附
        else if (area.Right - (Location.X + Width) < snapThreshold)
            newX = area.Right - Width - edgeMargin;

        // 顶部吸附
        if (Location.Y - area.Top < snapThreshold)
            newY = area.Top + edgeMargin;
        // 底部吸附
        else if (area.Bottom - (Location.Y + Height) < snapThreshold)
            newY = area.Bottom - Height - edgeMargin;

        if (newX != Location.X || newY != Location.Y)
            Location = new Point(newX, newY);
    }

    private async Task RefreshAsync()
    {
        var response = await NativeControlClient.GetStatusAsync();
        if (IsDisposed) return;
        if (response?.Status is not { } status || !response.Ok)
        {
            _statusDot.Breathing = false;
            _statusDot.DotColor = NativeTheme.Current.Danger;
            _titleLabel.MarqueeText = "后台记录服务未响应";
            _durationLabel.Text = "--:--:--";
            return;
        }

        _titleLabel.MarqueeText = status.ActiveWindowTitle;

        if (status.Tracking)
        {
            _statusDot.DotColor = Color.FromArgb(58, 190, 105);
            _statusDot.Breathing = status.IsVideoPlayback;
            var start = status.ActiveStartTime?.ToLocalTime();
            _durationLabel.Text = start is null ? "--:--:--" : FormatElapsed(DateTime.Now - start.Value);
        }
        else if (status.IsManualIdle)
        {
            _statusDot.Breathing = false;
            _statusDot.DotColor = Color.FromArgb(242, 190, 55);
            _durationLabel.Text = "未记录";
        }
        else if (status.IsIdle)
        {
            _statusDot.Breathing = false;
            _statusDot.DotColor = Color.FromArgb(230, 76, 76);
            _durationLabel.Text = "未记录";
        }
        else
        {
            _statusDot.Breathing = false;
            _statusDot.DotColor = NativeTheme.Current.TextSecondary;
            _durationLabel.Text = "未记录";
        }
    }

    private static string FormatElapsed(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private void ApplyTheme()
    {
        BackColor = NativeTheme.Current.Background;
        _statusDot.BackColor = BackColor;
        _titleLabel.ForeColor = NativeTheme.Current.TextPrimary;
        _titleLabel.BackColor = BackColor;
        _durationLabel.ForeColor = NativeTheme.Current.TextSecondary;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = BuildRoundedBounds(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var pen = new Pen(NativeTheme.Current.Border, 1);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _titleLabel.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon? TryLoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }
}

internal sealed class StatusDotControl : Control
{
    private Color _dotColor = Color.Gray;
    private bool _breathing;
    private DateTime _breathStartedAt = DateTime.Now;
    private const double BreathPeriodMs = 2800;
    private readonly System.Windows.Forms.Timer _breathTimer = new() { Interval = 30 };

    public Color DotColor
    {
        get => _dotColor;
        set
        {
            if (_dotColor == value) return;
            _dotColor = value;
            Invalidate();
        }
    }

    /// <summary>看视频状态：在状态绿与小窗背景色之间缓慢双向渐变（背景色随主题变量变化）。</summary>
    public bool Breathing
    {
        get => _breathing;
        set
        {
            if (_breathing == value) return;
            _breathing = value;
            if (value)
            {
                _breathStartedAt = DateTime.Now;
                _breathTimer.Start();
            }
            else
            {
                _breathTimer.Stop();
                Invalidate();
            }
        }
    }

    public StatusDotControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _breathTimer.Tick += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var diameter = Math.Min(ClientSize.Height - 12, 10);
        var x = (ClientSize.Width - diameter) / 2;
        var y = (ClientSize.Height - diameter) / 2;
        var color = _dotColor;
        if (_breathing)
        {
            var phase = (DateTime.Now - _breathStartedAt).TotalMilliseconds % BreathPeriodMs / BreathPeriodMs;
            // 0 → 小窗背景色，1 → 状态绿，正弦缓动；一个周期内 背景色→绿→背景色。
            var t = (1 - Math.Cos(2 * Math.PI * phase)) / 2;
            var windowColor = BackColor;
            var r = (int)(windowColor.R + (_dotColor.R - windowColor.R) * t);
            var g = (int)(windowColor.G + (_dotColor.G - windowColor.G) * t);
            var b = (int)(windowColor.B + (_dotColor.B - windowColor.B) * t);
            color = Color.FromArgb(r, g, b);
        }
        using var brush = new SolidBrush(color);
        e.Graphics.FillEllipse(brush, x, y, diameter, diameter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _breathTimer.Stop();
            _breathTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class SmoothMarqueeLabel : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 13 };
    private string _marqueeText = string.Empty;
    private Bitmap? _textBitmap;
    private int _textWidth;
    private int _offset;
    private float _scrollPosition;
    private int _cycleWidth;

    public string? MarqueeText
    {
        get => _marqueeText;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "无标题窗口" : value.Trim();
            if (string.Equals(_marqueeText, next, StringComparison.Ordinal)) return;
            _marqueeText = next;
            _offset = 0;
            _scrollPosition = 0;
            UpdateScrollState();
            Invalidate();
        }
    }

    public SmoothMarqueeLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _timer.Tick += (_, _) =>
        {
            if (_cycleWidth <= 0) return;
            _scrollPosition += 1.2F;
            if (_scrollPosition >= _cycleWidth) _scrollPosition -= _cycleWidth;
            _offset = (int)_scrollPosition;
            Invalidate();
        };
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateScrollState();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollState();
    }

    private void UpdateScrollState()
    {
        if (ClientSize.Width <= 0 || string.IsNullOrEmpty(_marqueeText)) return;
        _textBitmap?.Dispose();
        _textBitmap = null;
        using var measureBitmap = new Bitmap(1, 1);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        _textWidth = Math.Max(1, (int)Math.Ceiling(measureGraphics.MeasureString(_marqueeText, Font).Width));
        _cycleWidth = _textWidth + 32;
        if (_textWidth > ClientSize.Width)
        {
            _textBitmap = new Bitmap(_textWidth, Math.Max(1, ClientSize.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(_textBitmap);
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(BackColor);
            using var brush = new SolidBrush(ForeColor);
            var y = (ClientSize.Height - Font.GetHeight(graphics)) / 2F;
            graphics.DrawString(_marqueeText, Font, brush, 0, y);
            if (Visible) _timer.Start();
        }
        else
        {
            _timer.Stop();
            _offset = 0;
        }
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) UpdateScrollState();
        else _timer.Stop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        if (_textBitmap is null)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var brush = new SolidBrush(ForeColor);
            var y = (ClientSize.Height - Font.GetHeight(e.Graphics)) / 2F;
            e.Graphics.DrawString(_marqueeText, Font, brush, 0, y);
            return;
        }
        var x = -_offset;
        while (x < ClientSize.Width)
        {
            e.Graphics.DrawImageUnscaled(_textBitmap, x, 0);
            x += _cycleWidth;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _textBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }
}
