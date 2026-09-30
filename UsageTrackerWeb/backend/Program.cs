using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Windows.Forms;
using UsageTrackerNative;
using UsageTrackerWeb;

using var singleInstanceMutex = new Mutex(true, "Shiji.WebLauncher.WebView2Pilot.SingleInstance", out var isFirstInstance);
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
    "时迹");
var settingsReader = new SettingsReader(dataDirectory);
var store = new ReadOnlyStore(dataDirectory, settingsReader);
var webPreferences = new WebPreferencesStore(dataDirectory);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
});

var app = builder.Build();

app.UseDefaultFiles();
// index.html 禁用缓存：前端重发布后 hash 文件名变化，避免 WebView2 用缓存旧页面加载已删除的旧 bundle
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
    }
});

static IResult Json(object? payload) => Results.Json(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

static DateTime ParseDate(string? value)
    => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        ? parsed
        : UsageTimeRange.GetTimeDistributionDate(DateTime.Now);

static bool MatchesSubjectFilter(string? subject, string? filter, List<SubjectDefinitionDto> definitions)
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
            && descendants.Any(item => string.Equals(item, subject, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var parent in major.Parents ?? [])
        {
            if (string.Equals(parent.Name, selected, StringComparison.OrdinalIgnoreCase)
                && (parent.Children ?? []).Any(child => string.Equals(child, subject, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
    }

    return false;
}

static Func<SessionDto, bool>? CreateSubjectFilter(string? subject, SettingsReader settingsReader)
{
    if (string.IsNullOrWhiteSpace(subject)) return null;
    var definitions = settingsReader.Load().SubjectDefinitions ?? new List<SubjectDefinitionDto>();
    return session => MatchesSubjectFilter(session.ManualSubject, subject, definitions);
}

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

async Task<object?> GetCachedAsync(string key, TimeSpan ttl, Func<Task<object>> factory)
{
    lock (cacheLock)
    {
        if (cache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.Now)
        {
            return entry.Payload;
        }
    }

    var payload = await factory();
    lock (cacheLock)
    {
        cache[key] = (DateTime.Now.Add(ttl), payload);
    }
    return payload;
}

void InvalidateWebCaches()
{
    lock (cacheLock)
    {
        cache.Clear();
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

app.MapGet("/api/overview", (string? date, string? subject) =>
{
    var target = ParseDate(date);
    var filter = CreateSubjectFilter(subject, settingsReader);
    var payload = GetCached($"overview:{target:yyyy-MM-dd}:{subject ?? string.Empty}", TimeSpan.FromSeconds(20), () =>
    {
        var dayStart = UsageTimeRange.GetDayStart(target);
        var dayEnd = UsageTimeRange.GetDayEnd(target);
        var today = store.GetSecondsInRange(dayStart, dayEnd, filter);
        var weekStart = StartOfWeek(target);
        var week = store.GetSecondsInRange(UsageTimeRange.GetDayStart(weekStart), dayEnd, filter);
        var monthStart = new DateTime(target.Year, target.Month, 1);
        var month = store.GetSecondsInRange(UsageTimeRange.GetDayStart(monthStart), dayEnd, filter);
        var earliest = store.GetEarliestDate();
        double total = 0;
        var trackedDays = 0;
        long sessionCount = store.GetSessionCount();
        if (earliest is not null)
        {
            var series = store.GetDailySeries(earliest.Value, target, filter);
            total = series.Sum(x => x.Seconds);
            trackedDays = series.Count(x => x.Seconds > 0);
            if (filter is not null)
            {
                sessionCount = store.GetSessionsIntersecting(
                    UsageTimeRange.GetDayStart(earliest.Value), dayEnd, filter).Count;
            }
        }
        var active = store.GetActiveSession();
        if (filter is not null && active is not null && !filter(active)) active = null;
        return new OverviewDto
        {
            Date = target.ToString("yyyy-MM-dd"),
            TodaySeconds = today,
            WeekSeconds = week,
            MonthSeconds = month,
            TotalSeconds = total,
            TrackedDays = trackedDays,
            SessionCount = sessionCount,
            EarliestDate = earliest?.ToString("yyyy-MM-dd"),
            Active = active,
            DatabaseSizeMb = Math.Round(store.DatabaseSizeMb, 2)
        };
    });
    if (payload is OverviewDto overview)
    {
        var active = store.GetActiveSession();
        if (filter is not null && active is not null && !filter(active)) active = null;
        overview.Active = active;
    }
    return Json(payload!);
});

app.MapGet("/api/daily", (int? days, string? subject) =>
{
    var count = Math.Clamp(days ?? 30, 1, 365);
    var today = UsageTimeRange.GetTimeDistributionDate(DateTime.Now);
    var from = today.AddDays(-(count - 1));
    var filter = CreateSubjectFilter(subject, settingsReader);
    var payload = GetCached($"daily:{from:yyyy-MM-dd}:{count}:{subject ?? string.Empty}", TimeSpan.FromSeconds(30),
        () => store.GetDailySeries(from, today, filter));
    return Json(payload!);
});

app.MapGet("/api/range-summary", (string? from, string? to, string? subject) =>
{
    var startDate = ParseDate(from);
    var endDate = ParseDate(to);
    var filter = CreateSubjectFilter(subject, settingsReader);
    if (endDate < startDate)
    {
        (startDate, endDate) = (endDate, startDate);
    }

    var payload = GetCached($"range-summary:{startDate:yyyy-MM-dd}:{endDate:yyyy-MM-dd}:{subject ?? string.Empty}", TimeSpan.FromSeconds(20), () =>
    {
        var start = UsageTimeRange.GetDayStart(startDate);
        var end = UsageTimeRange.GetDayEnd(endDate);
        var summary = store.GetRangeSummary(start, end, filter);
        return new
        {
            from = startDate.ToString("yyyy-MM-dd"),
            to = endDate.ToString("yyyy-MM-dd"),
            seconds = summary.Seconds,
            trackedDays = summary.TrackedDays,
            sessionCount = summary.SessionCount,
            processCount = summary.ProcessCount,
            daily = summary.Daily,
            ranking = summary.Ranking
        };
    });

    return Json(payload!);
});

app.MapGet("/api/ranking", (string? date, string? type, int? top, string? subject) =>
{
    var target = ParseDate(date);
    var start = UsageTimeRange.GetDayStart(target);
    var end = UsageTimeRange.GetDayEnd(target);
    var limit = Math.Clamp(top ?? 15, 1, 100);
    var bySubject = string.Equals(type, "subject", StringComparison.OrdinalIgnoreCase);
    var filter = CreateSubjectFilter(subject, settingsReader);
    var payload = GetCached($"ranking:{target:yyyy-MM-dd}:{bySubject}:{limit}:{subject ?? string.Empty}", TimeSpan.FromSeconds(20),
        () => (object)(bySubject ? store.GetSubjectStats(start, end, filter) : store.GetProcessStats(start, end, filter))
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

        return (object)majors.OrderByDescending(x => x.Seconds).ToList();
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
            subjectDefinitions = snapshot.SubjectDefinitions ?? new List<SubjectDefinitionDto>(),
            idleTimeoutMinutes = snapshot.IdleTimeoutMinutes
        };
    });

    return Json(payload!);
});

var transferDirectory = Path.Combine(dataDirectory, "WebTransfers");
Directory.CreateDirectory(transferDirectory);
bool IsTransferPath(string path)
{
    try
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(transferDirectory) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

app.MapPost("/api/transfer/export", async (TransferExportRequest request, CancellationToken cancellationToken) =>
{
    var kind = string.IsNullOrWhiteSpace(request.Kind) ? "full" : request.Kind.Trim().ToLowerInvariant();
    var extension = kind == "settings" ? ".json" : ".zip";
    var filePath = Path.Combine(transferDirectory, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmssfff}{extension}");
    var response = await NativeControlClient.SendWebCommandAsync("transfer-export", new { path = filePath, kind }, cancellationToken);
    return response?.Ok == true
        ? Json(new { path = filePath, kind })
        : Results.Problem(response?.Error ?? "导出失败", statusCode: 503);
});

app.MapPost("/api/transfer/upload", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "请上传文件" });
    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "上传文件为空" });
    if (file.Length > 512L * 1024 * 1024) return Results.BadRequest(new { error = "文件超过 512 MB 限制" });
    var safeName = Path.GetFileName(file.FileName);
    var filePath = Path.Combine(transferDirectory, $"{Guid.NewGuid():N}-{safeName}");
    await using (var output = File.Create(filePath)) await file.CopyToAsync(output, cancellationToken);
    return Json(new { path = filePath, fileName = safeName, length = file.Length });
});

