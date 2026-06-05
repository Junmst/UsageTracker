﻿﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using static UsageTrackerNative.MainWindow;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 专业级时间分布图控件 — 基于 DrawingVisual 的纯渲染架构。
/// 所有渲染层（网格、会话条、表头、日期列、选中高亮）全部使用 DrawingVisual，
/// 零 UIElement 树，自包含一切交互与动画。
/// </summary>
public class TimeDistributionControl : FrameworkElement
{
    // ─── 常量 ───
    private const double HeaderHeight = 28;
    private const double DateColumnWidth = 128;
    private const double DefaultRowHeight = 52;
    private const double BottomPadding = 10;
    private const int TotalMinutes = 1440; // 4:00 ~ 次日4:00 = 24小时
    internal const double MinZoom = 0.96;
    internal const double MaxZoom = 15;

    // ─── 字段（不用 DP，避免回调风暴） ───
    internal double _zoom = 1.0;
    internal double _offsetX;
    internal double _offsetY;

    // ─── 公开属性 ───
    public double Zoom
    {
        get => _zoom;
        set { _zoom = Math.Clamp(value, MinZoom, MaxZoom); ScheduleRender(); }
    }

    public double OffsetX
    {
        get => _offsetX;
        set { _offsetX = value; ScheduleRender(); }
    }

    public double OffsetY
    {
        get => _offsetY;
        set { _offsetY = value; ScheduleRender(); }
    }

    // ─── 公开数据 ───
    public DateTime RangeStart => DateTime.Today.Date.AddHours(4);
    private Dictionary<DateTime, int> _dateIndex = new();

    public IReadOnlyList<DateTime> VisibleDates
    {
        get => _visibleDates;
        set
        {
            _visibleDates = value;
            // 重建日期索引，将线性查找 O(N) 降为 O(1)
            _dateIndex.Clear();
            if (value != null)
            {
                for (int i = 0; i < value.Count; i++)
                    _dateIndex[value[i]] = i;
            }
        }
    }
    private IReadOnlyList<DateTime> _visibleDates = [];

    /// <summary>根据日期快速查找行号（O(1)），供渲染层和命中测试使用</summary>
    internal int GetRowForDate(DateTime date) =>
        _dateIndex.TryGetValue(date, out var row) ? row : -1;
    public IReadOnlyList<UsageSession> Sessions { get; set; } = [];
    public double RowHeight { get; set; } = DefaultRowHeight;

    /// <summary>图表区世界空间宽度（1x 时等于图表区实际像素宽度）</summary>
    public double BaseWidth => _chartViewportWidth;

    /// <summary>图表区可见宽度（像素）</summary>
    public double ChartViewportWidth => _chartViewportWidth;

    /// <summary>图表区可见高度（像素）</summary>
    public double ChartViewportHeight => _chartViewportHeight;

    /// <summary>整个内容的世界空间总高度</summary>
    public double TotalWorldHeight => HeaderHeight + (VisibleDates?.Count ?? 0) * RowHeight + BottomPadding;

    // ─── 事件 ───
    public event EventHandler<UsageSession>? SessionClicked;

    // ─── 内部字段 ───
    private readonly VisualHost _headerHost;   // 时间刻度表头
    private readonly VisualHost _dateColumnHost; // 日期列旧 DrawingVisual 容器（保留但不再承担日期文字）
    private readonly System.Windows.Controls.Canvas _dateTextCanvas; // R2：日期列普通 WPF 文本层
    private readonly VisualHost _chartHost;     // 图表主体

    private readonly HeaderAxisLayer _headerAxisLayer;
    private readonly HeaderGridLineLayer _headerGridLineLayer;
    private readonly DateLabelLayer _dateLabelLayer;
    private readonly GridLayer _gridLayer;
    private readonly SessionBarLayer _sessionBarLayer;
    private readonly SelectionLayer _selectionLayer;
    private readonly SessionHitTester _hitTester;

