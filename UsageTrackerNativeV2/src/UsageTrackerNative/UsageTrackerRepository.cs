using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UsageTrackerNative;

public sealed class UsageTrackerRepository
{
    private const string DatabaseFileName = "usage-tracker.db";
    private const string BackupDirectoryName = "backups";

    private readonly string _dataDirectory;
    private readonly string _databaseFilePath;
    private readonly string _settingsFilePath;
    private readonly string _backupDirectory;
    private readonly object _diskWriteLock = new();
    private DateTime _lastBackupTime = DateTime.MinValue;
    private static readonly TimeSpan BackupInterval = TimeSpan.FromMinutes(30);

    public static UsageTrackerRepository Create(string dataDirectory, string settingsFilePath)
    {
        return new UsageTrackerRepository(
            dataDirectory,
            Path.Combine(dataDirectory, DatabaseFileName),
            settingsFilePath,
            Path.Combine(dataDirectory, BackupDirectoryName));
    }

    public UsageTrackerRepository(string dataDirectory, string databaseFilePath, string settingsFilePath, string backupDirectory)
    {
        _dataDirectory = dataDirectory;
        _databaseFilePath = databaseFilePath;
        _settingsFilePath = settingsFilePath;
        _backupDirectory = backupDirectory;
    }

    public void EnsureDatabase()
    {
        Directory.CreateDirectory(_dataDirectory);
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS UsageSessions (
                Id TEXT PRIMARY KEY,
                ProcessName TEXT NOT NULL,
                WindowTitle TEXT NOT NULL,
                StartTime TEXT NOT NULL,
                EndTime TEXT NULL,
                ManualSubject TEXT NULL,
                ParallelActivitiesJson TEXT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                DeletedAt TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_UsageSessions_StartTime ON UsageSessions(StartTime);
            CREATE INDEX IF NOT EXISTS IX_UsageSessions_IsDeleted_StartTime ON UsageSessions(IsDeleted, StartTime);
            CREATE TABLE IF NOT EXISTS ActiveSession (
                SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
                Id TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                WindowTitle TEXT NOT NULL,
                StartTime TEXT NOT NULL,
                EndTime TEXT NULL,
                ManualSubject TEXT NULL,
                ParallelActivitiesJson TEXT NULL,
                LastCapturedAt TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "UsageSessions", "ParallelActivitiesJson", "TEXT NULL");
        EnsureColumn(connection, "ActiveSession", "ParallelActivitiesJson", "TEXT NULL");
        EnsureColumn(connection, "ActiveSession", "LastCapturedAt", "TEXT NULL");
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = $"PRAGMA table_info({tableName})";
            using var reader = checkCommand.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        alterCommand.ExecuteNonQuery();
    }

    /// <summary>
    /// 将旧的 usage-sessions.json 数据迁移到 SQLite 数据库。
    /// 仅在 JSON 文件存在且包含数据库中尚无的历史记录时执行迁移。
    /// 迁移成功后将 JSON 文件重命名为 .migrated 备份。
    /// </summary>
    public bool MigrateJsonToDatabase()
    {
        var jsonPath = Path.Combine(_dataDirectory, "usage-sessions.json");
        var migratedPath = jsonPath + ".migrated";

        // 已迁移过则跳过
        if (!File.Exists(jsonPath))
            return false;

        try
        {
            using var stream = File.OpenRead(jsonPath);
            var state = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerState);
            if (state?.History is null || state.History.Count == 0)
                return false;

            // 查询数据库中已有的记录 ID
            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var conn = OpenDatabaseConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Id FROM UsageSessions";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    existingIds.Add(reader.GetString(0));
                }
            }

            // 过滤出数据库中不存在的记录
            var newRecords = state.History
                .Where(r =>
                {
                    r.EnsureId();
                    return !existingIds.Contains(r.Id);
                })
                .ToList();

            if (newRecords.Count == 0)
            {
                // JSON 数据已全部在数据库中，标记为已迁移
                File.Move(jsonPath, migratedPath, overwrite: true);
                return false;
            }

            // 批量插入新记录
            using (var conn = OpenDatabaseConnection())
            using (var transaction = conn.BeginTransaction())
            {
                foreach (var record in newRecords)
                {
                    UpsertHistoryRecord(conn, transaction, record);
                }
                transaction.Commit();
            }

