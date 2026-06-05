using System;
using System.Windows;
using System.Windows.Media;
using static UsageTrackerNative.MainWindow;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 悬停/选中高亮层 — 当鼠标悬停在某个会话条上时，绘制高亮边框和发光效果。
/// </summary>
public class SelectionLayer : RenderLayer
{
    private Brush _highlightBrush = Brushes.Transparent;
    private Pen _highlightPen = new(Brushes.Transparent, 1.5);
    private UsageSession? _hoveredSession;

    public SelectionLayer(TimeDistributionControl control) : base(control)
    {
        UpdateBrushes();
    }

    public override void UpdateBrushes()
    {
        _highlightBrush = _control.ThemeBrush("AccentBlueBrush");
        _highlightPen = new Pen(_highlightBrush, 1.5);
    }

    public void SetHoveredSession(UsageSession? session)
    {
        _hoveredSession = session;
    }

    public override void Render()
    {
        using var dc = _visual.RenderOpen();

        if (_hoveredSession == null) return;

        var session = _hoveredSession;
        var zoom = _control.Zoom;
        var baseWidth = _control.BaseWidth;
        var rowHeight = _control.RowHeight;
        var viewportWidth = _control.ChartViewportWidth;
        var viewportHeight = _control.ChartViewportHeight;
        const double totalMinutes = 1440;

        if (baseWidth <= 0 || viewportWidth <= 0) return;

        var row = GetRowForSession(session);
        if (row < 0) return;

        // 与 SessionBarLayer 一致：使用 dayRangeStart
        var sessionDate = TimeDistributionControl.GetTimeDistributionDate(session.StartTime);
        var dayRangeStart = sessionDate.AddHours(4);
        var dayRangeEnd = dayRangeStart.AddDays(1);

        var start = session.StartTime < dayRangeStart ? dayRangeStart : session.StartTime;
        var end = session.EndTime > dayRangeEnd ? dayRangeEnd : session.EndTime;
        if (end <= start) return;

        var startMinutes = (start - dayRangeStart).TotalMinutes;
        var endMinutes = (end - dayRangeStart).TotalMinutes;

        var worldX = startMinutes / totalMinutes * baseWidth;
        var worldWidth = (endMinutes - startMinutes) / totalMinutes * baseWidth;
        var worldY = row * rowHeight;

        var screenX = _control.WorldToScreenX(worldX);
        var screenWidth = worldWidth * zoom;
        var screenY = _control.WorldToScreenY(worldY);

        // 视口裁剪
        if (screenX + screenWidth < 0 || screenX > viewportWidth) return;
        if (screenY + rowHeight < 0 || screenY > viewportHeight) return;

        var height = Math.Max(8, Math.Min(rowHeight - 10, 34));
        var barTop = screenY + (rowHeight - height) / 2;
        var barWidth = Math.Max(2, screenWidth - 1);
        var rect = new Rect(screenX + 0.5, barTop, barWidth, height);

        // 发光效果：先画一个半透明放大版
        dc.PushOpacity(0.15);
        var glowRect = new Rect(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4);
        dc.DrawRoundedRectangle(_highlightBrush, null, glowRect, 5, 5);
        dc.Pop();

        // 高亮边框
        dc.DrawRoundedRectangle(null, _highlightPen, rect, 3, 3);
    }

    private int GetRowForSession(UsageSession session)
    {
        var sessionDate = TimeDistributionControl.GetTimeDistributionDate(session.StartTime);
        var visibleDates = _control.VisibleDates;
        for (int i = 0; i < visibleDates.Count; i++)
        {
            if (visibleDates[i] == sessionDate)
                return i;
        }
        return -1;
    }
}