    // 布局缓存
    private double _chartViewportWidth;
    private double _chartViewportHeight;
    private double _controlWidth;
    private double _controlHeight;

    // 渲染调度（避免 DP 回调 + 动画 + 手动调用导致的重复渲染）
    private bool _renderScheduled;
    private bool _isRenderingHooked;

    // 拖拽状态
    private bool _isDragging;
    private Point _dragStartPos;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;
    private DateTime _lastDragTime;
    private Vector _dragVelocity;
    private double _totalDragDistance;

    // 动画状态
    private ZoomAnimationState? _zoomAnimation;
    private InertiaAnimationState? _inertiaAnimation;
    private SmoothScrollAnimationState? _smoothScrollAnimation;

    // 悬停
    private UsageSession? _hoveredSession;

    // R2：表头后续单独处理；当前快速交互中仍跳过表头，避免字体缩放/压缩。

    // R2：日期列普通 WPF 文本层。滚动中按节流重建可见文本，避免 transform 滚出后出现空白。
    private double _dateColumnRenderedOffsetY;
    private DateTime _lastDateColumnFastRenderUtc;
    private const double DateColumnFastRenderIntervalMs = 50;

    // R2：快速交互渲染窗口。缩放/拖拽/惯性/平滑滚动期间减少非必要层重绘。
    private DateTime _fastRenderUntilUtc;
    internal bool IsFastInteractionRender => _isDragging
        || DateTime.UtcNow <= _fastRenderUntilUtc
        || _zoomAnimation?.IsAnimating == true
        || _inertiaAnimation?.IsAnimating == true
        || _smoothScrollAnimation?.IsAnimating == true;

    private void MarkFastInteraction(double milliseconds = 160)
    {
        _fastRenderUntilUtc = DateTime.UtcNow.AddMilliseconds(milliseconds);
        if (_hoveredSession != null)
        {
            _hoveredSession = null;
            _selectionLayer.SetHoveredSession(null);
        }
    }

    private void RenderHeaderAtCurrentView()
    {
        _headerHost.RenderTransform = Transform.Identity;
        _headerGridLineLayer.Render();
        _headerAxisLayer.Render();
    }

    private void ApplyDateColumnFastTransform()
    {
        if ((VisibleDates?.Count ?? 0) <= 7)
        {
            RenderDateColumnAtCurrentOffset();
            return;
        }

        var now = DateTime.UtcNow;
        var deltaY = _dateColumnRenderedOffsetY - _offsetY;
        var needsRebuild = Math.Abs(deltaY) >= RowHeight * 0.5
            || (now - _lastDateColumnFastRenderUtc).TotalMilliseconds >= DateColumnFastRenderIntervalMs;

        if (needsRebuild)
        {
            RenderDateColumnAtCurrentOffset();
            _lastDateColumnFastRenderUtc = now;
            return;
        }

        _dateTextCanvas.RenderTransform = Math.Abs(deltaY) < 0.01
            ? Transform.Identity
            : new TranslateTransform(0, deltaY);
    }

