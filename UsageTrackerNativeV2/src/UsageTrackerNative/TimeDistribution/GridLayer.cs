using System;
using System.Windows;
using System.Windows.Media;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 图表区网格线 — 垂直线（时间刻度）+ 水平线（行分隔）。
/// 垂直线间隔与 HeaderAxisLayer 完全同步，使用相同的坐标公式。
/// </summary>
public class GridLayer : RenderLayer
{
    private Brush _majorBrush = Brushes.Transparent;
    private Brush _minorBrush = Brushes.Transparent;

    public GridLayer(TimeDistributionControl control) : base(control)
    {
        UpdateBrushes();
    }

    public override void UpdateBrushes()
    {
        _majorBrush = _control.ThemeBrush("BorderBrush");
        _minorBrush = _control.ThemeBrush("BorderBrush");
    }

    public override void Render()
    {
        using var dc = _visual.RenderOpen();
        var zoom = _control.Zoom;
        var baseWidth = _control.BaseWidth;
        var viewportWidth = _control.ChartViewportWidth;
        var viewportHeight = _control.ChartViewportHeight;
        var rowHeight = _control.RowHeight;
        const double totalMinutes = 1440;

        if (baseWidth <= 0 || viewportWidth <= 0) return;

        // ── 垂直网格线 — 与 HeaderAxisLayer 完全相同的间隔和坐标公式 ──
        int minuteStep = TimeDistributionControl.GetTimeScaleStep(zoom);

        var majorPen = new Pen(_majorBrush, 1);

        for (int minutesOffset = 0; minutesOffset <= totalMinutes; minutesOffset += minuteStep)
        {
            // 与 HeaderAxisLayer 完全相同的坐标公式
            var worldX = minutesOffset / totalMinutes * baseWidth;
            var screenX = _control.WorldToScreenX(worldX);

            if (screenX < -1 || screenX > viewportWidth + 1) continue;

            dc.DrawLine(majorPen,
                new Point(screenX, 0),
                new Point(screenX, viewportHeight));
        }

        // ── 水平网格线 ──
        var totalRows = _control.VisibleDates.Count;
        var minorBrush = _minorBrush.Clone();
        minorBrush.Opacity = 0.4;
        var minorPen = new Pen(minorBrush, 0.5);

        for (var row = 1; row <= totalRows; row++)
        {
            var worldY = row * rowHeight;
            var screenY = _control.WorldToScreenY(worldY);

            if (screenY < -1 || screenY > viewportHeight + 1) continue;

            dc.DrawLine(minorPen,
                new Point(0, screenY),
                new Point(viewportWidth, screenY));
        }
    }
}
