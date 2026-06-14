using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Microsoft.Win32;
using System.Windows.Threading;
namespace UsageTrackerNative;
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UsageTrackerState))]
[JsonSerializable(typeof(UsageTrackerSettings))]
[JsonSerializable(typeof(UsageSessionRecord))]
[JsonSerializable(typeof(SubjectDefinition))]
[JsonSerializable(typeof(SubjectParentDefinition))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<ParallelActivitySnapshot>))]
[JsonSerializable(typeof(List<UsageSessionRecord>))]
public partial class UsageTrackerJsonContext : JsonSerializerContext
{
}
public sealed class UsageTrackerService : IDisposable
{
    public const string StartupCompactArgument = "--startup-compact";
    public const string ExportDirectoryName = "导出数据";
    public const string DataDirectoryName = AppDataDirectoryName;
    public const string DataFileNameForUninstall = "usage-tracker.db";
    private const string StartupRegistryValueName = "时迹";
    private const string AppDataDirectoryName = "UsageTrackerNative";
    private const string SettingsFileName = "settings.json";
    private const string LegacyAutoUnclassifiedLabel = "自动未分类";
    private const string ManualUnclassifiedLabel = "未分类";
    private const int DefaultIdleTimeoutMinutes = 1;
    private const int MaxDataRetentionDays = 3650;
    private const int MaxImportFileSizeBytes = 32 * 1024 * 1024;
    private const int MaxImportZipSizeBytes = 512 * 1024 * 1024;
    private const int ParallelAudioStartSampleThreshold = 2;
    private const int ParallelAudioStopSampleThreshold = 3;
    private static readonly TimeSpan ForegroundMediaAudioGracePeriod = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ForegroundVideoPlaybackGracePeriod = TimeSpan.FromSeconds(8);
    private const string UsageDataZipEntryName = "usage-data.json";
    private const string FullBackupDataZipEntryName = "full-backup.json";
    private const string FullBackupSettingsZipEntryName = "settings.json";
    private const int MaxImportHistoryRecords = 50000;
    private const string DefaultManualIdleShortcutText = "";
    private static readonly TimeSpan SleepGapThreshold = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LongIdleMediaProtectionThreshold = TimeSpan.FromMinutes(30);
    private static readonly SubjectDefinition[] DefaultSubjects = [];
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _idleClickTimer;
    private readonly string _dataDirectory;
    private readonly string _settingsFilePath;
    private readonly UsageTrackerRepository _repository;
    private readonly List<UsageSessionRecord> _history = new();
    private readonly ConcurrentDictionary<string, UsageSessionRecord> _sessionById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageSessionRecord> _dirtyRecords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _manualSubjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ParallelAudioStabilityState> _parallelAudioStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _subjectKeywordRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SubjectDefinition> _subjectDefinitions = new();
    private readonly List<string> _themeAccentRecentColors = new();
    private readonly List<string> _themeAccentSlots = ["#ED0012", "#2563EB", "#16A34A"];
    private readonly List<string> _parallelActivityWhitelistProcesses = DefaultParallelActivityWhitelistProcesses();
    private UsageSessionRecord? _activeRecord;
    private ParallelActivitySnapshot? _lastParallelActivitySnapshot;
    private DateTime _lastParallelAudioStateLogAt = DateTime.MinValue;
    private DateTime? _lastCaptureTime;
    private bool _isDisposed;
    private readonly object _saveLock = new();
    private IncrementalSaveRequest? _pendingSaveRequest;
    private bool _saveWorkerRunning;
    private readonly ManualResetEventSlim _saveIdleEvent = new(true);
    // SQLite incremental save tracking
    private readonly HashSet<string> _dirtyRecordIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _deletedRecordIds = new();
    private bool _fullSyncNeeded;
    private bool _activeSessionDirty;
    private bool _settingsDirty;
    // 渐进数据加载状态
    private DataLoadPhase _dataLoadPhase = DataLoadPhase.Idle;
    private DateTime _loadedRangeStart = DateTime.MaxValue;
    private DateTime _loadedRangeEnd = DateTime.MinValue;
    private readonly object _loadLock = new();
    public DataLoadPhase DataLoadPhase => _dataLoadPhase;
    public event EventHandler? DataLoadProgressChanged;
    private DateTime? _manualIdleStartedAt;
    private DateTime? _idleAwaitingClickStartedAt;
    private bool _wasMouseButtonDown;
    public event EventHandler<SessionChangedEventArgs>? SessionChanged;
    public UsageSession? ActiveSession => _activeRecord is null ? null : ToActiveSession(_activeRecord, DateTime.Now, ResolveSubjectForDisplay(_activeRecord));
    public int IdleTimeoutMinutes { get; private set; } = DefaultIdleTimeoutMinutes;
    public string ManualIdleShortcutText { get; private set; } = DefaultManualIdleShortcutText;
    public SubjectDeleteBehavior SubjectDeleteBehavior { get; private set; } = SubjectDeleteBehavior.MatchRules;
    public int DataRetentionDays => MaxDataRetentionDays;
    public string Theme { get; private set; } = "Dark";
    public string Language { get; private set; } = "zh";
    public string ThemeAccentColor { get; private set; } = "#C62828";
    public IReadOnlyList<string> ThemeAccentRecentColors => _themeAccentRecentColors;
    public IReadOnlyList<string> ThemeAccentSlots => _themeAccentSlots;
    public IReadOnlyList<string> ParallelActivityWhitelistProcesses => _parallelActivityWhitelistProcesses;
    public DateTime? EarliestSessionDate
    {
        get
        {
            if (_cachedEarliestDate is null)
                _cachedEarliestDate = _repository.GetEarliestDate();
            return _cachedEarliestDate;
        }
    }
    private DateTime? _cachedEarliestDate;
    private TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleTimeoutMinutes);
    public bool IsIdle => _idleAwaitingClickStartedAt.HasValue || _activeRecord is null || _manualIdleStartedAt.HasValue;
    public bool IsManualIdleMode => _idleAwaitingClickStartedAt.HasValue || _manualIdleStartedAt.HasValue;
    public UsageTrackerService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _dataDirectory = Path.Combine(localAppData, AppDataDirectoryName);
        _settingsFilePath = Path.Combine(_dataDirectory, SettingsFileName);
        _repository = UsageTrackerRepository.Create(_dataDirectory, _settingsFilePath);
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _idleClickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _idleClickTimer.Tick += IdleClickTimer_Tick;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
    }
    public async Task InitializeAsync()
    {
        try
        {
            await Task.Yield();
            Directory.CreateDirectory(_dataDirectory);
            LoadStateCore();
        }
        catch (Exception ex)
        {
            App.LogStartupException("UsageTrackerService.InitializeAsync", ex);
            throw;
        }
    }
    public static UsageTrackerSettings LoadPersistedThemeSnapshot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsFilePath = Path.Combine(localAppData, AppDataDirectoryName, SettingsFileName);
        if (!File.Exists(settingsFilePath))
        {
            return new UsageTrackerSettings
            {
                Theme = "Dark",
                ThemeAccentColor = "#C62828",
                ThemeAccentSlots = DefaultThemeAccentSlots()
            };
        }
        try
        {
            using var stream = File.OpenRead(settingsFilePath);
            var settings = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerSettings);
            return new UsageTrackerSettings
            {
                Theme = NormalizeTheme(settings?.Theme),
                ThemeAccentColor = NormalizeThemeAccentColor(settings?.ThemeAccentColor),
                ThemeAccentSlots = NormalizeThemeAccentSlots(settings?.ThemeAccentSlots)
            };
        }
        catch (Exception ex)
        {
            App.LogStartupException("LoadPersistedThemeSnapshot", ex);
            return new UsageTrackerSettings
            {
                Theme = "Dark",
                ThemeAccentColor = "#C62828",
                ThemeAccentSlots = DefaultThemeAccentSlots()
            };
        }
    }
    public static string LoadPersistedLanguage()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsFilePath = Path.Combine(localAppData, AppDataDirectoryName, SettingsFileName);
        if (!File.Exists(settingsFilePath)) return "zh-CN";
        try
        {
            using var stream = File.OpenRead(settingsFilePath);
            var settings = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerSettings);
            return string.Equals(settings?.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
        }
        catch (Exception ex)
        {
            App.LogStartupException("LoadPersistedLanguage", ex);
            return "zh-CN";
        }
    }
    public void SetTheme(string theme)
    {
        Theme = NormalizeTheme(theme);
        SaveSettingsImmediately();
    }
    public void SetLanguage(string language)
    {
        Language = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        SaveSettingsImmediately();
    }
    public void SetThemeAccentColor(string color)
    {
        ThemeAccentColor = NormalizeThemeAccentColor(color);
        RememberThemeAccentColor(ThemeAccentColor);
        SaveSettingsImmediately();
    }

    public string GetThemeAccentSlot(int index)
    {
        return index >= 0 && index < _themeAccentSlots.Count
            ? _themeAccentSlots[index]
            : NormalizeThemeAccentColor(null);
    }

    public void SetThemeAccentSlot(int index, string color)
    {
        if (index < 0 || index >= _themeAccentSlots.Count)
        {
            return;
        }

        _themeAccentSlots[index] = NormalizeThemeAccentColor(color);
        SaveSettingsImmediately();
    }

    private void RememberThemeAccentColor(string color)
    {
        var normalized = NormalizeThemeAccentColor(color);
        _themeAccentRecentColors.RemoveAll(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
        _themeAccentRecentColors.Insert(0, normalized);
        if (_themeAccentRecentColors.Count > 16)
        {
            _themeAccentRecentColors.RemoveRange(16, _themeAccentRecentColors.Count - 16);
        }
    }
    private static IReadOnlyList<string> NormalizeThemeAccentRecentColors(IEnumerable<string>? colors, string currentColor)
    {
        var result = new List<string>();
        foreach (var color in new[] { currentColor }.Concat(colors ?? Enumerable.Empty<string>()))
        {
            var normalized = NormalizeThemeAccentColor(color);
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(normalized);
            }
            if (result.Count >= 16)
            {
                break;
            }
        }
        return result;
    }

    private static List<string> DefaultThemeAccentSlots()
    {
        return ["#ED0012", "#2563EB", "#16A34A"];
    }

    private static List<string> NormalizeThemeAccentSlots(IEnumerable<string>? colors)
    {
        var defaults = DefaultThemeAccentSlots();
        var result = defaults.ToList();
        var index = 0;
        foreach (var color in colors ?? Enumerable.Empty<string>())
        {
            if (index >= result.Count)
            {
                break;
            }

            result[index] = NormalizeThemeAccentColor(color);
            index++;
        }

        return result;
    }

    private void ApplyThemeAccentSlots(IEnumerable<string>? colors)
    {
        var normalized = NormalizeThemeAccentSlots(colors);
        _themeAccentSlots.Clear();
        _themeAccentSlots.AddRange(normalized);
    }

    private static List<string> DefaultParallelActivityWhitelistProcesses()
    {
        return ["QQMusic.exe", "cloudmusic.exe", "Spotify.exe", "PotPlayerMini64.exe", "PotPlayerMini.exe", "vlc.exe", "mpv.exe", "BiliBili.exe"];
    }

    private static string NormalizeProcessName(string? processName)
    {
        var normalized = (processName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".exe";
    }

    private static List<string> NormalizeParallelActivityWhitelistProcesses(IEnumerable<string>? processes)
    {
        return (processes ?? DefaultParallelActivityWhitelistProcesses())
            .Select(NormalizeProcessName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ApplyParallelActivityWhitelistProcesses(IEnumerable<string>? processes)
    {
        var normalized = NormalizeParallelActivityWhitelistProcesses(processes);
        _parallelActivityWhitelistProcesses.Clear();
        _parallelActivityWhitelistProcesses.AddRange(normalized);
    }

    public bool AddParallelActivityWhitelistProcess(string processName)
    {
        var normalized = NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(normalized)
            || _parallelActivityWhitelistProcesses.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _parallelActivityWhitelistProcesses.Add(normalized);
        _parallelActivityWhitelistProcesses.Sort(StringComparer.OrdinalIgnoreCase);
        SaveState();
        return true;
    }

    public bool RemoveParallelActivityWhitelistProcess(string processName)
    {
        var normalized = NormalizeProcessName(processName);
        var removed = _parallelActivityWhitelistProcesses.RemoveAll(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            SaveState();
        }
        return removed;
    }

    private bool IsParallelActivityWhitelisted(string processName)
    {
        var normalized = NormalizeProcessName(processName);
        return _parallelActivityWhitelistProcesses.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeThemeAccentColor(string? color)
    {
        return string.IsNullOrWhiteSpace(color) || !System.Text.RegularExpressions.Regex.IsMatch(color.Trim(), "^#[0-9A-Fa-f]{6}$")
            ? "#C62828"
            : color.Trim().ToUpperInvariant();
    }
    private static string NormalizeTheme(string? theme)
    {
        return string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase)
                ? "System"
                : "Dark";
    }
    public void SetIdleTimeoutMinutes(int minutes)
    {
        IdleTimeoutMinutes = NormalizeIdleTimeoutMinutes(minutes);
        SaveIdleTimeoutImmediately();
        App.LogStartupMessage("IdleTimeout.Save", $"saved '{IdleTimeoutMinutes}' to settings.json");
    }
    public void SetManualIdleShortcutText(string shortcutText)
    {
        ManualIdleShortcutText = string.IsNullOrWhiteSpace(shortcutText)
            ? string.Empty
            : shortcutText.Trim();

        SaveManualIdleShortcutImmediately();
        App.LogStartupMessage("ManualIdleShortcut.Save", $"saved '{ManualIdleShortcutText}' to settings.json");
    }

    public void SetSubjectDeleteBehavior(SubjectDeleteBehavior behavior)
    {
        SubjectDeleteBehavior = behavior;
        SaveState();
    }

    private static SubjectDeleteBehavior NormalizeSubjectDeleteBehavior(string? behavior)
    {
        return Enum.TryParse<SubjectDeleteBehavior>(behavior, ignoreCase: true, out var parsed)
            ? parsed
            : SubjectDeleteBehavior.MatchRules;
    }

    public UsageSession? EnterManualIdle(DateTime? idleStartedAt = null)
    {
        var now = DateTime.Now;
        _manualIdleStartedAt = idleStartedAt ?? now;
        _idleAwaitingClickStartedAt = _manualIdleStartedAt;
        StartIdleClickDetection();
        _lastCaptureTime = now;
        var closed = CloseActiveSession(_manualIdleStartedAt.Value);
        RaiseChanged(closed, null);
        return closed;
    }
    private static int NormalizeIdleTimeoutMinutes(int minutes)
    {
        return Math.Clamp(minutes, 1, 1440);
    }
    public void Start()
    {
        _manualIdleStartedAt = null;
        _idleAwaitingClickStartedAt = null;
        _wasMouseButtonDown = IsAnyMouseButtonDown();
        var now = DateTime.Now;
        _lastCaptureTime = now;
        CaptureCurrentWindow(now, isInitial: true);
        _pollTimer.Start();
    }
    public IReadOnlyList<UsageSession> GetRecentSessions(int count)
    {
        return _history
            .OrderByDescending(x => x.StartTime)
            .Take(count)
            .Select(ToSession)
            .ToList();
    }
    public IReadOnlyList<UsageSession> GetSessionsByDate(DateTime date)
    {
        var targetDate = date.Date;
        var result = new List<UsageSession>(64);
        // 直接遍历，不做全量拷贝
        foreach (var record in _history)
        {
            var sessionDate = record.StartTime.Date;
            var adjustedDate = record.StartTime.TimeOfDay < TimeSpan.FromHours(4)
                ? sessionDate.AddDays(-1)
                : sessionDate;
            if (adjustedDate == targetDate)
                result.Add(ToSession(record));
        }
        result.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));
        return result;
    }
    public IReadOnlyList<UsageSession> GetSessionsByDate(DateTime date, int count)
    {
        return GetSessionsByDate(date)
            .Take(count)
            .ToList();
    }
    public UsageOverview GetOverview(DateTime endExclusive)
    {
        var weekStart = endExclusive.AddDays(-1).DayOfWeek == DayOfWeek.Sunday
            ? endExclusive.AddDays(-1).Date.AddDays(-6)
            : endExclusive.AddDays(-1).Date.AddDays(-(int)endExclusive.AddDays(-1).DayOfWeek + (int)DayOfWeek.Monday);
        var totalTicks = 0L;
        var weekTicks = 0L;
        var dailyTicks = new Dictionary<DateTime, long>();
        foreach (var record in _history)
        {
            if (record.StartTime >= endExclusive)
            {
                continue;
            }
            var durationTicks = ((record.EndTime ?? record.StartTime) - record.StartTime).Ticks;
            if (durationTicks <= 0)
            {
                continue;
            }
            totalTicks += durationTicks;
            if (record.StartTime >= weekStart)
            {
                weekTicks += durationTicks;
            }
            var date = record.StartTime.Date;
            dailyTicks[date] = dailyTicks.TryGetValue(date, out var existing) ? existing + durationTicks : durationTicks;
        }
        var averageTicks = dailyTicks.Count == 0 ? 0L : (long)dailyTicks.Values.Average();
        return new UsageOverview(TimeSpan.FromTicks(totalTicks), TimeSpan.FromTicks(averageTicks), TimeSpan.FromTicks(weekTicks));
    }
    public IReadOnlyList<UsageSession> GetSessions(DateTime startInclusive, DateTime endExclusive)
    {
        var records = _history.ToArray();
        return records
            .Where(x => x.StartTime >= startInclusive && x.StartTime < endExclusive)
            .OrderByDescending(x => x.StartTime)
            .Select(ToSession)
            .ToList();
    }
    public IReadOnlyList<UsageSession> GetSessionsInRange(DateTime startInclusive, DateTime endExclusive)
    {
        return GetSessions(startInclusive, endExclusive);
    }
    // ── 按需 SQL 查询方法（不走 _history 全量内存） ──
    public async Task<List<UsageSessionRecord>> QuerySessionsByDateAsync(DateTime date)
    {
        var dayStart = date.Date.AddHours(4);
        var dayEnd = dayStart.AddDays(1);
        var records = IsDateRangeLoaded(dayStart, dayEnd)
            ? await Task.FromResult(GetFromMemory(dayStart, dayEnd))
            : await Task.Run(() => { EnsureDailySnapshots(date); return _repository.GetSessionsByDate(date); });
        return ProjectDisplaySubjects(records);
    }
    public async Task<List<UsageSessionRecord>> QuerySessionsInRangeAsync(DateTime start, DateTime end)
    {
        var records = IsDateRangeLoaded(start, end)
            ? await Task.FromResult(GetFromMemory(start, end))
            : await Task.Run(() => _repository.GetSessionsInRange(start, end));
        return ProjectDisplaySubjects(records);
    }
    public Task<UsageOverview> QueryOverviewAsync(DateTime endExclusive)
    {
        return Task.Run(() =>
        {
            var (totalTicks, weekTicks, dayCount) = _repository.GetOverviewStats(endExclusive);
            var averageTicks = dayCount == 0 ? 0L : totalTicks / dayCount;
            return new UsageOverview(
                TimeSpan.FromTicks(totalTicks),
                TimeSpan.FromTicks(averageTicks),
                TimeSpan.FromTicks(weekTicks));
        });
    }

    public Task<(TimeSpan total, TimeSpan averageDaily, int dayCount)> QueryRangeStatsAsync(DateTime startInclusive, DateTime endExclusive)
    {
        return Task.Run(() =>
        {
            var (totalTicks, dayCount) = _repository.GetRangeStats(startInclusive, endExclusive);
            var averageTicks = dayCount == 0 ? 0L : totalTicks / dayCount;
            return (TimeSpan.FromTicks(totalTicks), TimeSpan.FromTicks(averageTicks), dayCount);
        });
    }

    public Task<TimeSpan> QueryWeekUsageAsync(DateTime weekStart, DateTime weekEndExclusive)
    {
        return Task.Run(() => TimeSpan.FromTicks(_repository.GetWeekTicks(weekStart, weekEndExclusive)));
    }
    public Task<List<(string ProcessName, long TotalTicks, int SessionCount)>> QueryProcessSummariesAsync(DateTime date)
    {
        return Task.Run(() =>
        {
            EnsureDailySnapshots(date);
            var raw = _repository.GetProcessSummaries(date);
            // Apply 凌晨4点 adjustment in-memory (SQL can't easily express this per-row)
            return raw;
        });
    }
    public Task<List<(string Subject, long TotalTicks, int SessionCount)>> QuerySubjectSummariesAsync(DateTime date)
    {
        return Task.Run(() =>
        {
            EnsureDailySnapshots(date);
            return _repository.GetSubjectSummaries(date);
        });
    }
    public async Task<(List<UsageSessionRecord> Items, int TotalCount)> SearchSessionsAsync(
        string keyword, int skip = 0, int take = 50)
    {
        if (_dataLoadPhase == DataLoadPhase.Loaded && _history.Count > 0)
        {
            var list = _history.Where(r => r.ProcessName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || r.WindowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (r.ManualSubject ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.StartTime).ToList();
            return await Task.FromResult((list.Skip(skip).Take(take).ToList(), list.Count));
        }
        return await Task.Run(() => _repository.SearchSessions(keyword, skip, take));
    }

    public Task<List<DateTime>> QueryActiveDatesAsync(DateTime from, DateTime to)
    {
        return Task.Run(() => _repository.GetActiveDates(from, to));
    }
    /// <summary>
    /// 确保指定日期的自动分类快照已应用（延迟执行，首次查询某天时触发）
    /// </summary>
    private readonly HashSet<DateTime> _appliedSnapshotDates = new();

    private List<UsageSessionRecord> ProjectDisplaySubjects(IEnumerable<UsageSessionRecord> records)
    {
        return records.Select(ProjectDisplaySubject).ToList();
    }

    private UsageSessionRecord ProjectDisplaySubject(UsageSessionRecord record)
    {
        var displaySubject = ResolveSubjectForDisplay(record);
        if (string.Equals(displaySubject, record.ManualSubject, StringComparison.Ordinal))
        {
            return record;
        }

        var clone = record.Clone();
        clone.ManualSubject = string.IsNullOrWhiteSpace(displaySubject) ? null : displaySubject;
        return clone;
    }

    private void EnsureDailySnapshots(DateTime date)
    {
        var targetDate = date.Date;
        if (_appliedSnapshotDates.Contains(targetDate))
            return;
        try
        {
            ApplySnapshotForDate(targetDate);
            _appliedSnapshotDates.Add(targetDate);
        }
        catch (Exception ex)
        {
            App.LogStartupException("EnsureDailySnapshots", ex);
        }
    }
    private void ApplySnapshotForDate(DateTime date)
    {
        var sessions = _repository.GetSessionsByDate(date);
        foreach (var record in sessions.Where(r => string.IsNullOrWhiteSpace(r.ManualSubject)))
        {
            record.ManualSubject = ResolveSubjectForNewSession(record);
            // Mark dirty for save
            lock (_saveLock)
            {
                _dirtyRecordIds.Add(record.Id);
                _dirtyRecords[record.Id] = record;
            }
        }
        // Trigger an incremental save for these snapshots
        if (sessions.Any(r => !string.IsNullOrWhiteSpace(r.ManualSubject)))
        {
            QueueIncrementalSave();
        }
    }
    public IReadOnlyList<SubjectDefinition> GetSubjectDefinitions()
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        return _subjectDefinitions
            .Select(x => x.Clone())
            .ToList();
    }
    public bool AddSubject(string subject)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        subject = subject.Trim();
        if (string.IsNullOrWhiteSpace(subject) || FindSubject(subject) is not null)
        {
            return false;
        }
        _subjectDefinitions.Add(new SubjectDefinition { Name = subject });
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }
    public bool RemoveSubject(string subject)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        var existing = FindSubject(subject);
        if (existing is null)
        {
            return false;
        }
        _subjectDefinitions.Remove(existing);
        var removedSubjects = existing.GetAllSubjectNames().ToList();
        ClearManualAssignments(removedSubjects);
        foreach (var removedSubject in removedSubjects)
        {
            _subjectKeywordRules.Remove(removedSubject);
        }
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }
    public bool RenameSubject(string oldSubject, string newSubject)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        oldSubject = oldSubject.Trim();
        newSubject = newSubject.Trim();
        var existing = FindSubject(oldSubject);
        if (existing is null || string.IsNullOrWhiteSpace(newSubject) || FindSubject(newSubject) is not null) return false;
        existing.Name = newSubject;
        RenameSubjectReferences(oldSubject, newSubject);
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }

    public bool RenameChildSubject(string majorSubject, string oldParentSubject, string newParentSubject)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        oldParentSubject = oldParentSubject.Trim();
        newParentSubject = newParentSubject.Trim();
        var major = FindSubject(majorSubject);
        var existing = major?.Parents.FirstOrDefault(x => string.Equals(x.Name, oldParentSubject, StringComparison.OrdinalIgnoreCase));
        if (major is null || existing is null || string.IsNullOrWhiteSpace(newParentSubject) || major.Parents.Any(x => !ReferenceEquals(x, existing) && string.Equals(x.Name, newParentSubject, StringComparison.OrdinalIgnoreCase))) return false;
        existing.Name = newParentSubject;
        RenameSubjectReferences(oldParentSubject, newParentSubject);
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }

    public bool RenameGrandChildSubject(string majorSubject, string parentSubject, string oldChildSubject, string newChildSubject)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        oldChildSubject = oldChildSubject.Trim();
        newChildSubject = newChildSubject.Trim();
        var parent = FindParentSubject(majorSubject, parentSubject);
        if (parent is null || string.IsNullOrWhiteSpace(newChildSubject)) return false;
        var index = parent.Children.FindIndex(x => string.Equals(x, oldChildSubject, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || parent.Children.Where((_, i) => i != index).Any(x => string.Equals(x, newChildSubject, StringComparison.OrdinalIgnoreCase))) return false;
        parent.Children[index] = newChildSubject;
        RenameSubjectReferences(oldChildSubject, newChildSubject);
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }
    public bool AddChildSubject(string parentSubject, string childSubject)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        var major = FindSubject(parentSubject);
        childSubject = childSubject.Trim();
        if (major is null || string.IsNullOrWhiteSpace(childSubject) || major.Parents.Any(x => string.Equals(x.Name, childSubject, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        major.Parents.Add(new SubjectParentDefinition { Name = childSubject });
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }
    public bool AddGrandChildSubject(string majorSubject, string parentSubject, string childSubject)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        var parent = FindParentSubject(majorSubject, parentSubject);
        childSubject = childSubject.Trim();
        if (parent is null || string.IsNullOrWhiteSpace(childSubject) || parent.Children.Any(x => string.Equals(x, childSubject, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        parent.Children.Add(childSubject);
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }
    public IReadOnlyList<string> GetSubjectKeywordRules(string subject)
    {
        return _subjectKeywordRules.TryGetValue(subject, out var keywords)
            ? keywords.ToList()
            : [];
    }
    public bool AddSubjectKeywordRule(string subject, string keyword)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        keyword = keyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword) || !SubjectExists(subject))
        {
            return false;
        }
        if (!_subjectKeywordRules.TryGetValue(subject, out var keywords))
        {
            keywords = new List<string>();
            _subjectKeywordRules[subject] = keywords;
        }
        if (keywords.Any(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        keywords.Add(keyword);
        RemoveInvalidKeywordRules();
        RefreshTodayUnmanualSnapshots();
        SaveSubjectKeywordRulesImmediately();
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }
    public bool RemoveSubjectKeywordRule(string subject, string keyword)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        keyword = keyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword) || !_subjectKeywordRules.TryGetValue(subject, out var keywords))
        {
            return false;
        }
        var removed = keywords.RemoveAll(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            return false;
        }
        if (keywords.Count == 0)
        {
            _subjectKeywordRules.Remove(subject);
        }
        RefreshTodayUnmanualSnapshots();
        SaveSubjectKeywordRulesImmediately();
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }
    public bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
            var value = key?.GetValue(StartupRegistryValueName) as string;
            return string.Equals(value, BuildStartupCommand(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            App.LogStartupException("IsStartWithWindowsEnabled", ex);
            return false;
        }
    }
    public bool SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key is null)
            {
                return false;
            }
            if (enabled)
            {
                key.SetValue(StartupRegistryValueName, BuildStartupCommand());
            }
            else
            {
                key.DeleteValue(StartupRegistryValueName, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            App.LogStartupException("SetStartWithWindows", ex);
            return false;
        }
    }
    public UsageSessionRecord? DeleteSession(UsageSession session)
    {
        // 使用更宽松的匹配：允许秒级精度差异（避免DateTime精度问题）
        var startTimeTolerance = TimeSpan.FromSeconds(1);
        // O(1) 索引查找
        UsageSessionRecord? record = null;
        if (!string.IsNullOrEmpty(session.Id) && _sessionById.TryGetValue(session.Id, out var indexedRecord)
            && SessionMatches(indexedRecord, session.ProcessName, session.WindowTitle)
            && Math.Abs((indexedRecord.StartTime - session.StartTime).TotalSeconds) < startTimeTolerance.TotalSeconds)
        {
            record = indexedRecord;
        }
        else
        {
            // Fallback: linear search
            record = _history.FirstOrDefault(x => SessionMatches(x, session.ProcessName, session.WindowTitle)
                && Math.Abs((x.StartTime - session.StartTime).TotalSeconds) < startTimeTolerance.TotalSeconds);
        }
        if (record is not null)
        {
            record.EnsureId();
            _history.Remove(record);
            _sessionById.TryRemove(record.Id, out _);
            _dirtyRecords.Remove(record.Id);
            SaveDeleteIncremental(record);
            return record.Clone();
        }
        // Fallback: 查询 DB（会话可能不在内存缓冲区中）
        if (!string.IsNullOrEmpty(session.Id))
        {
            var dbRecord = _repository.GetRecordById(session.Id);
            if (dbRecord is not null && SessionMatches(dbRecord, session.ProcessName, session.WindowTitle)
                && Math.Abs((dbRecord.StartTime - session.StartTime).TotalSeconds) < startTimeTolerance.TotalSeconds)
            {
                _repository.DeleteRecord(dbRecord.Id);
                return dbRecord.Clone();
            }
        }
        if (_activeRecord is not null && SessionMatches(_activeRecord, session.ProcessName, session.WindowTitle)
            && Math.Abs((_activeRecord.StartTime - session.StartTime).TotalSeconds) < startTimeTolerance.TotalSeconds)
        {
            var deletedActive = _activeRecord.Clone();
            _activeRecord = null;
            SaveActiveSessionIncremental();
            RaiseChanged(null, null);
            return deletedActive;
        }
        return null;
    }
    public void RestoreSession(UsageSessionRecord record)
    {
        var restored = record.Clone();
        restored.EnsureId();
        if (restored.EndTime is null)
        {
            if (_activeRecord is not null)
            {
                var closed = CloseActiveSession(DateTime.Now);
                RaiseChanged(closed, null);
            }
            _activeRecord = restored;
            SaveActiveSessionIncremental();
            RaiseChanged(null, ToActiveSession(_activeRecord, DateTime.Now, ResolveSubjectForDisplay(_activeRecord)));
            return;
        }
        var exists = _history.Any(x => SessionMatches(x, restored.ProcessName, restored.WindowTitle)
            && x.StartTime == restored.StartTime);
        if (!exists)
        {
            _history.Add(restored);
            _sessionById[restored.Id] = restored;
            _dirtyRecords[restored.Id] = restored;
            SaveSessionIncremental(restored);
        }
    }
    public string GetDefaultExportDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var exportDirectory = Path.Combine(baseDirectory, ExportDirectoryName);
        Directory.CreateDirectory(exportDirectory);
        return exportDirectory;
    }
    public Task ExportDataAsync(string filePath)
    {
        return ExportFullBackupAsync(filePath);
    }

    public Task ExportUsageDataAsync(string filePath)
    {
        var state = CreateUsageDataSnapshot();
        return Task.Run(() => ExportStateSnapshotZip(filePath, UsageDataZipEntryName, state));
    }

    public Task ExportSettingsDataAsync(string filePath)
    {
        var state = CreateSettingsDataSnapshot();
        return Task.Run(() => ExportStateSnapshot(filePath, state));
    }

    public Task ExportFullBackupAsync(string filePath)
    {
        var usageState = CreateUsageDataSnapshot();
        var settingsState = CreateSettingsDataSnapshot();
        return Task.Run(() => ExportFullBackupZip(filePath, usageState, settingsState));
    }
    private static void ExportStateSnapshot(string filePath, UsageTrackerState state)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        using var stream = File.Create(filePath);
        JsonSerializer.Serialize(stream, state, UsageTrackerJsonContext.Default.UsageTrackerState);
    }

    private static void ExportStateSnapshotZip(string filePath, string entryName, UsageTrackerState state)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(filePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteStateZipEntry(archive, entryName, state);
    }

    private static void ExportFullBackupZip(string filePath, UsageTrackerState usageState, UsageTrackerState settingsState)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(filePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteStateZipEntry(archive, FullBackupDataZipEntryName, usageState);
        WriteStateZipEntry(archive, FullBackupSettingsZipEntryName, settingsState);
    }

    private static void WriteStateZipEntry(ZipArchive archive, string entryName, UsageTrackerState state)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        JsonSerializer.Serialize(entryStream, state, UsageTrackerJsonContext.Default.UsageTrackerState);
    }
    public Task<ImportPreview> PreviewImportDataAsync(string filePath)
    {
        var existingIds = GetExistingSessionIds();
        var existingSessionKeys = GetExistingSessionKeys();
        return Task.Run(() => PreviewImportData(filePath, existingIds, existingSessionKeys));
    }

    public Task<ImportPackagePreview> PreviewImportPackageAsync(string filePath, ImportPayloadKind kind)
    {
        var existingIds = GetExistingSessionIds();
        var existingSessionKeys = GetExistingSessionKeys();
        return Task.Run(() => PreviewImportPackage(filePath, kind, existingIds, existingSessionKeys));
    }

    private static ImportPackagePreview PreviewImportPackage(string filePath, ImportPayloadKind kind, HashSet<string> existingIds, HashSet<string> existingSessionKeys)
    {
        var state = kind == ImportPayloadKind.Full
            ? ReadFullBackupImportState(filePath)
            : ReadImportState(filePath);
        var records = (state.History ?? []).Take(MaxImportHistoryRecords).ToList();
        var dataPreview = PreviewImportData(state, existingIds, existingSessionKeys);
        var subjectDefinitionCount = state.SubjectDefinitions?.Sum(definition => 1 + definition.Parents.Count + definition.Parents.Sum(parent => parent.Children.Count) + definition.Children.Count) ?? 0;
        var subjectKeywordRuleCount = state.SubjectKeywordRules?.Sum(pair => pair.Value.Count(keyword => !string.IsNullOrWhiteSpace(keyword))) ?? 0;

        return new ImportPackagePreview(
            filePath,
            kind,
            state,
            dataPreview,
            records.Count,
            records.Count == 0 ? null : records.Min(record => record.StartTime),
            records.Count == 0 ? null : records.Max(record => record.StartTime),
            subjectDefinitionCount,
            subjectKeywordRuleCount,
            state.Theme,
            state.ThemeAccentColor,
            state.IdleTimeoutMinutes,
            state.ManualIdleShortcutText,
            state.SubjectDeleteBehavior);
    }

    private static ImportPreview PreviewImportData(string filePath, HashSet<string> existingIds, HashSet<string> existingSessionKeys)
    {
        var incoming = ReadImportState(filePath);
        return PreviewImportData(incoming, existingIds, existingSessionKeys);
    }

    private static ImportPreview PreviewImportData(UsageTrackerState incoming, HashSet<string> existingIds, HashSet<string> existingSessionKeys)
    {
        var incomingRecords = (incoming.History ?? []).Take(MaxImportHistoryRecords).ToList();
        var imported = 0;
        var conflicts = 0;
        foreach (var record in incomingRecords)
        {
            record.EnsureId();
            var sessionKey = BuildSessionKey(record);
            if (existingIds.Contains(record.Id) || existingSessionKeys.Contains(sessionKey))
            {
                conflicts++;
                continue;
            }
            existingIds.Add(record.Id);
            existingSessionKeys.Add(sessionKey);
            imported++;
        }
        return new ImportPreview(imported, conflicts, incomingRecords.Count);
    }
    public ImportPreview PreviewImportData(string filePath)
    {
        return PreviewImportData(filePath, GetExistingSessionIds(), GetExistingSessionKeys());
    }

    public IReadOnlyList<UsageSession> GetPreviewSessions(ImportPackagePreview preview, string searchText, SessionSearchMode mode)
    {
        return UsageTrackerPreviewSearch.GetPreviewSessions(preview, searchText, mode);
    }

    public async Task<ImportResult> ImportDataAsync(string filePath, ImportConflictStrategy conflictStrategy = ImportConflictStrategy.KeepLocal)
    {
        var incoming = await Task.Run(() => ReadImportState(filePath));
        return ImportData(incoming, conflictStrategy, importData: true, importSettings: true);
    }
    public ImportResult ImportData(string filePath, ImportConflictStrategy conflictStrategy = ImportConflictStrategy.KeepLocal)
    {
        return ImportData(ReadImportState(filePath), conflictStrategy, importData: true, importSettings: true);
    }

    public async Task<ImportResult> ImportUsageDataAsync(string filePath, ImportConflictStrategy conflictStrategy = ImportConflictStrategy.KeepLocal)
    {
        var incoming = await Task.Run(() => ReadImportState(filePath));
        return ImportData(incoming, conflictStrategy, importData: true, importSettings: false);
    }

    public Task<ImportResult> ReplaceUsageDataAsync(ImportPackagePreview preview)
    {
        return Task.Run(() => ReplaceUsageData(preview.State));
    }

    public async Task<ImportResult> ImportSettingsDataAsync(string filePath)
    {
        var incoming = await Task.Run(() => ReadImportState(filePath));
        return ImportData(incoming, ImportConflictStrategy.KeepLocal, importData: false, importSettings: true);
    }

    public Task<ImportResult> ImportSettingsDataAsync(ImportPackagePreview preview, ImportSettingsMode mode)
    {
        return Task.Run(() => ImportSettingsData(preview.State, mode));
    }

    public async Task<ImportResult> ImportFullBackupAsync(string filePath, ImportConflictStrategy conflictStrategy = ImportConflictStrategy.KeepLocal)
    {
        var incoming = await Task.Run(() => ReadFullBackupImportState(filePath));
        return ImportData(incoming, conflictStrategy, importData: true, importSettings: true);
    }

    public Task<ImportResult> ImportFullBackupAsync(ImportPackagePreview preview, ImportDataMode dataMode, ImportConflictStrategy conflictStrategy, ImportSettingsMode settingsMode)
    {
        return Task.Run(() => ImportFullBackup(preview.State, dataMode, conflictStrategy, settingsMode));
    }

    private ImportResult ImportData(UsageTrackerState incoming, ImportConflictStrategy conflictStrategy)
    {
        return ImportData(incoming, conflictStrategy, importData: true, importSettings: true);
    }

    private ImportResult ImportData(UsageTrackerState incoming, ImportConflictStrategy conflictStrategy, bool importData, bool importSettings)
    {
        var imported = 0;
        var overwritten = 0;
        var skippedConflicts = 0;
        var incomingRecords = (incoming.History ?? []).Take(MaxImportHistoryRecords).ToList();
        var existingIds = GetExistingSessionIds();
        var existingSessionKeys = GetExistingSessionKeys();
        var indexById = _history
            .Select((record, index) => new { record, index })
            .Where(x => !string.IsNullOrWhiteSpace(x.record.Id))
            .GroupBy(x => x.record.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);
        var indexBySessionKey = _history
            .Select((record, index) => new { key = BuildSessionKey(record), index })
            .GroupBy(x => x.key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);
        if (importData)
        {
            foreach (var record in incomingRecords)
            {
                record.EnsureId();
                var sessionKey = BuildSessionKey(record);
                var hasIdConflict = indexById.TryGetValue(record.Id, out var idIndex);
                var hasKeyConflict = indexBySessionKey.TryGetValue(sessionKey, out var keyIndex);
                var conflictIndex = hasIdConflict ? idIndex : hasKeyConflict ? keyIndex : -1;
                if (conflictIndex >= 0)
                {
                    if (conflictStrategy == ImportConflictStrategy.UseIncoming)
                    {
                        var importedRecord = record.Clone();
                        _history[conflictIndex] = importedRecord;
                        _sessionById[importedRecord.Id] = importedRecord;
                        existingIds.Add(importedRecord.Id);
                        existingSessionKeys.Add(sessionKey);
                        indexById[importedRecord.Id] = conflictIndex;
                        indexBySessionKey[sessionKey] = conflictIndex;
                        MarkRecordDirty(importedRecord);
                        overwritten++;
                    }
                    else
                    {
                        skippedConflicts++;
                    }
                    continue;
                }
                var addedRecord = record.Clone();
                _history.Add(addedRecord);
                _sessionById[addedRecord.Id] = addedRecord;
                var addedIndex = _history.Count - 1;
                existingIds.Add(addedRecord.Id);
                existingSessionKeys.Add(sessionKey);
                indexById[addedRecord.Id] = addedIndex;
                indexBySessionKey[sessionKey] = addedIndex;
                MarkRecordDirty(addedRecord);
                imported++;
            }

            MergeManualSubjects(incoming.ManualSubjects);
        }

        if (importSettings)
        {
            MergeSubjectKeywordRules(incoming.SubjectKeywordRules);
            MergeSubjectDefinitions(incoming.SubjectDefinitions, incoming.SubjectOptions);
            ApplyScalarSettings(incoming);
            RemoveInvalidKeywordRules();
        }

        SaveState();
        return new ImportResult(imported, skippedConflicts, importData ? incomingRecords.Count : 0, overwritten);
    }

    private ImportResult ReplaceUsageData(UsageTrackerState incoming)
    {
        var backupPath = CreateAutomaticBackup(ImportPayloadKind.Usage);
        var incomingRecords = (incoming.History ?? []).Take(MaxImportHistoryRecords).Select(record => record.Clone()).ToList();
        foreach (var record in incomingRecords)
        {
            record.EnsureId();
        }

        _history.Clear();
        _history.AddRange(incomingRecords);
        RebuildSessionIndex();
        _activeRecord = incoming.Active?.Clone();
        if (_activeRecord is not null)
        {
            _activeRecord.EnsureId();
        }
        _manualSubjects.Clear();
        foreach (var pair in incoming.ManualSubjects ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                _manualSubjects[pair.Key] = pair.Value;
            }
        }

        _repository.WriteStateToDisk(CreateStateSnapshot());
        return new ImportResult(incomingRecords.Count, 0, incomingRecords.Count, 0, backupPath);
    }

    private ImportResult ImportSettingsData(UsageTrackerState incoming, ImportSettingsMode mode)
    {
        if (mode is ImportSettingsMode.None or ImportSettingsMode.ViewOnly)
        {
            return new ImportResult(0, 0, 0, 0);
        }

        string? backupPath = null;
        if (mode == ImportSettingsMode.Replace)
        {
            backupPath = CreateAutomaticBackup(ImportPayloadKind.Settings);
        }

        var changed = ApplySettingsImport(incoming, mode);
        SaveState();
        return new ImportResult(0, 0, 0, 0, backupPath, changed);
    }

    private ImportResult ImportFullBackup(UsageTrackerState incoming, ImportDataMode dataMode, ImportConflictStrategy conflictStrategy, ImportSettingsMode settingsMode)
    {
        if (dataMode == ImportDataMode.ViewOnly && settingsMode is ImportSettingsMode.None or ImportSettingsMode.ViewOnly)
        {
            return new ImportResult(0, 0, 0, 0);
        }

        if (dataMode == ImportDataMode.Replace)
        {
            var dataResult = ReplaceUsageData(incoming);
            var settingsResult = ImportSettingsData(incoming, settingsMode);
            return new ImportResult(dataResult.ImportedCount, 0, dataResult.TotalCount, 0, dataResult.BackupPath ?? settingsResult.BackupPath, settingsResult.SettingsChangedCount);
        }

        var result = dataMode == ImportDataMode.Merge
            ? ImportData(incoming, conflictStrategy, importData: true, importSettings: false)
            : new ImportResult(0, 0, 0, 0);
        var settingsImportResult = ImportSettingsData(incoming, settingsMode);
        return result with
        {
            BackupPath = result.BackupPath ?? settingsImportResult.BackupPath,
            SettingsChangedCount = settingsImportResult.SettingsChangedCount
        };
    }
    private void ApplyScalarSettings(UsageTrackerState incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming.Theme))
        {
            Theme = NormalizeTheme(incoming.Theme);
        }
        if (!string.IsNullOrWhiteSpace(incoming.ThemeAccentColor))
        {
            ThemeAccentColor = NormalizeThemeAccentColor(incoming.ThemeAccentColor);
        }
        ApplyThemeAccentSlots(incoming.ThemeAccentSlots);
        _themeAccentRecentColors.Clear();
        _themeAccentRecentColors.AddRange(NormalizeThemeAccentRecentColors(incoming.ThemeAccentRecentColors, ThemeAccentColor));
        if (incoming.IdleTimeoutMinutes is { } idleTimeoutMinutes)
        {
            IdleTimeoutMinutes = NormalizeIdleTimeoutMinutes(idleTimeoutMinutes);
        }
        if (!string.IsNullOrWhiteSpace(incoming.ManualIdleShortcutText))
        {
            ManualIdleShortcutText = incoming.ManualIdleShortcutText.Trim();
        }
        if (!string.IsNullOrWhiteSpace(incoming.SubjectDeleteBehavior))
        {
            SubjectDeleteBehavior = NormalizeSubjectDeleteBehavior(incoming.SubjectDeleteBehavior);
        }
    }

    private int ApplySettingsImport(UsageTrackerState incoming, ImportSettingsMode mode)
    {
        var changed = 0;
        if (mode is ImportSettingsMode.RulesOnly or ImportSettingsMode.Merge or ImportSettingsMode.Replace)
        {
            if (mode == ImportSettingsMode.Replace)
            {
                _manualSubjects.Clear();
                _subjectKeywordRules.Clear();
                _subjectDefinitions.Clear();
            }

            changed += CountSettingsRules(incoming);
            MergeManualSubjects(incoming.ManualSubjects);
            MergeSubjectKeywordRules(incoming.SubjectKeywordRules);
            MergeSubjectDefinitions(incoming.SubjectDefinitions, incoming.SubjectOptions);
            RemoveInvalidKeywordRules();
        }

        if (mode is ImportSettingsMode.BasicOnly or ImportSettingsMode.Merge or ImportSettingsMode.Replace)
        {
            ApplyScalarSettings(incoming);
            changed++;
        }

        return changed;
    }

    private static int CountSettingsRules(UsageTrackerState incoming)
    {
        var manualCount = incoming.ManualSubjects?.Count ?? 0;
        var keywordCount = incoming.SubjectKeywordRules?.Sum(pair => pair.Value.Count(keyword => !string.IsNullOrWhiteSpace(keyword))) ?? 0;
        var subjectCount = incoming.SubjectDefinitions?.Sum(definition => 1 + definition.Parents.Count + definition.Parents.Sum(parent => parent.Children.Count) + definition.Children.Count) ?? 0;
        return manualCount + keywordCount + subjectCount;
    }

    private static UsageTrackerState ReadImportState(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        ValidateImportFile(fileInfo, MaxImportZipSizeBytes);

        if (IsZipFile(filePath))
        {
            return ReadStateFromZip(filePath, UsageDataZipEntryName, FullBackupDataZipEntryName);
        }

        ValidateImportFile(fileInfo, MaxImportFileSizeBytes);
        using var stream = File.OpenRead(filePath);
        return JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerState)
            ?? new UsageTrackerState();
    }

    private static UsageTrackerState ReadFullBackupImportState(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        ValidateImportFile(fileInfo, MaxImportZipSizeBytes);

        if (!IsZipFile(filePath))
        {
            ValidateImportFile(fileInfo, MaxImportFileSizeBytes);
            using var stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerState)
                ?? new UsageTrackerState();
        }

        using var archive = ZipFile.OpenRead(filePath);
        var usageState = ReadStateZipEntry(archive, FullBackupDataZipEntryName)
            ?? ReadStateZipEntry(archive, UsageDataZipEntryName)
            ?? new UsageTrackerState();
        var settingsState = ReadStateZipEntry(archive, FullBackupSettingsZipEntryName);
        if (settingsState is null)
        {
            return usageState;
        }

        MergeSettingsIntoState(usageState, settingsState);
        return usageState;
    }

    private static void ValidateImportFile(FileInfo fileInfo, int maxBytes)
    {
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("导入文件不存在。", fileInfo.FullName);
        }
        if (fileInfo.Length <= 0)
        {
            throw new InvalidDataException("导入文件为空。");
        }
        if (fileInfo.Length > maxBytes)
        {
            throw new InvalidDataException($"导入文件过大，已限制为 {maxBytes / (1024 * 1024)} MB。");
        }
    }

    private static bool IsZipFile(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static UsageTrackerState ReadStateFromZip(string filePath, params string[] entryNames)
    {
        using var archive = ZipFile.OpenRead(filePath);
        foreach (var entryName in entryNames)
        {
            var state = ReadStateZipEntry(archive, entryName);
            if (state is not null)
            {
                return state;
            }
        }

        throw new InvalidDataException("ZIP 文件中未找到可导入的数据快照。");
    }

    private static UsageTrackerState? ReadStateZipEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null || entry.Length <= 0 || entry.Length > MaxImportFileSizeBytes)
        {
            return null;
        }

        using var stream = entry.Open();
        return JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerState);
    }

    private static void MergeSettingsIntoState(UsageTrackerState target, UsageTrackerState settings)
    {
        target.SubjectKeywordRules = settings.SubjectKeywordRules;
        target.SubjectDefinitions = settings.SubjectDefinitions;
        target.SubjectOptions = settings.SubjectOptions;
        target.Theme = settings.Theme;
        target.ThemeAccentColor = settings.ThemeAccentColor;
        target.ThemeAccentRecentColors = settings.ThemeAccentRecentColors;
        target.IdleTimeoutMinutes = settings.IdleTimeoutMinutes;
        target.ManualIdleShortcutText = settings.ManualIdleShortcutText;
        target.SubjectDeleteBehavior = settings.SubjectDeleteBehavior;
    }

    private string CreateAutomaticBackup(ImportPayloadKind kind)
    {
        var backupDirectory = Path.Combine(GetDefaultExportDirectory(), "自动备份");
        Directory.CreateDirectory(backupDirectory);
        var prefix = kind switch
        {
            ImportPayloadKind.Usage => "before-usage-import",
            ImportPayloadKind.Settings => "before-settings-import",
            _ => "before-full-import"
        };
        var filePath = Path.Combine(backupDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        ExportFullBackupZip(filePath, CreateUsageDataSnapshot(), CreateSettingsDataSnapshot());
        return filePath;
    }
    private HashSet<string> GetExistingSessionIds()
    {
        return _history
            .Select(x =>
            {
                x.EnsureId();
                return x.Id;
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private HashSet<string> GetExistingSessionKeys()
    {
        return _history
            .Select(BuildSessionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    public bool RemoveChildSubject(string parentSubject, string childSubject, bool promoteToParent)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        var major = FindSubject(parentSubject);
        if (major is null)
        {
            return false;
        }
        var existing = major.Parents.FirstOrDefault(x => string.Equals(x.Name, childSubject, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }
        major.Parents.Remove(existing);
        var removedSubjects = existing.GetAllSubjectNames().ToList();
        ReclassifyRemovedSubjects(removedSubjects, promoteToParent ? major.Name : null);
        foreach (var removedSubject in removedSubjects)
        {
            _subjectKeywordRules.Remove(removedSubject);
        }
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }
    public bool RemoveGrandChildSubject(string majorSubject, string parentSubject, string childSubject, bool promoteToParent)
    {
        ReloadSubjectDefinitionsFromSettingsFile();
        var parent = FindParentSubject(majorSubject, parentSubject);
        if (parent is null)
        {
            return false;
        }
        var existing = parent.Children.FirstOrDefault(x => string.Equals(x, childSubject, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }
        parent.Children.Remove(existing);
        ReclassifyRemovedSubjects([existing], promoteToParent ? parent.Name : null);
        _subjectKeywordRules.Remove(existing);
        SaveState();
        RaiseChanged(null, ActiveSession);
        return true;
    }
    public void SetManualSubjectScope(UsageSession session, string? manualSubject, DateTime? targetDate)
    {
        if (targetDate is null)
        {
            foreach (var record in _history.Where(record => SessionMatches(record, session.ProcessName, session.WindowTitle)))
            {
                record.ManualSubject = manualSubject ?? MatchSubjectByKeyword(record.ProcessName, record.WindowTitle);
            }
            SaveState();
            return;
        }
        SetManualSubjectForDate(session, manualSubject, targetDate.Value);
    }
    public void SetManualSubject(UsageSession session, string? manualSubject)
    {
        SetManualSubjectForDate(session, manualSubject, session.StartTime.Date);
    }
    public void SetManualSubjectForDate(UsageSession session, string? manualSubject, DateTime targetDate)
    {
        var date = targetDate.Date;
        var classificationKey = BuildClassificationKey(session.ProcessName, session.WindowTitle);
        var resolvedSubject = manualSubject ?? MatchSubjectByKeyword(session.ProcessName, session.WindowTitle);
        if (!string.Equals(session.ProcessName, "时迹", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(session.ProcessName, "时迹.exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(session.ProcessName, "UsageTrackerNative", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(session.ProcessName, "UsageTrackerNative.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(manualSubject))
            {
                _manualSubjects.Remove(classificationKey);
            }
            else
            {
                _manualSubjects[classificationKey] = manualSubject;
            }
            _settingsDirty = true;
        }
        var updatedRecord = false;
        foreach (var record in _history.Where(record => record.StartTime.Date == date && string.Equals(record.Id, session.Id, StringComparison.OrdinalIgnoreCase)))
        {
            record.ManualSubject = resolvedSubject;
            lock (_saveLock)
            {
                _dirtyRecordIds.Add(record.Id);
                _dirtyRecords[record.Id] = record;
            }
            updatedRecord = true;
        }
        if (!updatedRecord)
        {
            foreach (var record in _history.Where(record => record.StartTime.Date == date && SessionMatches(record, session.ProcessName, session.WindowTitle) && record.StartTime == session.StartTime))
            {
                record.ManualSubject = resolvedSubject;
                lock (_saveLock)
                {
                    _dirtyRecordIds.Add(record.Id);
                    _dirtyRecords[record.Id] = record;
                }
                updatedRecord = true;
            }
        }
        // Fallback: 如果记录不在内存缓冲区中，直接更新 DB
        if (!updatedRecord)
        {
            var dbRecord = _repository.GetRecordById(session.Id);
            if (dbRecord is not null)
            {
                dbRecord.ManualSubject = resolvedSubject;
                lock (_saveLock)
                {
                    _dirtyRecordIds.Add(dbRecord.Id);
                    _dirtyRecords[dbRecord.Id] = dbRecord;
                }
            }
        }
        if (_activeRecord is not null && date == DateTime.Today
            && (string.Equals(_activeRecord.Id, session.Id, StringComparison.OrdinalIgnoreCase)
                || (SessionMatches(_activeRecord, session.ProcessName, session.WindowTitle) && _activeRecord.StartTime == session.StartTime)))
        {
            _activeRecord.ManualSubject = resolvedSubject;
        }
        SaveState();
    }
    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        if (_lastCaptureTime is { } lastCaptureTime && now - lastCaptureTime > SleepGapThreshold)
        {
            var closedForSleep = CloseActiveSession(lastCaptureTime);
            _lastParallelActivitySnapshot = null;
            _lastCaptureTime = now;
            RaiseChanged(closedForSleep, null);
            return;
        }
        _lastCaptureTime = now;
        CaptureCurrentWindow(now, isInitial: false);
    }
    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            StopTrackingForPause();
            return;
        }
        if (e.Mode == PowerModes.Resume)
        {
            ResumeTrackingAfterPause();
        }
    }
    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            StopTrackingForPause();
            return;
        }
        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            ResumeTrackingAfterPause();
        }
    }
    private void StopTrackingForPause()
    {
        var pausedAt = _lastCaptureTime ?? DateTime.Now;
        var closed = CloseActiveSession(pausedAt);
        _lastParallelActivitySnapshot = null;
        _lastCaptureTime = pausedAt;
        _pollTimer.Stop();
        RaiseChanged(closed, null);
    }
    private void ResumeTrackingAfterPause()
    {
        var now = DateTime.Now;
        UsageSession? closed = null;
        if (_lastCaptureTime is { } lastCaptureTime && now - lastCaptureTime > SleepGapThreshold)
        {
            closed = CloseActiveSession(lastCaptureTime);
            _lastParallelActivitySnapshot = null;
        }
        _lastCaptureTime = now;
        if (!_isDisposed)
        {
            _pollTimer.Start();
        }
        RaiseChanged(closed, null);
    }
    private IdleMediaProtectionDecision EvaluateIdleMediaProtection(WindowSnapshot snapshot, TimeSpan idleDuration, MediaPlaybackSnapshot playback)
    {
        if (playback.HasAnyPlayback)
        {
            var parallelActivity = BuildParallelActivitySnapshot(playback, snapshot, idleDuration);
            if (IsForegroundAudioPlayback(snapshot, playback))
            {
                MarkForegroundAudioAudible(snapshot);
                return IdleMediaProtectionDecision.Protected("ForegroundAudioPlayback", parallelActivity);
            }

            if (HasWhitelistedAudioPlayback(playback))
            {
                return IdleMediaProtectionDecision.Protected("WhitelistedAudioPlayback", parallelActivity);
            }

            if (IsMediaPlaybackSurface(snapshot))
            {
                return IdleMediaProtectionDecision.Protected("BackgroundMediaWithContentSurface", parallelActivity);
            }

            if (idleDuration < LongIdleMediaProtectionThreshold && IsMediaFriendlySurface(snapshot))
            {
                return IdleMediaProtectionDecision.Protected("ShortIdleBackgroundMedia", parallelActivity);
            }

            if (IsShellOrSystemSurface(snapshot))
            {
                return IdleMediaProtectionDecision.Protected("ShellSurfaceWithActivePlayback", parallelActivity);
            }

            return IdleMediaProtectionDecision.NotProtected("BackgroundMediaNotAllowedForSurface", parallelActivity);
        }

        if (IsMusicPlaybackSurface(snapshot) && HasRecentForegroundAudio(snapshot, ForegroundMediaAudioGracePeriod))
        {
            return IdleMediaProtectionDecision.Protected("RecentMusicSurfaceWithoutAudioSession", null);
        }

        if (IsVideoPlaybackSurface(snapshot) && HasRecentForegroundAudio(snapshot, ForegroundVideoPlaybackGracePeriod))
        {
            return IdleMediaProtectionDecision.Protected("RecentVideoSurfaceWithoutAudioSession", null);
        }

        if (IsReadingSurface(snapshot))
        {
            return IdleMediaProtectionDecision.Protected("ContentSurfaceWithoutAudioSession", null);
        }

        if (IsShellOrSystemSurface(snapshot))
        {
            return IdleMediaProtectionDecision.NotProtected("ShellOrSystemSurface", null);
        }

        return IdleMediaProtectionDecision.NotProtected("NoActiveMedia", null);
    }

    private bool IsForegroundAudioPlayback(WindowSnapshot snapshot, MediaPlaybackSnapshot playback)
    {
        if (playback.IsPreferredProcessPlaying)
        {
            return true;
        }

        var foregroundProcessName = NormalizeProcessName(snapshot.ProcessName);
        return playback.ActiveProcessNames
            .Select(NormalizeProcessName)
            .Any(processName => string.Equals(processName, foregroundProcessName, StringComparison.OrdinalIgnoreCase));
    }

    private void MarkForegroundAudioAudible(WindowSnapshot snapshot)
    {
        var processName = NormalizeProcessName(snapshot.ProcessName);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        if (!_parallelAudioStates.TryGetValue(processName, out var state))
        {
            state = new ParallelAudioStabilityState();
            _parallelAudioStates[processName] = state;
        }

        state.AudibleSamples++;
        state.SilentSamples = 0;
        state.LastAudibleAt = DateTime.Now;
    }

    private bool HasRecentForegroundAudio(WindowSnapshot snapshot, TimeSpan gracePeriod)
    {
        var processName = NormalizeProcessName(snapshot.ProcessName);
        return _parallelAudioStates.TryGetValue(processName, out var state)
            && state.LastAudibleAt != default
            && DateTime.Now - state.LastAudibleAt <= gracePeriod;
    }

    private bool HasWhitelistedAudioPlayback(MediaPlaybackSnapshot playback)
    {
        return playback.ActiveProcessNames.Any(IsParallelActivityWhitelisted);
    }

    private ParallelActivitySnapshot? BuildParallelActivitySnapshot(MediaPlaybackSnapshot playback, WindowSnapshot snapshot, TimeSpan idleDuration)
    {
        var foregroundProcessName = NormalizeProcessName(snapshot.ProcessName);
        var audibleProcessNames = playback.ActiveProcessNames
            .Where(IsParallelActivityWhitelisted)
            .Where(x => !string.Equals(NormalizeProcessName(x), foregroundProcessName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (audibleProcessNames.Count == 0)
        {
            LogParallelAudioState(snapshot, playback, audibleProcessNames, null);
            foreach (var processName in _parallelAudioStates.Keys.ToList())
            {
                var state = _parallelAudioStates[processName];
                state.AudibleSamples = 0;
                state.SilentSamples++;
                if (state.SilentSamples >= ParallelAudioStopSampleThreshold)
                {
                    state.IsActiveParallel = false;
                }
            }
        }

        foreach (var processName in _parallelAudioStates.Keys.ToList())
        {
            if (!audibleProcessNames.Contains(processName))
            {
                var state = _parallelAudioStates[processName];
                state.AudibleSamples = 0;
                state.SilentSamples++;
                if (state.SilentSamples >= ParallelAudioStopSampleThreshold)
                {
                    state.IsActiveParallel = false;
                }
            }
        }

        foreach (var processName in audibleProcessNames)
        {
            if (!_parallelAudioStates.TryGetValue(processName, out var state))
            {
                state = new ParallelAudioStabilityState();
                _parallelAudioStates[processName] = state;
            }

            state.AudibleSamples++;
            state.SilentSamples = 0;
            state.LastAudibleAt = DateTime.Now;
            if (state.AudibleSamples >= ParallelAudioStartSampleThreshold)
            {
                state.IsActiveParallel = true;
            }
        }

        var activeProcessName = _parallelAudioStates
            .Where(x => x.Value.IsActiveParallel)
            .Select(x => x.Key)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(activeProcessName))
        {
            LogParallelAudioState(snapshot, playback, audibleProcessNames, null);
            return null;
        }

        var activity = new ParallelActivitySnapshot
        {
            Kind = ParallelActivityKind.MediaPlayback,
            ProcessName = activeProcessName,
            WindowTitle = "后台媒体播放",
            Description = "后台媒体播放中",
            CountInTotal = false,
            IsCurrentProcess = false,
            ObservedDuration = idleDuration
        };
        LogParallelAudioState(snapshot, playback, audibleProcessNames, activity);
        return activity;
    }

    private void LogParallelAudioState(WindowSnapshot snapshot, MediaPlaybackSnapshot playback, IReadOnlyCollection<string> audibleProcessNames, ParallelActivitySnapshot? activity)
    {
        if ((DateTime.Now - _lastParallelAudioStateLogAt) < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastParallelAudioStateLogAt = DateTime.Now;
        if (playback.ActiveProcessNames.Count == 0
            && audibleProcessNames.Count == 0
            && activity is null
            && _parallelAudioStates.Count == 0)
        {
            return;
        }

        var whitelist = string.Join(", ", _parallelActivityWhitelistProcesses);
        var states = string.Join(", ", _parallelAudioStates.Select(x => $"{x.Key}:aud={x.Value.AudibleSamples}:sil={x.Value.SilentSamples}:active={x.Value.IsActiveParallel}"));
        App.LogStartupMessage(
            "ParallelAudioState",
            $"fg={snapshot.ProcessName}, playback=[{string.Join(", ", playback.ActiveProcessNames)}], audibleWhitelist=[{string.Join(", ", audibleProcessNames)}], activity={activity?.ProcessName ?? "None"}, whitelist=[{whitelist}], states=[{states}]");
    }

    private static bool IsShellOrSystemSurface(WindowSnapshot snapshot)
    {
        var process = snapshot.ProcessName;
        var title = snapshot.WindowTitle;
        if (ProcessEquals(process, "explorer.exe"))
        {
            return string.IsNullOrWhiteSpace(title)
                || title.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                || title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase)
                || title.Contains("系统托盘", StringComparison.OrdinalIgnoreCase)
                || title.Contains("任务视图", StringComparison.OrdinalIgnoreCase)
                || title.Contains("开始", StringComparison.OrdinalIgnoreCase);
        }

        return ProcessEquals(process, "ShellHost.exe")
            || ProcessEquals(process, "WindowsShellExperienceHost.exe")
            || ProcessEquals(process, "StartMenuExperienceHost.exe")
            || ProcessEquals(process, "SearchHost.exe")
            || ProcessEquals(process, "SystemSettings.exe")
            || ProcessEquals(process, "时迹.exe")
            || ProcessEquals(process, "UsageTrackerNative.exe");
    }

    private static bool IsContentConsumptionSurface(WindowSnapshot snapshot)
    {
        return IsMediaPlaybackSurface(snapshot) || IsReadingSurface(snapshot);
    }

    private static bool IsMediaPlaybackSurface(WindowSnapshot snapshot)
    {
        return IsVideoPlaybackSurface(snapshot) || IsMusicPlaybackSurface(snapshot);
    }

    private static bool IsVideoPlaybackSurface(WindowSnapshot snapshot)
    {
        var process = snapshot.ProcessName;
        if (ProcessEquals(process, "msedge.exe")
            || ProcessEquals(process, "chrome.exe")
            || ProcessEquals(process, "firefox.exe")
            || ProcessEquals(process, "brave.exe")
            || ProcessEquals(process, "opera.exe")
            || ProcessEquals(process, "iexplore.exe")
            || ProcessEquals(process, "PotPlayerMini64.exe")
            || ProcessEquals(process, "PotPlayerMini.exe")
            || ProcessEquals(process, "vlc.exe")
            || ProcessEquals(process, "mpv.exe")
            || ProcessEquals(process, "BiliBili.exe"))
        {
            return true;
        }

        var title = snapshot.WindowTitle;
        return title.Contains("bilibili", StringComparison.OrdinalIgnoreCase)
            || title.Contains("哔哩", StringComparison.OrdinalIgnoreCase)
            || title.Contains("YouTube", StringComparison.OrdinalIgnoreCase)
            || title.Contains("视频", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMusicPlaybackSurface(WindowSnapshot snapshot)
    {
        var process = snapshot.ProcessName;
        return ProcessEquals(process, "QQMusic.exe")
            || ProcessEquals(process, "cloudmusic.exe")
            || ProcessEquals(process, "sodamusic.exe")
            || ProcessEquals(process, "Spotify.exe");
    }

    private static bool IsReadingSurface(WindowSnapshot snapshot)
    {
        var process = snapshot.ProcessName;
        if (ProcessEquals(process, "AcroRd32.exe")
            || ProcessEquals(process, "SumatraPDF.exe")
            || ProcessEquals(process, "CAJViewer.exe")
            || ProcessEquals(process, "Koodo Reader.exe")
            || ProcessEquals(process, "NeatReader.exe"))
        {
            return true;
        }

        var title = snapshot.WindowTitle;
        return title.Contains("小说", StringComparison.OrdinalIgnoreCase)
            || title.Contains("阅读", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Reader", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMediaFriendlySurface(WindowSnapshot snapshot)
    {
        return IsContentConsumptionSurface(snapshot)
            || ProcessEquals(snapshot.ProcessName, "Weixin.exe")
            || ProcessEquals(snapshot.ProcessName, "QQ.exe")
            || ProcessEquals(snapshot.ProcessName, "TIM.exe")
            || ProcessEquals(snapshot.ProcessName, "Doubao.exe");
    }

    private static bool ProcessEquals(string processName, string expected)
    {
        return string.Equals(processName, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void LogIdleMediaDecision(WindowSnapshot snapshot, TimeSpan idleDuration, IdleMediaProtectionDecision decision)
    {
        if (!decision.ShouldProtect && decision.ParallelActivity is null && !IsShellOrSystemSurface(snapshot))
        {
            return;
        }

        App.LogStartupMessage(
            "IdleMediaDecision",
            $"protect={decision.ShouldProtect}, reason={decision.Reason}, idle={idleDuration.TotalSeconds:F0}s, foreground={snapshot.ProcessName}, title='{snapshot.WindowTitle}', parallel={decision.ParallelActivity?.ProcessName ?? "None"}");
    }

    private readonly record struct IdleMediaProtectionDecision(bool ShouldProtect, string Reason, ParallelActivitySnapshot? ParallelActivity)
    {
        public static IdleMediaProtectionDecision Protected(string reason, ParallelActivitySnapshot? parallelActivity) => new(true, reason, parallelActivity);
        public static IdleMediaProtectionDecision NotProtected(string reason, ParallelActivitySnapshot? parallelActivity) => new(false, reason, parallelActivity);
    }

    private void CaptureCurrentWindow(DateTime now, bool isInitial)
    {
        if (_idleAwaitingClickStartedAt is not null)
        {
            return;
        }
        var snapshot = ForegroundWindowTracker.GetCurrent();
        var playbackSnapshot = MediaPlaybackMonitor.TryGetPlaybackSnapshot(snapshot?.ProcessId ?? 0, out var currentPlayback)
            ? currentPlayback
            : MediaPlaybackSnapshot.Empty;
        if (snapshot is null)
        {
            _lastParallelActivitySnapshot = null;
            var closedForUnavailableWindow = CloseActiveSession(now);
            if (!isInitial || closedForUnavailableWindow is not null)
            {
                RaiseChanged(closedForUnavailableWindow, null);
            }
            return;
        }
        if (_activeRecord is not null)
        {
            _activeRecord.LastCapturedAt = now;
        }
        if (SystemActivityMonitor.TryGetIdleDuration(out var idleDuration) && idleDuration >= IdleTimeout)
        {
            var mediaProtection = EvaluateIdleMediaProtection(snapshot, idleDuration, playbackSnapshot);
            _lastParallelActivitySnapshot = mediaProtection.ParallelActivity;
            if (_activeRecord is not null)
            {
                _activeRecord.ParallelActivities = _lastParallelActivitySnapshot is null
                    ? null
                    : [_lastParallelActivitySnapshot.Clone()];
            }
            if (mediaProtection.ShouldProtect)
            {
                LogIdleMediaDecision(snapshot, idleDuration, mediaProtection);
                return;
            }

            var idleStartedAt = now - idleDuration;
            _manualIdleStartedAt = idleStartedAt;
            _idleAwaitingClickStartedAt = idleStartedAt;
            StartIdleClickDetection();
            var closedForIdle = CloseActiveSession(idleStartedAt);
            LogIdleMediaDecision(snapshot, idleDuration, mediaProtection);
            if (!isInitial || closedForIdle is not null)
            {
                RaiseChanged(closedForIdle, null);
            }
            return;
        }
        _lastParallelActivitySnapshot = BuildParallelActivitySnapshot(playbackSnapshot, snapshot, TimeSpan.Zero);
        if (_activeRecord is not null)
        {
            _activeRecord.ParallelActivities = _lastParallelActivitySnapshot is null
                ? null
                : [_lastParallelActivitySnapshot.Clone()];
        }
        if (_activeRecord is null)
        {
            _activeRecord = UsageSessionRecord.Start(snapshot.ProcessName, snapshot.WindowTitle, now);
            _activeRecord.ManualSubject = ResolveSubjectForNewSession(_activeRecord);
            _activeRecord.LastCapturedAt = now;
            SaveActiveSessionIncremental();
            RaiseChanged(null, ToActiveSession(_activeRecord, now, ResolveSubjectForDisplay(_activeRecord)));
            return;
        }
        if (_activeRecord.ProcessName.Equals(snapshot.ProcessName, StringComparison.OrdinalIgnoreCase)
            && _activeRecord.WindowTitle.Equals(snapshot.WindowTitle, StringComparison.Ordinal))
        {
            SaveActiveSessionIncremental();
            if (!isInitial)
            {
                RaiseChanged(null, ToActiveSession(_activeRecord, now, ResolveSubjectForDisplay(_activeRecord)));
            }
            return;
        }
        var closed = CloseActiveSession(now);
        _activeRecord = UsageSessionRecord.Start(snapshot.ProcessName, snapshot.WindowTitle, now);
        _activeRecord.ManualSubject = ResolveSubjectForNewSession(_activeRecord);
        SaveActiveSessionIncremental();
        RaiseChanged(closed, ToActiveSession(_activeRecord, now, ResolveSubjectForDisplay(_activeRecord)));
    }
    private UsageSession? CloseActiveSession(DateTime endTime)
    {
        if (_activeRecord is null)
        {
            return null;
        }
        if (endTime <= _activeRecord.StartTime)
        {
            _lastParallelActivitySnapshot = null;
            _activeRecord = null;
            SaveActiveSessionIncremental();
            return null;
        }
        _activeRecord.EndTime = endTime;
        var activeParallelActivities = _lastParallelActivitySnapshot is null
            ? []
            : new List<ParallelActivitySnapshot> { _lastParallelActivitySnapshot.Clone() };
        _activeRecord.ParallelActivities = activeParallelActivities;
        var closed = ToSession(_activeRecord, ResolveSubjectForDisplay(_activeRecord));
        var closedRecord = _activeRecord;
        _history.Add(_activeRecord);
        _dirtyRecords[_activeRecord.Id] = _activeRecord;
        _lastParallelActivitySnapshot = null;
        _activeRecord = null;
        SaveSessionIncremental(closedRecord);
        return closed;
    }
    private void LoadStateCore()
    {
        try
        {
            App.LogStartupMessage("UsageTrackerService.LoadStateCore", "begin");
            // 1. Load settings (always from settings.json)
            LoadSettingsFromDisk();
            App.LogStartupMessage("UsageTrackerService.LoadStateCore", "settings loaded");
            // 2. Ensure DB exists, load only active session (fast, no full history load)
            try
            {
                App.LogStartupMessage("UsageTrackerService.LoadStateCore", "before EnsureDatabase");
                EnsureDatabase();
                App.LogStartupMessage("UsageTrackerService.LoadStateCore", "after EnsureDatabase");
                // 尝试将旧的 JSON 数据迁移到 SQLite
                try
                {
                    var migrated = _repository.MigrateJsonToDatabase();
                    App.LogStartupMessage("UsageTrackerService.LoadStateCore", $"JSON migration: {(migrated ? "migrated" : "skipped")}");
                }
                catch (Exception migEx)
                {
                    App.LogStartupException("UsageTrackerService.LoadStateCore.JsonMigration", migEx);
                }
                try
                {
                    var bakMerged = LoadMissingHistoryFromBackups();
                    App.LogStartupMessage("UsageTrackerService.LoadStateCore", $"backup merge: {(bakMerged ? "merged" : "skipped")}");
                }
                catch (Exception bakEx)
                {
                    App.LogStartupException("UsageTrackerService.LoadStateCore.BackupMerge", bakEx);
                }
                // 阶段1：只加载当天数据 + 活动会话，追踪器尽快启动
                _history.Clear();
                _repository.LoadTodayHistory(_history, out _activeRecord);
                CloseStaleActiveSessionOnStartup(DateTime.Now);
                RebuildSessionIndex();
                _cachedEarliestDate = _repository.GetEarliestDate();
                var today4am = DateTime.Today.AddHours(4);
                if (DateTime.Now.TimeOfDay < TimeSpan.FromHours(4)) today4am = DateTime.Today.AddDays(-1).AddHours(4);
                UpdateLoadedRange(today4am, today4am.AddDays(1));
                _dataLoadPhase = DataLoadPhase.Loading;
                App.LogStartupMessage("UsageTrackerService.LoadStateCore", $"today loaded ({_history.Count} records), earliest date: {_cachedEarliestDate}");
            }
            catch (Exception dbEx)
            {
                App.LogStartupException("UsageTrackerService.LoadStateCore.DBInit", dbEx);
            }
            App.LogStartupMessage("UsageTrackerService.LoadStateCore", "subject definitions loaded from user settings only");
            // 分类配置只允许来自用户 settings.json，不再使用任何默认分类兜底。
            App.LogStartupMessage("UsageTrackerService.LoadStateCore", "after subject check");
            // Apply daily snapshots on first query (deferred), not at startup
            App.LogStartupMessage("UsageTrackerService.LoadStateCore", "deferring daily snapshots to first query");
            // Save settings if missing
            if (!File.Exists(_settingsFilePath))
            {
                _settingsDirty = true;
                SaveState();
            }
            App.LogStartupMessage("UsageTrackerService.LoadStateCore", "completed");
        }
        catch (Exception ex)
        {
            App.LogStartupException("UsageTrackerService.LoadStateCore", ex);
        }
    }
    private void CloseStaleActiveSessionOnStartup(DateTime now)
    {
        if (_activeRecord is null)
        {
            return;
        }

        if (_activeRecord.LastCapturedAt is { } lastCapturedAt)
        {
            var gap = now - lastCapturedAt;
            if (gap <= SleepGapThreshold)
            {
                // gap 很小，说明刚刚被保存（比如正常退出前 Dispose 写入），保留 active session
                return;
            }
            // gap 过大（休眠/关机），按最后采样时刻截断
            var closed = CloseActiveSession(lastCapturedAt);
            App.LogStartupMessage(
                "ActiveSession.RecoveredAfterGap",
                $"closed stale active session at {lastCapturedAt:O}, startup={now:O}, gap={gap.TotalSeconds:F0}s, closed={closed?.ProcessName ?? "None"}");
        }
        else
        {
            // LastCapturedAt 缺失（旧版数据或未成功写入），无法可信截断，丢弃 ActiveSession
            App.LogStartupMessage(
                "ActiveSession.DiscardedNullLastCapturedAt",
                $"discarded active session with null LastCapturedAt, process={_activeRecord.ProcessName}, startTime={_activeRecord.StartTime:O}");
            _activeRecord = null;
            SaveActiveSessionIncremental();
        }
    }

    /// <summary>
    /// 阶段2+3：后台渐进加载历史数据。
    /// 阶段2 → 最近7天；阶段3 → 本月剩余天（已加载的跳过）。
    /// 不阻塞 UI，追踪器已在阶段1启动。
    /// </summary>
    public async Task LoadBackgroundHistoryAsync()
    {
        await Task.Yield(); // 让 UI 线程先完成当前帧

        // 阶段2：加载最近7天数据
        SetLoadPhase(DataLoadPhase.Loading, "正在加载最近7天数据...");
        try
        {
            await Task.Run(() =>
            {
                var today = DateTime.Today;
                var sevenDaysAgo = today.AddDays(-6);
                var from = sevenDaysAgo.AddHours(4);
                var to = today.AddHours(4).AddDays(1);

                // 只加载已加载区间之前的缺失日期
                if (from < _loadedRangeStart)
                {
                    var beforeCount = _history.Count;
                    _repository.LoadHistoryForDateRange(_history, from, _loadedRangeStart);
                    UpdateLoadedRange(from, _loadedRangeEnd);
                    RebuildSessionIndex();
                    App.LogStartupMessage("BackgroundLoad", $"Phase 2 loaded {_history.Count - beforeCount} records (last 7 days)");
                }
            });
        }
        catch (Exception ex)
        {
            App.LogStartupException("BackgroundLoad.Phase2", ex);
        }

        // 阶段3：加载本月剩余天
        SetLoadPhase(DataLoadPhase.Partial, "正在加载本月数据...");
        try
        {
            await Task.Run(() =>
            {
                var today = DateTime.Today;
                var monthStart = new DateTime(today.Year, today.Month, 1);

                if (monthStart < _loadedRangeStart)
                {
                    var beforeCount = _history.Count;
                    _repository.LoadHistoryForDateRange(_history, monthStart.AddHours(4), _loadedRangeStart);
                    UpdateLoadedRange(monthStart, _loadedRangeEnd);
                    RebuildSessionIndex();
                    App.LogStartupMessage("BackgroundLoad", $"Phase 3 loaded {_history.Count - beforeCount} records (month-to-date remainder)");
                }
            });
        }
        catch (Exception ex)
        {
            App.LogStartupException("BackgroundLoad.Phase3", ex);
        }

        SetLoadPhase(DataLoadPhase.Loaded, null);
        App.LogStartupMessage("BackgroundLoad", $"all phases complete, total _history: {_history.Count}");
    }
    /// <summary>
    /// 返回当前已加载到内存的日期范围（用于 UI 判断哪些日期可以直接从内存查）。
    /// </summary>


    private void RebuildSessionIndex()
    {
        _sessionById.Clear();
        foreach (var record in _history)
        {
            record.EnsureId();
            _sessionById[record.Id] = record;
        }
    }

    private bool IsDateRangeLoaded(DateTime from, DateTime to)
    {
        lock (_loadLock)
        {
            return from >= _loadedRangeStart && to <= _loadedRangeEnd;
        }
    }

    private List<UsageSessionRecord> GetFromMemory(DateTime from, DateTime to)
    {
        var result = new List<UsageSessionRecord>();
        foreach (var r in _history)
        {
            if (UsageTimeRange.Overlaps(r.StartTime, r.EndTime, from, to))
                result.Add(r);
        }
        result.Sort((a, b) => b.StartTime.CompareTo(a.StartTime));
        return result;
    }

    public (DateTime Start, DateTime End) GetLoadedDateRange()
    {
        lock (_loadLock)
        {
            return (_loadedRangeStart, _loadedRangeEnd);
        }
    }
    private void SetLoadPhase(DataLoadPhase phase, string? message)
    {
        _dataLoadPhase = phase;
        DataLoadProgressChanged?.Invoke(this, EventArgs.Empty);
    }
    private void UpdateLoadedRange(DateTime start, DateTime end)
    {
        lock (_loadLock)
        {
            if (start < _loadedRangeStart) _loadedRangeStart = start;
            if (end > _loadedRangeEnd) _loadedRangeEnd = end;
        }
    }
    private bool ApplyLoadedState(UsageTrackerState state)
    {
        var migratedLegacyUnclassified = false;
        if (!string.IsNullOrWhiteSpace(state.Theme))
        {
            Theme = NormalizeTheme(state.Theme);
        }
        if (!string.IsNullOrWhiteSpace(state.ThemeAccentColor))
        {
            ThemeAccentColor = NormalizeThemeAccentColor(state.ThemeAccentColor);
        }
        _themeAccentRecentColors.Clear();
        _themeAccentRecentColors.AddRange(NormalizeThemeAccentRecentColors(state.ThemeAccentRecentColors, ThemeAccentColor));
        if (state.IdleTimeoutMinutes is { } idleTimeoutMinutes)
        {
            // 启动时用户 settings.json 优先，旧 state 不再覆盖空闲判定配置。
        }
        // 启动时用户 settings.json 优先，旧 state 不再覆盖快捷键配置。
        _history.Clear();
        _history.AddRange((state.History ?? Enumerable.Empty<UsageSessionRecord>())
            .Take(MaxImportHistoryRecords)
            .Select(record =>
            {
                if (string.Equals(record.ManualSubject, LegacyAutoUnclassifiedLabel, StringComparison.OrdinalIgnoreCase))
                {
                    record.ManualSubject = ManualUnclassifiedLabel;
                    migratedLegacyUnclassified = true;
                }
                record.EnsureId();
                return record;
            }));
        _activeRecord = state.Active;
        _activeRecord?.EnsureId();
        if (_activeRecord is not null && string.Equals(_activeRecord.ManualSubject, LegacyAutoUnclassifiedLabel, StringComparison.OrdinalIgnoreCase))
        {
            _activeRecord.ManualSubject = ManualUnclassifiedLabel;
            migratedLegacyUnclassified = true;
        }
        if (_activeRecord is not null && DateTime.Now - _activeRecord.StartTime > IdleTimeout)
        {
            _activeRecord = null;
        }
        _manualSubjects.Clear();
        foreach (var pair in state.ManualSubjects ?? new Dictionary<string, string>())
        {
            var manualSubject = string.Equals(pair.Value, LegacyAutoUnclassifiedLabel, StringComparison.OrdinalIgnoreCase)
                ? ManualUnclassifiedLabel
                : pair.Value;
            if (!string.Equals(manualSubject, pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                migratedLegacyUnclassified = true;
            }
            _manualSubjects[pair.Key] = manualSubject;
        }
        _subjectKeywordRules.Clear();
        foreach (var pair in state.SubjectKeywordRules ?? new Dictionary<string, List<string>>())
        {
            var keywords = pair.Value
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (keywords.Count > 0)
            {
                _subjectKeywordRules[pair.Key] = keywords;
            }
        }
        if (state.SubjectDefinitions is { Count: > 0 })
        {
            _subjectDefinitions.Clear();
            foreach (var definition in state.SubjectDefinitions.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
            {
                _subjectDefinitions.Add(definition.Normalize());
            }
        }
        return migratedLegacyUnclassified;
    }
    private bool LoadMissingHistoryFromBackups()
    {
        var bakPath = Path.Combine(_dataDirectory, "usage-tracker.db.bak");
        if (!File.Exists(bakPath))
            return false;

        try
        {
            var merged = _repository.MergeFromBackupDb(bakPath);
            if (merged > 0)
            {
                App.LogStartupMessage("LoadMissingHistoryFromBackups", $"merged {merged} records from .bak");
                return true;
            }
        }
        catch (Exception ex)
        {
            App.LogStartupException("LoadMissingHistoryFromBackups", ex);
        }
        return false;
    }
    private void EnsureDatabase()
    {
        _repository.EnsureDatabase();
    }
    private bool MigrateJsonHistoryToDatabase()
    {
        return false;
    }
    private void LoadHistoryFromDatabase()
    {
        _repository.LoadHistoryFromDatabase(_history, out _activeRecord);
    }
    private void WriteHistoryToDatabase(UsageTrackerState state)
    {
        _repository.WriteStateToDisk(state);
    }
    private bool LoadSettingsFromDisk()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return false;
        }
        try
        {
            using var stream = File.OpenRead(_settingsFilePath);
            var settings = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerSettings);
            if (settings is null)
            {
                return false;
            }
            ApplySettings(settings);
            return true;
        }
        catch
        {
            return false;
        }
    }
    private void ReloadSubjectDefinitionsFromSettingsFile()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return;
        }
        try
        {
            using var stream = File.OpenRead(_settingsFilePath);
            var settings = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerSettings);
            if (settings?.SubjectDefinitions is not { Count: > 0 })
            {
                TryRecoverSubjectDefinitionsFromLatestBackup();
                return;
            }
            _subjectDefinitions.Clear();
            foreach (var definition in settings.SubjectDefinitions.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
            {
                _subjectDefinitions.Add(definition.Normalize());
            }
        }
        catch (Exception ex)
        {
            App.LogStartupException("ReloadSubjectDefinitionsFromSettingsFile", ex);
        }
    }

    private bool TryRecoverSubjectDefinitionsFromLatestBackup()
    {
        try
        {
            var backupDirectory = Path.Combine(_dataDirectory, "backups");
            if (!Directory.Exists(backupDirectory))
            {
                return false;
            }

            foreach (var file in Directory.EnumerateFiles(backupDirectory, "settings-*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    var backup = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerSettings);
                    if (backup?.SubjectDefinitions is not { Count: > 0 })
                    {
                        continue;
                    }

                    _subjectDefinitions.Clear();
                    foreach (var definition in backup.SubjectDefinitions.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
                    {
                        _subjectDefinitions.Add(definition.Normalize());
                    }
                    App.LogStartupMessage("SubjectDefinitions.Recovered", file);
                    return _subjectDefinitions.Count > 0;
                }
                catch (Exception ex)
                {
                    App.LogStartupException("SubjectDefinitions.RecoverBackup", ex);
                }
            }
        }
        catch (Exception ex)
        {
            App.LogStartupException("SubjectDefinitions.Recover", ex);
        }
        return false;
    }
    private void ApplySettings(UsageTrackerSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Theme))
        {
            Theme = NormalizeTheme(settings.Theme);
        }
        if (!string.IsNullOrWhiteSpace(settings.Language))
        {
            Language = string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        }
        if (!string.IsNullOrWhiteSpace(settings.ThemeAccentColor))
        {
            ThemeAccentColor = NormalizeThemeAccentColor(settings.ThemeAccentColor);
        }
        ApplyThemeAccentSlots(settings.ThemeAccentSlots);
        ApplyParallelActivityWhitelistProcesses(settings.ParallelActivityWhitelistProcesses);
        _themeAccentRecentColors.Clear();
        _themeAccentRecentColors.AddRange(NormalizeThemeAccentRecentColors(settings.ThemeAccentRecentColors, ThemeAccentColor));
        if (settings.IdleTimeoutMinutes is { } idleTimeoutMinutes)
        {
            IdleTimeoutMinutes = NormalizeIdleTimeoutMinutes(idleTimeoutMinutes);
        }
        if (settings.ManualIdleShortcutText is not null)
        {
            ManualIdleShortcutText = settings.ManualIdleShortcutText.Trim();
        }
        if (!string.IsNullOrWhiteSpace(settings.SubjectDeleteBehavior))
        {
            SubjectDeleteBehavior = NormalizeSubjectDeleteBehavior(settings.SubjectDeleteBehavior);
        }
        _manualSubjects.Clear();
        foreach (var pair in settings.ManualSubjects ?? new Dictionary<string, string>())
        {
            _manualSubjects[pair.Key] = string.Equals(pair.Value, LegacyAutoUnclassifiedLabel, StringComparison.OrdinalIgnoreCase)
                ? ManualUnclassifiedLabel
                : pair.Value;
        }
        _subjectKeywordRules.Clear();
        foreach (var pair in settings.SubjectKeywordRules ?? new Dictionary<string, List<string>>())
        {
            var keywords = pair.Value
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (keywords.Count > 0)
            {
                _subjectKeywordRules[pair.Key] = keywords;
            }
        }
        if (settings.SubjectDefinitions is { Count: > 0 })
        {
            _subjectDefinitions.Clear();
            foreach (var definition in settings.SubjectDefinitions.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
            {
                _subjectDefinitions.Add(definition.Normalize());
            }
        }
        else if (_subjectDefinitions.Count == 0)
        {
            TryRecoverSubjectDefinitionsFromLatestBackup();
        }
    }
    private UsageTrackerSettings CreateSettingsSnapshot(bool preserveExistingShortcut = false)
    {
        if (_subjectDefinitions.Count == 0)
        {
            ReloadSubjectDefinitionsFromSettingsFile();
        }

        string shortcutText = ManualIdleShortcutText;
        int idleTimeoutMinutes = IdleTimeoutMinutes;
        Dictionary<string, List<string>>? existingSubjectKeywordRules = null;
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                using var stream = File.OpenRead(_settingsFilePath);
                var existing = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerSettings);
                if (preserveExistingShortcut && existing?.ManualIdleShortcutText is not null)
                {
                    shortcutText = existing.ManualIdleShortcutText.Trim();
                }
                if (existing?.IdleTimeoutMinutes is { } existingIdleTimeoutMinutes)
                {
                    idleTimeoutMinutes = NormalizeIdleTimeoutMinutes(existingIdleTimeoutMinutes);
                }
                if (existing?.SubjectKeywordRules is { Count: > 0 })
                {
                    existingSubjectKeywordRules = NormalizeSubjectKeywordRules(existing.SubjectKeywordRules);
                }
            }
            catch (Exception ex)
            {
                App.LogStartupException("UsageTrackerService.CreateSettingsSnapshot.ReadExistingSettings", ex);
            }
        }

        return new UsageTrackerSettings
        {
            ManualSubjects = new Dictionary<string, string>(_manualSubjects, StringComparer.OrdinalIgnoreCase),
            SubjectKeywordRules = existingSubjectKeywordRules ?? SnapshotSubjectKeywordRules(),
            SubjectDefinitions = _subjectDefinitions.Select(x => x.Clone()).ToList(),
            SubjectOptions = null,
            Theme = Theme,
            Language = Language,
            ThemeAccentColor = ThemeAccentColor,
            ThemeAccentRecentColors = _themeAccentRecentColors.ToList(),
            ThemeAccentSlots = _themeAccentSlots.ToList(),
            ParallelActivityWhitelistProcesses = _parallelActivityWhitelistProcesses.ToList(),
            IdleTimeoutMinutes = idleTimeoutMinutes,
            ManualIdleShortcutText = shortcutText,
            SubjectDeleteBehavior = SubjectDeleteBehavior.ToString()
        };
    }
    private UsageTrackerState CreateUsageDataSnapshot()
    {
        foreach (var record in _history)
        {
            record.EnsureId();
        }
        _activeRecord?.EnsureId();
        return new UsageTrackerState
        {
            Active = _activeRecord?.Clone(),
            History = _history.OrderByDescending(x => x.StartTime).Select(x => x.Clone()).ToList(),
            ManualSubjects = new Dictionary<string, string>(_manualSubjects, StringComparer.OrdinalIgnoreCase),
            SubjectOptions = null
        };
    }

    private UsageTrackerState CreateSettingsDataSnapshot()
    {
        if (_subjectDefinitions.Count == 0)
        {
            ReloadSubjectDefinitionsFromSettingsFile();
        }

        return new UsageTrackerState
        {
            SubjectKeywordRules = _subjectKeywordRules.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            SubjectDefinitions = _subjectDefinitions.Select(x => x.Clone()).ToList(),
            SubjectOptions = null,
            Theme = Theme,
            ThemeAccentColor = ThemeAccentColor,
            ThemeAccentRecentColors = _themeAccentRecentColors.ToList(),
            ThemeAccentSlots = _themeAccentSlots.ToList(),
            ParallelActivityWhitelistProcesses = _parallelActivityWhitelistProcesses.ToList(),
            IdleTimeoutMinutes = IdleTimeoutMinutes,
            ManualIdleShortcutText = ManualIdleShortcutText,
            SubjectDeleteBehavior = SubjectDeleteBehavior.ToString()
        };
    }

    private UsageTrackerState CreateStateSnapshot()
    {
        foreach (var record in _history)
        {
            record.EnsureId();
        }
        _activeRecord?.EnsureId();
        return new UsageTrackerState
        {
            Active = _activeRecord?.Clone(),
            History = _history.OrderByDescending(x => x.StartTime).Select(x => x.Clone()).ToList(),
            ManualSubjects = new Dictionary<string, string>(_manualSubjects, StringComparer.OrdinalIgnoreCase),
            SubjectKeywordRules = _subjectKeywordRules.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            SubjectDefinitions = _subjectDefinitions.Select(x => x.Clone()).ToList(),
            SubjectOptions = null,
            Theme = Theme,
            ThemeAccentColor = ThemeAccentColor,
            ThemeAccentRecentColors = _themeAccentRecentColors.ToList(),
            ThemeAccentSlots = _themeAccentSlots.ToList(),
            ParallelActivityWhitelistProcesses = _parallelActivityWhitelistProcesses.ToList(),
            IdleTimeoutMinutes = IdleTimeoutMinutes,
            ManualIdleShortcutText = ManualIdleShortcutText,
            SubjectDeleteBehavior = SubjectDeleteBehavior.ToString()
        };
    }
    private void MarkLoadedHistoryDirty()
    {
        lock (_saveLock)
        {
            foreach (var record in _history)
            {
                record.EnsureId();
                _dirtyRecordIds.Add(record.Id);
                _dirtyRecords[record.Id] = record;
            }
        }
    }
    private void SaveState()
    {
        MarkLoadedHistoryDirty();
        lock (_saveLock)
        {
            _settingsDirty = true;
            _activeSessionDirty = true;
            _fullSyncNeeded = false;
        }
        QueueIncrementalSave();
        SaveSettingsImmediately();
    }
    private void SaveSettingsImmediately()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var settings = CreateSettingsSnapshot(preserveExistingShortcut: true);
            WriteSettingsAtomically(_settingsFilePath, settings);
        }
        catch (Exception ex)
        {
            App.LogStartupException("UsageTrackerService.SaveSettingsImmediately", ex);
            throw;
        }
    }
    private void SaveManualIdleShortcutImmediately()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            UsageTrackerSettings settings;
            if (File.Exists(_settingsFilePath))
            {
                using var stream = File.OpenRead(_settingsFilePath);
                settings = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerSettings) ?? new UsageTrackerSettings();
            }
            else
            {
                settings = new UsageTrackerSettings();
            }

            settings.ManualIdleShortcutText = ManualIdleShortcutText;
            WriteSettingsAtomically(_settingsFilePath, settings);

            using var verify = File.OpenRead(_settingsFilePath);
            var saved = JsonSerializer.Deserialize(verify, UsageTrackerJsonContext.Default.UsageTrackerSettings);
            var savedShortcut = saved?.ManualIdleShortcutText?.Trim() ?? string.Empty;
            if (!string.Equals(savedShortcut, ManualIdleShortcutText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"ManualIdleShortcutText write verification failed. expected='{ManualIdleShortcutText}', actual='{savedShortcut}'");
            }
        }
        catch (Exception ex)
        {
            App.LogStartupException("UsageTrackerService.SaveManualIdleShortcutImmediately", ex);
            throw;
        }
    }

    private void SaveIdleTimeoutImmediately()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            UsageTrackerSettings settings;
            if (File.Exists(_settingsFilePath))
            {
                using var stream = File.OpenRead(_settingsFilePath);
                settings = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerSettings) ?? new UsageTrackerSettings();
            }
            else
            {
                settings = new UsageTrackerSettings();
            }

            settings.IdleTimeoutMinutes = IdleTimeoutMinutes;
            WriteSettingsAtomically(_settingsFilePath, settings);

            using var verify = File.OpenRead(_settingsFilePath);
            var saved = JsonSerializer.Deserialize(verify, UsageTrackerJsonContext.Default.UsageTrackerSettings);
            var savedMinutes = saved?.IdleTimeoutMinutes ?? DefaultIdleTimeoutMinutes;
            if (savedMinutes != IdleTimeoutMinutes)
            {
                throw new InvalidOperationException($"IdleTimeoutMinutes write verification failed. expected='{IdleTimeoutMinutes}', actual='{savedMinutes}'");
            }
        }
        catch (Exception ex)
        {
            App.LogStartupException("UsageTrackerService.SaveIdleTimeoutImmediately", ex);
            throw;
        }
    }
    private static Dictionary<string, List<string>> NormalizeSubjectKeywordRules(Dictionary<string, List<string>>? rules)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in rules ?? new Dictionary<string, List<string>>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            var keywords = pair.Value
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (keywords.Count > 0)
            {
                result[pair.Key.Trim()] = keywords;
            }
        }

        return result;
    }

    private Dictionary<string, List<string>> SnapshotSubjectKeywordRules()
    {
        return NormalizeSubjectKeywordRules(_subjectKeywordRules.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase));
    }

    private void SaveSubjectKeywordRulesImmediately()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            UsageTrackerSettings settings;
            if (File.Exists(_settingsFilePath))
            {
                using var stream = File.OpenRead(_settingsFilePath);
                settings = JsonSerializer.Deserialize(stream, UsageTrackerJsonContext.Default.UsageTrackerSettings) ?? new UsageTrackerSettings();
            }
            else
            {
                settings = CreateSettingsSnapshot(preserveExistingShortcut: true);
            }

            settings.SubjectKeywordRules = SnapshotSubjectKeywordRules();
            WriteSettingsAtomically(_settingsFilePath, settings);

            using var verify = File.OpenRead(_settingsFilePath);
            var saved = JsonSerializer.Deserialize(verify, UsageTrackerJsonContext.Default.UsageTrackerSettings);
            var savedRules = NormalizeSubjectKeywordRules(saved?.SubjectKeywordRules);
            foreach (var pair in SnapshotSubjectKeywordRules())
            {
                if (!savedRules.TryGetValue(pair.Key, out var savedKeywords)
                    || !pair.Value.All(keyword => savedKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)))
                {
                    throw new IOException($"关键词规则保存校验失败：{pair.Key}");
                }
            }

            App.LogStartupMessage("SubjectKeywordRules.Save", $"saved {_subjectKeywordRules.Count} subject keyword rule group(s) to settings.json");
        }
        catch (Exception ex)
        {
            App.LogStartupException("UsageTrackerService.SaveSubjectKeywordRulesImmediately", ex);
            throw;
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
    private void SaveStateImmediately()
    {
        MarkLoadedHistoryDirty();
        lock (_saveLock)
        {
            _settingsDirty = true;
            _activeSessionDirty = true;
            _fullSyncNeeded = false;
        }
        QueueIncrementalSave();
    }
    private void SaveSessionIncremental(UsageSessionRecord record)
    {
        record.EnsureId();
        lock (_saveLock)
        {
            _dirtyRecordIds.Add(record.Id);
            _dirtyRecords[record.Id] = record;
            _activeSessionDirty = true;
        }
        QueueIncrementalSave();
    }
    private void SaveActiveSessionIncremental()
    {
        lock (_saveLock)
        {
            _activeSessionDirty = true;
        }
        QueueIncrementalSave();
    }
    private void SaveDeleteIncremental(UsageSessionRecord record)
    {
        record.EnsureId();
        lock (_saveLock)
        {
            _deletedRecordIds.Add(record.Id);
            _activeSessionDirty = true;
        }
        QueueIncrementalSave();
    }
    private void MarkRecordDirty(UsageSessionRecord record)
    {
        record.EnsureId();
        lock (_saveLock)
        {
            _dirtyRecordIds.Add(record.Id);
            _dirtyRecords[record.Id] = record;
        }
    }
    private void MarkActiveSessionDirty()
    {
        lock (_saveLock)
        {
            _activeSessionDirty = true;
        }
    }
    private void MarkRecordDeleted(string recordId)
    {
        lock (_saveLock)
        {
            _deletedRecordIds.Add(recordId);
        }
    }
    private void QueueIncrementalSave()
    {
        var request = BuildSaveRequest();
        lock (_saveLock)
        {
            _pendingSaveRequest = request;
            if (_saveWorkerRunning)
            {
                return;
            }
            _saveWorkerRunning = true;
            _saveIdleEvent.Reset();
        }
        _ = Task.Run(ProcessSaveQueue);
    }
    private IncrementalSaveRequest BuildSaveRequest()
    {
        lock (_saveLock)
        {
            var dirtyRecords = new List<UsageSessionRecord>();
            foreach (var id in _dirtyRecordIds)
            {
                if (_dirtyRecords.TryGetValue(id, out var record))
                {
                    dirtyRecords.Add(record.Clone());
                }
                else
                {
                    // Fallback: try to find in _history buffer
                    var recordFromHistory = _history.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (recordFromHistory is not null)
                    {
                        dirtyRecords.Add(recordFromHistory.Clone());
                        _dirtyRecords[id] = recordFromHistory;
                    }
                }
            }
            var request = new IncrementalSaveRequest
            {
                DirtyRecords = dirtyRecords,
                DeletedIds = _deletedRecordIds.ToList(),
                ActiveRecord = _activeSessionDirty ? _activeRecord?.Clone() : null,
                UpdateActiveSession = _activeSessionDirty,
                Settings = _settingsDirty ? CreateSettingsSnapshot() : null,
                FullSync = false
            };
            if (_fullSyncNeeded)
            {
                // 分阶段加载架构下，_history 只代表当前已加载片段，不再允许据此执行全量同步。
                _fullSyncNeeded = false;
            }
            return request;
        }
    }
    private void QueueStateForSave(UsageTrackerState state)
    {
        // Legacy compatibility: mark full sync needed
        lock (_saveLock)
        {
            _fullSyncNeeded = true;
            _activeSessionDirty = true;
            _settingsDirty = true;
        }
        QueueIncrementalSave();
    }
    private void ProcessSaveQueue()
    {
        IncrementalSaveRequest? request;
        lock (_saveLock)
        {
            request = _pendingSaveRequest;
            _pendingSaveRequest = null;
        }
        if (request is null)
        {
            lock (_saveLock)
            {
                _saveWorkerRunning = false;
                _saveIdleEvent.Set();
            }
            return;
        }
        try
        {
            WriteIncrementalToDatabase(request);
            // 保存成功，清空脏标记
            lock (_saveLock)
            {
                _dirtyRecordIds.Clear();
                _deletedRecordIds.Clear();
                _dirtyRecords.Clear();
                _activeSessionDirty = false;
                _settingsDirty = false;
                _fullSyncNeeded = false;
            }
        }
        catch
        {
            // 保存失败，保留请求以便下次重试
            lock (_saveLock)
            {
                _pendingSaveRequest = request;
            }
        }
        finally
        {
            lock (_saveLock)
            {
                _saveWorkerRunning = false;
                _saveIdleEvent.Set();
            }
        }
    }
    private void WriteIncrementalToDatabase(IncrementalSaveRequest request)
    {
        _repository.WriteIncrementalToDatabase(request);
    }
    private void MergeManualSubjects(Dictionary<string, string>? incoming)
    {
        if (incoming is null)
        {
            return;
        }
        foreach (var pair in incoming.Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value)))
        {
            _manualSubjects.TryAdd(pair.Key, pair.Value);
        }
    }
    private void MergeSubjectKeywordRules(Dictionary<string, List<string>>? incoming)
    {
        if (incoming is null)
        {
            return;
        }
        foreach (var pair in incoming.Where(x => !string.IsNullOrWhiteSpace(x.Key)))
        {
            if (!_subjectKeywordRules.TryGetValue(pair.Key, out var keywords))
            {
                keywords = new List<string>();
                _subjectKeywordRules[pair.Key] = keywords;
            }
            foreach (var keyword in pair.Value.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
            {
                if (!keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                {
                    keywords.Add(keyword);
                }
            }
        }
    }
    private void MergeSubjectDefinitions(List<SubjectDefinition>? incomingDefinitions, List<string>? incomingOptions)
    {
        if (incomingDefinitions is not { Count: > 0 })
        {
            return;
        }

        foreach (var definition in incomingDefinitions.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Normalize()))
        {
            MergeSubjectDefinition(definition);
        }
    }
    private void MergeSubjectDefinition(SubjectDefinition incoming)
    {
        var existing = FindSubject(incoming.Name);
        if (existing is null)
        {
            _subjectDefinitions.Add(incoming.Clone());
            return;
        }
        foreach (var incomingParent in incoming.Parents)
        {
            var existingParent = existing.Parents.FirstOrDefault(x => string.Equals(x.Name, incomingParent.Name, StringComparison.OrdinalIgnoreCase));
            if (existingParent is null)
            {
                existing.Parents.Add(incomingParent.Clone());
                continue;
            }
            foreach (var child in incomingParent.Children)
            {
                if (!existingParent.Children.Contains(child, StringComparer.OrdinalIgnoreCase))
                {
                    existingParent.Children.Add(child);
                }
            }
        }
    }
    private void RaiseChanged(UsageSession? closedSession, UsageSession? activeSession)
    {
        SessionChanged?.Invoke(this, new SessionChangedEventArgs(closedSession, activeSession));
    }
    private string? ResolveSubjectForNewSession(UsageSessionRecord record)
    {
        var primaryTitleSubject = MatchPrimaryTitleSubjectByKeyword(record.WindowTitle);
        if (!string.IsNullOrWhiteSpace(primaryTitleSubject))
        {
            return primaryTitleSubject;
        }

        _manualSubjects.TryGetValue(BuildClassificationKey(record.ProcessName, record.WindowTitle), out var subject);
        return subject ?? MatchSubjectByKeyword(record.ProcessName, record.WindowTitle);
    }
    private string? MatchSubjectByKeyword(string processName, string windowTitle)
    {
        return SearchExpressionMatcher.ResolveSubjectByKeywordRules(_subjectDefinitions, _subjectKeywordRules, processName, windowTitle);
    }
    private string? MatchPrimaryTitleSubjectByKeyword(string windowTitle)
    {
        return SearchExpressionMatcher.ResolvePrimaryTitleSubjectByKeywordRules(_subjectDefinitions, _subjectKeywordRules, windowTitle);
    }
    private void RemoveInvalidKeywordRules()
    {
        var validSubjects = _subjectDefinitions
            .SelectMany(x => x.GetAllSubjectNames())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var subject in _subjectKeywordRules.Keys.Where(x => !validSubjects.Contains(x)).ToList())
        {
            _subjectKeywordRules.Remove(subject);
        }
    }
    private bool SubjectExists(string subject)
    {
        return _subjectDefinitions.Any(x => x.GetAllSubjectNames().Contains(subject, StringComparer.OrdinalIgnoreCase));
    }
    private SubjectDefinition? FindSubject(string subject)
    {
        return _subjectDefinitions.FirstOrDefault(x => string.Equals(x.Name, subject, StringComparison.OrdinalIgnoreCase));
    }
    private SubjectParentDefinition? FindParentSubject(string majorSubject, string parentSubject)
    {
        return FindSubject(majorSubject)?.Parents.FirstOrDefault(x => string.Equals(x.Name, parentSubject, StringComparison.OrdinalIgnoreCase));
    }
    private void ReclassifyRemovedSubjects(IEnumerable<string> subjectNames, string? promotedSubject)
    {
        var subjectSet = new HashSet<string>(subjectNames.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        if (subjectSet.Count == 0)
        {
            return;
        }
        var matchingKeys = _manualSubjects
            .Where(x => subjectSet.Contains(x.Value))
            .Select(x => x.Key)
            .ToList();
        foreach (var key in matchingKeys)
        {
            _manualSubjects.Remove(key);
        }
        foreach (var record in _history.Where(record => subjectSet.Contains(record.ManualSubject ?? string.Empty)))
        {
            record.ManualSubject = promotedSubject ?? MatchSubjectByKeyword(record.ProcessName, record.WindowTitle);
        }
        if (_activeRecord is not null && subjectSet.Contains(_activeRecord.ManualSubject ?? string.Empty))
        {
            _activeRecord.ManualSubject = promotedSubject ?? MatchSubjectByKeyword(_activeRecord.ProcessName, _activeRecord.WindowTitle);
        }
    }
    private void RenameSubjectReferences(string oldSubject, string newSubject)
    {
        if (_subjectKeywordRules.Remove(oldSubject, out var keywords))
        {
            if (!_subjectKeywordRules.TryGetValue(newSubject, out var targetKeywords))
            {
                _subjectKeywordRules[newSubject] = keywords;
            }
            else
            {
                foreach (var keyword in keywords.Where(x => !targetKeywords.Contains(x, StringComparer.OrdinalIgnoreCase)))
                {
                    targetKeywords.Add(keyword);
                }
            }
        }
        foreach (var key in _manualSubjects.Where(x => string.Equals(x.Value, oldSubject, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToList())
        {
            _manualSubjects[key] = newSubject;
        }
        foreach (var record in _history.Where(x => string.Equals(x.ManualSubject, oldSubject, StringComparison.OrdinalIgnoreCase)))
        {
            record.ManualSubject = newSubject;
            MarkRecordDirty(record);
        }
        if (_activeRecord is not null && string.Equals(_activeRecord.ManualSubject, oldSubject, StringComparison.OrdinalIgnoreCase))
        {
            _activeRecord.ManualSubject = newSubject;
            MarkActiveSessionDirty();
        }
        var dbRecords = _repository.GetRecordsByManualSubject(oldSubject);
        foreach (var record in dbRecords)
        {
            record.ManualSubject = newSubject;
            MarkRecordDirty(record);
        }
    }

    private void ClearManualAssignments(IEnumerable<string> subjectNames)
    {
        var subjectSet = new HashSet<string>(subjectNames.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
        if (subjectSet.Count == 0)
        {
            return;
        }
        var matchingKeys = _manualSubjects
            .Where(x => subjectSet.Contains(x.Value))
            .Select(x => x.Key)
            .ToList();
        foreach (var key in matchingKeys)
        {
            _manualSubjects.Remove(key);
        }
        if (_activeRecord is not null && _activeRecord.ManualSubject is not null && subjectSet.Contains(_activeRecord.ManualSubject))
        {
            _activeRecord.ManualSubject = null;
        }
    }
    private static string BuildClassificationKey(string processName, string windowTitle)
    {
        return $"{processName}|{windowTitle}";
    }
    private static bool SessionMatches(UsageSessionRecord record, string processName, string windowTitle)
    {
        return record.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)
            && record.WindowTitle.Equals(windowTitle, StringComparison.Ordinal);
    }
    private static string BuildSessionKey(UsageSessionRecord record)
    {
        return BuildSessionKey(record.ProcessName, record.WindowTitle, record.StartTime);
    }
    private static string BuildSessionKey(string processName, string windowTitle, DateTime startTime)
    {
        return $"{processName}|{windowTitle}|{startTime:O}";
    }
    public string GetStartupTargetPath()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            return executablePath;
        }
        return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }
    public string GetStartupCommand()
    {
        return BuildStartupCommand();
    }
    private string BuildStartupCommand()
    {
        var executablePath = GetStartupTargetPath();
        return string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : $"\"{executablePath}\" {StartupCompactArgument}";
    }
    private UsageSession ToSession(UsageSessionRecord record)
    {
        return ToSession(record, ResolveSubjectForDisplay(record));
    }
    private string? ResolveSubjectForDisplay(UsageSessionRecord record)
    {
        var primaryTitleSubject = MatchPrimaryTitleSubjectByKeyword(record.WindowTitle);
        if (!string.IsNullOrWhiteSpace(primaryTitleSubject))
        {
            return primaryTitleSubject;
        }

        if (!string.IsNullOrWhiteSpace(record.ManualSubject))
        {
            return record.ManualSubject;
        }
        return ResolveSubjectForNewSession(record);
    }
    private void RefreshTodayUnmanualSnapshots()
    {
        foreach (var record in _history.Where(record => record.StartTime.Date == DateTime.Today && string.IsNullOrWhiteSpace(record.ManualSubject)))
        {
            record.ManualSubject = ResolveSubjectForNewSession(record);
        }
        if (_activeRecord is not null && string.IsNullOrWhiteSpace(_activeRecord.ManualSubject))
        {
            _activeRecord.ManualSubject = ResolveSubjectForNewSession(_activeRecord);
        }
    }
    private void ApplyDailySubjectSnapshots(DateTime now)
    {
        var snapshotDate = now.Date;
        if (_appliedSnapshotDates.Contains(snapshotDate))
        {
            return;
        }
        _appliedSnapshotDates.Add(snapshotDate);
        foreach (var record in _history.Where(record => record.StartTime.Date < snapshotDate && string.IsNullOrWhiteSpace(record.ManualSubject)))
        {
            record.ManualSubject = ResolveSubjectForNewSession(record);
        }
    }
    private static UsageSession ToSession(UsageSessionRecord record, string? manualSubject)
    {
        return new UsageSession
        {
            Id = record.Id,
            ProcessName = record.ProcessName,
            WindowTitle = record.WindowTitle,
            StartTime = record.StartTime,
            EndTime = record.EndTime ?? record.StartTime,
            ManualSubject = manualSubject,
            ParallelActivities = record.ParallelActivities?.Select(x => x.Clone()).ToList() ?? []
        };
    }
    private UsageSession ToActiveSession(UsageSessionRecord record, DateTime now, string? manualSubject)
    {
        record.EnsureId();
        return new UsageSession
        {
            Id = record.Id,
            ProcessName = record.ProcessName,
            WindowTitle = record.WindowTitle,
            StartTime = record.StartTime,
            EndTime = now,
            ManualSubject = manualSubject,
            ParallelActivities = _lastParallelActivitySnapshot is null ? [] : [_lastParallelActivitySnapshot]
        };
    }
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        _pollTimer.Stop();
        _pollTimer.Tick -= PollTimer_Tick;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        _idleClickTimer.Stop();
        _idleClickTimer.Tick -= IdleClickTimer_Tick;
        CloseActiveSession(DateTime.Now);
        // 同步写入（关机路径下 Task.Run 可能来不及调度，直接写 DB）
        try
        {
            var request = BuildSaveRequest();
            _repository.WriteIncrementalToDatabase(request);
            App.LogStartupMessage("UsageTrackerService.Dispose", "sync save completed");
        }
        catch (Exception ex)
        {
            App.LogStartupException("UsageTrackerService.Dispose.SyncSave", ex);
            // 降级：走异步队列 + 等待
            SaveStateImmediately();
            _saveIdleEvent.Wait(TimeSpan.FromSeconds(5));
        }
        _saveIdleEvent.Dispose();
    }
    private void StartIdleClickDetection()
    {
        _wasMouseButtonDown = IsAnyMouseButtonDown();
        if (!_idleClickTimer.IsEnabled)
        {
            _idleClickTimer.Start();
        }
    }
    private void IdleClickTimer_Tick(object? sender, EventArgs e)
    {
        if (_idleAwaitingClickStartedAt is null)
        {
            _idleClickTimer.Stop();
            _wasMouseButtonDown = IsAnyMouseButtonDown();
            return;
        }
        var isMouseButtonDown = IsAnyMouseButtonDown();
        if (isMouseButtonDown && !_wasMouseButtonDown)
        {
            _manualIdleStartedAt = null;
            _idleAwaitingClickStartedAt = null;
            _idleClickTimer.Stop();
        }
        _wasMouseButtonDown = isMouseButtonDown;
    }
    private static bool IsAnyMouseButtonDown()
    {
        return IsKeyDown(VkLButton)
            || IsKeyDown(VkRButton)
            || IsKeyDown(VkMButton)
            || IsKeyDown(VkXButton1)
            || IsKeyDown(VkXButton2);
    }
    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
    private const int VkXButton1 = 0x05;
    private const int VkXButton2 = 0x06;
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