app.MapGet("/api/transfer/download", (string path) =>
{
    if (!IsTransferPath(path) || !File.Exists(path)) return Results.NotFound();
    var contentType = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
        ? "application/json"
        : "application/zip";
    return Results.File(path, contentType, Path.GetFileName(path), enableRangeProcessing: true);
});

app.MapPost("/api/transfer/preview", async (TransferPreviewRequest request, CancellationToken cancellationToken) =>
{ 
    if (string.IsNullOrWhiteSpace(request.Path) || !IsTransferPath(request.Path))
        return Results.BadRequest(new { error = "导入文件路径无效" });
    var response = await NativeControlClient.SendWebCommandAsync("transfer-preview", new { path = request.Path, kind = request.Kind }, cancellationToken);
    return response?.Ok == true && response.Data.HasValue
        ? Results.Json(response.Data.Value)
        : Results.Problem(response?.Error ?? "无法预览导入文件", statusCode: 503);
});

app.MapPost("/api/transfer/preview-sessions", async (TransferPreviewRequest request, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Path) || !IsTransferPath(request.Path))
        return Results.BadRequest(new { error = "导入文件路径无效" });
    var response = await NativeControlClient.SendWebCommandAsync("transfer-preview-sessions", new { path = request.Path, search = string.Empty, mode = "All" }, cancellationToken);
    return response?.Ok == true && response.Data.HasValue
        ? Results.Json(response.Data.Value)
        : Results.Problem(response?.Error ?? "无法读取预览记录", statusCode: 503);
});

