using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using static UsageTrackerNative.MainWindow;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 日期列渲染层 — 左侧固定列，显示 "今天（周一）" + 时长。
/// 纯 DrawingVisual 替代旧的 Canvas + TextBlock。
/// </summary>
public class DateLabelLayer : RenderLayer
{
    private Brush _textBrush = Brushes.Transparent;
    private Brush _secondaryTextBrush = Brushes.Transparent;

    public DateLabelLayer(TimeDistributionControl control) : base(control)
    {
        UpdateBrushes();
    }

    public override void UpdateBrushes()
    {
        _textBrush = _control.ThemeBrush("SecondaryTextBrush");
        _secondaryTextBrush = _control.ThemeBrush("SecondaryTextBrush");
    }

    public override void Render()
    {
        using var dc = _visual.RenderOpen();
        var offsetY = _control.OffsetY;
        var rowHeight = _control.RowHeight;
        var viewportHeight = _control.ChartViewportHeight;

        if (viewportHeight <= 0) return;

        var visibleDates = _control.VisibleDates;
        var sessions = _control.Sessions;

        // 计算每日总时长
        var sessionsByDate = visibleDates.ToDictionary(
            date => date,
            date => TimeSpan.FromTicks(sessions.Sum(session => TimeDistributionControl.GetVisibleDurationForDate(session, date).Ticks)));

        var dpi = VisualTreeHelper.GetDpi(_visual).PixelsPerDip;
        var typefaceSemiBold = new Typeface("Segoe UI");
        var typefaceRegular = new Typeface("Segoe UI");

        for (int i = 0; i < visibleDates.Count; i++)
        {
            var worldY = i * rowHeight;
            var screenY = _control.WorldToScreenY(worldY);

            // 视口裁剪
            if (screenY + rowHeight < 0 || screenY > viewportHeight) continue;

            var date = visibleDates[i];
            var label = FormatDateLabel(date);

            // 日期文本
            var dateText = new FormattedText(label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typefaceSemiBold,
                13,
                _textBrush,
                dpi);
            dateText.MaxTextWidth = 104;
            dc.DrawText(dateText, new Point(4, screenY + 5));

            // 时长小字
            sessionsByDate.TryGetValue(date, out var dayTotal);
            var durationStr = TimeDistributionControl.FormatDurationShort(dayTotal);

            var durationText = new FormattedText(durationStr,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typefaceRegular,
                11,
                _secondaryTextBrush,
                dpi);
            durationText.MaxTextWidth = 104;

            dc.PushOpacity(0.72);
            dc.DrawText(durationText, new Point(4, screenY + 22));
            dc.Pop();
        }
    }

    private static string FormatDateLabel(DateTime date)
    {
        if (date.Date == DateTime.Today)
            return $"今天（{GetChineseWeekday(date)}）";
        if (date.Date == DateTime.Today.AddDays(-1))
            return $"昨天（{GetChineseWeekday(date)}）";
        return FormatDateWithWeekday(date);
    }

    private static string FormatDateWithWeekday(DateTime date)
    {
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
}
