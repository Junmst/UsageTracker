namespace UsageTrackerNative;

public static class ClassificationResolver
{
    public const string EmptySubjectMarker = "__SHIJI_EMPTY_SUBJECT__";

    public static string? Resolve(
        string processName,
        string windowTitle,
        IEnumerable<SubjectDefinition> definitions,
        IReadOnlyDictionary<string, List<string>> keywordRules,
        IReadOnlyDictionary<string, string>? manualSubjects = null,
        string? storedSubject = null)
    {
        var key = BuildKey(processName, windowTitle);
        // 空分类标记表示“用户显式清除了手动分类”：不是一个真实分类，
        // 必须继续向下走关键词匹配（命中则归类，都不命中才留空）。
        if (manualSubjects is not null
            && manualSubjects.TryGetValue(key, out var manual)
            && manual != EmptySubjectMarker)
        {
            return NormalizeSubject(manual);
        }

        var matched = SearchExpressionMatcher.ResolveSubjectByKeywordRules(definitions, keywordRules, processName, windowTitle);
        return NormalizeSubject(string.IsNullOrWhiteSpace(matched) ? storedSubject : matched);
    }

    // 按记录所属时间解析分类：手动分类是一条带生效时间点的时间线，
    // 取 EffectiveFrom <= at 的最后一个节点。节点 Subject 为 null/空分类标记
    // 表示“从该时刻起用户显式清除手动分类”，继续走关键词匹配。
    // 时间线节点之前的历史记录不会被后来的修改追溯影响（历史快照语义）。
    public static string? ResolveAt(
        string processName,
        string windowTitle,
        IEnumerable<SubjectDefinition> definitions,
        IReadOnlyDictionary<string, List<string>> keywordRules,
        IReadOnlyDictionary<string, List<ManualSubjectScope>>? manualScopes,
        DateTime at,
        string? storedSubject = null)
        => ResolveDetailedAt(processName, windowTitle, definitions, keywordRules, manualScopes, at, storedSubject).Subject;

    // ResolveAt 的可解释版本：除最终分类外，还返回来源（manual 手动指定 / keyword 关键词命中 /
    // stored 历史快照 / none 空分类）与命中的关键词规则原文（含 + * - () 等连接符，供界面展示）。
    public static SubjectResolution ResolveDetailedAt(
        string processName,
        string windowTitle,
        IEnumerable<SubjectDefinition> definitions,
        IReadOnlyDictionary<string, List<string>> keywordRules,
        IReadOnlyDictionary<string, List<ManualSubjectScope>>? manualScopes,
        DateTime at,
        string? storedSubject = null)
    {
        var key = BuildKey(processName, windowTitle);
        if (manualScopes is not null
            && manualScopes.TryGetValue(key, out var timeline)
            && timeline is not null
            && TryGetManualSubjectAt(timeline, at, out var manualAssignment)
            && !string.IsNullOrWhiteSpace(manualAssignment)
            && manualAssignment != EmptySubjectMarker)
        {
            return new SubjectResolution(NormalizeSubject(manualAssignment), SubjectResolutionSource.Manual, null);
        }

        var hit = SearchExpressionMatcher.ResolveKeywordRule(definitions, keywordRules, processName, windowTitle);
        if (hit is not null && !string.IsNullOrWhiteSpace(hit.Subject))
        {
            return new SubjectResolution(NormalizeSubject(hit.Subject), SubjectResolutionSource.Keyword, hit.Rule);
        }

        var stored = NormalizeSubject(storedSubject);
        return string.IsNullOrWhiteSpace(stored)
            ? new SubjectResolution(null, SubjectResolutionSource.None, null)
            : new SubjectResolution(stored, SubjectResolutionSource.Stored, null);
    }

    // 返回某时刻生效的手动分类；null 返回值区分两种情况：hasAssignment=false 表示该时刻无手动节点，
    // hasAssignment=true 且 assignment=null 表示存在“显式清除”节点。
    public static bool TryGetManualSubjectAt(
        IReadOnlyList<ManualSubjectScope>? timeline,
        DateTime at,
        out string? assignment)
    {
        assignment = null;
        if (timeline is null)
        {
            return false;
        }

        var found = false;
        foreach (var scope in timeline)
        {
            if (scope is not null && scope.EffectiveFrom <= at)
            {
                found = true;
                assignment = scope.Subject;
            }
        }

        return found;
    }

    public static string? NormalizeSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var normalized = subject.Trim();
        if (normalized.Equals(EmptySubjectMarker, StringComparison.OrdinalIgnoreCase)) return null;
        return normalized.Equals("空分类", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("未分类", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    public static string BuildKey(string processName, string windowTitle)
        => $"{processName}|{windowTitle}";
}

// 手动分类时间线节点：Subject 自 EffectiveFrom（业务日 04:00 边界）起生效。
// Subject 为 null 表示“从该时刻起清除手动分类”，之后回落到关键词自动匹配。
public sealed record ManualSubjectScope(DateTime EffectiveFrom, string? Subject);

// 分类解析来源：手动指定 / 关键词规则命中 / 记录上的历史快照 / 无分类。
public static class SubjectResolutionSource
{
    public const string Manual = "manual";
    public const string Keyword = "keyword";
    public const string Stored = "stored";
    public const string None = "none";
}

// 分类解析结果：Subject 为最终分类，Source 为来源，MatchedRule 为关键词命中时的规则原文。
public sealed record SubjectResolution(string? Subject, string Source, string? MatchedRule);
