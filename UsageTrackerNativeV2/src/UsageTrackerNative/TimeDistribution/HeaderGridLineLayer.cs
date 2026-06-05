using System;
using System.Windows;
using System.Windows.Media;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 表头区竖线层 — 在整点位置绘制短竖线，与 HeaderAxisLayer 完全同步。
/// </summary>
public class HeaderGridLineLayer : RenderLayer
{
    private Brush _majorBrush = Brushes.Transparent;

    public HeaderGridLineLayer(TimeDistributionControl control) : base(control)
    {
        UpdateBrushes();
    }

    public override void UpdateBrushes()
    {
        _majorBrush = _control.ThemeBrush("BorderBrush");
    }

    public override void Render()
    {
        using var dc = _visual.RenderOpen();
        var zoom = _control.Zoom;
        var baseWidth = _control.BaseWidth;
        var viewportWidth = _control.ChartViewportWidth;
        const double totalMinutes = 1440;
        const double headerH = 28;

        if (baseWidth <= 0 || viewportWidth <= 0) return;

        var pen = new Pen(_majorBrush, 1);

        // 与 HeaderAxisLayer 完全相同的间隔
        int minuteStep = TimeDistributionControl.GetTimeScaleStep(zoom);

        for (int minutesOffset = 0; minutesOffset <= totalMinutes; minutesOffset += minuteStep)
        {
            var worldX = minutesOffset / totalMinutes * baseWidth;
            var screenX = _control.WorldToScreenX(worldX);

            if (screenX < -1 || screenX > viewportWidth + 1) continue;

            dc.DrawLine(pen,
                new Point(screenX, headerH - 6),
                new Point(screenX, headerH));
        }
    }
}