            // 迁移成功，重命名 JSON 文件
            File.Move(jsonPath, migratedPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // 迁移失败不影响正常启动，下次启动时会重试
            System.Diagnostics.Debug.WriteLine($"[UsageTracker] JSON migration failed: {ex}");
            throw; // 重新抛出，让调用方记录异常
        }
    }

    
    /// <summary>
    /// 启动阶段1：只加载当天数据 + 活动会话，让追踪器尽快启动。
    /// </summary>
    public void LoadTodayHistory(List<UsageSessionRecord> history, out UsageSessionRecord? activeRecord)
    {
        // 凌晨4点分割：当天 = 今天04:00 ~ 明天04:00
        var todayStart = DateTime.Today.AddHours(4);
        if (DateTime.Now.TimeOfDay < TimeSpan.FromHours(4))
            todayStart = DateTime.Today.AddDays(-1).AddHours(4);
        var todayEnd = todayStart.AddDays(1);
        LoadHistoryForDateRange(history, todayStart, todayEnd);

        // 加载活动会话
        activeRecord = null;
        using var connection = OpenDatabaseConnection();
        using var activeCommand = connection.CreateCommand();
        activeCommand.CommandText = "SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson, LastCapturedAt FROM ActiveSession WHERE SingletonId = 1";
        using var reader = activeCommand.ExecuteReader();
        if (reader.Read())
            activeRecord = ReadSessionRecord(reader);
    }

    /// <summary>
    /// 加载指定日期范围的会话到列表（不清空现有数据，用于渐进加载）。
    /// 日期范围按凌晨4点分割。
    /// </summary>
    public void LoadHistoryForDateRange(List<UsageSessionRecord> history, DateTime fromInclusive, DateTime toExclusive)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
            FROM UsageSessions
            WHERE IsDeleted = 0
              AND StartTime < $to
              AND COALESCE(EndTime, $now) > $from
            ORDER BY StartTime DESC
            """;
        command.Parameters.AddWithValue("$from", fromInclusive.ToString("O"));
        command.Parameters.AddWithValue("$to", toExclusive.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var record = ReadSessionRecord(reader);
            if (history.All(x => !string.Equals(x.Id, record.Id, StringComparison.OrdinalIgnoreCase)))
            {
                history.Add(record);
            }
        }
    }

    /// <summary>
    /// 保留用于兼容旧调用（如还原/导入同步）。改为加载所有未删除数据。
    /// </summary>
    public void LoadHistoryFromDatabase(List<UsageSessionRecord> history, out UsageSessionRecord? activeRecord, int recentDays = 1)
    {
        history.Clear();
        using var connection = OpenDatabaseConnection();

        var cutoffDate = DateTime.Now.AddDays(-recentDays);
        var cutoffStartTime = cutoffDate.Date.AddHours(4); // 凌晨4点分割
        using (var historyCommand = connection.CreateCommand())
        {
            historyCommand.CommandText = """
                SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
                FROM UsageSessions
                WHERE IsDeleted = 0 AND StartTime >= $cutoff
                ORDER BY StartTime DESC
                """;
            historyCommand.Parameters.AddWithValue("$cutoff", cutoffStartTime.ToString("O"));
            using var historyReader = historyCommand.ExecuteReader();
            while (historyReader.Read())
            {
                history.Add(ReadSessionRecord(historyReader));
            }
        }

        // 加载活动会话
        using (var activeCommand = connection.CreateCommand())
        {
            activeCommand.CommandText = "SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson, LastCapturedAt FROM ActiveSession WHERE SingletonId = 1";
            using var reader = activeCommand.ExecuteReader();
            activeRecord = reader.Read() ? ReadSessionRecord(reader) : null;
        }
    }
    // ── 按需查询方法（SQL 下推，不走全量内存） ──

    public DateTime? GetEarliestDate()
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(StartTime) FROM UsageSessions WHERE IsDeleted = 0";
        var result = command.ExecuteScalar();
        if (result is DBNull || result is null)
            return null;
        return DateTime.Parse((string)result, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    public List<DateTime> GetActiveDates(DateTime from, DateTime to)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT StartTime
            FROM UsageSessions
            WHERE IsDeleted = 0
              AND StartTime >= $from
              AND StartTime < $to
            ORDER BY StartTime DESC
            """;
        command.Parameters.AddWithValue("$from", from.ToString("O"));
        command.Parameters.AddWithValue("$to", to.ToString("O"));
        var dates = new List<DateTime>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var dt = DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind);
            dates.Add(dt.Date);
        }
        return dates.Distinct().OrderByDescending(d => d).ToList();
    }

    public List<UsageSessionRecord> GetSessionsByDate(DateTime date)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        // 凌晨4点日期分割：目标日期的 04:00 到次日 04:00
        var dayStart = date.Date.AddHours(4);
        var dayEnd = dayStart.AddDays(1);
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
            FROM UsageSessions
            WHERE IsDeleted = 0
              AND StartTime < $dayEnd
              AND COALESCE(EndTime, $now) > $dayStart
            ORDER BY StartTime DESC
            """;
        command.Parameters.AddWithValue("$dayStart", dayStart.ToString("O"));
        command.Parameters.AddWithValue("$dayEnd", dayEnd.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));

        var sessions = new List<UsageSessionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(ReadSessionRecord(reader));
        }
        return sessions;
    }

    public List<UsageSessionRecord> GetSessionsInRange(DateTime startInclusive, DateTime endExclusive)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
            FROM UsageSessions
            WHERE IsDeleted = 0
              AND StartTime < $end
              AND COALESCE(EndTime, $now) > $start
            ORDER BY StartTime DESC
            """;
        command.Parameters.AddWithValue("$start", startInclusive.ToString("O"));
        command.Parameters.AddWithValue("$end", endExclusive.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        var sessions = new List<UsageSessionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(ReadSessionRecord(reader));
        }
        return sessions;
    }

    public (long totalTicks, long weekTicks, int dayCount) GetOverviewStats(DateTime endExclusive)
    {
        var weekStart = endExclusive.AddDays(-1).DayOfWeek == DayOfWeek.Sunday
            ? endExclusive.AddDays(-1).Date.AddDays(-6)
            : endExclusive.AddDays(-1).Date.AddDays(-(int)endExclusive.AddDays(-1).DayOfWeek + (int)DayOfWeek.Monday);

        using var connection = OpenDatabaseConnection();
        var totalTicks = SumClippedTicks(connection, DateTime.MinValue, endExclusive);
        var weekTicks = SumClippedTicks(connection, weekStart, endExclusive);
        var dayCount = GetActiveDates(DateTime.MinValue, endExclusive).Count;
        return (totalTicks, weekTicks, dayCount);
    }

    public (long totalTicks, int dayCount) GetRangeStats(DateTime startInclusive, DateTime endExclusive)
    {
        using var connection = OpenDatabaseConnection();
        var totalTicks = SumClippedTicks(connection, startInclusive, endExclusive);
        var dayCount = GetActiveDates(startInclusive, endExclusive).Count;
        return (totalTicks, dayCount);
    }

    public long GetWeekTicks(DateTime weekStart, DateTime weekEndExclusive)
    {
        using var connection = OpenDatabaseConnection();
        return SumClippedTicks(connection, weekStart, weekEndExclusive);
    }

    public List<(string ProcessName, long TotalTicks, int SessionCount)> GetProcessSummaries(DateTime date)
    {
        var dayStart = UsageTimeRange.GetDayStart(date);
        var dayEnd = dayStart.AddDays(1);
        return GetSessionsByDate(date)
            .GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(x => (
                ProcessName: x.Key,
                TotalTicks: x.Sum(record => UsageTimeRange.GetOverlapDuration(record.StartTime, record.EndTime, dayStart, dayEnd).Ticks),
                SessionCount: x.Count()))
            .Where(x => x.TotalTicks > 0)
            .OrderByDescending(x => x.TotalTicks)
            .ToList();
    }

    public List<(string Subject, long TotalTicks, int SessionCount)> GetSubjectSummaries(DateTime date)
    {
        var dayStart = UsageTimeRange.GetDayStart(date);
        var dayEnd = dayStart.AddDays(1);
        return GetSessionsByDate(date)
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ManualSubject) ? "空分类" : x.ManualSubject!, StringComparer.OrdinalIgnoreCase)
            .Select(x => (
                Subject: x.Key,
                TotalTicks: x.Sum(record => UsageTimeRange.GetOverlapDuration(record.StartTime, record.EndTime, dayStart, dayEnd).Ticks),
                SessionCount: x.Count()))
            .Where(x => x.TotalTicks > 0)
            .OrderByDescending(x => x.TotalTicks)
            .ToList();
    }

    public (List<UsageSessionRecord> items, int totalCount) SearchSessions(
        string keyword, int skip, int take)
    {
        // 转义 SQL LIKE 通配符，防止注入
        var escaped = keyword
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        var likeParam = $"%{escaped}%";
        using var connection = OpenDatabaseConnection();

        // 先查总数
        int totalCount;
        using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = """
                SELECT COUNT(*) FROM UsageSessions
                WHERE IsDeleted = 0
                  AND (ProcessName LIKE $kw ESCAPE '\' OR WindowTitle LIKE $kw ESCAPE '\')
                """;
            countCmd.Parameters.AddWithValue("$kw", likeParam);
            totalCount = Convert.ToInt32(countCmd.ExecuteScalar()!);
        }

        // 再查分页数据
        var items = new List<UsageSessionRecord>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
                FROM UsageSessions
                WHERE IsDeleted = 0
                  AND (ProcessName LIKE $kw ESCAPE '\' OR WindowTitle LIKE $kw ESCAPE '\')
                ORDER BY StartTime DESC
                LIMIT $take OFFSET $skip
                """;
            cmd.Parameters.AddWithValue("$kw", likeParam);
            cmd.Parameters.AddWithValue("$take", take);
            cmd.Parameters.AddWithValue("$skip", skip);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadSessionRecord(reader));
            }
        }

        return (items, totalCount);
    }

    public List<UsageSessionRecord> GetRecordsByManualSubject(string subject)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
            FROM UsageSessions
            WHERE IsDeleted = 0 AND ManualSubject = $subject
            """;
        command.Parameters.AddWithValue("$subject", subject);
        var result = new List<UsageSessionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadSessionRecord(reader));
        }
        return result;
    }

    public UsageSessionRecord? GetRecordById(string id)
    {
        using var connection = OpenDatabaseConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
            FROM UsageSessions
            WHERE Id = $id AND IsDeleted = 0
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSessionRecord(reader) : null;
    }

    public void DeleteRecord(string id)
    {
        lock (_diskWriteLock)
        {
            using var connection = OpenDatabaseConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE UsageSessions SET IsDeleted = 1, DeletedAt = $deletedAt WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$deletedAt", DateTime.Now.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    // ── 原始方法 ──

    public void WriteIncrementalToDatabase(IncrementalSaveRequest request)
    {
        lock (_diskWriteLock)
        {
            Directory.CreateDirectory(_dataDirectory);

            // Settings are written synchronously by UsageTrackerService.SaveSettingsImmediately().
            // Do not write request.Settings here: incremental save requests can complete out of order
            // during batch subject edits and overwrite newer settings.json content with an older snapshot.
            EnsureDatabase();
            using var connection = OpenDatabaseConnection();
            using var transaction = connection.BeginTransaction();

            if (request.FullSync && request.AllRecords is not null && request.AllRecords.Count > 0)
            {
                using (var clearCmd = connection.CreateCommand())
                {
                    clearCmd.Transaction = transaction;
                    clearCmd.CommandText = "DELETE FROM UsageSessions;";
                    clearCmd.ExecuteNonQuery();
                }

                foreach (var record in request.AllRecords)
                {
                    UpsertHistoryRecord(connection, transaction, record);
                }
            }
            else
            {
                foreach (var record in request.DirtyRecords)
                {
                    UpsertHistoryRecord(connection, transaction, record);
                }

                foreach (var deletedId in request.DeletedIds)
                {
                    SoftDeleteRecord(connection, transaction, deletedId);
                }
            }

            if (request.UpdateActiveSession)
            {
                SaveActiveRecord(connection, transaction, request.ActiveRecord);
            }

            transaction.Commit();
            BackupCurrentStorageFiles();
        }
    }

    public void WriteStateToDisk(UsageTrackerState state)
    {
        var request = new IncrementalSaveRequest
        {
            FullSync = true,
            AllRecords = (state.History ?? []).Select(r => { r.EnsureId(); return r; }).ToList(),
            ActiveRecord = state.Active,
            UpdateActiveSession = true,
            Settings = CreateSettingsSnapshot(state)
        };
        WriteIncrementalToDatabase(request);
    }

    private SqliteConnection OpenDatabaseConnection()
    {
        var connection = new SqliteConnection($"Data Source={_databaseFilePath}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static UsageSessionRecord ReadSessionRecord(SqliteDataReader reader)
    {
        var record = new UsageSessionRecord
        {
            Id = reader.GetString(0),
            ProcessName = reader.GetString(1),
            WindowTitle = reader.GetString(2),
            StartTime = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            EndTime = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
            ManualSubject = reader.IsDBNull(5) ? null : reader.GetString(5),
            ParallelActivities = reader.FieldCount > 6 && !reader.IsDBNull(6)
                ? ReadParallelActivities(reader.GetString(6))
                : null,
            LastCapturedAt = reader.FieldCount > 7 && !reader.IsDBNull(7)
                ? DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)
                : null
        };
        record.EnsureId();
        return record;
    }

    private static List<ParallelActivitySnapshot>? ReadParallelActivities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, UsageTrackerJsonContext.Default.ListParallelActivitySnapshot);
        }
        catch
        {
            return null;
        }
    }

    private static string? SerializeParallelActivities(IReadOnlyList<ParallelActivitySnapshot>? activities)
    {
        if (activities is null || activities.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(activities.ToList(), UsageTrackerJsonContext.Default.ListParallelActivitySnapshot);
    }

    private static long SumClippedTicks(SqliteConnection connection, DateTime rangeStart, DateTime rangeEnd)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson
            FROM UsageSessions
            WHERE IsDeleted = 0
              AND StartTime < $end
              AND COALESCE(EndTime, $now) > $start
            """;
        command.Parameters.AddWithValue("$start", rangeStart.ToString("O"));
        command.Parameters.AddWithValue("$end", rangeEnd.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTime.Now.ToString("O"));
        var ticks = 0L;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var record = ReadSessionRecord(reader);
            ticks += UsageTimeRange.GetOverlapDuration(record.StartTime, record.EndTime, rangeStart, rangeEnd).Ticks;
        }
        return ticks;
    }

    private static void UpsertHistoryRecord(SqliteConnection connection, SqliteTransaction transaction, UsageSessionRecord record)
    {
        record.EnsureId();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO UsageSessions (Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson, IsDeleted, DeletedAt)
            VALUES ($id, $processName, $windowTitle, $startTime, $endTime, $manualSubject, $parallelActivitiesJson, 0, NULL)
            ON CONFLICT(Id) DO UPDATE SET
                ProcessName = excluded.ProcessName,
                WindowTitle = excluded.WindowTitle,
                StartTime = excluded.StartTime,
                EndTime = excluded.EndTime,
                ManualSubject = excluded.ManualSubject,
                ParallelActivitiesJson = excluded.ParallelActivitiesJson,
                IsDeleted = 0,
                DeletedAt = NULL;
            """;
        AddSessionParameters(command, record);
        command.ExecuteNonQuery();
    }

    private static void SaveActiveRecord(SqliteConnection connection, SqliteTransaction transaction, UsageSessionRecord? record)
    {
        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM ActiveSession";
            deleteCommand.ExecuteNonQuery();
        }

        if (record is null)
        {
            return;
        }

        record.EnsureId();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ActiveSession (SingletonId, Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson, LastCapturedAt)
            VALUES (1, $id, $processName, $windowTitle, $startTime, $endTime, $manualSubject, $parallelActivitiesJson, $lastCapturedAt)
            """;
        AddSessionParameters(command, record);
        command.ExecuteNonQuery();
    }

    private static void AddSessionParameters(SqliteCommand command, UsageSessionRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$processName", record.ProcessName);
        command.Parameters.AddWithValue("$windowTitle", record.WindowTitle);
        command.Parameters.AddWithValue("$startTime", record.StartTime.ToString("O"));
        command.Parameters.AddWithValue("$endTime", record.EndTime is null ? DBNull.Value : record.EndTime.Value.ToString("O"));
        command.Parameters.AddWithValue("$manualSubject", record.ManualSubject is null ? DBNull.Value : record.ManualSubject);
        command.Parameters.AddWithValue("$parallelActivitiesJson", SerializeParallelActivities(record.ParallelActivities) is { } json ? json : DBNull.Value);
        command.Parameters.AddWithValue("$lastCapturedAt", record.LastCapturedAt is null ? DBNull.Value : record.LastCapturedAt.Value.ToString("O"));
    }

    private static void SoftDeleteRecord(SqliteConnection connection, SqliteTransaction transaction, string recordId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE UsageSessions SET IsDeleted = 1, DeletedAt = $deletedAt WHERE Id = $id";
        command.Parameters.AddWithValue("$id", recordId);
        command.Parameters.AddWithValue("$deletedAt", DateTime.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void BackupCurrentStorageFiles()
    {
        var now = DateTime.Now;
        // 基于时间戳判断，而非依赖调用频率
        if (now - _lastBackupTime < BackupInterval)
        {
            return;
        }

        _lastBackupTime = now;
        Directory.CreateDirectory(_backupDirectory);
        var stamp = now.ToString("yyyyMMdd-HHmm");
        CopyIfExists(_settingsFilePath, Path.Combine(_backupDirectory, $"settings-{stamp}.json"));
        CopyIfExists(_databaseFilePath, Path.Combine(_backupDirectory, $"usage-tracker-{stamp}.db"));
        foreach (var oldBackup in Directory.EnumerateFiles(_backupDirectory).Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTimeUtc).Skip(300))
        {
            try
            {
                oldBackup.Delete();
            }
            catch
            {
            }
        }
    }

    private static string BuildSessionSelectList(bool includeParallelActivities)
    {
        return includeParallelActivities
            ? "Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, ParallelActivitiesJson"
            : "Id, ProcessName, WindowTitle, StartTime, EndTime, ManualSubject, NULL AS ParallelActivitiesJson";
    }

    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public int MergeFromBackupDb(string backupDbPath)
    {
        if (!File.Exists(backupDbPath))
            return 0;

        var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var conn = OpenDatabaseConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id FROM UsageSessions";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingIds.Add(reader.GetString(0));
            }
        }

        var newRecords = new List<UsageSessionRecord>();
        using (var bakConn = new SqliteConnection($"Data Source={backupDbPath};Mode=ReadOnly"))
        {
            bakConn.Open();
            var selectList = BuildSessionSelectList(HasColumn(bakConn, "UsageSessions", "ParallelActivitiesJson"));
            using var cmd = bakConn.CreateCommand();
            cmd.CommandText = $"SELECT {selectList} FROM UsageSessions WHERE IsDeleted = 0";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (existingIds.Contains(id))
                    continue;
                newRecords.Add(ReadSessionRecord(reader));
            }
        }

        if (newRecords.Count == 0)
            return 0;

        using (var conn = OpenDatabaseConnection())
        using (var transaction = conn.BeginTransaction())
        {
            foreach (var record in newRecords)
            {
                record.EnsureId();
                UpsertHistoryRecord(conn, transaction, record);
            }
            transaction.Commit();
        }

        return newRecords.Count;
    }

    private static void CopyIfExists(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath) && !File.Exists(destinationPath))
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }

    private static void WriteSettingsAtomically(string filePath, UsageTrackerSettings settings)
    {
        var tempPath = filePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            System.Text.Json.JsonSerializer.Serialize(stream, settings, UsageTrackerJsonContext.Default.UsageTrackerSettings);
            stream.Flush();
        }

        File.Move(tempPath, filePath, overwrite: true);
    }

    private static UsageTrackerSettings CreateSettingsSnapshot(UsageTrackerState state)
    {
        return new UsageTrackerSettings
        {
            ManualSubjects = state.ManualSubjects is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(state.ManualSubjects, StringComparer.OrdinalIgnoreCase),
            SubjectKeywordRules = state.SubjectKeywordRules?.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase),
            SubjectDefinitions = state.SubjectDefinitions?.Select(x => x.Clone()).ToList() ?? [],
            SubjectOptions = state.SubjectOptions?.ToList() ?? [],
            Theme = state.Theme,
            ThemeAccentColor = state.ThemeAccentColor,
            ThemeAccentRecentColors = state.ThemeAccentRecentColors?.ToList() ?? [],
            ThemeAccentSlots = state.ThemeAccentSlots?.ToList() ?? [],
            IdleTimeoutMinutes = state.IdleTimeoutMinutes,
            ManualIdleShortcutText = state.ManualIdleShortcutText
        };
    }
}

public sealed class IncrementalSaveRequest
{
    public List<UsageSessionRecord> DirtyRecords { get; set; } = [];
    public List<string> DeletedIds { get; set; } = [];
    public UsageSessionRecord? ActiveRecord { get; set; }
    public bool UpdateActiveSession { get; set; }
    public UsageTrackerSettings? Settings { get; set; }
    public bool FullSync { get; set; }
    public List<UsageSessionRecord>? AllRecords { get; set; }
}