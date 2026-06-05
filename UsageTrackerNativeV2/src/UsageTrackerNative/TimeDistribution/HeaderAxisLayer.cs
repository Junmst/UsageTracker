using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 时间刻度表头文本层 — 与 SessionBarLayer 使用完全相同的坐标公式。
/// worldX = minutesOffset / 1440 * baseWidth，screenX = (worldX - OffsetX) * Zoom。
/// minutesOffset 从 0 开始（对应 4:00），到 1440（对应次日 4:00）。
/// </summary>
public class HeaderAxisLayer : RenderLayer
{
    private Brush _textBrush = Brushes.Transparent;
    private readonly Dictionary<string, FormattedText> _textCache = new();
    private int _cachedMinuteStep = -1;
    private double _cachedDpi;
    private Typeface _typeface = new("Segoe UI");

    public HeaderAxisLayer(TimeDistributionControl control) : base(control)
    {
        UpdateBrushes();
    }

    public override void UpdateBrushes()
    {
        _textBrush = _control.ThemeBrush("SecondaryTextBrush");
        _textCache.Clear();
        _cachedMinuteStep = -1;
        _cachedDpi = 0;
    }

    public override void Render()
    {
        using var dc = _visual.RenderOpen();
        var zoom = _control.Zoom;
        var baseWidth = _control.BaseWidth;
        var viewportWidth = _control.ChartViewportWidth;
        const double totalMinutes = 1440; // 4:00 ~ 次日4:00 = 24小时
        var rangeStart = _control.RangeStart; // Today + 4:00

        if (baseWidth <= 0 || viewportWidth <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(_visual).PixelsPerDip;
        var minuteStep = TimeDistributionControl.GetTimeScaleStep(zoom);
        EnsureTextCache(minuteStep, rangeStart, dpi);

        for (int minutesOffset = 0; minutesOffset <= totalMinutes; minutesOffset += minuteStep)
        {
            var worldX = minutesOffset / totalMinutes * baseWidth;
            var screenX = _control.WorldToScreenX(worldX);

            if (screenX < -80 || screenX > viewportWidth + 80) continue;

            var time = rangeStart.AddMinutes(minutesOffset);
            var text = FormatTimeText(time, minuteStep);
            var formattedText = _textCache[text];

            dc.DrawText(formattedText, new Point(screenX - formattedText.Width / 2, 8));
        }
    }

    private void EnsureTextCache(int minuteStep, DateTime rangeStart, double dpi)
    {
        if (_cachedMinuteStep == minuteStep && Math.Abs(_cachedDpi - dpi) < 0.0001 && _textCache.Count > 0)
            return;

        _textCache.Clear();
        _cachedMinuteStep = minuteStep;
        _cachedDpi = dpi;

        const int totalMinutes = 1440;
        for (int minutesOffset = 0; minutesOffset <= totalMinutes; minutesOffset += minuteStep)
        {
            var time = rangeStart.AddMinutes(minutesOffset);
            var text = FormatTimeText(time, minuteStep);
            if (_textCache.ContainsKey(text)) continue;

            _textCache[text] = new FormattedText(text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                11,
                _textBrush,
                dpi);
        }
    }

    private static string FormatTimeText(DateTime time, int minuteStep)
    {
        return minuteStep >= 60
            ? $"{time.Hour:D2}:00"
            : $"{time.Hour:D2}:{time.Minute:D2}";
    }

}
