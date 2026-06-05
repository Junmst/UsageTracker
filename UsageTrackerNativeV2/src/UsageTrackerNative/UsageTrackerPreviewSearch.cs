namespace UsageTrackerNative;

public static class UsageTrackerPreviewSearch
{
    public static IReadOnlyList<UsageSession> GetPreviewSessions(ImportPackagePreview preview, string searchText, SessionSearchMode mode)
    {
        var subjectSearchLookup = BuildSubjectSearchLookup(preview.State.SubjectDefinitions);
        return BuildPreviewSessions(preview.State)
            .Where(session => MatchesSessionSearch(session, searchText, mode, subjectSearchLookup))
            .OrderByDescending(session => session.StartTime)
            .Take(500)
            .ToList();
    }

    private static IReadOnlyList<UsageSession> BuildPreviewSessions(UsageTrackerState state)
    {
        var records = (state.History ?? []).Take(50000).Select(record => record.Clone()).ToList();
        var manualSubjects = state.ManualSubjects ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keywordRules = state.SubjectKeywordRules ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var definitions = state.SubjectDefinitions ?? [];
        foreach (var record in records)
        {
            record.EnsureId();
            if (string.IsNullOrWhiteSpace(record.ManualSubject))
            {
                record.ManualSubject = ResolvePreviewSubject(record, manualSubjects, keywordRules, definitions);
            }
        }

        return records.Select(ToPreviewSession).ToList();
    }

    private static UsageSession ToPreviewSession(UsageSessionRecord record)
    {
        record.EnsureId();
        return new UsageSession
        {
            Id = record.Id,
            ProcessName = record.ProcessName,
            WindowTitle = record.WindowTitle,
            ManualSubject = string.IsNullOrWhiteSpace(record.ManualSubject) ? null : record.ManualSubject,
            StartTime = record.StartTime,
            EndTime = record.EndTime ?? DateTime.Now
        };
    }

    private static string? ResolvePreviewSubject(UsageSessionRecord record, Dictionary<string, string> manualSubjects, Dictionary<string, List<string>> keywordRules, List<SubjectDefinition> definitions)
    {
        if (manualSubjects.TryGetValue(BuildClassificationKey(record.ProcessName, record.WindowTitle), out var subject))
        {
            return subject;
        }

        return MatchSubjectByKeyword(record.ProcessName, record.WindowTitle, keywordRules, definitions);
    }

    private static string? MatchSubjectByKeyword(string processName, string windowTitle, Dictionary<string, List<string>> keywordRules, List<SubjectDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            foreach (var parent in definition.Parents)
            {
                foreach (var child in parent.Children)
                {
                    if (MatchesSubjectRule(child, processName, windowTitle, keywordRules))
                    {
                        return child;
                    }
                }
                if (MatchesSubjectRule(parent.Name, processName, windowTitle, keywordRules))
                {
                    return parent.Name;
                }
            }
            if (MatchesSubjectRule(definition.Name, processName, windowTitle, keywordRules))
            {
                return definition.Name;
            }
        }
        return null;
    }

    private static bool MatchesSubjectRule(string subject, string processName, string windowTitle, Dictionary<string, List<string>> keywordRules)
    {
        return keywordRules.TryGetValue(subject, out var keywords)
            && keywords.Any(keyword => MatchesKeywordRule(keyword, processName, windowTitle));
    }

    private static bool MatchesKeywordRule(string keyword, string processName, string windowTitle)
    {
        return SearchExpressionMatcher.IsMatch(keyword, term => processName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || windowTitle.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildClassificationKey(string processName, string windowTitle)
    {
        return $"{processName}|{windowTitle}";
    }

    private static Dictionary<string, HashSet<string>> BuildSubjectSearchLookup(List<SubjectDefinition>? definitions)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void AddLinkedSubjects(string? key, IEnumerable<string> subjects)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            var normalizedKey = key.Trim();
            if (!lookup.TryGetValue(normalizedKey, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lookup[normalizedKey] = set;
            }

            foreach (var subject in subjects)
            {
                if (!string.IsNullOrWhiteSpace(subject))
                {
                    set.Add(subject.Trim());
                }
            }
        }

        foreach (var major in definitions ?? [])
        {
            AddLinkedSubjects(major.Name, major.GetAllSubjectNames().Concat(major.Children));

            foreach (var child in major.Children)
            {
                AddLinkedSubjects(child, [child]);
            }

            foreach (var parent in major.Parents)
            {
                AddLinkedSubjects(parent.Name, parent.GetAllSubjectNames());
                foreach (var child in parent.Children)
                {
                    AddLinkedSubjects(child, [child]);
                }
            }
        }

        return lookup;
    }

    private static bool MatchesSessionSearch(UsageSession session, string searchText, SessionSearchMode mode, IReadOnlyDictionary<string, HashSet<string>> subjectSearchLookup)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return SearchExpressionMatcher.IsMatch(searchText, term => mode switch
        {
            SessionSearchMode.Subject => MatchesSubjectSearch(session, term, subjectSearchLookup),
            SessionSearchMode.Title => session.WindowTitle.Contains(term, StringComparison.OrdinalIgnoreCase),
            SessionSearchMode.Process => session.ProcessName.Contains(term, StringComparison.OrdinalIgnoreCase),
            _ => MatchesSessionSearchTerm(session, term, subjectSearchLookup)
        });
    }

    private static bool MatchesSessionSearchTerm(UsageSession session, string searchText, IReadOnlyDictionary<string, HashSet<string>> subjectSearchLookup)
    {
        var separatorIndex = searchText.IndexOfAny([':', '：']);
        if (separatorIndex > 0 && separatorIndex < searchText.Length - 1)
        {
            var scope = searchText[..separatorIndex].Trim().ToLowerInvariant();
            var value = searchText[(separatorIndex + 1)..].Trim();
            return scope switch
            {
                "分类" or "科目" or "subject" => MatchesSubjectSearch(session, value, subjectSearchLookup),
                "标题" or "窗口" or "title" => session.WindowTitle.Contains(value, StringComparison.OrdinalIgnoreCase),
                "进程" or "程序" or "process" or "proc" => session.ProcessName.Contains(value, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        return MatchesSubjectSearch(session, searchText, subjectSearchLookup)
            || session.WindowTitle.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || session.ProcessName.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSubjectSearch(UsageSession session, string searchText, IReadOnlyDictionary<string, HashSet<string>> subjectSearchLookup)
    {
        var subject = string.IsNullOrWhiteSpace(session.ManualSubject) ? session.SubjectText : session.ManualSubject;
        var normalizedSearch = searchText.Trim();
        if (subjectSearchLookup.TryGetValue(normalizedSearch, out var linkedSubjects))
        {
            return linkedSubjects.Contains(subject);
        }

        return subject.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }
}
