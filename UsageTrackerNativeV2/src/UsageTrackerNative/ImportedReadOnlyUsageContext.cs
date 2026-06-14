namespace UsageTrackerNative;

public sealed class ImportedReadOnlyUsageContext
{
    private readonly List<UsageSessionRecord> _records;
    private readonly List<SubjectDefinition> _subjectDefinitions;
    private readonly Dictionary<string, List<string>> _subjectKeywordRules;
    private readonly DateTime? _earliestSessionDate;

    private ImportedReadOnlyUsageContext(ImportPackagePreview preview)
    {
        FilePath = preview.FilePath;
        Kind = preview.Kind;
        _records = (preview.State.History ?? [])
            .Take(50000)
            .Select(record => record.Clone())
            .ToList();
        foreach (var record in _records)
        {
            record.EnsureId();
        }

        _subjectDefinitions = preview.State.SubjectDefinitions?.Select(x => x.Clone()).ToList() ?? [];
        _subjectKeywordRules = preview.State.SubjectKeywordRules?.ToDictionary(
            x => x.Key,
            x => x.Value.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).Select(keyword => keyword.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _earliestSessionDate = _records.Count == 0 ? null : NormalizeToDateBoundary(_records.Min(x => x.StartTime));
    }

    public string FilePath { get; }
    public ImportPayloadKind Kind { get; }
    public DateTime? EarliestSessionDate => _earliestSessionDate;
    public IReadOnlyList<SubjectDefinition> SubjectDefinitions => _subjectDefinitions.Select(x => x.Clone()).ToList();
    public string DisplayName => System.IO.Path.GetFileName(FilePath);

    public static ImportedReadOnlyUsageContext FromPreview(ImportPackagePreview preview)
    {
        return new ImportedReadOnlyUsageContext(preview);
    }

    public Task<List<UsageSessionRecord>> QuerySessionsByDateAsync(DateTime date)
    {
        var dayStart = date.Date.AddHours(4);
        var dayEnd = dayStart.AddDays(1);
        return Task.FromResult(GetRecordsInRange(dayStart, dayEnd));
    }

    public Task<List<UsageSessionRecord>> QuerySessionsInRangeAsync(DateTime start, DateTime end)
    {
        return Task.FromResult(GetRecordsInRange(start, end));
    }

    public Task<UsageOverview> QueryOverviewAsync(DateTime endExclusive)
    {
        var records = _records.Where(record => record.StartTime < endExclusive).ToList();
        var totalTicks = records.Sum(GetDurationTicks);
        var dayCount = records
            .Select(record => NormalizeToDateBoundary(record.StartTime))
            .Distinct()
            .Count();
        var weekStart = endExclusive.Date.AddDays(-7);
        var weekTicks = _records.Sum(record => UsageTimeRange.GetOverlapDuration(record.StartTime, record.EndTime, weekStart, endExclusive).Ticks);
        var averageTicks = dayCount == 0 ? 0 : totalTicks / dayCount;
        return Task.FromResult(new UsageOverview(TimeSpan.FromTicks(totalTicks), TimeSpan.FromTicks(averageTicks), TimeSpan.FromTicks(weekTicks)));
    }

    public Task<TimeSpan> QueryWeekUsageAsync(DateTime weekStart, DateTime weekEndExclusive)
    {
        var ticks = _records.Sum(record => UsageTimeRange.GetOverlapDuration(record.StartTime, record.EndTime, weekStart, weekEndExclusive).Ticks);
        return Task.FromResult(TimeSpan.FromTicks(ticks));
    }

    public IReadOnlyList<UsageSession> GetSessionsInRange(DateTime startInclusive, DateTime endExclusive)
    {
        return GetRecordsInRange(startInclusive, endExclusive).Select(ToUsageSession).ToList();
    }

    public IReadOnlyDictionary<string, HashSet<string>> BuildSubjectSearchLookup()
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

        foreach (var major in _subjectDefinitions)
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

    private List<UsageSessionRecord> GetRecordsInRange(DateTime startInclusive, DateTime endExclusive)
    {
        return _records
            .Where(record => UsageTimeRange.Overlaps(record.StartTime, record.EndTime, startInclusive, endExclusive))
            .Select(CloneAndResolveSubject)
            .ToList();
    }

    private UsageSessionRecord CloneAndResolveSubject(UsageSessionRecord record)
    {
        var clone = record.Clone();
        if (string.IsNullOrWhiteSpace(clone.ManualSubject))
        {
            clone.ManualSubject = ResolveSubject(clone.ProcessName, clone.WindowTitle);
        }
        return clone;
    }

    private string? ResolveSubject(string processName, string windowTitle)
    {
        return SearchExpressionMatcher.ResolveSubjectByKeywordRules(_subjectDefinitions, _subjectKeywordRules, processName, windowTitle);
    }

    private static UsageSession ToUsageSession(UsageSessionRecord record)
    {
        record.EnsureId();
        return new UsageSession
        {
            Id = record.Id,
            ProcessName = record.ProcessName,
            WindowTitle = record.WindowTitle,
            ManualSubject = string.IsNullOrWhiteSpace(record.ManualSubject) ? null : record.ManualSubject,
            StartTime = record.StartTime,
            EndTime = record.EndTime ?? DateTime.Now,
            ParallelActivities = record.ParallelActivities?.Select(x => x.Clone()).ToList() ?? []
        };
    }

    private static long GetDurationTicks(UsageSessionRecord record)
    {
        var end = record.EndTime ?? DateTime.Now;
        return Math.Max(0, (end - record.StartTime).Ticks);
    }

    private static DateTime NormalizeToDateBoundary(DateTime time)
    {
        return time.TimeOfDay < TimeSpan.FromHours(4)
            ? time.Date.AddDays(-1)
            : time.Date;
    }
}
