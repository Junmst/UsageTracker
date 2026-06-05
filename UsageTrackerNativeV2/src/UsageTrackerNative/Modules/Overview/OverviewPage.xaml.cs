namespace UsageTrackerNative.Modules.Overview;

public partial class OverviewPage : System.Windows.Controls.UserControl
{
    private readonly Shell.V2AppContext _context;
    private System.Windows.Threading.DispatcherTimer? _sessionChangedDebounce;
    private bool _isEditingDateInputs;
    private string? _dateInputValidationMessage;
    private System.Windows.Threading.DispatcherTimer? _dateInputDebounce;

    public OverviewPage(Shell.V2AppContext context)
    {
        _context = context;
        InitializeComponent();
        _sessionChangedDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _sessionChangedDebounce.Tick += async (_, _) => { _sessionChangedDebounce.Stop(); await RefreshAsync(); };
        _dateInputDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _dateInputDebounce.Tick += (_, _) => { _dateInputDebounce.Stop(); NavigateToDateFromInputs(); };
        Loaded += OverviewPage_Loaded;
        Unloaded += OverviewPage_Unloaded;
        _context.SelectedDateChanged += Context_SelectedDateChanged;
        _context.PreviewModeChanged += Context_PreviewModeChanged;
        _context.DataChanged += Context_DataChanged;
        _context.Initialized += Context_Initialized;
        _context.TrackerService.SessionChanged += TrackerService_SessionChanged;
    }