app.MapPost("/api/transfer/import", async (TransferImportRequest request, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Path) || !IsTransferPath(request.Path))
        return Results.BadRequest(new { error = "导入文件路径无效" });
    var response = await NativeControlClient.SendWebCommandAsync("transfer-import", request, cancellationToken);
    if (response?.Ok != true) return Results.Problem(response?.Error ?? "导入失败", statusCode: 503);
    InvalidateWebCaches();
    return response.Data.HasValue ? Results.Json(response.Data.Value) : Results.NoContent();
});

app.MapPost("/api/settings/appearance", async (JsonElement body, CancellationToken cancellationToken) =>
{
    var theme = body.TryGetProperty("theme", out var themeValue) ? themeValue.GetString() : null;
    var accent = body.TryGetProperty("accent", out var accentValue) ? accentValue.GetString() : null;
    var response = await NativeControlClient.SendWebCommandAsync("settings-set-appearance", new { theme, accent }, cancellationToken);
    if (response?.Ok != true) return Results.Problem(response?.Error ?? "设置保存失败", statusCode: 503);
    InvalidateWebCaches();
    return Results.NoContent();
});

app.MapPost("/api/settings/idle-timeout", async (JsonElement body, CancellationToken cancellationToken) =>
{
    if (!body.TryGetProperty("minutes", out var minutesValue) || minutesValue.ValueKind != JsonValueKind.Number)
        return Results.BadRequest(new { error = "空闲判定时长无效" });
    var minutes = minutesValue.GetInt32();
    if (minutes < 1 || minutes > 1440)
        return Results.BadRequest(new { error = "空闲判定时长需在 1-1440 分钟之间" });
    var response = await NativeControlClient.SendWebCommandAsync("settings-set-idle-timeout", new { minutes = minutes.ToString() }, cancellationToken);
    if (response?.Ok != true) return Results.Problem(response?.Error ?? "设置保存失败", statusCode: 503);
    InvalidateWebCaches();
    return Results.NoContent();
});

