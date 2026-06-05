using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace UsageTrackerNative.Modules.Sessions;

public partial class SessionsPage : System.Windows.Controls.UserControl, Shell.INavigationTarget
{
    private const string ManualUnclassifiedLabel = "未分类";
    private const string NullSubjectMenuTag = "__NULL_SUBJECT__";
    private const string SearchPlaceholder = "搜索进程 / 标题 / 分类";

    private readonly Shell.V2AppContext _context;
    private readonly ObservableCollection<UsageSession> _sessions = new();
    private bool _searchAllHistory;
    private SessionSearchMode _searchMode = SessionSearchMode.All;
    private bool _subjectScopeAllHistory;
    private bool _isDraggingSessionSelection;
    private bool _hasSessionSelectionDragStarted;
    private UsageSession? _pressedSession;
    private UsageSession? _selectionAnchorSession;
    private Point _selectionStartPoint;
    private Rect _selectionRect;
    private HashSet<UsageSession> _selectionAnchor = new(ReferenceEqualityComparer.Instance);
    private bool _selectionRectUpdateQueued;
    private Rect _pendingSelectionRect;
    private string _lastSessionsHash = "";
    private System.Windows.Threading.DispatcherTimer? _sessionChangedDebounce;
    private ScrollViewer? _sessionsScrollViewer;
    private readonly Dictionary<ScrollViewer, SmoothScrollState> _smoothScrollStates = new();
    private bool _smoothScrollRenderingHooked;
    private const double DefaultSmoothScrollResponseSeconds = 0.20d;
    private string? _pendingHighlightSessionId;
    private SessionNavigationTarget? _pendingNavigationTarget;
    private UsageSession? _highlightedSession;
    private int _highlightVersion;
    private const double SubjectScopeInactiveFontSize = 13.5d;
    private const double SubjectScopeActiveFontSize = 15.3d;
    private static readonly Duration SubjectScopeAnimationDuration = new(TimeSpan.FromMilliseconds(240));
    private bool _isSearchModeMenuOpen;
    private Window? _searchModeHostWindow;
    private System.Windows.Threading.DispatcherTimer? _searchModeCloseTimer;
    private Popup? _sessionContextPopup;
    private Border? _sessionContextFlyout;
    private TranslateTransform? _sessionContextTransform;
    private Popup? _sessionSubjectSubmenuPopup;
    private Border? _sessionSubjectSubmenuFlyout;
    private TranslateTransform? _sessionSubjectSubmenuTransform;
    private Popup? _sessionChildSubmenuPopup;
    private Border? _sessionChildSubmenuFlyout;
    private TranslateTransform? _sessionChildSubmenuTransform;
    private System.Windows.Threading.DispatcherTimer? _sessionContextCloseTimer;
    private System.Windows.Threading.DispatcherTimer? _sessionSubmenuCloseTimer;
    private static readonly Duration SearchModeMenuAnimationDuration = new(TimeSpan.FromMilliseconds(240));
    private static readonly Duration InteractiveButtonPressAnimationDuration = new(TimeSpan.FromMilliseconds(120));

    public SessionsPage(Shell.V2AppContext context)
    {
        _context = context;
        InitializeComponent();
        SessionsGrid.ItemsSource = _sessions;
        _sessionChangedDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _sessionChangedDebounce.Tick += async (_, _) => { _sessionChangedDebounce.Stop(); await RefreshAsync(); };
        _sessionContextCloseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _sessionContextCloseTimer.Tick += SessionContextCloseTimer_Tick;
        _sessionSubmenuCloseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _sessionSubmenuCloseTimer.Tick += SessionSubmenuCloseTimer_Tick;
        _searchModeCloseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchModeCloseTimer.Tick += SearchModeCloseTimer_Tick;
        Loaded += SessionsPage_Loaded;
        Unloaded += SessionsPage_Unloaded;
        SessionsGrid.SelectionChanged += (_, _) => RefreshSelectionSummary();
        BuildSessionContextMenu();
        UpdateSearchButtonText();
        UpdateSubjectScopeButtonText(animate: false);
        _context.SelectedDateChanged += Context_SelectedDateChanged;
        _context.PreviewModeChanged += Context_PreviewModeChanged;
        _context.DataChanged += Context_DataChanged;
        _context.TrackerService.SessionChanged += TrackerService_SessionChanged;
        _context.UndoRequested += Context_UndoRequested;
    }

