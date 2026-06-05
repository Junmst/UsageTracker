using System;
using System.Windows;
using static UsageTrackerNative.MainWindow;

namespace UsageTrackerNative.TimeDistribution;

/// <summary>
/// 会话条命中测试 — 使用新的坐标公式 screenX = (worldX - OffsetX) * Zoom。
/// 将屏幕坐标反变换到世界坐标进行匹配。
/// </summary>
public class SessionHitTester
{
    private readonly TimeDistributionControl _control;

    public SessionHitTester(TimeDistributionControl control)
    {
        _control = control;
    }

    /// <summary>
    /// 对图表区内的鼠标位置进行命中测试。
    /// pos 是相对于 _chartHost 的坐标（已减去 HeaderHeight 和 DateColumnWidth）。
    /// </summary>
    public UsageSession? HitTest(Point pos)
    {
        var zoom = _control.Zoom;
        var baseWidth = _control.BaseWidth;
        var rowHeight = _control.RowHeight;
        const double totalMinutes = 1440; // 24小时

        if (baseWidth <= 0) return null;

        // 屏幕坐标 → 世界坐标
        var worldX = _control.ScreenToWorldX(pos.X);
        var worldY = _control.ScreenToWorldY(pos.Y);

        var row = (int)(worldY / rowHeight);
        if (row < 0 || row >= _control.VisibleDates.Count) return null;

        var date = _control.VisibleDates[row];
        var dayRangeStart = date.Date.AddHours(4);

        foreach (var segment in _control.EnumerateVisibleSessionSegments())
        {
            if (segment.Row != row) continue;

            var startMinutes = (segment.Start - dayRangeStart).TotalMinutes;
            var endMinutes = (segment.End - dayRangeStart).TotalMinutes;

            var sessionLeft = startMinutes / totalMinutes * baseWidth;
            var sessionRight = endMinutes / totalMinutes * baseWidth;

            if (worldX >= sessionLeft && worldX <= sessionRight)
            {
                return segment.Session;
            }
        }

        return null;
    }

    private int GetRowForSession(UsageSession session)
    {
        var sessionDate = TimeDistributionControl.GetTimeDistributionDate(session.StartTime);
        return _control.GetRowForDate(sessionDate);
    }
}
