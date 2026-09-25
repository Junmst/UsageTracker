namespace UsageTrackerWeb;

public sealed class SessionDto
{
    public string Id { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ManualSubject { get; set; }
    public double DurationSeconds { get; set; }
    public bool IsRunning => EndTime is null;
}

public sealed class BucketStatDto
{
    public string Key { get; set; } = string.Empty;
    public double Seconds { get; set; }
    public int SessionCount { get; set; }
}

public sealed class DailyPointDto
{
    public string Date { get; set; } = string.Empty;
    public double Seconds { get; set; }
}

public sealed class SearchResultDto
{
    public List<SessionDto> Items { get; set; } = new();
    public long TotalCount { get; set; }
}

/// <summary>分类统计树节点：大类 → 父类 → 子类，与桌面版 SubjectNode 一致。</summary>
public sealed record SubjectNodeDto(
    string Name,
    double Seconds,
    int SessionCount,
    List<SubjectNodeDto> Children);

public sealed class OverviewDto
{
    public string Date { get; set; } = string.Empty;
    public double TodaySeconds { get; set; }
    public double WeekSeconds { get; set; }
    public double MonthSeconds { get; set; }
    public double TotalSeconds { get; set; }
    public int TrackedDays { get; set; }
    public long SessionCount { get; set; }
    public string? EarliestDate { get; set; }
    public SessionDto? Active { get; set; }
    public double DatabaseSizeMb { get; set; }
}