    private void RenderDateColumnAtCurrentOffset()
    {
        _dateColumnHost.RenderTransform = Transform.Identity;
        _dateTextCanvas.RenderTransform = Transform.Identity;
        _dateLabelLayer.Visual.RenderOpen().Close();
        _dateTextCanvas.Children.Clear();

        var viewportHeight = ChartViewportHeight;
        var rowHeight = RowHeight;
        var visibleDates = VisibleDates;
        if (viewportHeight <= 0 || visibleDates.Count == 0)
        {
            _dateColumnRenderedOffsetY = _offsetY;
            return;
        }

        var textBrush = ThemeBrush("SecondaryTextBrush");
        var secondaryBrush = ThemeBrush("SecondaryTextBrush");
        var sessionsByDate = visibleDates.ToDictionary(
            date => date,
            date => TimeSpan.FromTicks(Sessions.Sum(session => GetVisibleDurationForDate(session, date).Ticks)));

        for (var i = 0; i < visibleDates.Count; i++)
        {
            var screenY = WorldToScreenY(i * rowHeight);
            if (screenY + rowHeight < 0 || screenY > viewportHeight) continue;

            var date = visibleDates[i];
            var dateText = new System.Windows.Controls.TextBlock
            {
                Text = FormatDateLabelForColumn(date),
                Foreground = textBrush,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Width = 116,
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
            };
            System.Windows.Controls.Canvas.SetLeft(dateText, 4);
            System.Windows.Controls.Canvas.SetTop(dateText, screenY + 5);
            _dateTextCanvas.Children.Add(dateText);

            sessionsByDate.TryGetValue(date, out var dayTotal);
            var durationText = new System.Windows.Controls.TextBlock
            {
                Text = FormatDurationShort(dayTotal),
                Foreground = secondaryBrush,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 11,
                Opacity = 0.72,
                Width = 116,
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
            };
            System.Windows.Controls.Canvas.SetLeft(durationText, 4);
            System.Windows.Controls.Canvas.SetTop(durationText, screenY + 22);
            _dateTextCanvas.Children.Add(durationText);
        }

        _dateColumnRenderedOffsetY = _offsetY;
        _lastDateColumnFastRenderUtc = DateTime.UtcNow;
    }

    private static string FormatDateLabelForColumn(DateTime date)
    {
        if (date.Date == DateTime.Today)
            return $"今天（{GetChineseWeekday(date)}）";
        if (date.Date == DateTime.Today.AddDays(-1))
            return $"昨天（{GetChineseWeekday(date)}）";
        var dateText = date.Year == DateTime.Today.Year
            ? $"{date.Month}月{date.Day}日"
            : $"{date.Year}年{date.Month}月{date.Day}日";
        return $"{dateText}（{GetChineseWeekday(date)}）";
    }

    private static string GetChineseWeekday(DateTime date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周天"
    };

    public TimeDistributionControl()
    {
        ClipToBounds = true;
        Focusable = true;

        // ── Header Host (2层) ──
        _headerAxisLayer = new HeaderAxisLayer(this);
        _headerGridLineLayer = new HeaderGridLineLayer(this);
        _headerHost = new VisualHost();
        _headerHost.AddVisual(_headerGridLineLayer.Visual);
        _headerHost.AddVisual(_headerAxisLayer.Visual);

        // ── Date Column Host (1层) ──
        _dateLabelLayer = new DateLabelLayer(this);
        _dateColumnHost = new VisualHost();
        _dateColumnHost.AddVisual(_dateLabelLayer.Visual);
        _dateTextCanvas = new System.Windows.Controls.Canvas
        {
            ClipToBounds = true,
            IsHitTestVisible = false
        };

        // ── Chart Host (3层，从底到顶) ──
        _gridLayer = new GridLayer(this);
        _sessionBarLayer = new SessionBarLayer(this);
        _selectionLayer = new SelectionLayer(this);
        _chartHost = new VisualHost();
        _chartHost.AddVisual(_gridLayer.Visual);
        _chartHost.AddVisual(_sessionBarLayer.Visual);
        _chartHost.AddVisual(_selectionLayer.Visual);

        _hitTester = new SessionHitTester(this);

        AddVisualChild(_headerHost);
        AddVisualChild(_dateColumnHost);
        AddVisualChild(_dateTextCanvas);
        AddVisualChild(_chartHost);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MouseMove += OnHoverMove;
        MouseLeave += OnHoverLeave;
    }

    // ─── 可视树 ───

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _headerHost,
        1 => _dateColumnHost,
        2 => _dateTextCanvas,
        3 => _chartHost,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    protected override int VisualChildrenCount => 4;

    // ─── 命中测试 ───

