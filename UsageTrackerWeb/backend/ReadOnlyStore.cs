using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using UsageTrackerNative;

namespace UsageTrackerWeb;

/// <summary>
/// 只读数据访问层。
/// 以 PRAGMA query_only=ON 打开桌面版正在写入的同一个 SQLite 文件，
/// 由 SQLite 引擎保证本进程绝不产生任何写操作，也不会触发建表/迁移/WAL 变更。
/// 统计口径全部复用桌面版 UsageTimeRange（凌晨 4 点日界线 + 区间裁剪）。
/// </summary>
public sealed class ReadOnlyStore
{
    private readonly string _databasePath;
    private readonly SettingsReader _settingsReader;

    public ReadOnlyStore(string dataDirectory, SettingsReader settingsReader)
    {
        _databasePath = Path.Combine(dataDirectory, "usage-tracker.db");
        _settingsReader = settingsReader;
    }

    public string DatabasePath => _databasePath;

    public bool DatabaseExists => File.Exists(_databasePath);

    public double DatabaseSizeMb => DatabaseExists ? new FileInfo(_databasePath).Length / 1024.0 / 1024.0 : 0;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        // query_only=ON 防写入；read_uncommitted 允许 WAL 快照不一致，绕过僵尸实例持有的读锁；
        // busy_timeout 兜底其他短暂竞争。
        pragma.CommandText = "PRAGMA query_only=ON; PRAGMA read_uncommitted=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static string Iso(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime? ParseTime(object? value)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : DateTime.TryParse(text, null, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }

    private static string Text(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    private static List<ParallelActivityDto> ParseParallelActivities(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return new List<ParallelActivityDto>();
        try
        {
            using var document = JsonDocument.Parse(reader.GetString(ordinal));
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(item => new ParallelActivityDto
                {
                    ProcessName = item.TryGetProperty("ProcessName", out var process) ? process.GetString() ?? string.Empty : item.TryGetProperty("processName", out process) ? process.GetString() ?? string.Empty : string.Empty,
                    WindowTitle = item.TryGetProperty("WindowTitle", out var title) ? title.GetString() ?? string.Empty : item.TryGetProperty("windowTitle", out title) ? title.GetString() ?? string.Empty : string.Empty,
                    Description = item.TryGetProperty("Description", out var description) ? description.GetString() ?? string.Empty : item.TryGetProperty("description", out description) ? description.GetString() ?? string.Empty : string.Empty,
                    ObservedSeconds = item.TryGetProperty("ObservedDuration", out var duration) && duration.ValueKind == JsonValueKind.String && TimeSpan.TryParse(duration.GetString(), out var span) ? span.TotalSeconds : 0,
                    CountInTotal = item.TryGetProperty("CountInTotal", out var count) && count.ValueKind == JsonValueKind.True
                }).ToList()
                : new List<ParallelActivityDto>();
        }
        catch
        {
            return new List<ParallelActivityDto>();
        }
    }

    private List<SessionDto> ResolveSubjects(List<SessionDto> sessions)
    {
        var settings = _settingsReader.Load();
        var definitions = ConvertDefinitions(settings.SubjectDefinitions);
        var rules = settings.SubjectKeywordRules ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var manual = settings.ManualSubjects ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            session.ManualSubject = ClassificationResolver.Resolve(
                session.ProcessName,
                session.WindowTitle,
                definitions,
                rules,
                manual,
                session.ManualSubject);
        }
        return sessions;
    }

    private static List<SubjectDefinition> ConvertDefinitions(List<SubjectDefinitionDto>? definitions)
        => (definitions ?? []).Select(x => new SubjectDefinition
        {
            Name = x.Name,
            Children = x.Children?.ToList() ?? [],
            Parents = (x.Parents ?? []).Select(parent => new SubjectParentDefinition
            {
                Name = parent.Name,
                Children = parent.Children?.ToList() ?? []
            }).ToList()
        }).ToList();