app.MapGet("/api/subject-management", async (CancellationToken cancellationToken) =>
{
    var payload = await GetCachedAsync("subject-management", TimeSpan.FromSeconds(15), async () =>
    {
        var response = await NativeControlClient.SendWebCommandAsync("subject-snapshot", cancellationToken: cancellationToken);
        return response?.Ok == true && response.Data.HasValue
            ? (object)response.Data.Value
            : throw new InvalidOperationException(response?.Error ?? "桌面版未响应");
    });
    return Json(payload!);
});

app.MapPost("/api/subject-management/command", async (JsonElement body, CancellationToken cancellationToken) =>
{
    if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("command", out var commandElement))
    {
        return Results.BadRequest(new { error = "缺少管理命令" });
    }

    var command = commandElement.GetString();
    if (string.IsNullOrWhiteSpace(command))
    {
        return Results.BadRequest(new { error = "管理命令不能为空" });
    }

    var args = body.TryGetProperty("args", out var argsElement) ? argsElement : (JsonElement?)null;
    var response = await NativeControlClient.SendWebCommandAsync(command, args, cancellationToken);
    if (response?.Ok != true)
    {
        return Results.Problem(response?.Error ?? "桌面版未响应", statusCode: 503);
    }

    InvalidateWebCaches();
    return response.Data.HasValue ? Results.Json(response.Data.Value) : Results.NoContent();
});

app.MapGet("/api/active", (string? subject) =>
{
    var active = store.GetActiveSession();
    var filter = CreateSubjectFilter(subject, settingsReader);
    if (filter is not null && active is not null && !filter(active)) active = null;
    return Json(active);
});

app.MapGet("/api/agent/status", async (CancellationToken cancellationToken) =>
{
    var response = await NativeControlClient.GetStatusAsync(cancellationToken);
    return response?.Status is not null
        ? Json(response.Status)
        : Results.Json(new { running = false, tracking = false, isIdle = true, isManualIdle = false, isVideoPlayback = false, error = response?.Error ?? "后台记录程序未响应" }, statusCode: 503);
});

app.MapPost("/api/agent/start", async (CancellationToken cancellationToken) =>
{
    var response = await NativeControlClient.StartAsync(cancellationToken);
    return response?.Ok == true ? Results.NoContent() : Results.Problem(response?.Error ?? "无法启动后台记录程序", statusCode: 503);
});

app.MapPost("/api/agent/restart", async (CancellationToken cancellationToken) =>
{
    await NativeControlClient.StopAsync(cancellationToken);
    await Task.Delay(500, cancellationToken);
    var response = await NativeControlClient.StartAsync(cancellationToken);
    return response?.Ok == true ? Results.NoContent() : Results.Problem(response?.Error ?? "无法重启后台记录程序", statusCode: 503);
});

app.MapPost("/api/agent/idle", async (CancellationToken cancellationToken) =>
{
    var ok = await NativeControlClient.EnsureStartedAndSendAsync("idle", cancellationToken: cancellationToken);
    return ok ? Results.NoContent() : Results.Problem("无法让后台记录进入手动空闲状态", statusCode: 503);
});

app.MapPost("/api/session/command", async (JsonElement body, CancellationToken cancellationToken) =>
{
    if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("command", out var commandElement))
        return Results.BadRequest(new { error = "缺少会话命令" });
    var command = commandElement.GetString();
    if (string.IsNullOrWhiteSpace(command)) return Results.BadRequest(new { error = "会话命令不能为空" });
    var args = body.TryGetProperty("args", out var argsElement) ? argsElement : (JsonElement?)null;
    var response = await NativeControlClient.SendWebCommandAsync(command, args, cancellationToken);
    if (response?.Ok != true) return Results.Problem(response?.Error ?? "后台记录程序未响应", statusCode: 503);
    InvalidateWebCaches();
    return response.Data.HasValue ? Results.Json(response.Data.Value) : Results.NoContent();
});