public sealed record SessionChangedEventArgs(UsageSession? ClosedSession, UsageSession? ActiveSession);
public sealed record UsageOverview(TimeSpan Total, TimeSpan AverageDaily, TimeSpan Week);
public enum ImportConflictStrategy
{
    KeepLocal,
    UseIncoming,
    NewOnly
}

public enum ImportPayloadKind
{
    Usage,
    Settings,
    Full
}

public enum ImportDataMode
{
    ViewOnly,
    Merge,
    Replace
}

public enum ImportSettingsMode
{
    None,
    ViewOnly,
    RulesOnly,
    BasicOnly,
    Merge,
    Replace
}

public sealed record ImportPreview(int NonConflictCount, int ConflictCount, int TotalCount);
public sealed record ImportResult(int ImportedCount, int ConflictCount, int TotalCount, int OverwrittenCount = 0, string? BackupPath = null, int SettingsChangedCount = 0);
public sealed record ImportPackagePreview(
    string FilePath,
    ImportPayloadKind Kind,
    UsageTrackerState State,
    ImportPreview DataPreview,
    int TotalRecords,
    DateTime? EarliestStartTime,
    DateTime? LatestStartTime,
    int SubjectDefinitionCount,
    int SubjectKeywordRuleCount,
    string? Theme,
    string? ThemeAccentColor,
    int? IdleTimeoutMinutes,
    string? ManualIdleShortcutText,
    string? SubjectDeleteBehavior);
