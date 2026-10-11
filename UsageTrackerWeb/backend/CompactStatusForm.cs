using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsageTrackerWeb;

/// <summary>
/// 桌面悬浮小窗：分层窗口（UpdateLayeredWindow 逐像素 alpha）。
/// 背景、边框与手绘柔光应用“小窗背景透明度”；透明度为 0 时三者全部消失，仅保留文字与状态灯。
/// </summary>
internal sealed class CompactStatusForm : Form
{
    private const int WindowWidth = 340;
    private const int WindowHeight = 34;
    private const int CornerRadius = 10;
    private const int WsExLayered = 0x00080000;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    // 手绘柔光的扩散层数与基准不透明度（最终 alpha 再乘小窗背景透明度，透明度 0 时整层不绘制）
    private const int ShadowSpread = 6;
    private const int ShadowBaseAlpha = 95;

    private readonly Action _showDashboard;
    private readonly System.Windows.Forms.Timer _frameTimer = new() { Interval = 30 };
    private readonly Font _titleFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private readonly Font _durationFont = new("Microsoft YaHei UI", 8.5F);
    private readonly SettingsReader _settingsReader = new(AppContext.BaseDirectory);
    private readonly StringFormat _durationFormat;
    private readonly StringFormat _titleFormat;

    private Bitmap? _bitmap;
    private DateTime _lastFrameAt = DateTime.Now;
    private DateTime _lastRefreshAt = DateTime.MinValue;
    private DateTime _lastSettingsCheckAt = DateTime.MinValue;
    private DateTime _settingsLastWrite = DateTime.MinValue;
    private float _scrollPosition;

    // 展示状态
    private string _title = string.Empty;
    private string _durationText = "--:--:--";
    private Color _dotColor = Color.Gray;
    private bool _breathing;
    private DateTime _breathStartedAt = DateTime.Now;
    private double _backgroundOpacity = 1.0;
    private int _textWidth = 1;

    private Point _dragStart;
    private Point _dragStartLocation;
    private bool _movedDuringDrag;

    protected override bool ShowWithoutActivation => true;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
        IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

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

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered;
            // 不使用 CS_DROPSHADOW：该系统阴影在窗口创建后无法可靠摘除（透明度为 0 时右/下边缘仍残留黑边）。
            // 改为在分层位图内手绘柔光，随背景透明度一起消失。
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
        Size = new Size(WindowWidth, WindowHeight);
        MinimumSize = new Size(WindowWidth, WindowHeight);
        MaximumSize = new Size(WindowWidth, WindowHeight);
        BackColor = Color.Black;
        Icon = TryLoadIcon();