    private List<SessionDto> ReadSessions(SqliteDataReader reader)
    {
        var list = new List<SessionDto>();
        while (reader.Read())
        {
            var start = ParseTime(reader.GetValue(3)) ?? DateTime.MinValue;
            var end = ParseTime(reader.GetValue(4));
            var effectiveEnd = end ?? DateTime.Now;
            list.Add(new SessionDto
            {
                Id = Text(reader, 0),
                ProcessName = Text(reader, 1),
                WindowTitle = Text(reader, 2),
                StartTime = start,
                EndTime = end,
                ManualSubject = ClassificationResolver.NormalizeSubject(reader.IsDBNull(5) ? null : reader.GetString(5)),
                ParallelActivities = ParseParallelActivities(reader, 6),
                DurationSeconds = effectiveEnd > start ? (effectiveEnd - start).TotalSeconds : 0
            });
        }

        return list;
    }

    /// <summary>与 [start, end) 相交的未删除会话，SQL 语义与桌面版一致。</summary>
    public List<SessionDto> GetSessionsIntersecting(DateTime start, DateTime end, Func<SessionDto, bool>? filter = null)
    {
        if (!DatabaseExists)
        {
            return new List<SessionDto>();
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
            FROM UsageSessions
            WHERE IsDeleted = 0
             
              AND StartTime < $end
              AND COALESCE(EndTime, $now) > $start
            ORDER BY StartTime ASC
            """;
        command.Parameters.AddWithValue("$start", Iso(start));
        command.Parameters.AddWithValue("$end", Iso(end));
        command.Parameters.AddWithValue("$now", Iso(DateTime.Now));
        using var reader = command.ExecuteReader();
        var sessions = ReadSessions(reader);
        sessions = ResolveSubjects(sessions);
        return filter is null ? sessions : sessions.Where(filter).ToList();
    }

    private static double SumSeconds(IEnumerable<SessionDto> sessions, DateTime start, DateTime end)
    {
        var ticks = 0L;
        foreach (var session in sessions)
        {
            ticks += UsageTimeRange.GetOverlapDuration(session.StartTime, session.EndTime, start, end).Ticks;
        }

        return ticks / 10_000_000.0;
    }

    public double GetSecondsInRange(DateTime start, DateTime end, Func<SessionDto, bool>? filter = null)
        => SumSeconds(GetSessionsIntersecting(start, end, filter), start, end);

    public List<BucketStatDto> GetProcessStats(DateTime start, DateTime end, Func<SessionDto, bool>? filter = null)
    {
        var sessions = GetSessionsIntersecting(start, end, filter);
        return sessions
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ProcessName) ? "未知" : x.ProcessName,
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => new BucketStatDto
            {
                Key = g.Key,
                Seconds = SumSeconds(g, start, end),
                SessionCount = g.Count()
            })
            .OrderByDescending(x => x.Seconds)
            .ToList();
    }

    public List<BucketStatDto> GetSubjectStats(DateTime start, DateTime end, Func<SessionDto, bool>? filter = null)
    {
        var sessions = GetSessionsIntersecting(start, end, filter);
        return sessions
            .Where(x => !string.IsNullOrWhiteSpace(x.ManualSubject))
            .GroupBy(x => x.ManualSubject!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BucketStatDto
            {
                Key = g.Key,
                Seconds = SumSeconds(g, start, end),
                SessionCount = g.Count()
            })
            .OrderByDescending(x => x.Seconds)
            .ToList();
    }

    /// <summary>
    /// 每日时长序列。只做一次范围查询，再按会话实际跨越的天数分摊，
    /// 避免逐日查库（2 年数据就是 700+ 次查询）。
    /// </summary>
    public List<DailyPointDto> GetDailySeries(DateTime fromDate, DateTime toDate, Func<SessionDto, bool>? filter = null)
    {
        var buckets = new Dictionary<string, double>();
        for (var date = fromDate.Date; date <= toDate.Date; date = date.AddDays(1))
        {
            buckets[date.ToString("yyyy-MM-dd")] = 0;
        }

        if (!DatabaseExists || buckets.Count == 0)
        {
            return buckets.Select(x => new DailyPointDto { Date = x.Key, Seconds = x.Value })
                          .OrderBy(x => x.Date).ToList();
        }

        var sessions = GetSessionsIntersecting(UsageTimeRange.GetDayStart(fromDate), UsageTimeRange.GetDayEnd(toDate), filter);
        foreach (var session in sessions)
        {
            var effectiveEnd = session.EndTime ?? DateTime.Now;
            var firstDay = UsageTimeRange.GetTimeDistributionDate(session.StartTime);
            var lastDay = UsageTimeRange.GetTimeDistributionDate(effectiveEnd);
            for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
            {
                var key = date.ToString("yyyy-MM-dd");
                if (!buckets.ContainsKey(key))
                {
                    continue;
                }

                buckets[key] += UsageTimeRange.GetOverlapDuration(
                    session.StartTime, session.EndTime,
                    UsageTimeRange.GetDayStart(date), UsageTimeRange.GetDayEnd(date)).TotalSeconds;
            }
        }

        return buckets.Select(x => new DailyPointDto { Date = x.Key, Seconds = x.Value })
                      .OrderBy(x => x.Date).ToList();
    }

    /// <summary>
    /// 区间统计：单次查询完成 seconds/daily/ranking/sessionCount，避免 4 次全表扫描。
    /// </summary>
    public RangeSummaryData GetRangeSummary(DateTime start, DateTime end, Func<SessionDto, bool>? filter = null)
    {
        var sessions = GetSessionsIntersecting(start, end, filter);
        var seconds = SumSeconds(sessions, start, end);

        var daily = new Dictionary<string, double>();
        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
        {
            daily[date.ToString("yyyy-MM-dd")] = 0;
        }
        foreach (var session in sessions)
        {
            var effectiveEnd = session.EndTime ?? DateTime.Now;
            var firstDay = UsageTimeRange.GetTimeDistributionDate(session.StartTime);
            var lastDay = UsageTimeRange.GetTimeDistributionDate(effectiveEnd);
            for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
            {
                var key = date.ToString("yyyy-MM-dd");
                if (daily.ContainsKey(key))
                {
                    daily[key] += UsageTimeRange.GetOverlapDuration(
                        session.StartTime, session.EndTime,
                        UsageTimeRange.GetDayStart(date), UsageTimeRange.GetDayEnd(date)).TotalSeconds;
                }
            }
        }

        var ranking = sessions
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ProcessName) ? "未知" : x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BucketStatDto
            {
                Key = g.Key,
                Seconds = SumSeconds(g, start, end),
                SessionCount = g.Count()
            })
            .OrderByDescending(x => x.Seconds)
            .ToList();

        return new RangeSummaryData
        {
            Seconds = seconds,
            TrackedDays = daily.Count(x => x.Value > 0),
            SessionCount = sessions.Count,
            ProcessCount = ranking.Count,
            Daily = daily.Select(x => new DailyPointDto { Date = x.Key, Seconds = x.Value }).OrderBy(x => x.Date).ToList(),
            Ranking = ranking.Take(10).ToList()
        };
    }

    public string GetSearchVersion()
    {
        if (!DatabaseExists) return string.Empty;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(StartTime), '') || ':' || COUNT(*) || ':' || COALESCE((SELECT EndTime FROM UsageSessions WHERE IsDeleted = 0 ORDER BY StartTime DESC LIMIT 1), '') FROM UsageSessions WHERE IsDeleted = 0";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public SearchResultDto Search(string? keyword, int skip, int take)
    {
        if (!DatabaseExists)
        {
            return new SearchResultDto();
        }

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var hasKeyword = !string.IsNullOrWhiteSpace(keyword);
        var like = hasKeyword
            ? $"%{keyword!.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%"
            : null;

        using var connection = Open();

        long totalCount;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = hasKeyword
                ? """
                  SELECT COUNT(*) FROM UsageSessions
                  WHERE IsDeleted = 0
                   
                    AND (ProcessName LIKE $kw ESCAPE '\' OR WindowTitle LIKE $kw ESCAPE '\')
                  """
                : "SELECT COUNT(*) FROM UsageSessions WHERE IsDeleted = 0";
            if (hasKeyword)
            {
                countCommand.Parameters.AddWithValue("$kw", like!);
            }

            totalCount = Convert.ToInt64(countCommand.ExecuteScalar());
        }

        using var command = connection.CreateCommand();
        command.CommandText = hasKeyword
            ? """
              SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
              FROM UsageSessions
              WHERE IsDeleted = 0
               
                AND (ProcessName LIKE $kw ESCAPE '\' OR WindowTitle LIKE $kw ESCAPE '\')
              ORDER BY StartTime DESC
              LIMIT $take OFFSET $skip
              """
            : """
              SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
              FROM UsageSessions
              WHERE IsDeleted = 0
               
              ORDER BY StartTime DESC
              LIMIT $take OFFSET $skip
              """;
        if (hasKeyword)
        {
            command.Parameters.AddWithValue("$kw", like!);
        }

        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);

        using var reader = command.ExecuteReader();
        return new SearchResultDto { Items = ResolveSubjects(ReadSessions(reader)), TotalCount = totalCount };
    }

    public SearchResultDto SearchAdvanced(
        string? keyword,
        int skip,
        int take,
        DateTime date,
        bool allHistory,
        string? mode,
        IReadOnlyList<SubjectDefinitionDto> definitions,
        string? subjectFilter = null)
    {
        var start = allHistory ? DateTime.MinValue : UsageTimeRange.GetDayStart(date);
        var end = allHistory ? DateTime.Now.AddSeconds(1) : UsageTimeRange.GetDayEnd(date);
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "all" : mode.Trim().ToLowerInvariant();
        var lookup = BuildSubjectLookup(definitions);
        var sessions = GetSessionsIntersecting(start, end)
            .Where(session => MatchesSubjectFilter(session.ManualSubject, subjectFilter, definitions))
            .Where(session => MatchesExpression(session, keyword, normalizedMode, lookup))
            .OrderByDescending(session => session.StartTime)
            .ToList();
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        return new SearchResultDto
        {
            Items = sessions.Skip(skip).Take(take).ToList(),
            TotalCount = sessions.Count
        };
    }

    private static bool MatchesSubjectFilter(string? subject, string? filter, IReadOnlyList<SubjectDefinitionDto> definitions)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (string.IsNullOrWhiteSpace(subject)) return false;
        var selected = filter.Trim();
        if (string.Equals(subject, selected, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var major in definitions)
        {
            var descendants = (major.Children ?? []).Concat(
                (major.Parents ?? []).SelectMany(parent => new[] { parent.Name }.Concat(parent.Children ?? [])));
            if (string.Equals(major.Name, selected, StringComparison.OrdinalIgnoreCase)
                && descendants.Any(item => string.Equals(item, subject, StringComparison.OrdinalIgnoreCase))) return true;
            foreach (var parent in major.Parents ?? [])
            {
                if (string.Equals(parent.Name, selected, StringComparison.OrdinalIgnoreCase)
                    && (parent.Children ?? []).Any(child => string.Equals(child, subject, StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        return false;
    }

    private static Dictionary<string, HashSet<string>> BuildSubjectLookup(IReadOnlyList<SubjectDefinitionDto> definitions)
    {
        var lookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, IEnumerable<string> values)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!lookup.TryGetValue(key.Trim(), out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lookup[key.Trim()] = set;
            }
            foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x))) set.Add(value.Trim());
        }
        foreach (var major in definitions)
        {
            var descendants = (major.Parents ?? []).SelectMany(parent => new[] { parent.Name }.Concat(parent.Children ?? []));
            Add(major.Name, new[] { major.Name }.Concat(major.Children ?? []).Concat(descendants));
            foreach (var child in major.Children ?? []) Add(child, [child]);
            foreach (var parent in major.Parents ?? [])
            {
                Add(parent.Name, new[] { parent.Name }.Concat(parent.Children ?? []));
                foreach (var child in parent.Children ?? []) Add(child, [child]);
            }
        }
        return lookup;
    }

    private static bool MatchesExpression(SessionDto session, string? expression, string mode, IReadOnlyDictionary<string, HashSet<string>> lookup)
    {
        if (string.IsNullOrWhiteSpace(expression)) return true;
        return EvaluateOr(expression.Trim());

        bool EvaluateOr(string text)
        {
            var parts = SplitOutside(text, '|');
            return parts.Count > 1 ? parts.Any(EvaluateAnd) : EvaluateAnd(text);
        }
        bool EvaluateAnd(string text)
        {
            var parts = SplitOutside(text, '&');
            return parts.Count > 1 ? parts.All(EvaluateNot) : EvaluateNot(text);
        }
        bool EvaluateNot(string text)
        {
            text = text.Trim();
            var negate = false;
            while (text.StartsWith('!') || text.StartsWith('！'))
            {
                negate = !negate;
                text = text[1..].Trim();
            }
            var value = text.Length >= 2 && ((text[0] == '(' && text[^1] == ')') || (text[0] == '（' && text[^1] == '）'))
                ? EvaluateOr(text[1..^1])
                : MatchTerm(text);
            return negate ? !value : value;
        }
        bool MatchTerm(string term)
        {
            var separator = term.IndexOfAny([':', '：']);
            if (separator > 0 && separator < term.Length - 1)
            {
                var scope = term[..separator].Trim().ToLowerInvariant();
                term = term[(separator + 1)..].Trim();
                return scope is "分类" or "科目" or "subject" ? MatchSubject(term)
                    : scope is "标题" or "窗口" or "title" ? Contains(session.WindowTitle, term)
                    : scope is "进程" or "程序" or "process" or "proc" && Contains(session.ProcessName, term);
            }
            return mode switch
            {
                "subject" => MatchSubject(term),
                "title" => Contains(session.WindowTitle, term),
                "process" => Contains(session.ProcessName, term),
                _ => MatchSubject(term) || Contains(session.WindowTitle, term) || Contains(session.ProcessName, term)
            };
        }
        bool MatchSubject(string term)
        {
            var subject = session.ManualSubject ?? string.Empty;
            return lookup.TryGetValue(term.Trim(), out var linked)
                ? linked.Contains(subject)
                : Contains(subject, term);
        }
        static bool Contains(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitOutside(string text, char separator)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '(' or '（') depth++;
            else if (text[i] is ')' or '）') depth--;
            else if (text[i] == separator && depth == 0)
            {
                result.Add(text[start..i]);
                start = i + 1;
            }
        }
        result.Add(text[start..]);
        return result;
    }

    /// <summary>当前进行中的会话（ActiveSession 单行表，由桌面版维护）。</summary>
    public SessionDto? GetActiveSession()
    {
        if (!DatabaseExists)
        {
            return null;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson, LastCapturedAt
            FROM ActiveSession
            WHERE SingletonId = 1
             
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var lastCapturedAt = ParseTime(reader.GetValue(7));
        if (lastCapturedAt is null || DateTime.Now - lastCapturedAt.Value > TimeSpan.FromSeconds(5))
        {
            return null;
        }

        var start = ParseTime(reader.GetValue(3)) ?? DateTime.MinValue;
        var end = ParseTime(reader.GetValue(4));
        var effectiveEnd = end ?? DateTime.Now;
        var session = new SessionDto
        {
            Id = Text(reader, 0),
            ProcessName = Text(reader, 1),
            WindowTitle = Text(reader, 2),
            StartTime = start,
            EndTime = end,
            ManualSubject = ClassificationResolver.NormalizeSubject(reader.IsDBNull(5) ? null : reader.GetString(5)),
            ParallelActivities = ParseParallelActivities(reader, 6),
            LastCapturedAt = lastCapturedAt,
            DurationSeconds = effectiveEnd > start ? (effectiveEnd - start).TotalSeconds : 0
        };
        return ResolveSubjects(new List<SessionDto> { session })[0];
    }

    public DateTime? GetEarliestDate()
    {
        if (!DatabaseExists)
        {
            return null;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(StartTime) FROM UsageSessions WHERE IsDeleted = 0";
        return ParseTime(command.ExecuteScalar());
    }

    public long GetSessionCount()
    {
        if (!DatabaseExists)
        {
            return 0;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM UsageSessions WHERE IsDeleted = 0";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}

/// <summary>区间统计结果（单次查询返回，避免 4 次全表扫描）。</summary>
public sealed record RangeSummaryData
{
    public double Seconds { get; init; }
    public int TrackedDays { get; init; }
    public int SessionCount { get; init; }
    public int ProcessCount { get; init; }
    public List<DailyPointDto> Daily { get; init; } = [];
    public List<BucketStatDto> Ranking { get; init; } = [];
}
