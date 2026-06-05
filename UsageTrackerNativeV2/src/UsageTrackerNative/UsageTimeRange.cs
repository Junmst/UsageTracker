namespace UsageTrackerNative;

public static class UsageTimeRange
{
    public static bool Overlaps(DateTime start, DateTime? end, DateTime rangeStart, DateTime rangeEnd)
    {
        var effectiveEnd = end ?? DateTime.Now;
        return start < rangeEnd && effectiveEnd > rangeStart;
    }

    public static TimeSpan GetOverlapDuration(DateTime start, DateTime? end, DateTime rangeStart, DateTime rangeEnd)
    {
        var effectiveEnd = end ?? DateTime.Now;
        var clippedStart = start > rangeStart ? start : rangeStart;
        var clippedEnd = effectiveEnd < rangeEnd ? effectiveEnd : rangeEnd;
        return clippedEnd > clippedStart ? clippedEnd - clippedStart : TimeSpan.Zero;
    }

    public static DateTime GetTimeDistributionDate(DateTime time)
    {
        var date = time.Date;
        return time.TimeOfDay < TimeSpan.FromHours(4) ? date.AddDays(-1) : date;
    }

    public static DateTime GetDayStart(DateTime date)
    {
        return date.Date.AddHours(4);
    }

    public static DateTime GetDayEnd(DateTime date)
    {
        return GetDayStart(date).AddDays(1);
    }
}
