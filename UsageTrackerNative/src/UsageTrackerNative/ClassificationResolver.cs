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
        if (manualSubjects is not null && manualSubjects.TryGetValue(key, out var manual))
        {
            return manual == EmptySubjectMarker ? null : NormalizeSubject(manual);
        }

        var matched = SearchExpressionMatcher.ResolveSubjectByKeywordRules(definitions, keywordRules, processName, windowTitle);
        return NormalizeSubject(string.IsNullOrWhiteSpace(matched) ? storedSubject : matched);
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
