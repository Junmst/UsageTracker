using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Windows.Forms;
using UsageTrackerNative;
using UsageTrackerWeb;

using var singleInstanceMutex = new Mutex(true, "Shiji.WebLauncher.SingleInstance", out var isFirstInstance);
if (!isFirstInstance)
{
    return;
}

var port = 17890;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed))
    {
        port = parsed;
    }
}

var dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "UsageTrackerNative");
var store = new ReadOnlyStore(dataDirectory);
var settingsReader = new SettingsReader(dataDirectory);
var webPreferences = new WebPreferencesStore(dataDirectory);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

static IResult Json(object? payload) => Results.Json(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

static DateTime ParseDate(string? value)
    => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        ? parsed
        : UsageTimeRange.GetTimeDistributionDate(DateTime.Now);

static bool IsLocalWebRequest(HttpRequest request, int expectedPort)
{
    var host = request.Host.Host;
    return (host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        && request.Host.Port == expectedPort;
}

// 重查询缓存：桌面版每秒写入，短时间内的重复统计没必要每次全表扫描
var cache = new Dictionary<string, (DateTime Expiry, object Payload)>();
var cacheLock = new object();
var browserPresenceLock = new object();
var lastBrowserPresence = DateTime.MinValue;
object? GetCached(string key, TimeSpan ttl, Func<object> factory)
{
    lock (cacheLock)
    {
        if (cache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.Now)
        {
            return entry.Payload;
        }

        var payload = factory();
        cache[key] = (DateTime.Now.Add(ttl), payload);
        return payload;
    }
}

app.MapGet("/api/meta", () => Json(new
{
    databasePath = store.DatabasePath,
    databaseExists = store.DatabaseExists,
    databaseSizeMb = Math.Round(store.DatabaseSizeMb, 2),
    sessionCount = store.GetSessionCount(),
    earliestDate = store.GetEarliestDate()?.ToString("yyyy-MM-dd")
}));

app.MapGet("/api/overview", (string? date) =>
{
    var target = ParseDate(date);
    var payload = GetCached($"overview:{target:yyyy-MM-dd}", TimeSpan.FromSeconds(20), () =>
    {
        var dayStart = UsageTimeRange.GetDayStart(target);
        var dayEnd = UsageTimeRange.GetDayEnd(target);

        var today = store.GetSecondsInRange(dayStart, dayEnd);

        var weekStart = StartOfWeek(target);
        var week = store.GetSecondsInRange(UsageTimeRange.GetDayStart(weekStart), dayEnd);

        var monthStart = new DateTime(target.Year, target.Month, 1);
        var month = store.GetSecondsInRange(UsageTimeRange.GetDayStart(monthStart), dayEnd);

        var earliest = store.GetEarliestDate();
        double total = 0;
        var trackedDays = 0;
        if (earliest is not null)
        {
            var series = store.GetDailySeries(earliest.Value, target);
            total = series.Sum(x => x.Seconds);
            trackedDays = series.Count(x => x.Seconds > 0);
        }

        return new OverviewDto
        {
            Date = target.ToString("yyyy-MM-dd"),
            TodaySeconds = today,
            WeekSeconds = week,
            MonthSeconds = month,
            TotalSeconds = total,
            TrackedDays = trackedDays,
            SessionCount = store.GetSessionCount(),
            EarliestDate = earliest?.ToString("yyyy-MM-dd"),
            Active = store.GetActiveSession(),
            DatabaseSizeMb = Math.Round(store.DatabaseSizeMb, 2)
        };
    });

    return Json(payload!);
});

app.MapGet("/api/daily", (int? days) =>
{
    var count = Math.Clamp(days ?? 30, 1, 365);
    var today = UsageTimeRange.GetTimeDistributionDate(DateTime.Now);
    var from = today.AddDays(-(count - 1));
    var payload = GetCached($"daily:{from:yyyy-MM-dd}:{count}", TimeSpan.FromSeconds(30),
        () => store.GetDailySeries(from, today));
    return Json(payload!);
});

app.MapGet("/api/range-summary", (string? from, string? to) =>
{
    var startDate = ParseDate(from);
    var endDate = ParseDate(to);
    if (endDate < startDate)
    {
        (startDate, endDate) = (endDate, startDate);
    }

    var payload = GetCached($"range-summary:{startDate:yyyy-MM-dd}:{endDate:yyyy-MM-dd}", TimeSpan.FromSeconds(20), () =>
    {
        var start = UsageTimeRange.GetDayStart(startDate);
        var end = UsageTimeRange.GetDayEnd(endDate);
        var series = store.GetDailySeries(startDate, endDate);
        var sessions = store.GetSessionsIntersecting(start, end);
        var ranking = store.GetProcessStats(start, end).Take(10).ToList();
        return new
        {
            from = startDate.ToString("yyyy-MM-dd"),
            to = endDate.ToString("yyyy-MM-dd"),
            seconds = store.GetSecondsInRange(start, end),
            trackedDays = series.Count(item => item.Seconds > 0),
            sessionCount = sessions.Count,
            processCount = store.GetProcessStats(start, end).Count,
            daily = series,
            ranking
        };
    });

    return Json(payload!);
});

app.MapGet("/api/ranking", (string? date, string? type, int? top) =>
{
    var target = ParseDate(date);
    var start = UsageTimeRange.GetDayStart(target);
    var end = UsageTimeRange.GetDayEnd(target);
    var limit = Math.Clamp(top ?? 15, 1, 100);
    var bySubject = string.Equals(type, "subject", StringComparison.OrdinalIgnoreCase);
    var payload = GetCached($"ranking:{target:yyyy-MM-dd}:{bySubject}:{limit}", TimeSpan.FromSeconds(20),
        () => (object)(bySubject ? store.GetSubjectStats(start, end) : store.GetProcessStats(start, end))
                      .Take(limit).ToList());
    return Json(payload!);
});

// 时间分布图：返回原始会话区间，分段与裁剪完全交给前端按桌面版同一公式执行
app.MapGet("/api/distribution", (string? from, string? to) =>
{
    var today = UsageTimeRange.GetTimeDistributionDate(DateTime.Now);
    var startDate = from is not null ? ParseDate(from) : today.AddDays(-13);
    var endDate = to is not null ? ParseDate(to) : today;
    if (endDate < startDate)
    {
        (startDate, endDate) = (endDate, startDate);
    }

    if ((endDate - startDate).TotalDays > 120)
    {
        startDate = endDate.AddDays(-120);
    }

    var sessions = store.GetSessionsIntersecting(
        UsageTimeRange.GetDayStart(startDate),
        UsageTimeRange.GetDayEnd(endDate));

    var dates = new List<string>();
    for (var d = startDate; d <= endDate; d = d.AddDays(1))
    {
        dates.Add(d.ToString("yyyy-MM-dd"));
    }

    return Json(new
    {
        dates,
        sessions = sessions.Select(x => new
        {
            id = x.Id,
            processName = x.ProcessName,
            windowTitle = x.WindowTitle,
            startTime = x.StartTime.ToString("o"),
            endTime = x.EndTime?.ToString("o"),
            manualSubject = x.ManualSubject,
            durationSeconds = x.DurationSeconds
        }).ToList()
    });
});

// 分类统计：大类 → 父类 → 子类三层树，镜像 StatsPage.GetSubjectTreeAsync
app.MapGet("/api/subject-tree", (string? date) =>
{
    var target = ParseDate(date);
    var payload = GetCached($"subjecttree:{target:yyyy-MM-dd}", TimeSpan.FromSeconds(20), () =>
    {
        var start = UsageTimeRange.GetDayStart(target);
        var end = UsageTimeRange.GetDayEnd(target);
        var summaries = store.GetSubjectStats(start, end);
        var pathMap = BuildSubjectPathMap(settingsReader.Load().SubjectDefinitions);

        var items = summaries
            .Where(s => !string.IsNullOrWhiteSpace(s.Key) && pathMap.ContainsKey(s.Key))
            .Select(s => (Path: pathMap[s.Key], Seconds: s.Seconds, Count: s.SessionCount))
            .ToList();

        var majors = new List<SubjectNodeDto>();
        foreach (var majorGroup in items.GroupBy(x => x.Path.Major).OrderByDescending(g => g.Sum(i => i.Seconds)))
        {
            var parents = majorGroup
                .Where(x => x.Path.Parent is not null)
                .GroupBy(x => x.Path.Parent!)
                .OrderByDescending(g => g.Sum(i => i.Seconds))
                .Select(pg => new SubjectNodeDto(
                    pg.Key,
                    pg.Sum(x => x.Seconds),
                    pg.Sum(x => x.Count),
                    pg.Where(x => x.Path.Child is not null)
                      .GroupBy(x => x.Path.Child!)
                      .OrderByDescending(cg => cg.Sum(x => x.Seconds))
                      .Select(cg => new SubjectNodeDto(
                          cg.Key,
                          cg.Sum(x => x.Seconds),
                          cg.Sum(x => x.Count),
                          new List<SubjectNodeDto>()))
                      .ToList()))
                .ToList();

            majors.Add(new SubjectNodeDto(
                majorGroup.Key,
                majorGroup.Sum(x => x.Seconds),
                majorGroup.Sum(x => x.Count),
                parents));
        }

        return (object)majors;
    });

    return Json(payload!);
});

app.MapGet("/api/settings", () =>
{
    var payload = GetCached("settings", TimeSpan.FromSeconds(15), () =>
    {
        var snapshot = settingsReader.Load();
        return (object)new
        {
            theme = snapshot.Theme,
            themeAccentColor = snapshot.ThemeAccentColor,
            themeAccentRecentColors = snapshot.ThemeAccentRecentColors ?? new List<string>(),
            themeAccentSlots = snapshot.ThemeAccentSlots ?? new List<string>(),
            subjectCount = snapshot.SubjectDefinitions?.Count ?? 0,
            subjectDefinitions = snapshot.SubjectDefinitions ?? new List<SubjectDefinitionDto>()
        };
    });

    return Json(payload!);
});

app.MapGet("/api/active", () => Json(store.GetActiveSession()));

app.MapGet("/api/web-preferences", () => Json(webPreferences.Load()));

app.MapPost("/api/web-preferences/range", (SavedOverviewRange range) =>
{
    if (string.IsNullOrWhiteSpace(range.From) || string.IsNullOrWhiteSpace(range.To)
        || !DateTime.TryParseExact(range.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        || !DateTime.TryParseExact(range.To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
    {
        return Results.BadRequest(new { error = "区间日期无效" });
    }

    return Json(webPreferences.SaveRange(range.From, range.To));
});

app.MapDelete("/api/web-preferences/range", () =>
{
    webPreferences.ClearRange();
    return Results.NoContent();
});

app.MapPost("/api/browser-presence", (HttpRequest request) =>
{
    if (!IsLocalWebRequest(request, port))
    {
        return Results.BadRequest(new { error = "仅接受本机网页地址" });
    }

    lock (browserPresenceLock)
    {
        lastBrowserPresence = DateTime.UtcNow;
    }

    return Results.NoContent();
});

app.MapPost("/api/browser-presence/close", (HttpRequest request) =>
{
    if (!IsLocalWebRequest(request, port))
    {
        return Results.BadRequest(new { error = "仅接受本机网页地址" });
    }

    lock (browserPresenceLock)
    {
        lastBrowserPresence = DateTime.MinValue;
    }

    return Results.NoContent();
});

app.MapGet("/api/browser-presence", () =>
{
    DateTime lastSeen;
    lock (browserPresenceLock)
    {
        lastSeen = lastBrowserPresence;
    }

    return Json(new
    {
        alive = lastSeen != DateTime.MinValue && DateTime.UtcNow - lastSeen < TimeSpan.FromSeconds(2)
    });
});

app.MapGet("/api/search", (string? q, int? skip, int? take) =>
    Json(store.Search(q, skip ?? 0, take ?? 50)));

app.MapFallbackToFile("index.html");

var url = $"http://127.0.0.1:{port}";

Application.SetHighDpiMode(HighDpiMode.SystemAware);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var runTask = app.RunAsync();
_ = WarmWebCacheAsync(url, webPreferences, app.Lifetime.ApplicationStopping);
using var tray = new TrayForm(app, url, args.Contains("--show"));
Application.Run(tray);

runTask.GetAwaiter().GetResult();

static async Task WarmWebCacheAsync(string baseUrl, WebPreferencesStore preferences, CancellationToken cancellationToken)
{
    try
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };
        var today = UsageTimeRange.GetTimeDistributionDate(DateTime.Now).ToString("yyyy-MM-dd");
        var urls = new List<string>
        {
            $"/api/overview?date={today}",
            "/api/daily?days=30",
            $"/api/ranking?date={today}&type=process&top=10",
        };
        var savedRange = preferences.Load().OverviewRange;
        if (savedRange is not null)
        {
            urls.Add($"/api/range-summary?from={savedRange.From}&to={savedRange.To}");
        }

        foreach (var path in urls)
        {
            using var response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            response.Dispose();
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch
    {
        // 预热失败不影响网页正常加载。
    }
}

static DateTime StartOfWeek(DateTime date)
{
    var diff = (7 + ((int)date.DayOfWeek - 1)) % 7;
    return date.Date.AddDays(-diff);
}

// 镜像 StatsPage.BuildSubjectPathMap：把分类名映射到大类/父类/子类路径
static Dictionary<string, (string Major, string? Parent, string? Child)> BuildSubjectPathMap(
    List<SubjectDefinitionDto>? definitions)
{
    var map = new Dictionary<string, (string Major, string? Parent, string? Child)>(StringComparer.OrdinalIgnoreCase);
    if (definitions is null)
    {
        return map;
    }

    foreach (var major in definitions)
    {
        if (string.IsNullOrWhiteSpace(major.Name))
        {
            continue;
        }

        map.TryAdd(major.Name, (major.Name, null, null));
        foreach (var parent in major.Parents ?? new List<SubjectParentDefinitionDto>())
        {
            if (string.IsNullOrWhiteSpace(parent.Name))
            {
                continue;
            }

            map.TryAdd(parent.Name, (major.Name, parent.Name, null));
            foreach (var child in (parent.Children ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                map.TryAdd(child, (major.Name, parent.Name, child));
            }
        }
    }

    return map;
}