public enum SubjectDeleteBehavior
{
    MatchRules,
    PromoteToParent
}

public sealed class UsageTrackerSettings
{
    public Dictionary<string, string>? ManualSubjects { get; set; }
    public Dictionary<string, List<string>>? SubjectKeywordRules { get; set; }
    public List<string>? SubjectOptions { get; set; }
    public List<SubjectDefinition>? SubjectDefinitions { get; set; }
    public string? Theme { get; set; }
    public string? Language { get; set; }
    public string? ThemeAccentColor { get; set; }
    public List<string>? ThemeAccentRecentColors { get; set; }
    public List<string>? ThemeAccentSlots { get; set; }
    public List<string>? ParallelActivityWhitelistProcesses { get; set; }
    public int? IdleTimeoutMinutes { get; set; }
    public string? ManualIdleShortcutText { get; set; }
    public string? SubjectDeleteBehavior { get; set; }
}
public sealed class UsageTrackerState
{
    public UsageSessionRecord? Active { get; set; }
    public List<UsageSessionRecord>? History { get; set; }
    public Dictionary<string, string>? ManualSubjects { get; set; }
    public Dictionary<string, List<string>>? SubjectKeywordRules { get; set; }
    public List<string>? SubjectOptions { get; set; }
    public List<SubjectDefinition>? SubjectDefinitions { get; set; }
    public string? Theme { get; set; }
    public string? ThemeAccentColor { get; set; }
    public List<string>? ThemeAccentRecentColors { get; set; }
    public List<string>? ThemeAccentSlots { get; set; }
    public List<string>? ParallelActivityWhitelistProcesses { get; set; }
    public int? IdleTimeoutMinutes { get; set; }
    public string? ManualIdleShortcutText { get; set; }
    public string? SubjectDeleteBehavior { get; set; }
}
public sealed class SubjectDefinition
{
    private const string LegacyMajorSubjectName = "默认大类";
    public string Name { get; set; } = string.Empty;
    public List<SubjectParentDefinition> Parents { get; set; } = new();
    public List<string> Children { get; set; } = new();
    public IEnumerable<string> GetAllSubjectNames()
    {
        yield return Name;
        foreach (var parent in Parents)
        {
            foreach (var subject in parent.GetAllSubjectNames())
            {
                yield return subject;
            }
        }
    }
    public SubjectDefinition Clone()
    {
        return new SubjectDefinition
        {
            Name = Name,
            Parents = Parents.Select(x => x.Clone()).ToList(),
            Children = Children.ToList()
        };
    }
    public SubjectDefinition Normalize()
    {
        Name = Name.Trim();
        Parents = Parents
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => x.Normalize())
            .DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Children = Children
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Parents.Count == 0 && Children.Count > 0)
        {
            Parents.Add(new SubjectParentDefinition
            {
                Name = Name,
                Children = Children.ToList()
            }.Normalize());
            Name = LegacyMajorSubjectName;
            Children.Clear();
        }
        return this;
    }
}
public sealed class SubjectParentDefinition
{
    public string Name { get; set; } = string.Empty;
    public List<string> Children { get; set; } = new();
    public IEnumerable<string> GetAllSubjectNames()
    {
        yield return Name;
        foreach (var child in Children)
        {
            yield return child;
        }
    }
    public SubjectParentDefinition Clone()
    {
        return new SubjectParentDefinition
        {
            Name = Name,
            Children = Children.ToList()
        };
    }
    public SubjectParentDefinition Normalize()
    {
        Name = Name.Trim();
        Children = Children
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return this;
    }
}
public sealed class UsageSessionRecord
{
    public string Id { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ManualSubject { get; set; }
    public List<ParallelActivitySnapshot>? ParallelActivities { get; set; }
    public DateTime? LastCapturedAt { get; set; }
    public void EnsureId()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = BuildStableId(ProcessName, WindowTitle, StartTime);
        }
    }
    public UsageSessionRecord Clone()
    {
        EnsureId();
        return new UsageSessionRecord
        {
            Id = Id,
            ProcessName = ProcessName,
            WindowTitle = WindowTitle,
            StartTime = StartTime,
            EndTime = EndTime,
            ManualSubject = ManualSubject,
            ParallelActivities = ParallelActivities?.Select(x => x.Clone()).ToList(),
            LastCapturedAt = LastCapturedAt
        };
    }
    public static UsageSessionRecord Start(string processName, string windowTitle, DateTime startTime)
    {
        return new UsageSessionRecord
        {
            Id = BuildStableId(processName, windowTitle, startTime),
            ProcessName = processName,
            WindowTitle = windowTitle,
            StartTime = startTime,
            LastCapturedAt = startTime
        };
    }
    private static string BuildStableId(string processName, string windowTitle, DateTime startTime)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{processName}|{windowTitle}|{startTime:O}")))[..16];
    }
}
public sealed class WindowSnapshot
{
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
}
internal sealed class ParallelAudioStabilityState
{
    public int AudibleSamples { get; set; }
    public int SilentSamples { get; set; }
    public bool IsActiveParallel { get; set; }
    public DateTime LastAudibleAt { get; set; }
}
public static class SystemActivityMonitor
{
    public static bool IsIdle(TimeSpan threshold)
    {
        return TryGetIdleDuration(out var idleDuration) && idleDuration >= threshold;
    }
    public static DateTime? GetLastInputTime(DateTime now)
    {
        return TryGetIdleDuration(out var idleDuration) ? now - idleDuration : null;
    }
    public static bool TryGetIdleDuration(out TimeSpan idleDuration)
    {
        var duration = GetIdleDuration();
        idleDuration = duration ?? TimeSpan.Zero;
        return duration is not null;
    }
    private static TimeSpan? GetIdleDuration()
    {
        var lastInputInfo = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };
        if (!GetLastInputInfo(ref lastInputInfo))
        {
            return null;
        }
        var idleMilliseconds = Environment.TickCount64 - lastInputInfo.dwTime;
        return TimeSpan.FromMilliseconds(Math.Max(0, idleMilliseconds));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
public static class MediaPlaybackMonitor
{
    private const float SessionAudioPeakThreshold = 0.005f;
    private static readonly TimeSpan AudioDiagnosticFailureLogInterval = TimeSpan.FromMinutes(10);
    private static DateTime _lastAudioDiagnosticLogAt = DateTime.MinValue;
    private static DateTime _lastAudioDiagnosticFailureLogAt = DateTime.MinValue;
    private static bool _preferDefaultAudioEndpointOnly;