    private async void OverviewPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        SyncDateInputs();
        await RefreshAsync();
    }

    private void OverviewPage_Unloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _sessionChangedDebounce?.Stop();
        _dateInputDebounce?.Stop();
        _context.SelectedDateChanged -= Context_SelectedDateChanged;
        _context.PreviewModeChanged -= Context_PreviewModeChanged;
        _context.DataChanged -= Context_DataChanged;
        _context.Initialized -= Context_Initialized;
        _context.TrackerService.SessionChanged -= TrackerService_SessionChanged;
    }

    private async void Context_SelectedDateChanged(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async void Context_DataChanged(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async void Context_PreviewModeChanged(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async void Context_Initialized(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private void TrackerService_SessionChanged(object? sender, SessionChangedEventArgs e)
    {
        if (_context.IsPreviewMode)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => TrackerService_SessionChanged(sender, e));
            return;
        }

        _sessionChangedDebounce?.Stop();
        _sessionChangedDebounce?.Start();
    }

    private async Task RefreshAsync()
    {
        var selectedDate = _context.SelectedDate;
        SyncDateInputs();
        if (!string.IsNullOrWhiteSpace(_dateInputValidationMessage))
        {
            SelectedDateStatusText.Text = _dateInputValidationMessage;
        }
        else
        {
            SelectedDateStatusText.Text = _context.IsPreviewMode
                ? "当前查看：导入包内存数据（仅查看）"
                : _context.IsInitialized
                    ? selectedDate.Date == DateTime.Today
                        ? "当前查看：今天（含实时更新）"
                        : selectedDate.Date > DateTime.Today
                            ? "当前查看：未来日期"
                            : "当前查看：历史日期"
                    : "正在初始化数据服务";
        }
        if (!_context.IsInitialized)
        {
            OverviewStateText.Text = string.IsNullOrWhiteSpace(_context.InitializationError) ? "正在加载数据库和追踪服务" : $"初始化失败：{_context.InitializationError}";
            EarliestDateText.Text = "--";
            CurrentScopeText.Text = "初始化中";
            IdleStateText.Text = "--";
            return;
        }

        try
        {
            var dayStart = selectedDate.Date.AddHours(4);
            var dayEnd = dayStart.AddDays(1);
            var weekStart = selectedDate.Date.AddDays(-(int)selectedDate.DayOfWeek + (int)DayOfWeek.Monday);
            if (selectedDate.DayOfWeek == DayOfWeek.Sunday)
            {
                weekStart = selectedDate.Date.AddDays(-6);
            }

            var overview = await _context.QueryOverviewAsync(dayEnd);
            var todayRecords = await _context.QuerySessionsByDateAsync(selectedDate);
            var weekUsage = await _context.QueryWeekUsageAsync(weekStart, weekStart.AddDays(7));

            var todayTicks = todayRecords.Sum(x => UsageTimeRange.GetOverlapDuration(x.StartTime, x.EndTime, dayStart, dayEnd).Ticks);
            TotalUsageText.Text = FormatDuration(overview.Total);
            AverageUsageText.Text = FormatDuration(overview.AverageDaily);
            TodayUsageText.Text = FormatDuration(TimeSpan.FromTicks(Math.Max(0, todayTicks)));
            WeekUsageText.Text = FormatDuration(weekUsage);
            SessionCountText.Text = todayRecords.Count.ToString();
            ProcessCountText.Text = todayRecords.Select(x => x.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString();
            OverviewStateText.Text = todayRecords.Count == 0
                ? (_context.IsPreviewMode ? "导入包当前日期没有会话记录" : "当前日期没有会话记录")
                : _context.IsPreviewMode
                    ? $"仅查看模式：导入包当前日期有 {todayRecords.Count} 条会话记录"
                    : $"当前日期有 {todayRecords.Count} 条真实会话记录";
            EarliestDateText.Text = _context.EarliestSessionDate?.ToString("yyyy-MM-dd") ?? "暂无";
            CurrentScopeText.Text = $"{dayStart:MM-dd HH:mm} → {dayEnd:MM-dd HH:mm}";
            IdleStateText.Text = _context.IsPreviewMode ? "仅查看" : (_context.TrackerService.IsIdle ? "空闲" : "记录中");
        }
        catch (Exception ex)
        {
            OverviewStateText.Text = $"查询失败：{ex.Message}";
            TotalUsageText.Text = "--";
            AverageUsageText.Text = "--";
            TodayUsageText.Text = "--";
            WeekUsageText.Text = "--";
            SessionCountText.Text = "--";
            ProcessCountText.Text = "--";
        }
    }

    private void SyncDateInputs()
    {
        if (_isEditingDateInputs || YearInput.IsKeyboardFocusWithin || MonthInput.IsKeyboardFocusWithin || DayInput.IsKeyboardFocusWithin)
        {
            return;
        }

        var d = _context.SelectedDate;
        YearInput.Text = d.Year.ToString();
        MonthInput.Text = d.Month.ToString();
        DayInput.Text = d.Day.ToString();
    }

    private void DateInput_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        var delta = e.Delta > 0 ? 1 : -1;
        if (int.TryParse(tb.Text, out var val))
        {
            _isEditingDateInputs = true;
            if (tb == YearInput)
            {
                var newYear = Math.Clamp(val + delta, 1, 9999);
                YearInput.Text = newYear.ToString();
                ClampDayInputToCurrentMonth();
            }
            else if (tb == MonthInput)
            {
                var newMonth = Math.Clamp(val + delta, 1, 12);
                MonthInput.Text = newMonth.ToString();
                ClampDayInputToCurrentMonth();
            }
            else if (tb == DayInput)
            {
                var maxDay = GetCurrentInputMaxDay();
                var newDay = Math.Clamp(val + delta, 1, maxDay);
                DayInput.Text = newDay.ToString();
            }
            NavigateToDateFromInputs();
            _isEditingDateInputs = false;
        }
        e.Handled = true;
    }

    private int GetCurrentInputMaxDay()
    {
        if (!int.TryParse(YearInput.Text, out var year) || year < 1 || year > 9999)
        {
            year = _context.SelectedDate.Year;
        }

        if (!int.TryParse(MonthInput.Text, out var month) || month < 1 || month > 12)
        {
            month = _context.SelectedDate.Month;
        }

        return DateTime.DaysInMonth(year, month);
    }

    private void ClampDayInputToCurrentMonth()
    {
        if (!int.TryParse(DayInput.Text, out var day))
        {
            return;
        }

        var maxDay = GetCurrentInputMaxDay();
        if (day > maxDay)
        {
            DayInput.Text = maxDay.ToString();
        }
        else if (day < 1)
        {
            DayInput.Text = "1";
        }
    }

    private void DateInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        _isEditingDateInputs = true;
        _dateInputDebounce?.Stop();
        if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Return)
        {
            NavigateToDateFromInputs();
            _isEditingDateInputs = false;
            e.Handled = true;
        }
        else
        {
            _dateInputDebounce?.Start();
        }
    }

    private void GoToDateButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        NavigateToDateFromInputs();
        _isEditingDateInputs = false;
    }

    private void GoToTodayButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _context.SetSelectedDate(DateTime.Now);
    }

    private void NavigateToDateFromInputs()
    {
        if (!TryReadDateInputs(out var baseDate, out var errorMessage))
        {
            _dateInputValidationMessage = errorMessage;
            SelectedDateStatusText.Text = errorMessage;
            return;
        }

        _dateInputValidationMessage = null;
        _context.SetSelectedDate(baseDate);
        SelectedDateStatusText.Text = "日期已切换";
    }

    private bool TryReadDateInputs(out DateTime date, out string errorMessage)
    {
        date = default;
        errorMessage = "日期无效，请输入合法年月日";

        if (!int.TryParse(YearInput.Text, out var y) || !int.TryParse(MonthInput.Text, out var m) || !int.TryParse(DayInput.Text, out var d))
        {
            return false;
        }

        if (y < 1 || y > 9999)
        {
            errorMessage = "年份必须在 1 到 9999 之间";
            return false;
        }

        if (m < 1 || m > 12)
        {
            errorMessage = "月份必须在 1 到 12 之间";
            return false;
        }

        var maxDay = DateTime.DaysInMonth(y, m);
        if (d < 1 || d > maxDay)
        {
            errorMessage = $"{y}年{m}月只有 {maxDay} 天";
            return false;
        }

        date = new DateTime(y, m, d, 12, 0, 0);
        return true;
    }

    private void OpenSessionsButton_Click(object sender, System.Windows.RoutedEventArgs e) => _context.RequestNavigate("sessions");
    private void OpenDistributionButton_Click(object sender, System.Windows.RoutedEventArgs e) => _context.RequestNavigate("distribution");
    private void OpenSubjectManagementButton_Click(object sender, System.Windows.RoutedEventArgs e) => _context.RequestNavigate("subjectManagement");

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            return "0m";
        }

        var totalHours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        return totalHours > 0 ? $"{totalHours}h{minutes}m" : $"{minutes}m";
    }


}
