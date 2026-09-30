using System.Text.Json;

namespace UsageTrackerNative;

internal sealed class HeadlessAgentHost : ITrayHost, IDisposable
{
    private const int MaxUndoSteps = 10;
    private readonly UsageTrackerService _service = new();
    private readonly Queue<Action> _undoActions = new();
    private readonly Dictionary<string, ImportPackagePreview> _transferPreviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action _shutdown;
    private int _exitRequested;
    private bool _initialized;
    private bool _disposed;

    public HeadlessAgentHost(Action shutdown)
    {
        _shutdown = shutdown;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _service.InitializeAsync().ConfigureAwait(false);
            _service.Start();
            _initialized = true;
        }
        catch (Exception exception)
        {
            NativeLogger.LogStartupException("HeadlessAgentHost.Initialize", exception);
        }
    }

    public void RestoreFromTray()
    {
        NativeStartupLog.LogStartupMessage("HeadlessAgentHost", "Ignored request to show a window in web-only mode.");
    }

    public void ShowCompactSessionWindow()
    {
        NativeStartupLog.LogStartupMessage("HeadlessAgentHost", "Ignored compact window request in web-only mode.");
    }

    public void HideCompactSessionWindow()
    {
    }

    public AgentStatusSnapshot GetAgentStatus()
    {
        var active = _service.ActiveSession;
        return new AgentStatusSnapshot(
            Running: !_disposed,
            Tracking: _initialized && !_service.IsIdle,
            IsIdle: _service.IsIdle,
            IsManualIdle: _service.IsManualIdleMode,
            IsVideoPlayback: _service.IsForegroundVideoPlayback,
            ActiveProcessName: active?.ProcessName,
            ActiveWindowTitle: active?.WindowTitle,
            ActiveStartTime: active?.StartTime,
            LastCapturedAt: _service.LastCapturedAt);
    }

    public bool EnterManualIdleFromWeb()
    {
        if (_disposed || !_initialized)
        {
            return false;
        }

        _service.EnterManualIdle();
        return true;
    }

    public WebCommandResult ExecuteWebCommand(string commandJson)
    {
        if (_disposed || !_initialized)
        {
            return new WebCommandResult(false, "后台记录服务尚未就绪");
        }

        try
        {
            using var document = JsonDocument.Parse(commandJson);
            var root = document.RootElement;
            var command = root.TryGetProperty("command", out var commandElement)
                ? commandElement.GetString()?.Trim().ToLowerInvariant()
                : null;
            var args = root.TryGetProperty("args", out var argsElement) ? argsElement : default;
            string? Arg(string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
                ? value.GetString()
                : null;
            bool ArgBool(string name, bool fallback = false)
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return fallback;
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
            }
            List<string> StringArrayArg(string name)
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                    return [];
                return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()?.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).ToList()!;
            }

            switch (command)
            {
                case "session-set-subject":
                {
                    var session = ParseSession(args);
                    if (session is null) return new WebCommandResult(false, "会话参数无效");
                    _service.SetManualSubjectScope(session, Arg("subject"), ParseTargetDate(args));
                    return new WebCommandResult(true);
                }
                case "session-bulk-set-subject":
                {
                    if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("sessions", out var sessionsElement) || sessionsElement.ValueKind != JsonValueKind.Array)
                    {
                        return new WebCommandResult(false, "批量会话参数无效");
                    }
                    var subject = Arg("subject");
                    var targetDate = ParseTargetDate(args);
                    var changed = 0;
                    foreach (var item in sessionsElement.EnumerateArray())
                    {
                        var session = ParseSession(item);
                        if (session is null) continue;
                        _service.SetManualSubjectScope(session, subject, targetDate);
                        changed++;
                    }
                    return new WebCommandResult(changed > 0, changed > 0 ? string.Empty : "没有可修改的会话", new { changed });
                }
                case "session-delete":
                {
                    var session = ParseSession(args);
                    if (session is null) return new WebCommandResult(false, "会话参数无效");
                    var deleted = _service.DeleteSession(session);
                    if (deleted is null) return new WebCommandResult(false, "会话不存在");
                    var deletedCopy = deleted.Clone();
                    RegisterUndo(() => _service.RestoreSession(deletedCopy));
                    return new WebCommandResult(true, Data: new { id = deleted.Id });
                }
                case "session-bulk-delete":
                {
                    if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("sessions", out var sessionsElement) || sessionsElement.ValueKind != JsonValueKind.Array)
                    {
                        return new WebCommandResult(false, "批量会话参数无效");
                    }
                    var deletedCopies = new List<UsageSessionRecord>();
                    foreach (var item in sessionsElement.EnumerateArray())
                    {
                        var session = ParseSession(item);
                        if (session is null) continue;
                        var deleted = _service.DeleteSession(session);
                        if (deleted is not null) deletedCopies.Add(deleted.Clone());
                    }
                    if (deletedCopies.Count == 0) return new WebCommandResult(false, "没有可删除的会话");
                    RegisterUndo(() =>
                    {
                        foreach (var deleted in deletedCopies) _service.RestoreSession(deleted);
                    });
                    return new WebCommandResult(true, Data: new { deleted = deletedCopies.Count });
                }
                case "transfer-export":
                {
                    var path = Arg("path");
                    if (string.IsNullOrWhiteSpace(path)) return new WebCommandResult(false, "导出路径不能为空");
                    var kind = (Arg("kind") ?? "full").Trim().ToLowerInvariant();
                    var task = kind switch
                    {
                        "usage" => _service.ExportUsageDataAsync(path),
                        "settings" => _service.ExportSettingsDataAsync(path),
                        _ => _service.ExportFullBackupAsync(path)
                    };
                    task.GetAwaiter().GetResult();
                    return new WebCommandResult(true, Data: new { path, kind });
                }
                case "transfer-preview":
                {
                    var path = Arg("path");
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new WebCommandResult(false, "导入文件不存在");
                    var kind = ParsePayloadKind(Arg("kind"));
                    var preview = _service.PreviewImportPackageAsync(path, kind).GetAwaiter().GetResult();
                    _transferPreviews[path] = preview;
                    return new WebCommandResult(true, Data: ToTransferPreview(preview));
                }
                case "transfer-preview-sessions":
                {
                    var path = Arg("path");
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new WebCommandResult(false, "导入文件不存在");
                    var preview = GetTransferPreview(path);
                    var search = Arg("search") ?? string.Empty;
                    var mode = Enum.TryParse<SessionSearchMode>(Arg("mode"), true, out var parsedMode) ? parsedMode : SessionSearchMode.All;
                    var sessions = _service.GetPreviewSessions(preview, search, mode).Take(200).ToList();
                    return new WebCommandResult(true, Data: sessions);
                }
                case "transfer-import":
                {
                    var path = Arg("path");
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new WebCommandResult(false, "导入文件不存在");
                    var preview = GetTransferPreview(path);
                    var dataMode = Enum.TryParse<ImportDataMode>(Arg("dataMode"), true, out var parsedDataMode) ? parsedDataMode : ImportDataMode.Merge;
                    var conflict = Enum.TryParse<ImportConflictStrategy>(Arg("conflictStrategy"), true, out var parsedConflict) ? parsedConflict : ImportConflictStrategy.KeepLocal;
                    var settingsMode = Enum.TryParse<ImportSettingsMode>(Arg("settingsMode"), true, out var parsedSettingsMode) ? parsedSettingsMode : ImportSettingsMode.None;
                    if (dataMode == ImportDataMode.ViewOnly && settingsMode is ImportSettingsMode.None or ImportSettingsMode.ViewOnly)
                    {
                        return new WebCommandResult(true, Data: ToTransferPreview(preview));
                    }
                    var result = preview.Kind switch
                    {
                        ImportPayloadKind.Usage when dataMode == ImportDataMode.Replace => _service.ReplaceUsageDataAsync(preview).GetAwaiter().GetResult(),
                        ImportPayloadKind.Usage => _service.ImportUsageDataAsync(path, conflict).GetAwaiter().GetResult(),
                        ImportPayloadKind.Settings => _service.ImportSettingsDataAsync(preview, settingsMode).GetAwaiter().GetResult(),
                        _ => _service.ImportFullBackupAsync(preview, dataMode, conflict, settingsMode).GetAwaiter().GetResult()
                    };
                    _transferPreviews.Remove(path);
                    return new WebCommandResult(true, Data: result);
                }
                case "settings-set-appearance":
                {
                    var theme = Arg("theme");
                    var accent = Arg("accent");
                    if (!string.IsNullOrWhiteSpace(theme)) _service.SetTheme(theme);
                    if (!string.IsNullOrWhiteSpace(accent)) _service.SetThemeAccentColor(accent);
                    return new WebCommandResult(true);
                }
                case "settings-set-idle-timeout":
                {
                    var minutesString = Arg("minutes");
                    if (!int.TryParse(minutesString, out var minutes))
                        return new WebCommandResult(false, "空闲判定时长无效");
                    _service.SetIdleTimeoutMinutes(minutes);
                    return new WebCommandResult(true, Data: new { idleTimeoutMinutes = _service.IdleTimeoutMinutes });
                }
                case "subject-snapshot":
                {
                    var definitions = _service.GetSubjectDefinitions();
                    var keywordRules = definitions.SelectMany(x => x.GetAllSubjectNames())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x, _service.GetSubjectKeywordRules, StringComparer.OrdinalIgnoreCase);
                    return new WebCommandResult(true, Data: new
                    {
                        subjectDefinitions = definitions,
                        keywordRules,
                        parallelWhitelistProcesses = _service.ParallelActivityWhitelistProcesses,
                        deleteBehavior = _service.SubjectDeleteBehavior.ToString(),
                        canUndo = _undoActions.Count > 0
                    });
                }
                case "subject-add-major": return RunSubjectChangeEx(() => { var ok = _service.AddSubject(Arg("name") ?? string.Empty, out var error); return (ok, error); });
                case "subject-remove-major": return RunSubjectChange(() => _service.RemoveSubject(Arg("name") ?? string.Empty));
                case "subject-remove-majors": return RunSubjectChange(() => StringArrayArg("names").Count > 0 && StringArrayArg("names").All(_service.RemoveSubject));
                case "subject-rename-major": return RunSubjectChangeEx(() => { var ok = _service.RenameSubject(Arg("oldName") ?? string.Empty, Arg("newName") ?? string.Empty, out var error); return (ok, error); });
                case "subject-add-parent": return RunSubjectChangeEx(() => { var ok = _service.AddChildSubject(Arg("major") ?? string.Empty, Arg("name") ?? string.Empty, out var error); return (ok, error); });
                case "subject-remove-parent": return RunSubjectChange(() => _service.RemoveChildSubject(Arg("major") ?? string.Empty, Arg("name") ?? string.Empty, ArgBool("promoteToParent")));
                case "subject-remove-parents":
                {
                    var major = Arg("major") ?? string.Empty;
                    var names = StringArrayArg("names");
                    return RunSubjectChange(() => names.Count > 0 && names.All(name => _service.RemoveChildSubject(major, name, ArgBool("promoteToParent"))));
                }
                case "subject-rename-parent": return RunSubjectChangeEx(() => { var ok = _service.RenameChildSubject(Arg("major") ?? string.Empty, Arg("oldName") ?? string.Empty, Arg("newName") ?? string.Empty, out var error); return (ok, error); });
                case "subject-add-child": return RunSubjectChangeEx(() => { var ok = _service.AddGrandChildSubject(Arg("major") ?? string.Empty, Arg("parent") ?? string.Empty, Arg("name") ?? string.Empty, out var error); return (ok, error); });
                case "subject-remove-child": return RunSubjectChange(() => _service.RemoveGrandChildSubject(Arg("major") ?? string.Empty, Arg("parent") ?? string.Empty, Arg("name") ?? string.Empty, ArgBool("promoteToParent")));
                case "subject-remove-children":
                {
                    var major = Arg("major") ?? string.Empty;
                    var parent = Arg("parent") ?? string.Empty;
                    var names = StringArrayArg("names");
                    return RunSubjectChange(() => names.Count > 0 && names.All(name => _service.RemoveGrandChildSubject(major, parent, name, ArgBool("promoteToParent"))));
                }
                case "subject-rename-child": return RunSubjectChangeEx(() => { var ok = _service.RenameGrandChildSubject(Arg("major") ?? string.Empty, Arg("parent") ?? string.Empty, Arg("oldName") ?? string.Empty, Arg("newName") ?? string.Empty, out var error); return (ok, error); });
                case "subject-add-keyword": return RunSubjectChangeEx(() => { var ok = _service.AddSubjectKeywordRule(Arg("subject") ?? string.Empty, Arg("keyword") ?? string.Empty, out var error); return (ok, error); });
                case "subject-remove-keyword": return RunSubjectChange(() => _service.RemoveSubjectKeywordRule(Arg("subject") ?? string.Empty, Arg("keyword") ?? string.Empty));
                case "subject-remove-keywords":
                {
                    var subject = Arg("subject") ?? string.Empty;
                    var keywords = StringArrayArg("keywords");
                    return RunSubjectChange(() => keywords.Count > 0 && keywords.All(keyword => _service.RemoveSubjectKeywordRule(subject, keyword)));
                }
                case "subject-add-whitelist": return RunSubjectChangeEx(() => { var ok = _service.AddParallelActivityWhitelistProcess(Arg("process") ?? string.Empty, out var error); return (ok, error); });
                case "subject-remove-whitelist": return RunSubjectChange(() => _service.RemoveParallelActivityWhitelistProcess(Arg("process") ?? string.Empty));
                case "subject-remove-whitelist-processes": return RunSubjectChange(() => StringArrayArg("processes").Count > 0 && StringArrayArg("processes").All(_service.RemoveParallelActivityWhitelistProcess));
                case "subject-set-delete-behavior":
                    if (!Enum.TryParse<SubjectDeleteBehavior>(Arg("behavior"), true, out var behavior)) return new WebCommandResult(false, "删除行为无效");
                    return RunSubjectChange(() => { _service.SetSubjectDeleteBehavior(behavior); return true; });
                case "undo":
                {
                    var undone = TryUndo();
                    return new WebCommandResult(undone, undone ? string.Empty : "没有可撤销的操作");
                }
                default: return new WebCommandResult(false, "未知网页管理命令");
            }

            WebCommandResult RunSubjectChange(Func<bool> change)
            {
                var before = _service.CreateStateForUndo();
                var changed = change();
                if (changed)
                {
                    RegisterUndo(() => _service.RestoreSettingsForUndo(before));
                }
                return changed ? new WebCommandResult(true) : new WebCommandResult(false, "操作未生效：内容可能已被修改或删除，请刷新后重试");
            }

            // 带具体失败原因的变更：service 返回 false 时把友好原因透传给网页
            WebCommandResult RunSubjectChangeEx(Func<(bool Ok, string? Error)> change)
            {
                var before = _service.CreateStateForUndo();
                var (changed, error) = change();
                if (changed)
                {
                    RegisterUndo(() => _service.RestoreSettingsForUndo(before));
                }
                return changed
                    ? new WebCommandResult(true)
                    : new WebCommandResult(false, string.IsNullOrWhiteSpace(error) ? "操作未生效，请刷新后重试" : error);
            }
        }
        catch (Exception exception)
        {
            NativeStartupLog.LogStartupException("HeadlessAgentHost.ExecuteWebCommand", exception);
            return new WebCommandResult(false, exception.Message);
        }
    }

    private ImportPackagePreview GetTransferPreview(string path)
    {
        if (_transferPreviews.TryGetValue(path, out var preview)) return preview;
        preview = _service.PreviewImportPackageAsync(path, ParsePayloadKind(Path.GetExtension(path))).GetAwaiter().GetResult();
        _transferPreviews[path] = preview;
        return preview;
    }

    private static ImportPayloadKind ParsePayloadKind(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "usage" => ImportPayloadKind.Usage,
            "settings" => ImportPayloadKind.Settings,
            _ => ImportPayloadKind.Full
        };

    private static object ToTransferPreview(ImportPackagePreview preview) => new
    {
        filePath = preview.FilePath,
        kind = preview.Kind.ToString(),
        totalRecords = preview.TotalRecords,
        earliestStartTime = preview.EarliestStartTime,
        latestStartTime = preview.LatestStartTime,
        nonConflictCount = preview.DataPreview.NonConflictCount,
        conflictCount = preview.DataPreview.ConflictCount,
        subjectDefinitionCount = preview.SubjectDefinitionCount,
        subjectKeywordRuleCount = preview.SubjectKeywordRuleCount,
        theme = preview.Theme,
        themeAccentColor = preview.ThemeAccentColor,
        idleTimeoutMinutes = preview.IdleTimeoutMinutes,
        manualIdleShortcutText = preview.ManualIdleShortcutText,
        subjectDeleteBehavior = preview.SubjectDeleteBehavior
    };

    private void RegisterUndo(Action action)
    {
        _undoActions.Enqueue(action);
        while (_undoActions.Count > MaxUndoSteps) _undoActions.Dequeue();
    }

    private bool TryUndo()
    {
        if (_undoActions.Count == 0) return false;
        var actions = _undoActions.ToList();
        _undoActions.Clear();
        for (var i = 0; i < actions.Count - 1; i++) _undoActions.Enqueue(actions[i]);
        actions[^1]();
        return true;
    }

    private string TryUndoMessage() => _undoActions.Count == 0 ? "没有可撤销的操作" : string.Empty;

    private static UsageSession? ParseSession(JsonElement args)
        => args.ValueKind == JsonValueKind.Object ? ParseSessionFromObject(args) : null;

    private static UsageSession? ParseSessionFromObject(JsonElement value)
    {
        if (!value.TryGetProperty("id", out var id) || !value.TryGetProperty("processName", out var processName)
            || !value.TryGetProperty("windowTitle", out var windowTitle) || !value.TryGetProperty("startTime", out var startTime)
            || !DateTime.TryParse(startTime.GetString(), out var parsedStart)) return null;
        DateTime parsedEnd = DateTime.MinValue;
        if (value.TryGetProperty("endTime", out var end) && end.ValueKind == JsonValueKind.String) DateTime.TryParse(end.GetString(), out parsedEnd);
        return new UsageSession
        {
            Id = id.GetString() ?? string.Empty,
            ProcessName = processName.GetString() ?? string.Empty,
            WindowTitle = windowTitle.GetString() ?? string.Empty,
            StartTime = parsedStart,
            EndTime = parsedEnd,
            ManualSubject = value.TryGetProperty("manualSubject", out var subject) && subject.ValueKind == JsonValueKind.String ? subject.GetString() : null
        };
    }

    private static DateTime? ParseTargetDate(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("targetDate", out var date)
            || date.ValueKind != JsonValueKind.String || !DateTime.TryParse(date.GetString(), out var parsed)) return null;
        return parsed;
    }

    public void ExitFromTray()
    {
        if (_disposed || Interlocked.Exchange(ref _exitRequested, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            _shutdown();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _service.Dispose(); }
        catch (Exception exception) { NativeStartupLog.LogStartupException("HeadlessAgentHost.Dispose", exception); }
    }
}
