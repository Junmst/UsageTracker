namespace UsageTrackerWeb;

public sealed class SessionDto
{
    public string Id { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ManualSubject { get; set; }
    // 分类来源：manual 手动指定 / keyword 关键词命中 / stored 历史快照 / none 空分类。
    public string? SubjectSource { get; set; }
    // 关键词命中时用户配置的规则原文（含 + * - () 等连接符），供详情面板展示。
    public string? MatchedKeywordRule { get; set; }
    public List<ParallelActivityDto> ParallelActivities { get; set; } = new();
    public DateTime? LastCapturedAt { get; set; }
    public double DurationSeconds { get; set; }
    public bool IsRunning => EndTime is null;
}

public sealed class ParallelActivityDto
{
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double ObservedSeconds { get; set; }
    public bool CountInTotal { get; set; }
}

public sealed class BucketStatDto
{
    public string Key { get; set; } = string.Empty;
    /// <summary>前台时长（秒）：计入总使用时长。</summary>
    public double Seconds { get; set; }
    public int SessionCount { get; set; }
    /// <summary>后台并行时长（秒）：该进程作为白名单媒体在其他前台记录期间发声的累计，不重复计入总时长。</summary>
    public double ParallelSeconds { get; set; }
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
