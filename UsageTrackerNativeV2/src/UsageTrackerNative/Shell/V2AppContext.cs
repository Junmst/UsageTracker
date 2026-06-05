namespace UsageTrackerNative.Shell;

public interface INavigationTarget
{
    void ApplyNavigationTarget(string targetId);
}

public sealed class V2AppContext
{
    public const int MaxUndoSteps = 10;

    private readonly Queue<Action> _undoActions = new();

    public UsageTrackerService TrackerService { get; } = new();
    public AppLoadingState LoadingState { get; } = new();
    public ImportedReadOnlyUsageContext? PreviewContext { get; private set; }
    public bool IsPreviewMode => PreviewContext is not null;
    public DateTime SelectedDate { get; private set; } = NormalizeToDateBoundary(DateTime.Now);
    public Task Initialization { get; private set; } = Task.CompletedTask;
    public bool IsInitialized { get; private set; }
    public string? InitializationError { get; private set; }

    public event EventHandler? Initialized;
    public event EventHandler? SelectedDateChanged;
    public event EventHandler? ManualIdleShortcutChanged;
    public event EventHandler? PreviewModeChanged;
    public event EventHandler? DataChanged;
    public event EventHandler<NavigateRequestedEventArgs>? NavigateRequested;
    public event EventHandler? UndoRequested;

    public V2AppContext()
    {
        TrackerService.DataLoadProgressChanged += (_, _) =>
        {
            LoadingState.Phase = TrackerService.DataLoadPhase;
            LoadingState.Message = TrackerService.DataLoadPhase switch
            {
                DataLoadPhase.Loading => "正在加载最近数据...",
                DataLoadPhase.Partial => "正在加载历史数据...",
                DataLoadPhase.Loaded => null,
                DataLoadPhase.Error => InitializationError,
                _ => null
            };
            LoadingState.Progress = TrackerService.DataLoadPhase switch
            {
                DataLoadPhase.Loading => 35,
                DataLoadPhase.Partial => 75,
                DataLoadPhase.Loaded => 100,
                DataLoadPhase.Error => -1,
                _ => 0
            };
        };
    }

    public Task InitializeAsync()
    {
        Initialization = InitializeCoreAsync();
        return Initialization;
    }

    private async Task InitializeCoreAsync()
    {
        LoadingState.Phase = DataLoadPhase.Loading;
        LoadingState.Message = "正在加载当天数据...";
        LoadingState.Progress = 10;
        try
        {
            await TrackerService.InitializeAsync();
            ManualIdleShortcutChanged?.Invoke(this, EventArgs.Empty);
            TrackerService.Start();
            IsInitialized = true;
            LoadingState.Phase = DataLoadPhase.Partial;
            LoadingState.Message = "当天数据已就绪，正在加载历史数据...";
            LoadingState.Progress = 30;
            _ = TrackerService.LoadBackgroundHistoryAsync();
        }
        catch (Exception ex)
        {
            InitializationError = ex.Message;
            LoadingState.Phase = DataLoadPhase.Error;
            LoadingState.Message = ex.Message;
            LoadingState.Progress = -1;
            throw;
        }
        finally
        {
            Initialized?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetSelectedDate(DateTime date)
    {
        var normalized = NormalizeToDateBoundary(date);
        if (SelectedDate == normalized)
        {
            return;
        }

        SelectedDate = normalized;
        SelectedDateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsDateRangeLoaded(DateTime from, DateTime to)
    {
        if (IsPreviewMode)
        {
            return true;
        }

        var range = TrackerService.GetLoadedDateRange();
        return from >= range.Start && to <= range.End;
    }

    public Task<List<UsageSessionRecord>> QuerySessionsByDateAsync(DateTime date)
    {
        return PreviewContext?.QuerySessionsByDateAsync(date)
            ?? TrackerService.QuerySessionsByDateAsync(date);
    }

    public Task<List<UsageSessionRecord>> QuerySessionsInRangeAsync(DateTime start, DateTime end)
    {
        return PreviewContext?.QuerySessionsInRangeAsync(start, end)
            ?? TrackerService.QuerySessionsInRangeAsync(start, end);
    }

    public Task<UsageOverview> QueryOverviewAsync(DateTime endExclusive)
    {
        return PreviewContext?.QueryOverviewAsync(endExclusive)
            ?? TrackerService.QueryOverviewAsync(endExclusive);
    }

    public Task<TimeSpan> QueryWeekUsageAsync(DateTime weekStart, DateTime weekEndExclusive)
    {
        return PreviewContext?.QueryWeekUsageAsync(weekStart, weekEndExclusive)
            ?? TrackerService.QueryWeekUsageAsync(weekStart, weekEndExclusive);
    }

    public IReadOnlyList<UsageSession> GetSessionsInRange(DateTime startInclusive, DateTime endExclusive)
    {
        return PreviewContext?.GetSessionsInRange(startInclusive, endExclusive)
            ?? TrackerService.GetSessionsInRange(startInclusive, endExclusive);
    }

    public IReadOnlyList<SubjectDefinition> GetSubjectDefinitions()
    {
        return PreviewContext?.SubjectDefinitions
            ?? TrackerService.GetSubjectDefinitions();
    }

    public DateTime? EarliestSessionDate => PreviewContext?.EarliestSessionDate ?? TrackerService.EarliestSessionDate;

    public UsageSession? ActiveSession => IsPreviewMode ? null : TrackerService.ActiveSession;

    public void EnterPreviewMode(ImportPackagePreview preview)
    {
        PreviewContext = ImportedReadOnlyUsageContext.FromPreview(preview);
        PreviewModeChanged?.Invoke(this, EventArgs.Empty);
        SelectedDateChanged?.Invoke(this, EventArgs.Empty);
        RequestNavigate("sessions");
    }

    public void ExitPreviewMode()
    {
        if (PreviewContext is null)
        {
            return;
        }

        PreviewContext = null;
        PreviewModeChanged?.Invoke(this, EventArgs.Empty);
        SelectedDateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetManualIdleShortcutText(string shortcutText)
    {
        TrackerService.SetManualIdleShortcutText(shortcutText);
        ManualIdleShortcutChanged?.Invoke(this, EventArgs.Empty);
    }

    public static DateTime NormalizeToDateBoundary(DateTime time)
    {
        return time.TimeOfDay < TimeSpan.FromHours(4)
            ? time.Date.AddDays(-1)
            : time.Date;
    }

    public void RequestUndo()
    {
        UndoRequested?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyDataChanged()
    {
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RegisterUndo(Action undoAction)
    {
        _undoActions.Enqueue(undoAction);
        while (_undoActions.Count > MaxUndoSteps)
        {
            _undoActions.Dequeue();
        }
    }

    public bool TryUndo()
    {
        if (_undoActions.Count == 0)
        {
            return false;
        }

        var actions = _undoActions.ToList();
        _undoActions.Clear();
        var undoAction = actions[^1];
        for (var i = 0; i < actions.Count - 1; i++)
        {
            _undoActions.Enqueue(actions[i]);
        }

        undoAction();
        return true;
    }

    public void RequestNavigate(string moduleId, string? targetId = null)
    {
        NavigateRequested?.Invoke(this, new NavigateRequestedEventArgs(moduleId, targetId));
    }
}

public sealed record NavigateRequestedEventArgs(string ModuleId, string? TargetId);
