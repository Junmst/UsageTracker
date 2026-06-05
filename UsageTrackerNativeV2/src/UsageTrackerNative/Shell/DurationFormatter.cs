namespace UsageTrackerNative.Shell;

public static class DurationFormatter
{
    public static string Format(TimeSpan span)
    {
        if (span.TotalSeconds < 1)
        {
            return "0秒";
        }

        if (span.TotalMinutes < 1)
        {
            return $"{Math.Max(1, (int)Math.Round(span.TotalSeconds))}秒";
        }

        var totalHours = (int)span.TotalHours;
        var minutes = span.Minutes;
        return totalHours > 0 ? $"{totalHours}h{minutes}m" : $"{(int)span.TotalMinutes}分钟";
    }
}