        _durationFormat = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        _titleFormat = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.None };

        MouseUp += (_, e) => HandleMouseUp(e);
        MouseDown += (_, e) => BeginDrag(e);
        MouseMove += (_, e) => MoveDrag(e);

        _frameTimer.Tick += OnFrameTick;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 小窗固定到所有虚拟桌面（任务视图 / Win+Tab 的每个桌面都可见）
        VirtualDesktopHelper.PinToAllDesktops(Handle);
        _bitmap ??= new Bitmap(WindowWidth, WindowHeight, PixelFormat.Format32bppPArgb);
        RenderFrame();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _lastFrameAt = DateTime.Now;
            _frameTimer.Start();
        }
        else
        {
            _frameTimer.Stop();
        }
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var elapsedMs = (float)(now - _lastFrameAt).TotalMilliseconds;
        _lastFrameAt = now;

        if ((now - _lastRefreshAt).TotalSeconds >= 1)
        {
            _lastRefreshAt = now;
            _ = RefreshAsync();
        }
        if ((now - _lastSettingsCheckAt).TotalMilliseconds >= 500)
        {
            _lastSettingsCheckAt = now;
            RefreshOpacityFromSettings();
        }

        if (_textWidth > TitleRect.Width)
        {
            // 与旧版一致：每 13ms 滚动 1.2px
            _scrollPosition += 1.2F * (elapsedMs / 13F);
            var cycleWidth = _textWidth + 32;
            if (_scrollPosition >= cycleWidth) _scrollPosition -= cycleWidth;
        }
        else
        {
            _scrollPosition = 0;
        }

        RenderFrame();
    }

    private void RefreshOpacityFromSettings()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "时迹",
                "settings.json");
            if (!File.Exists(path))
            {
                _backgroundOpacity = 1.0;
                return;
            }

            var lastWrite = File.GetLastWriteTimeUtc(path);
            if (lastWrite == _settingsLastWrite) return;
            _settingsLastWrite = lastWrite;

            var snapshot = _settingsReader.Load();
            _backgroundOpacity = Math.Clamp(snapshot.CompactWindowOpacity ?? 1.0, 0.0, 1.0);
        }
        catch
        {
            // 读取失败保持现状，不打扰小窗展示
        }
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
        _lastRefreshAt = DateTime.MinValue;
        Show();
        WindowState = FormWindowState.Normal;
        TopMost = true;
        BringToFront();
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
            _breathing = false;
            _dotColor = NativeTheme.Current.Danger;
            SetTitle("后台记录服务未响应");
            _durationText = "--:--:--";
            return;
        }

        SetTitle(status.ActiveWindowTitle);

        if (status.Tracking)
        {
            _dotColor = Color.FromArgb(58, 190, 105);
            SetBreathing(status.IsVideoPlayback);
            var start = status.ActiveStartTime?.ToLocalTime();
            _durationText = start is null ? "--:--:--" : FormatElapsed(DateTime.Now - start.Value);
        }
        else if (status.IsManualIdle)
        {
            SetBreathing(false);
            _dotColor = Color.FromArgb(242, 190, 55);
            _durationText = "未记录";
        }
        else if (status.IsIdle)
        {
            SetBreathing(false);
            _dotColor = Color.FromArgb(230, 76, 76);
            _durationText = "未记录";
        }
        else
        {
            SetBreathing(false);
            _dotColor = NativeTheme.Current.TextSecondary;
            _durationText = "未记录";
        }
    }

    private void SetTitle(string? value)
    {
        var next = string.IsNullOrWhiteSpace(value) ? "无标题窗口" : value.Trim();
        if (string.Equals(_title, next, StringComparison.Ordinal)) return;
        _title = next;
        _scrollPosition = 0;
        using var measureBitmap = new Bitmap(1, 1);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        _textWidth = Math.Max(1, (int)Math.Ceiling(measureGraphics.MeasureString(_title, _titleFont).Width));
    }

    private void SetBreathing(bool value)
    {
        if (_breathing == value) return;
        _breathing = value;
        if (value) _breathStartedAt = DateTime.Now;
    }

    private static string FormatElapsed(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static Rectangle TitleRect => new(24, 0, WindowWidth - 24 - 82, WindowHeight);

    /// <summary>
    /// 在卡片轮廓之外绘制分层柔光（位图内手绘，替代无法运行时摘除的 CS_DROPSHADOW）。
    /// 阴影 alpha 与背景透明度同比例缩放：透明度为 0 时整层跳过，窗口边缘不留任何黑影。
    /// </summary>
    private static void DrawManualShadow(Graphics graphics, double opacity)
    {
        var baseBounds = new Rectangle(0, 0, WindowWidth - 1, WindowHeight - 1);
        var previousClip = graphics.Clip;
        try
        {
            using var basePath = BuildRoundedBounds(baseBounds, CornerRadius);
            using var outsideClip = new Region(new Rectangle(0, 0, WindowWidth, WindowHeight));
            outsideClip.Exclude(basePath);
            graphics.Clip = outsideClip;

            for (var spread = ShadowSpread; spread >= 1; spread--)
            {
                var ratio = (double)(ShadowSpread - spread + 1) / ShadowSpread;
                var alpha = (int)Math.Round(ShadowBaseAlpha * ratio * opacity);
                if (alpha <= 0)
                {
                    continue;
                }

                var layerBounds = Rectangle.Inflate(baseBounds, spread, spread);
                using var layerPath = BuildRoundedBounds(layerBounds, CornerRadius + spread);
                using var layerBrush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
                graphics.FillPath(layerBrush, layerPath);
            }
        }
        finally
        {
            graphics.Clip = previousClip;
            previousClip.Dispose();
        }
    }

    private void RenderFrame()
    {
        if (!IsHandleCreated || _bitmap is null) return;

        var palette = NativeTheme.Current;
        using (var graphics = Graphics.FromImage(_bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            // 背景：填充使用可配置不透明度（圆角外像素 alpha=0，自然透明且可穿透点击）；
            // 透明度为 0 时背景与边框都不绘制，窗口只剩文字与状态灯“浮”在桌面上。
            var chromeVisible = _backgroundOpacity > 0;
            if (chromeVisible)
            {
                // 柔光必须先于背景绘制，且裁剪到卡片轮廓之外，避免压暗半透明背景。
                DrawManualShadow(graphics, _backgroundOpacity);

                var backgroundAlpha = (int)Math.Round(255 * _backgroundOpacity);
                using (var backgroundPath = BuildRoundedBounds(new Rectangle(0, 0, WindowWidth - 1, WindowHeight - 1), CornerRadius))
                using (var backgroundBrush = new SolidBrush(Color.FromArgb(backgroundAlpha, palette.Background)))
                {
                    graphics.FillPath(backgroundBrush, backgroundPath);
                }

                // 边框随背景一起按配置隐现（0 时消失），其余情况下保持完全不透明。
                using (var borderPath = BuildRoundedBounds(new Rectangle(0, 0, WindowWidth - 1, WindowHeight - 1), CornerRadius))
                using (var borderPen = new Pen(palette.Border, 1))
                {
                    graphics.DrawPath(borderPen, borderPath);
                }
            }
            else
            {
                // 分层窗口 alpha=0 的像素默认穿透鼠标，视觉全透明时只有文字/状态灯能点中。
                // 用 alpha=1（1/255，肉眼不可见）铺满整个窗口矩形：外观仍是纯文字悬浮，
                // 但整块区域都可命中，拖动/双击/右键与有背景时完全一致。
                using var hitBrush = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
                graphics.FillRectangle(hitBrush, 0, 0, WindowWidth, WindowHeight);
            }

            // 状态灯：直径 10，位置与旧版圆点控件一致 (11, 12)；
            // 呼吸时色相始终为绿色，仅在不透明绿色 ↔ 完全透明之间往复（不再向背景白色渐变）。
            var dotColor = _dotColor;
            if (_breathing)
            {
                const double breathPeriodMs = 2800;
                var phase = (DateTime.Now - _breathStartedAt).TotalMilliseconds % breathPeriodMs / breathPeriodMs;
                var t = (1 - Math.Cos(2 * Math.PI * phase)) / 2;
                dotColor = Color.FromArgb((int)Math.Round(255 * t), _dotColor);
            }
            using (var dotBrush = new SolidBrush(dotColor))
            {
                graphics.FillEllipse(dotBrush, 11, 12, 10, 10);
            }

            // 标题：静态或跑马灯
            var titleRect = TitleRect;
            var titleY = (WindowHeight - _titleFont.GetHeight(graphics)) / 2F;
            using var titleBrush = new SolidBrush(palette.TextPrimary);
            if (_textWidth <= titleRect.Width)
            {
                graphics.DrawString(_title, _titleFont, titleBrush, titleRect.X, titleY, _titleFormat);
            }
            else
            {
                var cycleWidth = _textWidth + 32;
                var state = graphics.Save();
                graphics.SetClip(titleRect);
                var x = titleRect.X - (int)_scrollPosition;
                while (x < titleRect.Right)
                {
                    graphics.DrawString(_title, _titleFont, titleBrush, x, titleY, _titleFormat);
                    x += cycleWidth;
                }
                graphics.Restore(state);
            }

            // 时长（右对齐，宽 76，右留 6px）
            var durationRect = new Rectangle(WindowWidth - 6 - 76, 0, 76, WindowHeight);
            using var durationBrush = new SolidBrush(palette.TextSecondary);
            graphics.DrawString(_durationText, _durationFont, durationBrush, durationRect, _durationFormat);
        }

        PresentBitmap();
    }

    private void PresentBitmap()
    {
        if (_bitmap is null) return;

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmapHandle = _bitmap.GetHbitmap(Color.FromArgb(0));
        var previousObject = SelectObject(memoryDc, bitmapHandle);

        try
        {
            var destination = new Point(Left, Top);
            var size = new Size(WindowWidth, WindowHeight);
            var source = new Point(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memoryDc, previousObject);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frameTimer.Stop();
            _frameTimer.Dispose();
            _titleFont.Dispose();
            _durationFont.Dispose();
            _durationFormat.Dispose();
            _titleFormat.Dispose();
            _bitmap?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon? TryLoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }
}
