using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using static UsageTrackerNative.MainWindow;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 会话条渲染层 — 使用 PushOpacity 替代 Brush.Clone()，
/// 使用 screenX = (worldX - OffsetX) * Zoom 坐标公式。
/// </summary>
public class SessionBarLayer : RenderLayer
{
    private Brush _barBrush = Brushes.Transparent;
    private Brush _accentBrush = Brushes.Transparent;
    private Brush _categoryBrush = Brushes.Transparent;

    public SessionBarLayer(TimeDistributionControl control) : base(control)
    {
        UpdateBrushes();
    }

    public override void UpdateBrushes()
    {
        _barBrush = _control.ThemeBrush("AccentBlueBrush");
        _accentBrush = _control.ThemeBrush("AccentBlueBrush");
        _categoryBrush = _control.ThemeBrush("MenuSelectedBrush");
    }

    public override void Render()
    {
        using var dc = _visual.RenderOpen();
        var zoom = _control.Zoom;
        var offsetX = _control.OffsetX;
        var baseWidth = _control.BaseWidth;
        var rowHeight = _control.RowHeight;
        var viewportWidth = _control.ChartViewportWidth;
        var viewportHeight = _control.ChartViewportHeight;
        const double totalMinutes = 1440; // 24小时 = 4:00 ~ 次日4:00
        var rangeStart = _control.RangeStart;

        if (baseWidth <= 0 || viewportWidth <= 0) return;

        foreach (var segment in _control.EnumerateVisibleSessionSegments())
        {
            var session = segment.Session;
            var row = segment.Row;
            var dayRangeStart = segment.Date.Date.AddHours(4);
            var start = segment.Start;
            var end = segment.End;
            if (end <= start) continue;

            var startMinutes = (start - dayRangeStart).TotalMinutes;
            var endMinutes = (end - dayRangeStart).TotalMinutes;

            var worldX = startMinutes / totalMinutes * baseWidth;
            var worldWidth = (endMinutes - startMinutes) / totalMinutes * baseWidth;
            var worldY = row * rowHeight;

            var screenX = _control.WorldToScreenX(worldX);
            var screenWidth = worldWidth * zoom;
            var screenY = _control.WorldToScreenY(worldY);

            // 视口裁剪
            if (screenX + screenWidth < 0 || screenX > viewportWidth) continue;
            if (screenY + rowHeight < 0 || screenY > viewportHeight) continue;

            // 绘制会话条：保持日期行距不变，只收窄条形高度，让行与行之间自然留出间隙。
            var height = Math.Max(8, Math.Min(rowHeight - 10, 34));
            var barTop = screenY + (rowHeight - height) / 2;
            var barWidth = Math.Max(2, screenWidth - 1);
            var rect = new Rect(screenX + 0.5, barTop, barWidth, height);

            var brush = GetSessionBrush(session);
            if (_control.IsFastInteractionRender)
            {
                // R2：交互帧使用矩形和不透明绘制，减少圆角/透明度组合成本。
                dc.DrawRectangle(brush, null, rect);
            }
            else
            {
                // PushOpacity 替代 Brush.Clone()
                dc.PushOpacity(session.IsHighlighted ? 1.0 : 0.88);
                dc.DrawRoundedRectangle(brush, null, rect, 3, 3);
                dc.Pop();
            }
        }
    }

    private Brush GetSessionBrush(UsageSession session)
    {
        // 高亮的用更亮的颜色
        if (session.IsHighlighted)
            return _accentBrush;
        return _barBrush;
    }

    private int GetRowForSession(UsageSession session)
    {
        var sessionDate = TimeDistributionControl.GetTimeDistributionDate(session.StartTime);
        return _control.GetRowForDate(sessionDate);
    }
}
