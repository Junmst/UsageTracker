using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UsageTrackerWeb;

internal sealed class TrayMenuForm : Form
{
    private static Color MenuBackground => NativeTheme.Current.Panel;
    private static Color MenuBorder => NativeTheme.Current.Border;
    private static Color TextPrimary => NativeTheme.Current.TextPrimary;
    private static Color AccentSoft => NativeTheme.Current.AccentSoft;
    private static Color Danger => NativeTheme.Current.Danger;
    private static Color DangerSoft => NativeTheme.Current.DangerSoft;
    private static readonly Font TitleFont = new("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
    private static readonly Font ActionFont = new("Microsoft YaHei UI", 10F, FontStyle.Bold);
    private static readonly Font IconFont = new("Segoe UI Symbol", 10.5F, FontStyle.Bold);
    private static readonly string[] Glyphs = ["⌂", "◷", "▣", "—", "×"];
    private static readonly string[] Labels = ["打开启动器", "打开网页看板", "打开时迹", "隐藏时迹", "退出全部程序"];

    private readonly System.Windows.Forms.Timer _closeTimer = new();
    private readonly System.Windows.Forms.Timer _animationTimer = new();
    private readonly Action[] _actions;
    private readonly Point _menuPadding = new(12, 12);
    private Point _trayHotspot;
    private int _hoverIndex = -1;
    private int _targetTop;
    private bool _closing;
    private bool _opening;
    private int _animationStep;
    private Action? _pendingAction;

    public TrayMenuForm(
        Action openLauncher,
        Action openBrowser,
        Action showNative,
        Action hideNative,
        Action exitAll)
    {
        _actions = [openLauncher, openBrowser, showNative, hideNative, exitAll];
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(264, 352);
        BackColor = MenuBackground;
        SetRoundedRegion(12);
        _closeTimer.Interval = 140;
        _closeTimer.Tick += CloseTimer_Tick;
        _animationTimer.Interval = 16;
        _animationTimer.Tick += AnimationTimer_Tick;
        NativeTheme.Changed += NativeTheme_Changed;
        MouseEnter += (_, _) => _closeTimer.Stop();
        MouseLeave += (_, _) => StartCloseTimer();
    }

    private void NativeTheme_Changed(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() =>
                {
                    BackColor = MenuBackground;
                    Invalidate();
                }));
                return;
            }
            BackColor = MenuBackground;
            Invalidate();
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void ShowFromTray()
    {
        var cursor = Cursor.Position;
        var area = Screen.FromPoint(cursor).WorkingArea;
        _trayHotspot = cursor;
        Left = Math.Clamp(cursor.X - Width + 18, area.Left + 8, area.Right - Width - 8);
        _targetTop = Math.Clamp(cursor.Y - Height - 10, area.Top + 8, area.Bottom - Height - 8);
        Top = _targetTop - 12;
        Opacity = 0d;
        _hoverIndex = -1;
        _closing = false;
        _opening = true;
        _animationStep = 0;
        SetRoundedRegion(12);
        CreateControl();
        Show();
        Update();
        BringToFront();
        Activate();
        _animationTimer.Start();
        StartCloseTimer();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(MenuBackground);

        using (var borderPath = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 12))
        using (var borderPen = new Pen(MenuBorder, 1))
        {
            graphics.DrawPath(borderPen, borderPath);
        }

        using var titleBrush = new SolidBrush(TextPrimary);
        using var titleFormat = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
        };
        graphics.DrawString("时迹", TitleFont, titleBrush, new Rectangle(
            _menuPadding.X + 8,
            _menuPadding.Y,
            Width - (_menuPadding.X * 2) - 8,
            38), titleFormat);

        for (var index = 0; index < Labels.Length; index++)
        {
            var bounds = GetActionBounds(index);
            var isDanger = index == Labels.Length - 1;
            var isHighlighted = index == 0 || index == _hoverIndex;
            var background = isDanger && index == _hoverIndex
                ? DangerSoft
                : isHighlighted ? AccentSoft : MenuBackground;

            using var path = RoundedPath(bounds, 8);
            using var backgroundBrush = new SolidBrush(background);
            graphics.FillPath(backgroundBrush, path);

            var palette = NativeTheme.Current;
            using var iconBrush = new SolidBrush(isDanger ? Danger : palette.IsDark ? Color.White : Color.Black);
            using var textBrush = new SolidBrush(isDanger ? Danger : TextPrimary);
            using var iconFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            using var textFormat = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
            };
            var iconBounds = new Rectangle(bounds.X + 7, bounds.Y, 36, bounds.Height);
            var textBounds = new Rectangle(bounds.X + 51, bounds.Y, bounds.Width - 59, bounds.Height);
            graphics.DrawString(Glyphs[index], IconFont, iconBrush, iconBounds, iconFormat);
            graphics.DrawString(Labels[index], ActionFont, textBrush, textBounds, textFormat);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var nextHover = -1;
        for (var index = 0; index < Labels.Length; index++)
        {
            if (GetActionBounds(index).Contains(e.Location))
            {
                nextHover = index;
                break;
            }
        }

        if (nextHover != _hoverIndex)
        {
            _hoverIndex = nextHover;
            _closeTimer.Stop();
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || _hoverIndex < 0)
        {
            return;
        }

        _pendingAction = _actions[_hoverIndex];
        CloseAnimated();
    }

    private Rectangle GetActionBounds(int index)
    {
        const int titleHeight = 38;
        const int actionHeight = 48;
        const int actionGap = 6;
        var x = _menuPadding.X;
        var y = _menuPadding.Y + titleHeight + 8 + index * (actionHeight + actionGap);
        return new Rectangle(x, y, Width - (_menuPadding.X * 2), actionHeight);
    }

    private void StartCloseTimer()
    {
        if (_closing || !Visible)
        {
            return;
        }

        _closeTimer.Stop();
        _closeTimer.Start();
    }

    private void CloseTimer_Tick(object? sender, EventArgs e)
    {
        var cursor = Cursor.Position;
        var menuBounds = Rectangle.Inflate(Bounds, 8, 8);
        var trayBounds = new Rectangle(_trayHotspot.X - 28, _trayHotspot.Y - 28, 56, 56);
        if (!menuBounds.Contains(cursor) && !trayBounds.Contains(cursor))
        {
            CloseAnimated();
        }
    }

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _opening = false;
        _animationStep = 0;
        _closeTimer.Stop();
        _animationTimer.Start();
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        _animationStep++;
        var progress = Math.Min(1d, _animationStep / 14d);
        var eased = 1d - Math.Pow(1d - progress, 3d);
        if (_opening)
        {
            Opacity = eased;
            Top = _targetTop - (int)Math.Round((1d - eased) * 12d);
            if (progress >= 1d)
            {
                _animationTimer.Stop();
                StartCloseTimer();
            }
            return;
        }

        Opacity = 1d - eased;
        Top = _targetTop - (int)Math.Round(eased * 8d);
        if (progress >= 1d)
        {
            _animationTimer.Stop();
            var action = _pendingAction;
            _pendingAction = null;
            CloseImmediate();
            action?.Invoke();
        }
    }

    private void CloseImmediate()
    {
        if (_closing && !Visible)
        {
            return;
        }

        _closing = true;
        _opening = false;
        _closeTimer.Stop();
        _animationTimer.Stop();
        Hide();
        Close();
        Dispose();
    }

    private void SetRoundedRegion(int radius)
    {
        using var path = RoundedPath(new Rectangle(0, 0, Width, Height), radius);
        Region = new Region(path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SetRoundedRegion(12);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(MenuBackground);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closeTimer.Stop();
            _closeTimer.Dispose();
            NativeTheme.Changed -= NativeTheme_Changed;
        }
        base.Dispose(disposing);
    }

    private static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