    private async void SessionsPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        AttachSearchModeWindowHandlers();
        BuildSessionContextMenu();
        await RefreshAsync(force: true);
    }

    private void SessionsPage_Unloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _sessionChangedDebounce?.Stop();
        _sessionContextCloseTimer?.Stop();
        _sessionSubmenuCloseTimer?.Stop();
        _searchModeCloseTimer?.Stop();
        if (_sessionsScrollViewer is not null)
        {
            _sessionsScrollViewer.ScrollChanged -= SessionsScrollViewer_ScrollChanged;
            ResetSmoothScrollState(_sessionsScrollViewer);
        }
        if (_smoothScrollRenderingHooked)
        {
            CompositionTarget.Rendering -= SmoothScroll_Rendering;
            _smoothScrollRenderingHooked = false;
        }
        _context.SelectedDateChanged -= Context_SelectedDateChanged;
        _context.PreviewModeChanged -= Context_PreviewModeChanged;
        _context.DataChanged -= Context_DataChanged;
        _context.TrackerService.SessionChanged -= TrackerService_SessionChanged;
        _context.UndoRequested -= Context_UndoRequested;
        DetachSearchModeWindowHandlers();
        SearchModePopup.IsOpen = false;
        HideSessionContextMenu(immediate: true);
    }

    private async void Context_SelectedDateChanged(object? sender, EventArgs e)
    {
        _searchAllHistory = false;
        _subjectScopeAllHistory = false;
        UpdateSubjectScopeButtonText(animate: false);
        await RefreshAsync(force: true);
    }

    private async void Context_DataChanged(object? sender, EventArgs e)
    {
        await RefreshAsync(force: true);
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

    private async void Context_PreviewModeChanged(object? sender, EventArgs e)
    {
        _searchAllHistory = _context.IsPreviewMode;
        _subjectScopeAllHistory = _context.IsPreviewMode;
        UpdateSubjectScopeButtonText(animate: false);
        UpdatePreviewModeVisuals();
        BuildSessionContextMenu();
        await RefreshAsync(force: true);
    }

    private void UpdatePreviewModeVisuals()
    {
        var isPreview = _context.IsPreviewMode;
        PreviewModeBanner.Visibility = isPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewModeText.Text = isPreview && _context.PreviewContext is not null
            ? $"仅查看模式：{_context.PreviewContext.DisplayName}。当前页面使用导入包内存数据，后台实时记录仍写入本地正式库。"
            : string.Empty;
        UndoButton.IsEnabled = !isPreview;
        DeleteSelectedButton.IsEnabled = !isPreview;
        SubjectScopeButton.IsEnabled = !isPreview;
    }

    private void ExitPreviewModeButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _context.ExitPreviewMode();
    }

    private async Task RefreshAsync(bool force = false)
    {
        await _context.Initialization;
        UpdatePreviewModeVisuals();
        var query = SearchTextBox.Text?.Trim();
        if (string.Equals(query, SearchPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            query = string.Empty;
        }

        IReadOnlyList<UsageSessionRecord> records;
        if (_context.IsPreviewMode)
        {
            var earliest = _context.EarliestSessionDate ?? _context.SelectedDate;
            records = await _context.QuerySessionsInRangeAsync(earliest.Date, DateTime.MaxValue.Date);
        }
        else if (_searchAllHistory)
        {
            var earliest = _context.EarliestSessionDate ?? _context.SelectedDate;
            var rangeStart = earliest.Date;
            var rangeEnd = DateTime.Today.AddDays(1);
            if (_pendingNavigationTarget is not null && _pendingNavigationTarget.StartTime != DateTime.MinValue)
            {
                rangeStart = rangeStart <= _pendingNavigationTarget.StartTime.Date
                    ? rangeStart
                    : _pendingNavigationTarget.StartTime.Date.AddDays(-1);
                var targetRangeEnd = _pendingNavigationTarget.StartTime.Date.AddDays(2);
                if (targetRangeEnd > rangeEnd)
                {
                    rangeEnd = targetRangeEnd;
                }
            }
            records = await _context.QuerySessionsInRangeAsync(rangeStart, rangeEnd);
        }
        else
        {
            records = await _context.QuerySessionsByDateAsync(_context.SelectedDate);
        }

        var subjectSearchLookup = BuildSubjectSearchLookup();
        var items = records
            .Select(ToUsageSession)
            .Where(x => MatchesSessionSearch(x, query ?? string.Empty, _searchMode, subjectSearchLookup))
            .OrderByDescending(x => x.StartTime)
            .ToList();

        if (!_context.IsPreviewMode && !_searchAllHistory && _context.SelectedDate.Date == DateTime.Today && _context.ActiveSession is { } activeSession)
        {
            var activeMatches = MatchesSessionSearch(activeSession, query ?? string.Empty, _searchMode, subjectSearchLookup);
            if (activeMatches && !items.Any(x => string.Equals(x.Id, activeSession.Id, StringComparison.OrdinalIgnoreCase)))
            {
                items.Insert(0, activeSession);
            }
        }

        // 数据不变则跳过 UI 重建，避免闪烁
        var hash = string.Join('|', items.Take(200).Select(x => $"{x.Id}:{x.ManualSubject}:{x.HasParallelActivities}:{x.ParallelSummaryText}"));
        if (force || hash != _lastSessionsHash)
        {
            _lastSessionsHash = hash;
            SessionsGrid.ItemsSource = null;
            _sessions.Clear();
            foreach (var item in items)
            {
                _sessions.Add(item);
            }
            SessionsGrid.ItemsSource = _sessions;
            _selectionAnchorSession = null;
            _pressedSession = null;
            _selectionAnchor.Clear();
            RestoreHighlightAfterRebuild();
        }

        var active = _context.ActiveSession;
        ActiveSessionText.Text = active is null
            ? "当前活跃：空闲"
            : $"当前活跃：{active.ProcessName} · {active.DurationText}";
        EmptyHintText.Visibility = items.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        RefreshSelectionSummary();
        ApplyPendingHighlightIfNeeded();
    }

    public void ApplyNavigationTarget(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return;
        }

        _pendingNavigationTarget = SessionNavigationTarget.Parse(targetId);
        _pendingHighlightSessionId = _pendingNavigationTarget?.Id ?? targetId;
        _searchAllHistory = true;
        _searchMode = SessionSearchMode.All;
        SearchTextBox.Text = SearchPlaceholder;
        SearchTextBox.SetResourceReference(System.Windows.Controls.TextBox.ForegroundProperty, "SecondaryTextBrush");
        UpdateSearchButtonText();
        _ = RefreshAsync(force: true);
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

    private async void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter && e.Key != System.Windows.Input.Key.Return)
        {
            return;
        }

        e.Handled = true;
        await RefreshAsync();
    }

    private void SearchTextBox_GotFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.Equals(SearchTextBox.Text, SearchPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            SearchTextBox.Text = string.Empty;
        }
    }

    private void SubjectScopeButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _subjectScopeAllHistory = !_subjectScopeAllHistory;
        UpdateSubjectScopeButtonText(animate: true);
    }

    private void UpdateSubjectScopeButtonText(bool animate = false)
    {
        SubjectScopeButton.Background = Brushes.Transparent;
        SubjectScopeButton.BorderBrush = (Brush)System.Windows.Application.Current.FindResource("ButtonBorderBrush");
        SubjectScopeButton.ToolTip = null;

        var todayActive = !_subjectScopeAllHistory;
        var historyActive = _subjectScopeAllHistory;
        UpdateScopeTextVisual(TodayScopeText, todayActive, todayActive ? "AccentRedBrush" : "PrimaryTextBrush", animate);
        UpdateScopeTextVisual(HistoryScopeText, historyActive, historyActive ? "AccentRedBrush" : "PrimaryTextBrush", animate);
    }

    private static void UpdateScopeTextVisual(TextBlock textBlock, bool active, string targetResourceKey, bool animate)
    {
        var targetFontSize = active ? SubjectScopeActiveFontSize : SubjectScopeInactiveFontSize;
        textBlock.Opacity = 1.0;

        if (animate)
        {
            textBlock.BeginAnimation(TextBlock.FontSizeProperty, new System.Windows.Media.Animation.DoubleAnimation
            {
                To = targetFontSize,
                Duration = SubjectScopeAnimationDuration,
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            });
            AnimateTextForeground(textBlock, targetResourceKey);
            ApplyFontWeightAfterDelay(textBlock, active ? FontWeights.Black : FontWeights.SemiBold);
        }
        else
        {
            textBlock.BeginAnimation(TextBlock.FontSizeProperty, null);
            textBlock.FontSize = targetFontSize;
            textBlock.FontWeight = active ? FontWeights.Black : FontWeights.SemiBold;
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, targetResourceKey);
        }
    }

    private static async void ApplyFontWeightAfterDelay(TextBlock textBlock, FontWeight targetWeight)
    {
        await Task.Delay(120);
        textBlock.FontWeight = targetWeight;
    }

    private static void AnimateTextForeground(TextBlock textBlock, string targetResourceKey)
    {
        var targetBrush = System.Windows.Application.Current.FindResource(targetResourceKey) as SolidColorBrush;
        if (targetBrush is null)
        {
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, targetResourceKey);
            return;
        }

        var currentColor = (textBlock.Foreground as SolidColorBrush)?.Color ?? targetBrush.Color;
        var animatedBrush = new SolidColorBrush(currentColor);
        textBlock.Foreground = animatedBrush;
        var animation = new System.Windows.Media.Animation.ColorAnimation
        {
            To = targetBrush.Color,
            Duration = SubjectScopeAnimationDuration,
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        animation.Completed += (_, _) => textBlock.SetResourceReference(TextBlock.ForegroundProperty, targetResourceKey);
        animatedBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void UndoButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_context.TryUndo())
        {
            _ = RefreshAsync(force: true);
        }
    }


    private void Context_UndoRequested(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        if (_context.TryUndo())
        {
            _ = RefreshAsync(force: true);
        }
    }

    private void DeleteSelectedButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DeleteSelectedSessions();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0
            && e.Key == System.Windows.Input.Key.Z)
        {
            if (_context.TryUndo())
            {
                _ = RefreshAsync(force: true);
            }
            e.Handled = true;
        }
    }

    private void SessionsGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _sessionsScrollViewer = FindVisualChild<ScrollViewer>(SessionsGrid);
        if (_sessionsScrollViewer is not null)
        {
            _sessionsScrollViewer.ScrollChanged -= SessionsScrollViewer_ScrollChanged;
            _sessionsScrollViewer.ScrollChanged += SessionsScrollViewer_ScrollChanged;
        }
    }

    private void SessionsGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            return;
        }

        _sessionsScrollViewer ??= FindVisualChild<ScrollViewer>(SessionsGrid);
        if (_sessionsScrollViewer is null || _sessionsScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        SmoothScrollVertical(_sessionsScrollViewer, e.Delta, 0.08d, maxWheelDelta: 120d, maxTargetDistance: 180d);
        e.Handled = true;
    }

    private void SessionsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateSelectionRectangle(_selectionRect);
    }

    private void SessionsGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back)
        {
            DeleteSelectedSessions();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Z && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            UndoButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void SessionsGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<DataGridColumnHeader>(source) is not null
            || FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is not null)
        {
            return;
        }

        var row = FindAncestor<DataGridRow>(source);
        if (row?.Item is UsageSession clickedSession)
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
            {
                ApplyShiftRangeSelection(clickedSession);
                SessionsGrid.Focus();
                e.Handled = true;
                return;
            }

            _pressedSession = clickedSession;
            _isDraggingSessionSelection = true;
            _hasSessionSelectionDragStarted = false;
            _selectionStartPoint = e.GetPosition(SessionsGrid);
            _selectionRect = new Rect(_selectionStartPoint, _selectionStartPoint);
            _selectionAnchor = new HashSet<UsageSession>(SessionsGrid.SelectedItems.OfType<UsageSession>(), ReferenceEqualityComparer.Instance);
            SessionsGrid.CaptureMouse();
            SelectionOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        _pressedSession = null;
        _isDraggingSessionSelection = true;
        _hasSessionSelectionDragStarted = true;
        _selectionStartPoint = e.GetPosition(SessionsGrid);
        _selectionRect = new Rect(_selectionStartPoint, _selectionStartPoint);
        SessionsGrid.UnselectAll();
        _selectionAnchor = new HashSet<UsageSession>(ReferenceEqualityComparer.Instance);
        SessionsGrid.CaptureMouse();
        SelectionOverlay.Visibility = Visibility.Collapsed;
        ApplySelectionRect();
        e.Handled = true;
    }

    private void SessionsGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_context.IsPreviewMode)
        {
            e.Handled = true;
            return;
        }

        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not UsageSession session)
        {
            return;
        }

        // 如果右键的行已在选中项中，保持当前选择（支持框选+右键操作）
        if (!SessionsGrid.SelectedItems.Contains(session))
        {
            SessionsGrid.UnselectAll();
            SessionsGrid.SelectedItems.Add(session);
            SessionsGrid.CurrentItem = session;
        }
        ShowSessionContextMenu();
        e.Handled = true;
    }

    private void SessionsGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingSessionSelection || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        _selectionRect = new Rect(_selectionStartPoint, e.GetPosition(SessionsGrid));
        if (!_hasSessionSelectionDragStarted)
        {
            var dragDelta = e.GetPosition(SessionsGrid) - _selectionStartPoint;
            if (Math.Abs(dragDelta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(dragDelta.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _hasSessionSelectionDragStarted = true;
            if (_pressedSession is not null)
            {
                _selectionAnchor.Remove(_pressedSession);
            }
        }

        ApplySelectionRect();
        e.Handled = true;
    }

    private void SessionsGrid_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isDraggingSessionSelection)
        {
            return;
        }

        if (_hasSessionSelectionDragStarted)
        {
            ApplySelectionRectCore(_selectionRect);
        }
        else if (_pressedSession is not null)
        {
            ToggleSessionSelection(_pressedSession);
            _selectionAnchorSession = _pressedSession;
        }

        _isDraggingSessionSelection = false;
        _hasSessionSelectionDragStarted = false;
        _pressedSession = null;
        if (SessionsGrid.IsMouseCaptured)
        {
            SessionsGrid.ReleaseMouseCapture();
        }
        SelectionOverlay.Visibility = Visibility.Collapsed;
        SessionsGrid.Focus();
        e.Handled = true;
    }

    private void DeleteSelectedSessions()
    {
        if (_context.IsPreviewMode)
        {
            return;
        }

        var selectedSessions = SessionsGrid.SelectedItems.OfType<UsageSession>().ToList();
        if (selectedSessions.Count == 0 && SessionsGrid.SelectedItem is UsageSession selected)
        {
            selectedSessions.Add(selected);
        }

        if (selectedSessions.Count == 0)
        {
            return;
        }

        var deleted = new List<UsageSessionRecord>();
        foreach (var session in selectedSessions)
        {
            var record = _context.TrackerService.DeleteSession(session);
            if (record is not null)
            {
                deleted.Add(record);
            }
        }

        if (deleted.Count == 0)
        {
            return;
        }

        var deletedRecords = deleted.Select(record => record.Clone()).ToList();
        _context.RegisterUndo(() =>
        {
            foreach (var record in deletedRecords)
            {
                _context.TrackerService.RestoreSession(record);
            }
            _context.NotifyDataChanged();
        });
        _context.NotifyDataChanged();
        _ = RefreshAsync(force: true);
        RefreshSelectionSummary();
    }

    private void BuildSessionContextMenu()
    {
        SessionsGrid.ContextMenu = null;
    }

    private void ShowSessionContextMenu()
    {
        if (_context.IsPreviewMode)
        {
            return;
        }

        HideSearchModeMenu();
        HideSessionContextMenu(immediate: true);
        var content = new StackPanel();
        var deleteItem = CreateFlyoutItem("删除选中记录", null, () => DeleteSelectedSessions());
        deleteItem.MouseEnter += (_, _) => HideSessionSubmenus();
        content.Children.Add(deleteItem);
        content.Children.Add(CreateFlyoutSeparator());

        foreach (var definition in _context.GetSubjectDefinitions())
        {
            var majorHost = CreateFlyoutItem(definition.Name, "›", null);
            majorHost.MouseEnter += (_, _) =>
            {
                _sessionContextCloseTimer?.Stop();
                _sessionSubmenuCloseTimer?.Stop();
                ShowSubjectSubmenu(majorHost, BuildMajorSubjectMenu(definition));
            };
            majorHost.MouseLeave += (_, _) =>
            {
                if (_sessionSubjectSubmenuPopup?.IsOpen == true && IsMouseWithin(_sessionSubjectSubmenuFlyout))
                    return;
                StartSessionContextCloseCheck();
            };
            content.Children.Add(majorHost);
        }

        content.Children.Add(CreateFlyoutSeparator());
        var clearSubjectItem = CreateFlyoutItem("设为空分类", null, () => ApplySubjectFromFlyout(null));
        clearSubjectItem.MouseEnter += (_, _) => HideSessionSubmenus();
        content.Children.Add(clearSubjectItem);
        var manualUnclassifiedItem = CreateFlyoutItem($"设置为{ManualUnclassifiedLabel}", null, () => ApplySubjectFromFlyout(ManualUnclassifiedLabel));
        manualUnclassifiedItem.MouseEnter += (_, _) => HideSessionSubmenus();
        content.Children.Add(manualUnclassifiedItem);
        _sessionContextPopup = CreateAnimatedPopup(content, out _sessionContextFlyout, out _sessionContextTransform);
        _sessionContextPopup.PlacementTarget = SessionsGrid;
        _sessionContextPopup.Placement = PlacementMode.MousePoint;
        _sessionContextPopup.IsOpen = true;
        AnimateFlyout(_sessionContextFlyout, _sessionContextTransform, show: true);
    }

    private StackPanel BuildMajorSubjectMenu(SubjectDefinition definition)
    {
        var panel = new StackPanel();
        var directMajorItem = CreateFlyoutItem($"直接归到{definition.Name}", null, () => ApplySubjectFromFlyout(definition.Name));
        directMajorItem.MouseEnter += (_, _) => HideChildSubmenu();
        panel.Children.Add(directMajorItem);
        if (definition.Parents.Count > 0)
        {
            panel.Children.Add(CreateFlyoutSeparator());
        }

        foreach (var parent in definition.Parents)
        {
            var parentHost = CreateFlyoutItem(parent.Name, parent.Children.Count > 0 ? "›" : null, parent.Children.Count == 0 ? () => ApplySubjectFromFlyout(parent.Name) : null);
            parentHost.MouseEnter += (_, _) =>
            {
                if (parent.Children.Count == 0)
                {
                    StartSessionSubmenuCloseCheck();
                }
            };
            if (parent.Children.Count > 0)
            {
                parentHost.MouseEnter += (_, _) =>
                {
                    _sessionContextCloseTimer?.Stop();
                    _sessionSubmenuCloseTimer?.Stop();
                    ShowChildSubmenu(parentHost, BuildParentSubjectMenu(parent));
                };
                parentHost.MouseLeave += (_, _) =>
                {
                    if (_sessionChildSubmenuPopup?.IsOpen == true && IsMouseWithin(_sessionChildSubmenuFlyout))
                        return;
                    StartSessionContextCloseCheck();
                };
            }
            panel.Children.Add(parentHost);
        }

        return panel;
    }

    private StackPanel BuildParentSubjectMenu(SubjectParentDefinition parent)
    {
        var panel = new StackPanel();
        var directParentItem = CreateFlyoutItem($"直接归到{parent.Name}", null, () => ApplySubjectFromFlyout(parent.Name));
        directParentItem.MouseEnter += (_, _) => StartSessionSubmenuCloseCheck();
        panel.Children.Add(directParentItem);
        panel.Children.Add(CreateFlyoutSeparator());
        foreach (var child in parent.Children)
        {
            var childName = child;
            panel.Children.Add(CreateFlyoutItem(childName, null, () => ApplySubjectFromFlyout(childName)));
        }
        return panel;
    }

    private Popup CreateAnimatedPopup(StackPanel content, out Border flyout, out TranslateTransform transform)
    {
        transform = new TranslateTransform { Y = -8 };
        flyout = new Border
        {
            Width = 168,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0, 6, 0, 6),
            Opacity = 0,
            RenderTransform = transform,
            Child = content
        };
        flyout.SetResourceReference(Border.BackgroundProperty, "ButtonBackgroundBrush");
        flyout.SetResourceReference(Border.BorderBrushProperty, "ButtonBorderBrush");
        flyout.MouseEnter += (_, _) =>
        {
            _sessionContextCloseTimer?.Stop();
            _sessionSubmenuCloseTimer?.Stop();
        };
        flyout.MouseLeave += (_, _) => StartSessionContextCloseCheck();
        return new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Child = flyout
        };
    }

    private Border CreateFlyoutItem(string text, string? arrow, Action? click)
    {
        var item = new Border();
        item.Style = TryFindResource("SearchModeItemStyle") as Style;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        grid.Children.Add(label);
        if (!string.IsNullOrWhiteSpace(arrow))
        {
            var arrowText = new TextBlock { Text = arrow, FontSize = 14, FontWeight = FontWeights.Black, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            arrowText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            Grid.SetColumn(arrowText, 1);
            grid.Children.Add(arrowText);
        }
        item.Child = grid;
        if (click is not null)
        {
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                click();
            };
        }
        return item;
    }

    private static Separator CreateFlyoutSeparator()
    {
        return new Separator { Margin = new Thickness(8, 5, 8, 5) };
    }

    private void ShowSubjectSubmenu(FrameworkElement owner, StackPanel content)
    {
        HideChildSubmenu(immediate: true);
        if (_sessionSubjectSubmenuPopup is not null)
        {
            _sessionSubjectSubmenuPopup.IsOpen = false;
        }
        _sessionSubjectSubmenuPopup = CreateAnimatedPopup(content, out _sessionSubjectSubmenuFlyout, out _sessionSubjectSubmenuTransform);
        _sessionSubjectSubmenuPopup.PlacementTarget = owner;
        _sessionSubjectSubmenuPopup.Placement = PlacementMode.Right;
        _sessionSubjectSubmenuPopup.HorizontalOffset = 6;
        _sessionSubjectSubmenuPopup.IsOpen = true;
        AnimateFlyout(_sessionSubjectSubmenuFlyout, _sessionSubjectSubmenuTransform, show: true);
    }

    private void ShowChildSubmenu(FrameworkElement owner, StackPanel content)
    {
        HideChildSubmenu(immediate: true);
        _sessionChildSubmenuPopup = CreateAnimatedPopup(content, out _sessionChildSubmenuFlyout, out _sessionChildSubmenuTransform);
        _sessionChildSubmenuPopup.PlacementTarget = owner;
        _sessionChildSubmenuPopup.Placement = PlacementMode.Right;
        _sessionChildSubmenuPopup.HorizontalOffset = 6;
        _sessionChildSubmenuPopup.IsOpen = true;
        AnimateFlyout(_sessionChildSubmenuFlyout, _sessionChildSubmenuTransform, show: true);
    }

    private void ApplySubjectFromFlyout(string? subject)
    {
        ApplyBatchSessionSubject(subject);
        HideSessionContextMenu();
    }

    private void HideSessionContextMenu(bool immediate = false)
    {
        _sessionContextCloseTimer?.Stop();
        _sessionSubmenuCloseTimer?.Stop();
        HideChildSubmenu(immediate);
        if (_sessionSubjectSubmenuPopup is not null)
        {
            if (immediate || _sessionSubjectSubmenuFlyout is null || _sessionSubjectSubmenuTransform is null)
            {
                _sessionSubjectSubmenuPopup.IsOpen = false;
            }
            else
            {
                var popup = _sessionSubjectSubmenuPopup;
                AnimateFlyout(_sessionSubjectSubmenuFlyout, _sessionSubjectSubmenuTransform, show: false, () => popup.IsOpen = false);
            }
            _sessionSubjectSubmenuPopup = null;
        }
        if (_sessionContextPopup is not null)
        {
            if (immediate || _sessionContextFlyout is null || _sessionContextTransform is null)
            {
                _sessionContextPopup.IsOpen = false;
            }
            else
            {
                var popup = _sessionContextPopup;
                AnimateFlyout(_sessionContextFlyout, _sessionContextTransform, show: false, () => popup.IsOpen = false);
            }
            _sessionContextPopup = null;
        }
    }

    private void HideChildSubmenu(bool immediate = false, bool stopTimer = true)
    {
        if (stopTimer)
        {
            _sessionSubmenuCloseTimer?.Stop();
        }
        if (_sessionChildSubmenuPopup is null)
        {
            return;
        }
        if (immediate || _sessionChildSubmenuFlyout is null || _sessionChildSubmenuTransform is null)
        {
            _sessionChildSubmenuPopup.IsOpen = false;
        }
        else
        {
            var popup = _sessionChildSubmenuPopup;
            AnimateFlyout(_sessionChildSubmenuFlyout, _sessionChildSubmenuTransform, show: false, () => popup.IsOpen = false);
        }
        _sessionChildSubmenuPopup = null;
    }

    private void HideSubjectSubmenu(bool immediate = false)
    {
        HideChildSubmenu(immediate);
        if (_sessionSubjectSubmenuPopup is null)
        {
            return;
        }
        if (immediate || _sessionSubjectSubmenuFlyout is null || _sessionSubjectSubmenuTransform is null)
        {
            _sessionSubjectSubmenuPopup.IsOpen = false;
        }
        else
        {
            var popup = _sessionSubjectSubmenuPopup;
            AnimateFlyout(_sessionSubjectSubmenuFlyout, _sessionSubjectSubmenuTransform, show: false, () => popup.IsOpen = false);
        }
        _sessionSubjectSubmenuPopup = null;
    }

    private void HideSessionSubmenus(bool immediate = false)
    {
        _sessionSubmenuCloseTimer?.Stop();
        HideSubjectSubmenu(immediate);
    }

    private void StartSessionContextCloseCheck()
    {
        if (_sessionContextPopup?.IsOpen != true)
            return;
        _sessionContextCloseTimer?.Stop();
        _sessionContextCloseTimer?.Start();
    }

    private void StartSessionSubmenuCloseCheck()
    {
        if (_sessionSubjectSubmenuPopup?.IsOpen != true && _sessionChildSubmenuPopup?.IsOpen != true)
            return;
        _sessionSubmenuCloseTimer?.Stop();
        _sessionSubmenuCloseTimer?.Start();
    }

    private void SessionSubmenuCloseTimer_Tick(object? sender, EventArgs e)
    {
        _sessionSubmenuCloseTimer?.Stop();

        if (_sessionChildSubmenuPopup?.IsOpen == true
            && !IsMouseWithin(_sessionChildSubmenuFlyout))
        {
            HideChildSubmenu(stopTimer: false);
        }

        if (_sessionSubjectSubmenuPopup?.IsOpen == true
            && !IsMouseWithin(_sessionSubjectSubmenuFlyout)
            && !IsMouseWithin(_sessionChildSubmenuFlyout))
        {
            HideSubjectSubmenu();
        }
    }

    private void SessionContextCloseTimer_Tick(object? sender, EventArgs e)
    {
        _sessionContextCloseTimer?.Stop();
        _sessionSubmenuCloseTimer?.Stop();
        if (_sessionContextPopup?.IsOpen != true)
            return;

        if (IsMouseWithin(_sessionContextFlyout)
            || IsMouseWithin(_sessionSubjectSubmenuFlyout)
            || IsMouseWithin(_sessionChildSubmenuFlyout))
            return;

        HideSessionContextMenu();
    }

    private static void AnimateFlyout(Border flyout, TranslateTransform transform, bool show, Action? completed = null)
    {
        var easing = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var opacityAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = SearchModeMenuAnimationDuration,
            EasingFunction = easing
        };
        var yAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 0 : -8,
            Duration = SearchModeMenuAnimationDuration,
            EasingFunction = easing
        };
        if (completed is not null)
        {
            opacityAnimation.Completed += (_, _) => completed();
        }
        flyout.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        transform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
    }

    private void SetSessionSubject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
        {
            return;
        }

        var subject = string.Equals(tag, NullSubjectMenuTag, StringComparison.Ordinal) ? null : tag;
        ApplyBatchSessionSubject(subject);
    }

    private void ApplyBatchSessionSubject(string? manualSubject)
    {
        if (_context.IsPreviewMode)
        {
            return;
        }

        var selectedSessions = SessionsGrid.SelectedItems.OfType<UsageSession>().ToList();
        if (selectedSessions.Count == 0)
        {
            return;
        }

        var affected = selectedSessions.Select(x => (Session: x, PreviousSubject: x.ManualSubject)).ToList();
        foreach (var session in selectedSessions)
        {
            session.ManualSubject = manualSubject;
            _context.TrackerService.SetManualSubjectScope(session, manualSubject, _subjectScopeAllHistory ? null : _context.SelectedDate);
        }

        var undoDate = _subjectScopeAllHistory ? (DateTime?)null : _context.SelectedDate;
        var undoAffected = affected
            .Select(item => (item.Session, item.PreviousSubject))
            .ToList();
        _context.RegisterUndo(() =>
        {
            foreach (var (session, previousSubject) in undoAffected)
            {
                session.ManualSubject = previousSubject;
                _context.TrackerService.SetManualSubjectScope(session, previousSubject, undoDate);
            }
            _context.NotifyDataChanged();
        });
        _context.NotifyDataChanged();
        _ = RefreshAsync();
    }

    private void UpdateSelectionRectangle(Rect rect)
    {
        SelectionOverlay.Width = SessionsGrid.ActualWidth;
        SelectionOverlay.Height = SessionsGrid.ActualHeight;
        SelectionOverlay.Visibility = Visibility.Collapsed;
    }

    private void ApplySelectionRect()
    {
        if (_selectionRectUpdateQueued)
        {
            return;
        }

        _selectionRectUpdateQueued = true;
        _pendingSelectionRect = _selectionRect;
        Dispatcher.InvokeAsync(() =>
        {
            _selectionRectUpdateQueued = false;
            ApplySelectionRectCore(_pendingSelectionRect);
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ApplySelectionRectCore(Rect rect)
    {
        var normalized = NormalizeRect(rect);
        var selected = new HashSet<UsageSession>(_selectionAnchor, ReferenceEqualityComparer.Instance);
        foreach (var row in FindVisualChildren<DataGridRow>(SessionsGrid))
        {
            if (row.Item is UsageSession item && GetElementBounds(row, SessionsGrid).IntersectsWith(normalized))
            {
                selected.Add(item);
            }
        }

        SessionsGrid.SelectedItems.Clear();
        foreach (var item in selected)
        {
            SessionsGrid.SelectedItems.Add(item);
        }

        if (SessionsGrid.SelectedItems.Count > 0)
        {
            SessionsGrid.Focus();
        }
    }

    private void ToggleSessionSelection(UsageSession session)
    {
        if (SessionsGrid.SelectedItems.Contains(session))
        {
            SessionsGrid.SelectedItems.Remove(session);
        }
        else
        {
            SessionsGrid.SelectedItems.Add(session);
            SessionsGrid.CurrentItem = session;
        }
    }

    private void ApplyShiftRangeSelection(UsageSession target)
    {
        var anchor = _selectionAnchorSession is not null && _sessions.Contains(_selectionAnchorSession)
            ? _selectionAnchorSession
            : target;
        var anchorIndex = _sessions.IndexOf(anchor);
        var targetIndex = _sessions.IndexOf(target);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var startIndex = Math.Min(anchorIndex, targetIndex);
        var endIndex = Math.Max(anchorIndex, targetIndex);
        SessionsGrid.SelectedItems.Clear();
        for (var i = startIndex; i <= endIndex; i++)
        {
            SessionsGrid.SelectedItems.Add(_sessions[i]);
        }

        SessionsGrid.CurrentItem = target;
        SessionsGrid.ScrollIntoView(target);
    }

    private void SessionsGrid_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (IsFocusInsideSessionsComponent(e.NewFocus as DependencyObject))
        {
            return;
        }

        ClearSessionSelection();
    }

    private bool IsFocusInsideSessionsComponent(DependencyObject? target)
    {
        while (target is not null)
        {
            if (ReferenceEquals(target, SessionsGrid) || ReferenceEquals(target, this))
            {
                return true;
            }

            if (target.GetType().Name == "PopupRoot")
            {
                return true;
            }

            target = target is Visual
                ? VisualTreeHelper.GetParent(target)
                : LogicalTreeHelper.GetParent(target);
        }

        return false;
    }

    private void ClearSessionSelection()
    {
        if (_isDraggingSessionSelection)
        {
            _isDraggingSessionSelection = false;
            _hasSessionSelectionDragStarted = false;
            _pressedSession = null;
            if (SessionsGrid.IsMouseCaptured)
            {
                SessionsGrid.ReleaseMouseCapture();
            }
        }

        SessionsGrid.UnselectAll();
        _selectionAnchorSession = null;
        _selectionAnchor.Clear();
        SelectionOverlay.Visibility = Visibility.Collapsed;
        RefreshSelectionSummary();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T typed)
            {
                return typed;
            }

            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private static Rect NormalizeRect(Rect rect)
    {
        return new Rect(
            Math.Min(rect.Left, rect.Right),
            Math.Min(rect.Top, rect.Bottom),
            Math.Abs(rect.Width),
            Math.Abs(rect.Height));
    }

    private static Rect GetElementBounds(FrameworkElement element, UIElement relativeTo)
    {
        var topLeft = element.TranslatePoint(new Point(0, 0), relativeTo);
        return new Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
    }

    private void RefreshSelectionSummary()
    {
        SelectedCountText.Text = $"已选 {SessionsGrid.SelectedItems.Count} 项";
    }

    private void ApplyPendingHighlightIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_pendingHighlightSessionId) && _pendingNavigationTarget is null)
        {
            return;
        }

        var target = FindPendingNavigationTarget();
        if (target is null)
        {
            return;
        }

        _pendingHighlightSessionId = null;
        _pendingNavigationTarget = null;
        HighlightSession(target);
    }

    private UsageSession? FindPendingNavigationTarget()
    {
        if (!string.IsNullOrWhiteSpace(_pendingHighlightSessionId))
        {
            var byId = _sessions.FirstOrDefault(x => string.Equals(x.Id, _pendingHighlightSessionId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        var target = _pendingNavigationTarget;
        if (target is null)
        {
            return null;
        }

        return _sessions.FirstOrDefault(x =>
            string.Equals(x.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.WindowTitle, target.WindowTitle, StringComparison.OrdinalIgnoreCase)
            && Math.Abs((x.StartTime - target.StartTime).TotalSeconds) < 1.0d);
    }

    private void RestoreHighlightAfterRebuild()
    {
        if (_highlightedSession is null)
        {
            return;
        }

        var replacement = _sessions.FirstOrDefault(x => string.Equals(x.Id, _highlightedSession.Id, StringComparison.OrdinalIgnoreCase));
        if (replacement is null)
        {
            _highlightedSession.IsHighlighted = false;
            _highlightedSession = null;
            return;
        }

        _highlightedSession = replacement;
        _highlightedSession.IsHighlighted = true;
    }

    private void HighlightSession(UsageSession target)
    {
        if (_highlightedSession is not null && !ReferenceEquals(_highlightedSession, target))
        {
            _highlightedSession.IsHighlighted = false;
        }

        _highlightedSession = target;
        target.IsHighlighted = true;
        SessionsGrid.SelectedItems.Clear();
        SessionsGrid.SelectedItem = target;
        SessionsGrid.CurrentItem = target;
        SessionsGrid.ScrollIntoView(target);
        _ = Dispatcher.InvokeAsync(() =>
        {
            SessionsGrid.UpdateLayout();
            SessionsGrid.ScrollIntoView(target);
        }, System.Windows.Threading.DispatcherPriority.Background);
        var version = ++_highlightVersion;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(2600);
            if (version == _highlightVersion && ReferenceEquals(_highlightedSession, target))
            {
                target.IsHighlighted = false;
                _highlightedSession = null;
            }
        });
    }


    private sealed record SessionNavigationTarget(string? Id, string ProcessName, string WindowTitle, DateTime StartTime)
    {
        public static SessionNavigationTarget? Parse(string raw)
        {
            var parts = raw.Split('|');
            if (parts.Length < 4 || !long.TryParse(parts[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ticks))
            {
                return string.IsNullOrWhiteSpace(raw) ? null : new SessionNavigationTarget(raw, string.Empty, string.Empty, DateTime.MinValue);
            }

            static string Unescape(string value) => Uri.UnescapeDataString(value);
            var id = Unescape(parts[0]);
            return new SessionNavigationTarget(
                string.IsNullOrWhiteSpace(id) ? null : id,
                Unescape(parts[1]),
                Unescape(parts[2]),
                new DateTime(ticks));
        }
    }

    private enum SessionSearchMode
    {
        All,
        Subject,
        Title,
        Process
    }

    private void SearchModeButton_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left || sender is not Border border)
        {
            return;
        }

        AnimateSearchModeButtonPressedState(border, pressed: true);
    }

    private void SearchModeButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            AnimateSearchModeButtonPressedState(border, pressed: false);
        }
    }

    private void SearchModeButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border border)
        {
            AnimateSearchModeButtonPressedState(border, pressed: false);
        }
    }

    private void AnimateSearchModeButtonPressedState(Border border, bool pressed)
    {
        AnimateInteractiveBrush(border, Border.BackgroundProperty, GetInteractiveBrushColor(border.Background, "ButtonBackgroundBrush"), pressed);
        AnimateInteractiveBrush(border, Border.BorderBrushProperty, GetInteractiveBrushColor(border.BorderBrush, "ButtonBorderBrush"), pressed);
    }

    private static void AnimateInteractiveBrush(Border border, DependencyProperty property, System.Windows.Media.Color baseColor, bool pressed)
    {
        var targetColor = pressed ? DarkenInteractiveColor(baseColor, 0.14) : baseColor;
        var currentColor = property == Border.BackgroundProperty
            ? GetInteractiveBrushColor(border.Background, null)
            : GetInteractiveBrushColor(border.BorderBrush, null);
        var brush = new SolidColorBrush(currentColor);
        border.SetValue(property, brush);
        var animation = new System.Windows.Media.Animation.ColorAnimation
        {
            To = targetColor,
            Duration = InteractiveButtonPressAnimationDuration,
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        if (!pressed)
        {
            animation.Completed += (_, _) =>
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                border.ClearValue(property);
            };
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private static System.Windows.Media.Color GetInteractiveBrushColor(Brush? brush, string? fallbackResourceKey)
    {
        if (brush is SolidColorBrush solid)
        {
            return solid.Color;
        }

        if (!string.IsNullOrWhiteSpace(fallbackResourceKey) && System.Windows.Application.Current.Resources[fallbackResourceKey] is SolidColorBrush fallback)
        {
            return fallback.Color;
        }

        return Colors.Transparent;
    }

    private static System.Windows.Media.Color DarkenInteractiveColor(System.Windows.Media.Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return System.Windows.Media.Color.FromArgb(
            color.A,
            (byte)Math.Round(color.R * (1 - amount)),
            (byte)Math.Round(color.G * (1 - amount)),
            (byte)Math.Round(color.B * (1 - amount)));
    }

    private async void SearchButtonText_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        await ExecuteSearchAsync();
    }

    private void SearchModeDropDown_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleSearchModeMenu();
    }

    private void SearchModeHost_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleSearchModeMenu();
    }

    private void ClearSearchModeButtonInteractionState()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            System.Windows.Input.Keyboard.ClearFocus();
            System.Windows.Input.Mouse.Capture(null);
            AnimateSearchModeButtonPressedState(SearchModeButtonBorder, pressed: false);
        });
    }

    private async Task ExecuteSearchAsync()
    {
        _searchAllHistory = _searchMode == SessionSearchMode.All;
        await RefreshAsync(force: true);
        ClearSearchModeButtonInteractionState();
    }

    private void ToggleSearchModeMenu()
    {
        if (_isSearchModeMenuOpen)
        {
            HideSearchModeMenu();
        }
        else
        {
            ShowSearchModeMenu();
        }
    }

    private void ShowSearchModeMenu()
    {
        _searchModeCloseTimer?.Stop();
        AttachSearchModeWindowHandlers();
        UpdateSearchModeMenuVisuals();
        SearchModePopup.IsOpen = true;
        SearchModePopup.HorizontalOffset = 0;
        SearchModePopup.VerticalOffset = 8;
        _isSearchModeMenuOpen = true;
        AnimateSearchModeMenu(show: true);
    }

    private void HideSearchModeMenu()
    {
        _searchModeCloseTimer?.Stop();
        if (!_isSearchModeMenuOpen)
        {
            return;
        }

        _isSearchModeMenuOpen = false;
        AnimateSearchModeMenu(show: false);
    }

    private void AnimateSearchModeMenu(bool show)
    {
        var easing = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var opacityAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = SearchModeMenuAnimationDuration,
            EasingFunction = easing
        };
        var yAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 0 : -8,
            Duration = SearchModeMenuAnimationDuration,
            EasingFunction = easing
        };
        if (!show)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                if (!_isSearchModeMenuOpen)
                {
                    SearchModePopup.IsOpen = false;
                }
            };
        }
        SearchModeFlyout.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        SearchModeFlyoutTransform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
    }

    private void SearchModeFlyout_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _searchModeCloseTimer?.Stop();
    }

    private void SearchModeFlyout_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isSearchModeMenuOpen)
        {
            _searchModeCloseTimer?.Stop();
            _searchModeCloseTimer?.Start();
        }
    }

    private void SearchModeCloseTimer_Tick(object? sender, EventArgs e)
    {
        _searchModeCloseTimer?.Stop();
        if (_isSearchModeMenuOpen && !IsMouseWithin(SearchModeFlyout) && !IsMouseWithin(SearchModeHost))
        {
            HideSearchModeMenu();
        }
    }

    private void SearchModeItem_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string tag } || !Enum.TryParse<SessionSearchMode>(tag, out var mode))
        {
            return;
        }

        e.Handled = true;
        _searchMode = mode;
        UpdateSearchButtonText();
        UpdateSearchModeMenuVisuals();
        HideSearchModeMenu();
        _ = ExecuteSearchAsync();
    }

    private void SessionsPage_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isSearchModeMenuOpen)
        {
            if (IsMouseWithin(SearchModeHost) || IsMouseWithin(SearchModeFlyout))
            {
                return;
            }

            HideSearchModeMenu();
            return;
        }

        if (_sessionContextPopup?.IsOpen == true)
        {
            if (IsMouseWithin(_sessionContextFlyout) || IsMouseWithin(_sessionSubjectSubmenuFlyout) || IsMouseWithin(_sessionChildSubmenuFlyout))
            {
                return;
            }

            HideSessionContextMenu();
        }
    }

    private void AttachSearchModeWindowHandlers()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(_searchModeHostWindow, window))
        {
            return;
        }

        DetachSearchModeWindowHandlers();
        _searchModeHostWindow = window;
        if (_searchModeHostWindow is null)
        {
            return;
        }

        _searchModeHostWindow.LocationChanged += SearchModeHostWindow_PositionChanged;
        _searchModeHostWindow.SizeChanged += SearchModeHostWindow_PositionChanged;
        _searchModeHostWindow.Deactivated += SearchModeHostWindow_Deactivated;
        _searchModeHostWindow.PreviewMouseDown += SearchModeHostWindow_PreviewMouseDown;
    }

    private void DetachSearchModeWindowHandlers()
    {
        if (_searchModeHostWindow is null)
        {
            return;
        }

        _searchModeHostWindow.LocationChanged -= SearchModeHostWindow_PositionChanged;
        _searchModeHostWindow.SizeChanged -= SearchModeHostWindow_PositionChanged;
        _searchModeHostWindow.Deactivated -= SearchModeHostWindow_Deactivated;
        _searchModeHostWindow.PreviewMouseDown -= SearchModeHostWindow_PreviewMouseDown;
        _searchModeHostWindow = null;
    }

    private void SearchModeHostWindow_PositionChanged(object? sender, EventArgs e)
    {
        HideSearchModeMenu();
        HideSessionContextMenu();
    }

    private void SearchModeHostWindow_Deactivated(object? sender, EventArgs e)
    {
        HideSearchModeMenu();
        HideSessionContextMenu();
        ClearSessionSelection();
    }

    private void SearchModeHostWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsMouseWithin(SessionsGrid) || IsMouseWithin(_sessionContextFlyout) || IsMouseWithin(_sessionSubjectSubmenuFlyout) || IsMouseWithin(_sessionChildSubmenuFlyout))
        {
            return;
        }

        ClearSessionSelection();
    }

    private static bool IsMouseWithin(FrameworkElement? element)
    {
        if (element is null || !element.IsVisible)
        {
            return false;
        }

        var point = System.Windows.Input.Mouse.GetPosition(element);
        return point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
    }

    private void UpdateSearchModeMenuVisuals()
    {
        UpdateSearchModeItemVisual(AllSearchModeText, _searchMode == SessionSearchMode.All);
        UpdateSearchModeItemVisual(SubjectSearchModeText, _searchMode == SessionSearchMode.Subject);
        UpdateSearchModeItemVisual(TitleSearchModeText, _searchMode == SessionSearchMode.Title);
        UpdateSearchModeItemVisual(ProcessSearchModeText, _searchMode == SessionSearchMode.Process);
    }

    private static void UpdateSearchModeItemVisual(TextBlock textBlock, bool active)
    {
        textBlock.FontWeight = active ? FontWeights.Black : FontWeights.SemiBold;
        textBlock.FontSize = active ? 14.5 : 13.5;
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, active ? "AccentRedBrush" : "PrimaryTextBrush");
    }

    private void UpdateSearchButtonText()
    {
        SearchButtonText.Text = _searchMode switch
        {
            SessionSearchMode.Subject => "分类查找",
            SessionSearchMode.Title => "标题查找",
            SessionSearchMode.Process => "进程查找",
            _ => "全盘查找"
        };
        UpdateSearchModeMenuVisuals();
    }

    private Dictionary<string, HashSet<string>> BuildSubjectSearchLookup()
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

        foreach (var major in _context.GetSubjectDefinitions())
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

    private static bool MatchesSessionSearch(UsageSession session, string searchText, SessionSearchMode mode, IReadOnlyDictionary<string, HashSet<string>> subjectSearchLookup)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return SearchExpressionMatcher.IsMatch(searchText, term => mode switch
        {
            SessionSearchMode.Subject => MatchesSubjectSearch(session, term, subjectSearchLookup),
            SessionSearchMode.Title => session.WindowTitle.Contains(term, StringComparison.OrdinalIgnoreCase),
            SessionSearchMode.Process => session.ProcessName.Contains(term, StringComparison.OrdinalIgnoreCase),
            _ => MatchesSessionSearchTerm(session, term, subjectSearchLookup)
        });
    }

    private static bool MatchesSessionSearchTerm(UsageSession session, string searchText, IReadOnlyDictionary<string, HashSet<string>> subjectSearchLookup)
    {
        var separatorIndex = searchText.IndexOfAny([':', '：']);
        if (separatorIndex > 0 && separatorIndex < searchText.Length - 1)
        {
            var scope = searchText[..separatorIndex].Trim().ToLowerInvariant();
            var value = searchText[(separatorIndex + 1)..].Trim();
            return scope switch
            {
                "分类" or "科目" or "subject" => MatchesSubjectSearch(session, value, subjectSearchLookup),
                "标题" or "窗口" or "title" => session.WindowTitle.Contains(value, StringComparison.OrdinalIgnoreCase),
                "进程" or "程序" or "process" or "proc" => session.ProcessName.Contains(value, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        return MatchesSubjectSearch(session, searchText, subjectSearchLookup)
            || session.WindowTitle.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || session.ProcessName.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSubjectSearch(UsageSession session, string searchText, IReadOnlyDictionary<string, HashSet<string>> subjectSearchLookup)
    {
        var subject = string.IsNullOrWhiteSpace(session.ManualSubject) ? session.SubjectText : session.ManualSubject;
        var normalizedSearch = searchText.Trim();
        if (subjectSearchLookup.TryGetValue(normalizedSearch, out var linkedSubjects))
        {
            return linkedSubjects.Contains(subject);
        }

        return subject.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void SmoothScrollVertical(
        ScrollViewer scrollViewer,
        int wheelDelta,
        double speed,
        double immediateResponseRatio = 0d,
        double responseSeconds = DefaultSmoothScrollResponseSeconds,
        double? maxWheelDelta = null,
        double? maxTargetDistance = null)
    {
        if (maxWheelDelta.HasValue)
        {
            wheelDelta = (int)Math.Clamp(wheelDelta, -maxWheelDelta.Value, maxWheelDelta.Value);
        }

        if (!_smoothScrollStates.TryGetValue(scrollViewer, out var state))
        {
            state = new SmoothScrollState(scrollViewer.VerticalOffset, responseSeconds);
            _smoothScrollStates[scrollViewer] = state;
        }
        else if (!state.IsAnimating)
        {
            state.TargetOffset = scrollViewer.VerticalOffset;
            state.LastFrameTime = TimeSpan.Zero;
            state.ResponseSeconds = responseSeconds;
        }

        var targetOffset = Math.Clamp(state.TargetOffset - wheelDelta * speed, 0d, scrollViewer.ScrollableHeight);
        if (maxTargetDistance.HasValue)
        {
            targetOffset = Math.Clamp(targetOffset, scrollViewer.VerticalOffset - maxTargetDistance.Value, scrollViewer.VerticalOffset + maxTargetDistance.Value);
            targetOffset = Math.Clamp(targetOffset, 0d, scrollViewer.ScrollableHeight);
        }

        state.TargetOffset = targetOffset;

        if (immediateResponseRatio > 0d)
        {
            var immediate = (state.TargetOffset - scrollViewer.VerticalOffset) * immediateResponseRatio;
            if (Math.Abs(immediate) > 0.35d)
            {
                scrollViewer.ScrollToVerticalOffset(Math.Clamp(scrollViewer.VerticalOffset + immediate, 0d, scrollViewer.ScrollableHeight));
            }
        }

        if (Math.Abs(state.TargetOffset - scrollViewer.VerticalOffset) < 0.35d)
        {
            scrollViewer.ScrollToVerticalOffset(state.TargetOffset);
            state.IsAnimating = false;
            return;
        }

        state.IsAnimating = true;
        if (!_smoothScrollRenderingHooked)
        {
            CompositionTarget.Rendering += SmoothScroll_Rendering;
            _smoothScrollRenderingHooked = true;
        }
    }

    private void ResetSmoothScrollState(ScrollViewer scrollViewer)
    {
        if (_smoothScrollStates.TryGetValue(scrollViewer, out var state))
        {
            state.TargetOffset = scrollViewer.VerticalOffset;
            state.IsAnimating = false;
            state.LastFrameTime = TimeSpan.Zero;
        }
    }

    private void SmoothScroll_Rendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs renderingEventArgs)
        {
            return;
        }

        var anyAnimating = false;
        foreach (var pair in _smoothScrollStates.ToList())
        {
            var scrollViewer = pair.Key;
            var state = pair.Value;
            if (!scrollViewer.IsLoaded || !state.IsAnimating)
            {
                continue;
            }

            var deltaSeconds = state.LastFrameTime == TimeSpan.Zero
                ? 1d / 60d
                : Math.Clamp((renderingEventArgs.RenderingTime - state.LastFrameTime).TotalSeconds, 1d / 240d, 1d / 20d);
            state.LastFrameTime = renderingEventArgs.RenderingTime;
            state.TargetOffset = Math.Clamp(state.TargetOffset, 0d, scrollViewer.ScrollableHeight);

            var current = scrollViewer.VerticalOffset;
            var distance = state.TargetOffset - current;
            if (Math.Abs(distance) < 0.35d)
            {
                scrollViewer.ScrollToVerticalOffset(state.TargetOffset);
                state.IsAnimating = false;
                continue;
            }

            var stepRatio = 1d - Math.Pow(0.001d, deltaSeconds / state.ResponseSeconds);
            scrollViewer.ScrollToVerticalOffset(Math.Clamp(current + distance * stepRatio, 0d, scrollViewer.ScrollableHeight));
            anyAnimating = true;
        }

        if (!anyAnimating && _smoothScrollRenderingHooked)
        {
            CompositionTarget.Rendering -= SmoothScroll_Rendering;
            _smoothScrollRenderingHooked = false;
        }
    }

    private sealed class SmoothScrollState(double targetOffset, double responseSeconds)
    {
        public double TargetOffset { get; set; } = targetOffset;
        public bool IsAnimating { get; set; }
        public TimeSpan LastFrameTime { get; set; }
        public double ResponseSeconds { get; set; } = responseSeconds;
    }
}

internal sealed class ReferenceEqualityComparer : IEqualityComparer<UsageSession>
{
    public static ReferenceEqualityComparer Instance { get; } = new();

    public bool Equals(UsageSession? x, UsageSession? y) => ReferenceEquals(x, y);

    public int GetHashCode(UsageSession obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