    public static bool IsAnyPlaybackActive(uint preferredProcessId = 0)
    {
        return TryGetPlaybackSnapshot(preferredProcessId, out var snapshot) && snapshot.HasAnyPlayback;
    }

    public static bool IsProcessPlaying(uint processId)
    {
        return TryGetPlaybackSnapshot(processId, out var snapshot) && snapshot.IsPreferredProcessPlaying;
    }

    public static bool TryGetPlaybackSnapshot(uint preferredProcessId, out MediaPlaybackSnapshot snapshot)
    {
        snapshot = MediaPlaybackSnapshot.Empty;
        return TryEnumeratePlaybackSessions(preferredProcessId, out snapshot);
    }

    private static bool TryEnumeratePlaybackSessions(uint preferredProcessId, out MediaPlaybackSnapshot snapshot)
    {
        var activeProcessIds = new List<uint>();
        var activeProcessNames = new List<string>();
        var diagnosticSessions = new List<string>();
        var preferredProcessName = string.Empty;
        var isPreferredProcessPlaying = false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? deviceCollection = null;
        if (_preferDefaultAudioEndpointOnly)
        {
            return TryEnumerateDefaultPlaybackSession(preferredProcessId, out snapshot);
        }

        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: true)!)!;
            enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out deviceCollection);
            deviceCollection.GetCount(out var deviceCount);
            for (var deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                IMMDevice? device = null;
                IAudioSessionManager2? sessionManager = null;
                IAudioSessionEnumerator? sessionEnumerator = null;
                IAudioMeterInformation? deviceMeter = null;
                try
                {
                    deviceCollection.Item(deviceIndex, out device);
                    if (device is null)
                    {
                        continue;
                    }

                    var audioMeterId = typeof(IAudioMeterInformation).GUID;
                    try
                    {
                        device.Activate(ref audioMeterId, ClsCtx.InprocServer, IntPtr.Zero, out var deviceMeterObject);
                        deviceMeter = deviceMeterObject as IAudioMeterInformation;
                        // Device peak is only diagnostic-friendly context. Do not gate by device peak here:
                        // some endpoints report 0 briefly while per-session meters already contain valid signal.
                        deviceMeter?.GetPeakValue(out _);
                    }
                    catch
                    {
                        deviceMeter = null;
                    }

                    var audioSessionManager2Id = typeof(IAudioSessionManager2).GUID;
                    device.Activate(ref audioSessionManager2Id, ClsCtx.InprocServer, IntPtr.Zero, out var sessionManagerObject);
                    sessionManager = sessionManagerObject as IAudioSessionManager2;
                    if (sessionManager is null)
                    {
                        continue;
                    }

                    sessionManager.GetSessionEnumerator(out sessionEnumerator);
                    sessionEnumerator.GetCount(out var count);
                    for (var index = 0; index < count; index++)
                    {
                        sessionEnumerator.GetSession(index, out var sessionControl);
                        try
                        {
                            if (sessionControl is not IAudioSessionControl2 sessionControl2)
                            {
                                continue;
                            }

                            sessionControl.GetState(out var state);
                            if (state != AudioSessionState.AudioSessionStateActive)
                            {
                                continue;
                            }

                            var sessionMeter = sessionControl as IAudioMeterInformation;
                            if (sessionMeter is null)
                            {
                                continue;
                            }

                            sessionMeter.GetPeakValue(out var sessionPeakValue);
                            sessionControl2.GetProcessId(out var sessionProcessId);
                            if (sessionProcessId == 0)
                            {
                                continue;
                            }

                            var sessionProcessName = TryGetProcessName(sessionProcessId);
                            if (sessionPeakValue > 0)
                            {
                                diagnosticSessions.Add($"{sessionProcessName}:{sessionPeakValue:F6}:state={state}");
                            }
                            if (sessionPeakValue < SessionAudioPeakThreshold)
                            {
                                continue;
                            }

                            activeProcessIds.Add(sessionProcessId);
                            if (!string.IsNullOrWhiteSpace(sessionProcessName) && !string.Equals(sessionProcessName, "Unknown", StringComparison.OrdinalIgnoreCase))
                            {
                                activeProcessNames.Add(sessionProcessName);
                            }
                            if (sessionProcessId == preferredProcessId)
                            {
                                isPreferredProcessPlaying = true;
                            }

                            if (string.IsNullOrWhiteSpace(preferredProcessName))
                            {
                                preferredProcessName = sessionProcessName;
                            }
                        }
                        finally
                        {
                            if (sessionControl is not null)
                            {
                                Marshal.ReleaseComObject(sessionControl);
                            }
                        }
                    }
                }
                finally
                {
                    if (sessionEnumerator is not null)
                    {
                        Marshal.ReleaseComObject(sessionEnumerator);
                    }
                    if (sessionManager is not null)
                    {
                        Marshal.ReleaseComObject(sessionManager);
                    }
                    if (deviceMeter is not null)
                    {
                        Marshal.ReleaseComObject(deviceMeter);
                    }
                    if (device is not null)
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _preferDefaultAudioEndpointOnly = true;
            LogAudioDiagnosticFailure(ex, "falling back to default endpoint");
            return TryEnumerateDefaultPlaybackSession(preferredProcessId, out snapshot);
        }
        finally
        {
            if (deviceCollection is not null)
            {
                Marshal.ReleaseComObject(deviceCollection);
            }
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        snapshot = new MediaPlaybackSnapshot(
            activeProcessIds.Distinct().ToList(),
            activeProcessNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            isPreferredProcessPlaying,
            string.IsNullOrWhiteSpace(preferredProcessName) ? "Unknown" : preferredProcessName);
        LogAudioDiagnostic(snapshot, diagnosticSessions);
        return snapshot.HasAnyPlayback;
    }

    private static bool LogAndCollectSession(
        IAudioSessionControl sessionControl,
        uint preferredProcessId,
        List<uint> activeProcessIds,
        List<string> activeProcessNames,
        List<string> diagnosticSessions,
        ref bool isPreferredProcessPlaying,
        ref string preferredProcessName)
    {
        if (sessionControl is not IAudioSessionControl2 sessionControl2)
        {
            return false;
        }

        sessionControl.GetState(out var state);
        if (state != AudioSessionState.AudioSessionStateActive)
        {
            return false;
        }

        var sessionMeter = sessionControl as IAudioMeterInformation;
        if (sessionMeter is null)
        {
            return false;
        }

        sessionMeter.GetPeakValue(out var sessionPeakValue);
        sessionControl2.GetProcessId(out var sessionProcessId);
        if (sessionProcessId == 0)
        {
            return false;
        }

        var sessionProcessName = TryGetProcessName(sessionProcessId);
        if (sessionPeakValue > 0)
        {
            diagnosticSessions.Add($"{sessionProcessName}:{sessionPeakValue:F6}:state={state}");
        }
        if (sessionPeakValue < SessionAudioPeakThreshold)
        {
            return false;
        }

        activeProcessIds.Add(sessionProcessId);
        if (!string.IsNullOrWhiteSpace(sessionProcessName) && !string.Equals(sessionProcessName, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            activeProcessNames.Add(sessionProcessName);
        }
        if (sessionProcessId == preferredProcessId)
        {
            isPreferredProcessPlaying = true;
        }

        if (string.IsNullOrWhiteSpace(preferredProcessName))
        {
            preferredProcessName = sessionProcessName;
        }

        return true;
    }

    private static bool TryEnumerateDefaultPlaybackSession(uint preferredProcessId, out MediaPlaybackSnapshot snapshot)
    {
        var activeProcessIds = new List<uint>();
        var activeProcessNames = new List<string>();
        var diagnosticSessions = new List<string>();
        var preferredProcessName = string.Empty;
        var isPreferredProcessPlaying = false;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? sessionManager = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: true)!)!;
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
            var audioSessionManager2Id = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref audioSessionManager2Id, ClsCtx.InprocServer, IntPtr.Zero, out var sessionManagerObject);
            sessionManager = sessionManagerObject as IAudioSessionManager2;
            if (sessionManager is null)
            {
                snapshot = MediaPlaybackSnapshot.Empty;
                LogAudioDiagnostic(snapshot, diagnosticSessions);
                return false;
            }

            sessionManager.GetSessionEnumerator(out sessionEnumerator);
            sessionEnumerator.GetCount(out var count);
            for (var index = 0; index < count; index++)
            {
                sessionEnumerator.GetSession(index, out var sessionControl);
                try
                {
                    LogAndCollectSession(sessionControl, preferredProcessId, activeProcessIds, activeProcessNames, diagnosticSessions, ref isPreferredProcessPlaying, ref preferredProcessName);
                }
                finally
                {
                    if (sessionControl is not null)
                    {
                        Marshal.ReleaseComObject(sessionControl);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            snapshot = MediaPlaybackSnapshot.Empty;
            LogAudioDiagnosticFailure(ex, "default endpoint unavailable");
            return false;
        }
        finally
        {
            if (sessionEnumerator is not null)
            {
                Marshal.ReleaseComObject(sessionEnumerator);
            }
            if (sessionManager is not null)
            {
                Marshal.ReleaseComObject(sessionManager);
            }
            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        snapshot = new MediaPlaybackSnapshot(
            activeProcessIds.Distinct().ToList(),
            activeProcessNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            isPreferredProcessPlaying,
            string.IsNullOrWhiteSpace(preferredProcessName) ? "Unknown" : preferredProcessName);
        LogAudioDiagnostic(snapshot, diagnosticSessions);
        return snapshot.HasAnyPlayback;
    }

    private static void LogAudioDiagnostic(MediaPlaybackSnapshot snapshot, IReadOnlyList<string> diagnosticSessions)
    {
        if (!snapshot.HasAnyPlayback && diagnosticSessions.Count == 0)
        {
            return;
        }

        var now = DateTime.Now;
        if (now - _lastAudioDiagnosticLogAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastAudioDiagnosticLogAt = now;
        App.LogStartupMessage(
            "AudioParallelDiagnostic",
            $"active=[{string.Join(", ", snapshot.ActiveProcessNames)}], preferred={snapshot.PreferredProcessName}, raw=[{string.Join(", ", diagnosticSessions.Take(20))}]");
    }

    private static void LogAudioDiagnosticFailure(Exception exception, string context)
    {
        var now = DateTime.Now;
        if (now - _lastAudioDiagnosticFailureLogAt < AudioDiagnosticFailureLogInterval)
        {
            return;
        }

        _lastAudioDiagnosticFailureLogAt = now;
        App.LogStartupMessage("AudioParallelDiagnostic", $"{context}: {exception.GetType().Name}: {exception.Message}");
    }

    private static string TryGetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.IsNullOrWhiteSpace(process.ProcessName) ? "Unknown" : process.ProcessName + ".exe";
        }
        catch
        {
            return "Unknown";
        }
    }

    private enum EDataFlow
    {
        eRender,
        eCapture,
        eAll
    }
    private enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications
    }
    private enum ClsCtx
    {
        InprocServer = 0x1
    }
    [Flags]
    private enum DeviceState
    {
        Active = 0x00000001,
        Disabled = 0x00000002,
        NotPresent = 0x00000004,
        Unplugged = 0x00000008,
        All = 0x0000000F
    }
    private enum AudioSessionState
    {
        AudioSessionStateInactive,
        AudioSessionStateActive,
        AudioSessionStateExpired
    }
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }
    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0E5ACDC8778")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out int deviceCount);
        void Item(int deviceIndex, out IMMDevice device);
    }
    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, ClsCtx clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        void OpenPropertyStore(int access, out IntPtr properties);
        void GetId(out IntPtr id);
        void GetState(out DeviceState state);
    }
    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int NotImpl1();
        int NotImpl2();
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    }
    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int sessionCount);
        void GetSession(int sessionCount, out IAudioSessionControl session);
    }
    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        void GetState(out AudioSessionState state);
        void GetDisplayName(out IntPtr displayName);
        void SetDisplayName(string displayName, ref Guid eventContext);
        void GetIconPath(out IntPtr iconPath);
        void SetIconPath(string iconPath, ref Guid eventContext);
        void GetGroupingParam(out Guid groupingId);
        void SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        void RegisterAudioSessionNotification(IntPtr newNotifications);
        void UnregisterAudioSessionNotification(IntPtr newNotifications);
    }
    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        void GetState(out AudioSessionState state);
        void GetDisplayName(out IntPtr displayName);
        void SetDisplayName(string displayName, ref Guid eventContext);
        void GetIconPath(out IntPtr iconPath);
        void SetIconPath(string iconPath, ref Guid eventContext);
        void GetGroupingParam(out Guid groupingId);
        void SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        void RegisterAudioSessionNotification(IntPtr newNotifications);
        void UnregisterAudioSessionNotification(IntPtr newNotifications);
        void GetSessionIdentifier(out IntPtr sessionId);
        void GetSessionInstanceIdentifier(out IntPtr instanceId);
        void GetProcessId(out uint processId);
    }
    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        void GetPeakValue(out float peakValue);
        void GetMeteringChannelCount(out int channelCount);
        void GetChannelsPeakValues(int channelCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] peakValues);
        void QueryHardwareSupport(out int hardwareSupportMask);
    }
}
public sealed record MediaPlaybackSnapshot(
    IReadOnlyList<uint> ActiveProcessIds,
    IReadOnlyList<string> ActiveProcessNames,
    bool IsPreferredProcessPlaying,
    string PreferredProcessName)
{
    public static MediaPlaybackSnapshot Empty { get; } = new([], [], false, string.Empty);
    public bool HasAnyPlayback => ActiveProcessIds.Count > 0;
}