app.MapPost("/api/agent/show", () => Results.Conflict(new { error = "Web-only 模式不提供 WPF 窗口" }));
app.MapPost("/api/agent/hide", () => Results.NoContent());
app.MapPost("/api/agent/compact", () => Results.Conflict(new { error = "Web-only 模式不提供悬浮窗口" }));

app.MapPost("/api/agent/exit", async (CancellationToken cancellationToken) =>
{
    var ok = await NativeControlClient.StopAsync(cancellationToken);
    return ok ? Results.NoContent() : Results.Problem("后台记录程序未响应", statusCode: 503);
});

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

app.MapGet("/api/search", (string? q, int? skip, int? take, string? date, bool? allHistory, string? mode, string? subject) =>
{
    if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var selectedDate))
    {
        return Json(store.SearchAdvanced(q, skip ?? 0, take ?? 50, selectedDate, allHistory == true, mode, settingsReader.Load().SubjectDefinitions ?? [], subject));
    }

    return Json(store.Search(q, skip ?? 0, take ?? 50));
});

app.MapGet("/api/search-version", () => Json(new { version = store.GetSearchVersion() }));

// 数据变更推送（SSE）：后台任何数据/配置落盘后，前端无需手动刷新即可局部更新。
// - settings 事件：settings.json / web-preferences.json 的修改时间变化
//   （手动分类、关键词重匹配、规则增删、热键、空闲时长、区间等所有配置类写入）
// - data 事件：记录集结构签名变化（新记录、记录结束、删除；不含进行中记录的秒级抖动）
app.MapGet("/api/events", async (HttpContext context) =>
{
    var response = context.Response;
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    response.Headers.Connection = "keep-alive";

    var settingsPath = Path.Combine(dataDirectory, "settings.json");
    var preferencesPath = Path.Combine(dataDirectory, "web-preferences.json");

    string ReadSettingsSignature()
    {
        var a = File.GetLastWriteTimeUtc(settingsPath).Ticks;
        var b = File.GetLastWriteTimeUtc(preferencesPath).Ticks;
        return $"{a}:{b}";
    }

    var stream = response.Body;
    var token = context.RequestAborted;

    async Task SendEvent(string eventName, string payload)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {payload}\n\n");
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    // 先建立基线，连接时不推送，避免打开看板就触发一轮无谓刷新。
    var lastSettings = ReadSettingsSignature();
    var lastData = store.GetDataSignature();

    while (!token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(2000, token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        string currentSettings;
        string currentData;
        try
        {
            currentSettings = ReadSettingsSignature();
            currentData = store.GetDataSignature();
        }
        catch
        {
            continue; // 查询瞬时失败（DB 被独占等）下一轮再试
        }

        if (currentSettings != lastSettings)
        {
            lastSettings = currentSettings;
            await SendEvent("settings", currentSettings);
        }
        if (currentData != lastData)
        {
            lastData = currentData;
            await SendEvent("data", currentData);
        }
    }
});

app.MapFallbackToFile("index.html");

var url = $"http://127.0.0.1:{port}";

var startTask = app.StartAsync();
startTask.GetAwaiter().GetResult();
_ = WarmWebCacheAsync(url, webPreferences, CancellationToken.None);

var uiThread = new Thread(() =>
{
    Application.SetHighDpiMode(HighDpiMode.SystemAware);
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    using var tray = new TrayForm(app, url, args.Contains("--show"), webPreferences);
    // 隐式启动：默认（无参数 / 开机自启）只显示小窗 + 托盘，不弹启动器与网页看板。
    // --show 显式打开启动器 + 网页看板；--status-window 与默认一致，仅显示小窗。
    if (!args.Contains("--show"))
    {
        tray.BeginInvoke(() => tray.ShowStatusWindowFromStartup());
    }
    Application.Run(tray);
})
{
    IsBackground = false,
};
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
uiThread.Join();

app.StopAsync().GetAwaiter().GetResult();

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
        foreach (var child in major.Children ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(child)) map.TryAdd(child, (major.Name, null, child));
        }
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
