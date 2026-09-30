namespace UsageTrackerNative;

public static class DurationFormatter
{
    public static string Format(TimeSpan span)
    {
        var totalMinutes = span.TotalMinutes;
        if (totalMinutes < 60)
        {
            return $"{totalMinutes:F1}分钟";
        }

        var hours = (int)(totalMinutes / 60);
        var minutes = (int)Math.Round(totalMinutes - hours * 60, MidpointRounding.AwayFromZero);
        if (minutes == 60)
        {
            hours++;
            minutes = 0;
        }

        return $"{hours}h{minutes}m";
    }
}