    /// <summary>
    /// FrameworkElement 没有 Background 属性，默认 HitTestCore 只对有内容的区域响应。
    /// 重写此方法让整个控件区域都能接收鼠标事件。
    /// </summary>
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        return new PointHitTestResult(this, hitTestParameters.HitPoint);
    }

    // ─── 布局 ───

    protected override Size MeasureOverride(Size availableSize)
    {
        _controlWidth = availableSize.Width;
        _controlHeight = availableSize.Height;
        _chartViewportWidth = Math.Max(0, _controlWidth - DateColumnWidth);
        _chartViewportHeight = Math.Max(0, _controlHeight - HeaderHeight);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _controlWidth = finalSize.Width;
        _controlHeight = finalSize.Height;
        _chartViewportWidth = Math.Max(0, _controlWidth - DateColumnWidth);
        _chartViewportHeight = Math.Max(0, _controlHeight - HeaderHeight);

        // 最近七天保持固定舒适行距，避免被强行拉得过长；月视图沿用同一行距并允许滚动。
        RowHeight = DefaultRowHeight;

        _headerHost.Arrange(new Rect(DateColumnWidth, 0, _chartViewportWidth, HeaderHeight));
        _dateColumnHost.Arrange(new Rect(0, HeaderHeight, DateColumnWidth, _chartViewportHeight));
        _dateTextCanvas.Arrange(new Rect(0, HeaderHeight, DateColumnWidth, _chartViewportHeight));
        _chartHost.Arrange(new Rect(DateColumnWidth, HeaderHeight, _chartViewportWidth, _chartViewportHeight));

        if (Math.Abs(_zoom - 1.0) < 0.001)
            _zoom = MinZoom;
        ClampOffset();
        ScheduleRender();
        return finalSize;
    }

    // ─── 渲染调度 ───

    /// <summary>
    /// 标记需要重新渲染，在下一个 CompositionTarget.Rendering 帧执行。
    /// 避免同一帧内多次调用 UpdateRender() 导致重复渲染。
    /// </summary>
    internal void ScheduleRender()
    {
        if (_renderScheduled) return;
        _renderScheduled = true;
        EnsureRenderingHook();
    }

    private void FlushRender()
    {
        if (!_renderScheduled) return;
        _renderScheduled = false;
        UpdateRenderCore();
    }

    private void UpdateRenderCore()
    {
        if (IsFastInteractionRender)
        {
            // R2：交互帧保持主体快速渲染；不再做 30ms 跳帧限频，保证拖动/缩放跟手。

            // 日期列是 WPF 文本层，按节流重建可见文本。
            // 顶部短刻度线与时间文字仍用 DrawingVisual，但文字缓存 FormattedText，避免每帧重新排版。
            ApplyDateColumnFastTransform();
            _headerGridLineLayer.Render();
            _headerAxisLayer.Render();
            _gridLayer.Render();
            _sessionBarLayer.Render();
            _selectionLayer.SetHoveredSession(null);
            _selectionLayer.Render();
            return;
        }

        _headerGridLineLayer.Render();
        _headerAxisLayer.Render();
        RenderDateColumnAtCurrentOffset();
        _gridLayer.Render();
        _sessionBarLayer.Render();
        _selectionLayer.Render();
    }

    /// <summary>公开方法：外部数据更新后调用，立即刷新</summary>
    public void UpdateRender()
    {
        _renderScheduled = false;
        UpdateRenderCore();
    }

    public void UpdateTheme()
    {
        _headerGridLineLayer.UpdateBrushes();
        _headerAxisLayer.UpdateBrushes();
        _dateLabelLayer.UpdateBrushes();
        _gridLayer.UpdateBrushes();
        _sessionBarLayer.UpdateBrushes();
        _selectionLayer.UpdateBrushes();
        UpdateRender();
    }

    public Brush ThemeBrush(string key)
    {
        return (Brush)System.Windows.Application.Current.Resources[key];
    }

    /// <summary>
    /// 重置缩放和偏移到初始状态。
    /// 默认视图显示完整 24 小时（4:00 ~ 次日 4:00），并滚动到今天。
    /// </summary>
    public void ResetView()
    {
        Zoom = MinZoom;
        // 完整 24 小时视图：从 4:00 开始（无水平偏移）
        OffsetX = 0;
        // 最近七天固定铺满，不做垂直拖动；月视图才允许上下拖日期。
        if ((VisibleDates?.Count ?? 0) <= 7)
        {
            OffsetY = 0;
            ClampOffset();
            return;
        }

        var today = DateTime.Today;
        if (_dateIndex.TryGetValue(today, out var todayIndex))
        {
            OffsetY = todayIndex * RowHeight;
        }
        else
        {
            OffsetY = 0;
        }
        ClampOffset();
    }

    // ─── 动画系统 ───

    private void AnimateTo(double startZoom, double endZoom,
                           double startOffsetX, double endOffsetX,
                           double startOffsetY, double endOffsetY,
                           double durationMs = 150)
    {
        StopAnimations();
        _zoomAnimation = new ZoomAnimationState(this, startZoom, endZoom, startOffsetX, endOffsetX, startOffsetY, endOffsetY, durationMs);
        _zoomAnimation.Start();
        EnsureRenderingHook();
    }

    private void StartInertia(Vector velocity)
    {
        var cappedVelocity = new Vector(
            Math.Clamp(velocity.X, -180, 180),
            Math.Clamp(velocity.Y, -180, 180)
        );
        StopInertia();
        _inertiaAnimation = new InertiaAnimationState(this, cappedVelocity);
        _inertiaAnimation.Start();
        EnsureRenderingHook();
    }

    private void StopAnimations()
    {
        _zoomAnimation?.Stop();
        _zoomAnimation = null;
        _inertiaAnimation?.Stop();
        _inertiaAnimation = null;
        _smoothScrollAnimation?.Stop();
        _smoothScrollAnimation = null;
    }

    private void StopInertia()
    {
        _inertiaAnimation?.Stop();
        _inertiaAnimation = null;
    }

    private void StopSmoothScroll()
    {
        _smoothScrollAnimation?.Stop();
        _smoothScrollAnimation = null;
    }

    private void EnsureRenderingHook()
    {
        if (_isRenderingHooked) return;
        CompositionTarget.Rendering += OnRendering;
        _isRenderingHooked = true;
    }

    private void UnhookRendering()
    {
        if (!_isRenderingHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _isRenderingHooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // 先处理动画
        if (_zoomAnimation?.IsAnimating == true)
        {
            _zoomAnimation.Tick();
        }
        else if (_zoomAnimation != null)
        {
            _zoomAnimation = null;
        }

        if (_inertiaAnimation?.IsAnimating == true)
        {
            _inertiaAnimation.Tick();
        }
        else if (_inertiaAnimation != null)
        {
            _inertiaAnimation = null;
        }

        if (_smoothScrollAnimation?.IsAnimating == true)
        {
            _smoothScrollAnimation.Tick();
        }
        else if (_smoothScrollAnimation != null)
        {
            _smoothScrollAnimation = null;
        }

        // 统一刷新渲染
        FlushRender();

        // 没有动画也没待渲染，断开钩子
        if (!_renderScheduled && _zoomAnimation == null && _inertiaAnimation == null && _smoothScrollAnimation == null)
        {
            UnhookRendering();
        }
    }

    // ─── 坐标变换 ───

    public double WorldToScreenX(double worldX) => (worldX - OffsetX) * Zoom;
    public double WorldToScreenY(double worldY) => worldY - OffsetY;
    public double ScreenToWorldX(double screenX) => OffsetX + screenX / Zoom;
    public double ScreenToWorldY(double screenY) => screenY + OffsetY;

    // ─── Clamp ───

    public void ClampOffset()
    {
        // 当完整 24 小时时间轴小于视口宽度时居中显示，避免左侧贴边、右侧空白。
        // 仅改变横向显示位置，不改变日期行距、七天固定逻辑或缩放范围。
        var worldWidthInViewport = BaseWidth * Zoom;
        if (worldWidthInViewport <= _chartViewportWidth)
        {
            _offsetX = BaseWidth / 2 - _chartViewportWidth / (2 * Zoom);
        }
        else
        {
            var maxOffsetX = Math.Max(0, BaseWidth - _chartViewportWidth / Zoom);
            _offsetX = Math.Clamp(_offsetX, 0, maxOffsetX);
        }

        var maxOffsetY = Math.Max(0, TotalWorldHeight - _chartViewportHeight);
        _offsetY = Math.Clamp(_offsetY, 0, maxOffsetY);
    }

    // ─── 鼠标交互 ───

    // ─── 输入设备检测 ───
    private bool _lastInputWasTouchpad;
    private DateTime _lastWheelTime;
    private System.Windows.Interop.HwndSource? _horizontalWheelSource;
    private const int WmMouseHWheel = 0x020E;

    /// <summary>
    /// 检测输入来源：触摸板事件通常以快速连续的小增量到达，
    /// 而鼠标滚轮通常是较大的离散增量。
    /// </summary>
    private bool IsTouchpadInput(int delta)
    {
        var now = DateTime.Now;
        var timeSinceLastWheel = (now - _lastWheelTime).TotalMilliseconds;
        _lastWheelTime = now;

        // 触摸板特征：增量小（通常 |delta| < 30）且事件间隔短（< 80ms）
        // 鼠标滚轮特征：增量大（通常 120 的倍数）且间隔较长
        if (Math.Abs(delta) < 30 && timeSinceLastWheel < 80)
        {
            _lastInputWasTouchpad = true;
            return true;
        }

        // 如果连续多个小增量，也认为是触摸板
        if (Math.Abs(delta) < 30 && _lastInputWasTouchpad)
        {
            return true;
        }

        _lastInputWasTouchpad = false;
        return false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl+Wheel: 缩放
            HandleZoom(e.Delta, e.GetPosition(_chartHost));
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift+Wheel: 水平滚动（时间轴）
            if (BaseWidth * Zoom >= _chartViewportWidth)
                SmoothScrollOffset(-e.Delta * 0.5 / _zoom, 0);
        }
        else
        {
            var isTouchpad = IsTouchpadInput(e.Delta);
            if (isTouchpad)
            {
                // 触摸板双指：垂直分量走 OffsetY；水平分量由 WM_MOUSEHWHEEL 进入 OnHorizontalWheel。
                if ((VisibleDates?.Count ?? 0) > 7)
                {
                    _offsetY -= e.Delta * 1.0;
                }
                MarkFastInteraction();
                ClampOffset();
                ScheduleRender();
            }
            else
            {
                // 普通鼠标滚轮：垂直滚动（浏览日期）
                if ((VisibleDates?.Count ?? 0) > 7) SmoothScrollOffset(0, -e.Delta * 0.5);
                else MarkFastInteraction();
            }
        }
    }

    private void OnHorizontalWheel(int delta)
    {
        if (BaseWidth * Zoom >= _chartViewportWidth)
        {
            _offsetX += delta * 0.5 / _zoom;
        }
        MarkFastInteraction();
        ClampOffset();
        ScheduleRender();
    }

    private IntPtr TimeDistributionWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseHWheel)
        {
            var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
            OnHorizontalWheel(delta);
            handled = true;
        }
        return IntPtr.Zero;
    }
    /// <summary>
    /// 平滑滚轮滚动：将增量叠加到当前滚动动画，实现连续流畅的移动。
    /// </summary>
    public void SmoothScrollOffset(double deltaOffsetX, double deltaOffsetY)
    {
        // 停止惯性动画（滚轮接管）
        StopInertia();
        if (BaseWidth * Zoom < _chartViewportWidth) deltaOffsetX = 0;
        MarkFastInteraction();

        if (_smoothScrollAnimation != null && _smoothScrollAnimation.IsAnimating)
        {
            // 追加增量到现有动画
            _smoothScrollAnimation.AppendDelta(deltaOffsetX, deltaOffsetY);
        }
        else
        {
            // 启动新动画
            var targetX = _offsetX + deltaOffsetX;
            var targetY = _offsetY + deltaOffsetY;
            _smoothScrollAnimation = new SmoothScrollAnimationState(this, targetX, targetY);
            _smoothScrollAnimation.Start();
            EnsureRenderingHook();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            ResetView();
            return;
        }

        _dragStartPos = e.GetPosition(this);
        _dragStartOffsetX = OffsetX;
        _dragStartOffsetY = OffsetY;
        _lastDragTime = DateTime.Now;
        _dragVelocity = new Vector(0, 0);
        _isDragging = false;
        StopInertia();
        StopSmoothScroll();
        MarkFastInteraction();

        CaptureMouse();
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!IsMouseCaptured) return;

        var currentPos = e.GetPosition(this);
        var delta = currentPos - _dragStartPos;
        _totalDragDistance = Math.Max(_totalDragDistance, Math.Abs(delta.X) + Math.Abs(delta.Y));

        if (!_isDragging && _totalDragDistance > 3)
        {
            _isDragging = true;
            Cursor = Cursors.SizeAll;
        }

        if (!_isDragging) return;

        e.Handled = true;

        var now = DateTime.Now;
        var deltaTime = (now - _lastDragTime).TotalSeconds;
        if (deltaTime > 0)
        {
            _dragVelocity = new Vector(-delta.X / deltaTime, -delta.Y / deltaTime) * 0.18;
        }
        _lastDragTime = now;
        _dragStartPos = currentPos;
        _dragStartOffsetX = OffsetX;
        _dragStartOffsetY = OffsetY;

        _offsetX = _dragStartOffsetX - delta.X / _zoom;
        if ((VisibleDates?.Count ?? 0) > 7) _offsetY = _dragStartOffsetY - delta.Y;
        ClampOffset();
        MarkFastInteraction();
        ScheduleRender(); // 拖拽用 ScheduleRender，帧内合并渲染
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;

        if (!_isDragging)
        {
            var pos = e.GetPosition(_chartHost);
            var session = _hitTester.HitTest(pos);
            if (session != null)
            {
                SessionClicked?.Invoke(this, session);
            }
        }

        _isDragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Hand;

        if (_dragVelocity.Length > 320)
        {
            MarkFastInteraction();
            StartInertia(_dragVelocity);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        e.Handled = true;
        ResetView();
    }

    // ─── 悬停交互（节流） ───

    private DateTime _lastHoverTime;
    private const double HoverThrottleMs = 50; // 50ms 节流

    private void OnHoverMove(object sender, MouseEventArgs e)
    {
        if (_isDragging || IsFastInteractionRender) return;

        // 节流：避免每次鼠标移动都做 HitTest + 渲染
        var now = DateTime.Now;
        if ((now - _lastHoverTime).TotalMilliseconds < HoverThrottleMs) return;
        _lastHoverTime = now;

        var pos = e.GetPosition(_chartHost);
        var session = _hitTester.HitTest(pos);

        if (session != _hoveredSession)
        {
            _hoveredSession = session;
            _selectionLayer.SetHoveredSession(session);
            ScheduleRender();
        }

    }

    private void OnHoverLeave(object sender, MouseEventArgs e)
    {
        if (_hoveredSession != null)
        {
            _hoveredSession = null;
            _selectionLayer.SetHoveredSession(null);
            ScheduleRender();
        }
    }

    // ─── 缩放 ───

    private void HandleZoom(int delta, Point mousePos)
    {
        var oldZoom = Zoom;
        var zoomFactor = delta > 0 ? 1.12 : 1 / 1.12;
        var newZoom = Math.Clamp(Zoom * zoomFactor, MinZoom, MaxZoom);

        var mouseWorldX = ScreenToWorldX(mousePos.X);
        var newOffsetX = mouseWorldX - mousePos.X / newZoom;

        AnimateTo(oldZoom, newZoom, OffsetX, newOffsetX, OffsetY, OffsetY);
    }

    // ─── 生命周期 ───

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 首次加载时设置默认视图（完整 24 小时，4:00 ~ 次日 4:00）
        if (_offsetX == 0 && _zoom == 1.0)
        {
            _zoom = MinZoom;
            ClampOffset();
            // OffsetX 保持 0，从 4:00 开始显示完整 24 小时
        }

        var source = System.Windows.PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
        if (source != null && !ReferenceEquals(source, _horizontalWheelSource))
        {
            _horizontalWheelSource?.RemoveHook(TimeDistributionWndProc);
            _horizontalWheelSource = source;
            _horizontalWheelSource.AddHook(TimeDistributionWndProc);
        }

        UpdateRender();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _horizontalWheelSource?.RemoveHook(TimeDistributionWndProc);
        _horizontalWheelSource = null;
        UnhookRendering();
    }

    // ─── 辅助 ───

    /// <summary>
    /// 根据缩放级别返回时间刻度的分钟步长。
    /// zoom >= 10 → 10min, zoom >= 6 → 15min, zoom >= 4 → 30min,
    /// zoom >= 3 → 1h, zoom >= 1.3 → 2h, else → 4h
    /// </summary>
    internal static int GetTimeScaleStep(double zoom)
    {
        if (zoom >= 10) return 10;
        if (zoom >= 6) return 15;
        if (zoom >= 4) return 30;
        if (zoom >= 3) return 60;
        if (zoom >= 1.3) return 120;
        return 240;
    }

    internal static DateTime GetTimeDistributionDate(DateTime time)
    {
        return UsageTimeRange.GetTimeDistributionDate(time);
    }

    internal static TimeSpan GetVisibleDurationForDate(UsageSession session, DateTime date)
    {
        var dayStart = date.Date.AddHours(4);
        var dayEnd = dayStart.AddDays(1);
        return UsageTimeRange.GetOverlapDuration(session.StartTime, session.EndTime, dayStart, dayEnd);
    }

    internal bool TryGetVisibleSegment(UsageSession session, DateTime date, out DateTime start, out DateTime end)
    {
        var dayStart = date.Date.AddHours(4);
        var dayEnd = dayStart.AddDays(1);
        start = session.StartTime > dayStart ? session.StartTime : dayStart;
        end = session.EndTime < dayEnd ? session.EndTime : dayEnd;
        return end > start;
    }

    internal IEnumerable<(UsageSession Session, int Row, DateTime Date, DateTime Start, DateTime End)> EnumerateVisibleSessionSegments()
    {
        foreach (var session in Sessions)
        {
            foreach (var date in VisibleDates)
            {
                var row = GetRowForDate(date);
                if (row < 0 || !TryGetVisibleSegment(session, date, out var start, out var end))
                {
                    continue;
                }

                yield return (session, row, date, start, end);
            }
        }
    }

    internal static string FormatDurationShort(TimeSpan span)
    {
        var totalMinutes = span.TotalMinutes;
        if (totalMinutes < 60)
            return $"{totalMinutes:F1}分钟";
        var hours = (int)(totalMinutes / 60);
        var minutes = (int)Math.Round(totalMinutes - hours * 60, MidpointRounding.AwayFromZero);
        if (minutes == 60) { hours++; minutes = 0; }
        return $"{hours}h{minutes}m";
    }
}