public enum ParallelActivityKind
{
    MediaPlayback
}

public sealed class ParallelActivitySnapshot
{
    public ParallelActivityKind Kind { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool CountInTotal { get; init; }
    public bool IsCurrentProcess { get; init; }
    public TimeSpan ObservedDuration { get; init; }
    public string DurationText => MainWindow.FormatDuration(ObservedDuration);
    public string DisplayText => string.IsNullOrWhiteSpace(ProcessName)
        ? Description
        : $"{ProcessName} · {Description}";

    public ParallelActivitySnapshot Clone()
    {
        return new ParallelActivitySnapshot
        {
            Kind = Kind,
            ProcessName = ProcessName,
            WindowTitle = WindowTitle,
            Description = Description,
            CountInTotal = CountInTotal,
            IsCurrentProcess = IsCurrentProcess,
            ObservedDuration = ObservedDuration
        };
    }
}

public static class ForegroundWindowTracker
{
    public static WindowSnapshot? GetCurrent()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return null;
        }
        string processName;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = string.IsNullOrWhiteSpace(process.ProcessName) ? "Unknown" : process.ProcessName + ".exe";
        }
        catch
        {
            processName = "Unknown.exe";
        }
        var title = GetWindowText(handle);
        var browserContentTitle = TryGetBrowserContentTitle(handle, processName, title);
        if (!string.IsNullOrWhiteSpace(browserContentTitle))
        {
            title = browserContentTitle;
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            title = processName;
        }
        return new WindowSnapshot
        {
            ProcessId = processId,
            ProcessName = processName,
            WindowTitle = title.Trim()
        };
    }
    private static string? TryGetBrowserContentTitle(IntPtr handle, string processName, string fallbackTitle)
    {
        if (!IsBrowserProcess(processName))
        {
            return null;
        }

        try
        {
            var root = AutomationElement.FromHandle(handle);
            var browserRoot = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "BrowserRootView"));
            var browserRootTitle = browserRoot?.Current.Name;
            if (IsUsableBrowserContentTitle(browserRootTitle, fallbackTitle))
            {
                return browserRootTitle?.Trim();
            }

            var label = root.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "Label")));
            var labelTitle = label?.Current.Name;
            if (IsUsableBrowserContentTitle(labelTitle, fallbackTitle))
            {
                return labelTitle?.Trim();
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }

        return null;
    }

    private static bool IsBrowserProcess(string processName)
    {
        return processName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableBrowserContentTitle(string? title, string fallbackTitle)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalizedTitle = title.Trim();
        if (string.Equals(normalizedTitle, fallbackTitle.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        return !normalizedTitle.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase)
            && !normalizedTitle.Equals("Google Chrome", StringComparison.OrdinalIgnoreCase)
            && !normalizedTitle.Equals("Mozilla Firefox", StringComparison.OrdinalIgnoreCase);
    }
    private static string GetWindowText(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
