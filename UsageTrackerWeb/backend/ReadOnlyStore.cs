using Microsoft.Data.Sqlite;
using System.Globalization;
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

    public ReadOnlyStore(string dataDirectory)
    {
        _databasePath = Path.Combine(dataDirectory, "usage-tracker.db");
    }

    public string DatabasePath => _databasePath;

    public bool DatabaseExists => File.Exists(_databasePath);

    public double DatabaseSizeMb => DatabaseExists ? new FileInfo(_databasePath).Length / 1024.0 / 1024.0 : 0;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";
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

    private static List<SessionDto> ReadSessions(SqliteDataReader reader)
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
                ManualSubject = reader.IsDBNull(5) ? null : reader.GetString(5),
                DurationSeconds = effectiveEnd > start ? (effectiveEnd - start).TotalSeconds : 0
            });
        }

        return list;
    }

    /// <summary>与 [start, end) 相交的未删除会话，SQL 语义与桌面版一致。</summary>
    public List<SessionDto> GetSessionsIntersecting(DateTime start, DateTime end)
    {
        if (!DatabaseExists)
        {
            return new List<SessionDto>();
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject
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
        return ReadSessions(reader);
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

    public double GetSecondsInRange(DateTime start, DateTime end)
        => SumSeconds(GetSessionsIntersecting(start, end), start, end);

    public List<BucketStatDto> GetProcessStats(DateTime start, DateTime end)
    {
        var sessions = GetSessionsIntersecting(start, end);
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

    public List<BucketStatDto> GetSubjectStats(DateTime start, DateTime end)
    {
        var sessions = GetSessionsIntersecting(start, end);
        return sessions
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ManualSubject) ? "未分类" : x.ManualSubject!)
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
    public List<DailyPointDto> GetDailySeries(DateTime fromDate, DateTime toDate)
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

        var sessions = GetSessionsIntersecting(UsageTimeRange.GetDayStart(fromDate), UsageTimeRange.GetDayEnd(toDate));
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
              SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject
              FROM UsageSessions
              WHERE IsDeleted = 0
                AND (ProcessName LIKE $kw ESCAPE '\' OR WindowTitle LIKE $kw ESCAPE '\')
              ORDER BY StartTime DESC
              LIMIT $take OFFSET $skip
              """
            : """
              SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject
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
        return new SearchResultDto { Items = ReadSessions(reader), TotalCount = totalCount };
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
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject
            FROM ActiveSession
            WHERE SingletonId = 1
            """;
        using var reader = command.ExecuteReader();
        var items = ReadSessions(reader);
        return items.Count > 0 ? items[0] : null;
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
