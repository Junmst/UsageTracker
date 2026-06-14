using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using UsageTrackerNative.TimeDistribution;


namespace UsageTrackerNative;

public partial class MainWindow : Window, ITrayHost
{
    private const string ManualUnclassifiedLabel = "未分类";
    private const string EmptySubjectLabel = "空分类";
    private const string NullSubjectMenuTag = "__NULL_SUBJECT__";
    private static readonly TimeSpan DashboardRefreshInterval = TimeSpan.FromSeconds(15);

    private enum SessionSubjectScope
    {
        CurrentDate,
        AllHistory
    }

    private enum AverageDailyRange
    {
        RecentSevenDays,
        CurrentWeek,
        CurrentMonth,
        All
    }

    private readonly ObservableCollection<UsageSession> _sessions = new();
    private readonly ObservableCollection<ProcessSummary> _summaries = new();
    private readonly ObservableCollection<SubjectTreeSummary> _subjectSummaries = new();
    private readonly ObservableCollection<string> _majorSubjectOptions = new();
    private readonly ObservableCollection<string> _subjectOptions = new();
    private readonly ObservableCollection<string> _parentSubjectOptions = new();
    private readonly ObservableCollection<string> _keywordRuleSubjects = new();
    private readonly ObservableCollection<string> _keywordRules = new();
    private bool _isSyncingKeywordRuleSelection;
    private readonly Dictionary<string, HashSet<string>> _subjectSearchGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _timeDistributionSubjectGroups = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (string Major, string? Parent, string? Child)>? _subjectPathMap;
    private string? _selectedKeywordRuleSubject;
    private string? _timeDistributionSubjectFilter;
    private AverageDailyRange _averageDailyRange = AverageDailyRange.All;
    private SessionSearchMode _sessionSearchMode = SessionSearchMode.All;
    private SessionSubjectScope _sessionSubjectScope = SessionSubjectScope.CurrentDate;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _transferMenuCloseTimer;
    private UsageTrackerService? _trackerService;
    private Popup? _activeTransferPopup;
    private Border? _activeTransferFlyout;
    private bool _allowClose;
    private UsageSession? _activeSession;
    private DateTime _selectedDate = DateTime.Today;
    private CompactSessionWindow? _compactSessionWindow;
    private string _sessionSearchText = string.Empty;
    private bool _isTimeDistributionMonthView;
    private UsageSession? _highlightedSession;
    private int _highlightSessionVersion;
    private Action? _undoSessionAction;
    private bool _isDraggingSessionSelection;
    private System.Windows.Point _sessionsSelectionStartPoint;
    private Rect _sessionsSelectionRect;
    private Rect _pendingSessionsSelectionRect;
    private bool _sessionsSelectionRectUpdateQueued;
    private HashSet<UsageSession> _sessionsSelectionAnchor = new();
    private ScrollViewer? _sessionsScrollViewer;
    private bool _isDraggingSessionsScrollThumb;
    private double _sessionsScrollThumbDragStartY;
    private double _sessionsScrollViewerDragStartOffset;
    private double _sessionsDragThumbVisualTop;
    private double _sessionsDragThumbTargetOffset;
    private double _sessionsDragThumbVisualOffset;
    private double _sessionsDragScrollTargetOffset;
    private bool _hasSessionsDragScrollTarget;
    private TimeSpan _lastSessionsDragFrameTime = TimeSpan.Zero;
    private bool _isRefreshingDashboard;
    private bool _dashboardRefreshPending;
    private DateTime _lastDashboardRefresh = DateTime.MinValue;
    private DateTime _lastHeavyDashboardRefresh = DateTime.MinValue;
    private DashboardMetricSnapshot? _cachedMetricSnapshot;
    private DateTime _cachedMetricSnapshotDate = DateTime.MinValue;
    private AverageDailyRange _cachedMetricSnapshotRange;
    private int _sessionLoadVersion;
    private DateTime _cachedTimeDistributionStart = DateTime.MinValue;
    private DateTime _cachedTimeDistributionEnd = DateTime.MinValue;
    private bool _cachedTimeDistributionIncludesToday;
    private IReadOnlyList<UsageSession>? _cachedTimeDistributionSessions;
    private bool _timeDistributionRenderQueued;
    private IReadOnlyCollection<UsageSession>? _pendingTimeDistributionSessions;
    private readonly Dictionary<ScrollViewer, SmoothScrollState> _smoothScrollStates = new();
    private bool _smoothScrollRenderingHooked;
    private const double DefaultSmoothScrollResponseSeconds = 0.20d;
    private System.Windows.Input.KeyGesture? _manualIdleShortcut;
    private IntPtr _windowHandle;
    private bool _manualIdleHotkeyRegistered;
    private const int ManualIdleHotkeyId = 0x534A;
    private const int WmHotkey = 0x0312;
    private const string PersonalizeRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";
    private bool _isFollowingSystemTheme;
    private bool _isLightTheme = true;
    private readonly AppLoadingState _loadingState = new();



    public MainWindow()
    {
        InitializeComponent();

        SessionsGrid.ItemsSource = _sessions;
        SummaryItemsControl.ItemsSource = _summaries;
        SubjectItemsControl.ItemsSource = _subjectSummaries;
        ParentSubjectList.ItemsSource = _majorSubjectOptions;
        ParentSubjectComboBox.ItemsSource = _majorSubjectOptions;
        ChildSubjectList.ItemsSource = _parentSubjectOptions;
        GrandChildParentComboBox.ItemsSource = _parentSubjectOptions;
        KeywordRuleList.ItemsSource = _keywordRules;

        _trackerService = new UsageTrackerService();

        _uiTimer = new DispatcherTimer
        {
            Interval = DashboardRefreshInterval
        };
        _uiTimer.Tick += UiTimer_Tick;

        _transferMenuCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _transferMenuCloseTimer.Tick += TransferMenuCloseTimer_Tick;

        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;

        _loadingState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppLoadingState.IsLoading))
            {
                Dispatcher.InvokeAsync(() =>
                {
                    LoadingIndicator.Visibility = _loadingState.IsLoading
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                });
            }
        };
    }


    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (!ShouldAutoRefreshDashboard())
        {
            return;
        }

        if (_dashboardRefreshPending)
        {
            RequestDashboardRefresh(force: true);
        }
        else
        {
            RequestDashboardRefresh();
        }
    }

    private bool ShouldAutoRefreshDashboard()
    {
        return _selectedDate.Date == DateTime.Today;
    }


    private async void RequestDashboardRefresh(bool force = false)
    {
        if (IsUiScrollingOrDragging())
        {
            _dashboardRefreshPending = true;
            return;
        }

        if (!force && DateTime.Now - _lastDashboardRefresh < TimeSpan.FromMilliseconds(2500))
        {
            _dashboardRefreshPending = true;
            return;
        }

        if (_isRefreshingDashboard)
        {
            _dashboardRefreshPending = true;
            return;
        }

        _dashboardRefreshPending = false;
        _isRefreshingDashboard = true;
        try
        {
            await RefreshDashboardAsync();
            _lastDashboardRefresh = DateTime.Now;
        }
        finally
        {
            _isRefreshingDashboard = false;
        }
    }

    private bool IsUiScrollingOrDragging()
    {
        return _isDraggingSessionsScrollThumb
            || _smoothScrollStates.Values.Any(state => state.IsAnimating);
    }

    private void ThemeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeModeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string theme)
        {
            ApplyTheme(theme);
        }
    }

    private void SelectThemeMode(string theme)
    {
        ThemeModeComboBox.SelectionChanged -= ThemeModeComboBox_SelectionChanged;
        try
        {
            foreach (var item in ThemeModeComboBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, theme, StringComparison.OrdinalIgnoreCase))
                {
                    ThemeModeComboBox.SelectedItem = item;
                    return;
                }
            }
        }
        finally
        {
            ThemeModeComboBox.SelectionChanged += ThemeModeComboBox_SelectionChanged;
        }
    }

    private async void ApplyTheme(string theme, bool save = true)
    {
        var followSystem = string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase);
        var isLight = followSystem ? IsSystemLightTheme() : string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        _isLightTheme = isLight;
        ApplyThemePalette(isLight, _trackerService.ThemeAccentColor);

        SelectThemeMode(followSystem ? "System" : isLight ? "Light" : "Dark");
        ApplyTitleBarTheme(isLight);
        RefreshTimeDistributionViewState();
        _timeDistributionRenderQueued = false;
        _pendingTimeDistributionSessions = null;
        if (TimeDistributionControl is not null && TimeDistributionControl.ActualWidth > 0)
        {
            UpdateTimeDistribution(await GetTimeDistributionSessionsAsync(includeActiveSession: true));
        }
        _isFollowingSystemTheme = followSystem;
        if (save)
        {
            _trackerService.SetTheme(followSystem ? "System" : isLight ? "Light" : "Dark");
        }
    }

    public static void ApplyThemePaletteToApplication(bool isLight, string accentColor)
    {
        ApplyThemeResources(isLight, accentColor);
    }

    private void ApplyThemePalette(bool isLight, string accentColor)
    {
        ApplyThemeResources(isLight, accentColor);
        ThemeAccentButton.Foreground = ThemeBrush("AccentTextBrush");
        RefreshBottomPanelEntryButtons();
        RefreshTimeDistributionViewState();
    }

    private static void ApplyThemeResources(bool isLight, string accentColor)
    {
        var accent = ParseThemeColor(accentColor, System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28));
        var selectedBackground = isLight ? Blend(accent, Colors.White, 0.86) : Blend(accent, Colors.Black, 0.12);
        var selectedInactiveBackground = isLight ? Blend(accent, Colors.White, 0.91) : Blend(accent, Colors.Black, 0.24);
        var rowHoverBackground = isLight ? System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF5) : System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30);
        var buttonHoverBackground = isLight ? System.Windows.Media.Color.FromRgb(0xE5, 0xE5, 0xE5) : System.Windows.Media.Color.FromRgb(0x3E, 0x3E, 0x42);
        var accentText = GetReadableTextColor(accent);

        SetBrush("WindowBackgroundBrush", isLight ? 0xF7 : 0x11, isLight ? 0xF7 : 0x12, isLight ? 0xF8 : 0x17);
        SetBrush("PanelBrush", isLight ? 0xFF : 0x1A, isLight ? 0xFF : 0x1C, isLight ? 0xFF : 0x24);
        SetBrush("PanelBrushAlt", isLight ? 0xFB : 0x17, isLight ? 0xFB : 0x19, isLight ? 0xFC : 0x21);
        SetBrush("BorderBrush", isLight ? 0xE5 : 0x2A, isLight ? 0xE7 : 0x2E, isLight ? 0xEB : 0x39);
        SetBrush("PrimaryTextBrush", isLight ? 0x1F : 0xF7, isLight ? 0x23 : 0xF9, isLight ? 0x2A : 0xFF);
        SetBrush("SecondaryTextBrush", isLight ? 0x6B : 0xC2, isLight ? 0x72 : 0xC8, isLight ? 0x80 : 0xD8);
        SetBrush("HeaderBackgroundBrush", isLight ? 0xFF : 0x15, isLight ? 0xFF : 0x17, isLight ? 0xFF : 0x1E);
        SetBrush("HeaderBorderBrush", isLight ? 0xEA : 0x20, isLight ? 0xEA : 0x23, isLight ? 0xEE : 0x2D);
        SetBrush("InputBackgroundBrush", isLight ? 0xFF : 0x17, isLight ? 0xFF : 0x1A, isLight ? 0xFF : 0x22);
        SetBrush("InputBorderBrush", isLight ? 0xDD : 0x2B, isLight ? 0xE0 : 0x31, isLight ? 0xE6 : 0x40);
        SetBrush("ButtonBackgroundBrush", isLight ? 0xFF : 0x20, isLight ? 0xFF : 0x24, isLight ? 0xFF : 0x2E);
        SetBrush("ButtonBorderBrush", isLight ? 0xDD : 0x2B, isLight ? 0xE0 : 0x31, isLight ? 0xE6 : 0x40);
        SetBrush("MenuBackgroundBrush", isLight ? 0xFF : 0x1A, isLight ? 0xFF : 0x1D, isLight ? 0xFF : 0x26);
        SetBrush("MenuBorderBrush", isLight ? 0xE5 : 0x31, isLight ? 0xE7 : 0x37, isLight ? 0xEB : 0x48);
        SetBrush("MenuHoverBrush", buttonHoverBackground);
        SetBrush("ButtonHoverBackgroundBrush", buttonHoverBackground);
        SetBrush("MenuSelectedBrush", accent);
        SetBrush("AccentBlueBrush", accent);
        SetBrush("AccentGreenBrush", accent);
        SetBrush("AccentTextBrush", accentText);
        SetBrush("MetricValueBrush", isLight ? 0x1F : 0xF7, isLight ? 0x23 : 0xF9, isLight ? 0x2A : 0xFF);
        SetBrush("CategoryCardBrush", isLight ? 0xFC : 0x20, isLight ? 0xFC : 0x24, isLight ? 0xFD : 0x2E);
        SetBrush("CategoryChildCardBrush", isLight ? 0xF6 : 0x17, isLight ? 0xF7 : 0x19, isLight ? 0xFA : 0x21);
        SetBrush("CategoryBadgeBrush", isLight ? 0xEE : 0x2A, isLight ? 0xF0 : 0x2E, isLight ? 0xF4 : 0x39);
        SetBrush("CategoryBadgeTextBrush", isLight ? 0x5F : 0xC2, isLight ? 0x68 : 0xC8, isLight ? 0x79 : 0xD8);
        SetBrush("DataGridRowHoverBrush", rowHoverBackground);
        SetBrush("DataGridRowSelectedBrush", selectedBackground);
        SetBrush("DataGridRowSelectedInactiveBrush", selectedInactiveBackground);
        SetBrush("SelectedRowTextBrush", isLight ? System.Windows.Media.Color.FromRgb(0x1F, 0x23, 0x2A) : Colors.White);
        SetBrush("MenuDisabledTextBrush", isLight ? 0x9C : 0x7C, isLight ? 0xA3 : 0x84, isLight ? 0xAF : 0x98);
        SetBrush("InputHoverBorderBrush", Blend(accent, Colors.White, isLight ? 0.35 : 0.0));
        SetBrush("InputFocusBorderBrush", accent);
    }



    private void ThemeAccentButton_Click(object sender, RoutedEventArgs e)
    {
        var contextMenu = CreateDarkContextMenu();
        var title = CreateDarkMenuItem("选择主题色");
        title.IsEnabled = false;
        contextMenu.Items.Add(title);
        contextMenu.Items.Add(new Separator());

        foreach (var color in GetThemeAccentPalette())
        {
            var item = CreateThemeAccentMenuItem(color);
            item.Click += ThemeAccentMenuItem_Click;
            contextMenu.Items.Add(item);
        }

        contextMenu.Items.Add(new Separator());
        var customItem = CreateDarkMenuItem("添加自定义颜色…");
        customItem.Click += AddCustomThemeAccent_Click;
        contextMenu.Items.Add(customItem);
        contextMenu.PlacementTarget = ThemeAccentButton;
        contextMenu.IsOpen = true;
    }

    private IEnumerable<string> GetThemeAccentPalette()
    {
        var colors = new[]
        {
            "#C62828", "#E53935", "#D81B60", "#8E24AA", "#5E35B1", "#3949AB",
            "#1E88E5", "#039BE5", "#00897B", "#43A047", "#7CB342", "#F9A825",
            "#FB8C00", "#F4511E", "#6D4C41", "#546E7A"
        };

        return _trackerService.ThemeAccentRecentColors
            .Concat(colors)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24);
    }

    private MenuItem CreateThemeAccentMenuItem(string color)
    {
        var parsed = ParseThemeColor(color, System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28));
        var item = CreateDarkMenuItem(string.Empty, color);
        item.Header = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Children =
            {
                new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(parsed),
                    BorderBrush = ThemeBrush(string.Equals(color, _trackerService.ThemeAccentColor, StringComparison.OrdinalIgnoreCase) ? "PrimaryTextBrush" : "MenuBorderBrush"),
                    BorderThickness = new Thickness(string.Equals(color, _trackerService.ThemeAccentColor, StringComparison.OrdinalIgnoreCase) ? 2 : 1),
                    Margin = new Thickness(0, 0, 10, 0)
                },
                new TextBlock
                {
                    Text = string.Equals(color, _trackerService.ThemeAccentColor, StringComparison.OrdinalIgnoreCase) ? $"当前  {color}" : color,
                    Foreground = ThemeBrush("PrimaryTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = string.Equals(color, _trackerService.ThemeAccentColor, StringComparison.OrdinalIgnoreCase) ? FontWeights.SemiBold : FontWeights.Normal
                }
            }
        };
        return item;
    }

    private void ThemeAccentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string color })
        {
            ApplyThemeAccentColor(color);
        }
    }

    private void AddCustomThemeAccent_Click(object sender, RoutedEventArgs e)
    {
        var current = ParseThemeColor(_trackerService.ThemeAccentColor, System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28));
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        ApplyThemeAccentColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }

    private void ApplyThemeAccentColor(string color)
    {
        _trackerService.SetThemeAccentColor(color);
        ApplyTheme(_trackerService.Theme, save: false);
    }

    private void SubjectManagementEntryButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleBottomPanel(SubjectManagementPanel, SubjectManagementEntryButton, "分类管理");
    }

    private void HistoryToolsEntryButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleBottomPanel(HistoryToolsPanel, HistoryToolsEntryButton, "历史日期查看");
    }

    private void ToggleBottomPanel(Border panel, System.Windows.Controls.Button entryButton, string title)
    {
        var shouldShow = panel.Visibility != Visibility.Visible;
        var wasHistoryPanelVisible = HistoryToolsPanel.Visibility == Visibility.Visible;
        SubjectManagementPanel.Visibility = Visibility.Collapsed;
        HistoryToolsPanel.Visibility = Visibility.Collapsed;
        if (shouldShow)
        {
            panel.Visibility = Visibility.Visible;
        }

        // 收起历史日期查看面板时自动回到今天
        if (wasHistoryPanelVisible && !shouldShow && _selectedDate.Date != DateTime.Today)
        {
            _selectedDate = DateTime.Today;
            DatePickerTextBox.Text = _selectedDate.ToString("yyyy-MM-dd");
            RefreshDateRangeHint();
            RefreshTimeDistributionViewState();
            LoadSessionsForSelectedDate();
        }

        RefreshBottomPanelEntryButtons();
    }

    private void RefreshBottomPanelEntryButtons()
    {
        if (SubjectManagementEntryButton is not null)
        {
            var subjectPanelVisible = SubjectManagementPanel.Visibility == Visibility.Visible;
            SubjectManagementEntryButton.Background = ThemeBrush(subjectPanelVisible ? "MenuSelectedBrush" : "ButtonBackgroundBrush");
            SubjectManagementEntryButton.BorderBrush = ThemeBrush(subjectPanelVisible ? "InputFocusBorderBrush" : "InputBorderBrush");
            SubjectManagementEntryButton.Foreground = ThemeBrush(subjectPanelVisible ? "SelectedRowTextBrush" : "PrimaryTextBrush");
        }

        if (HistoryToolsEntryButton is not null)
        {
            var historyPanelVisible = HistoryToolsPanel.Visibility == Visibility.Visible;
            HistoryToolsEntryButton.Background = ThemeBrush(historyPanelVisible ? "MenuSelectedBrush" : "ButtonBackgroundBrush");
            HistoryToolsEntryButton.BorderBrush = ThemeBrush(historyPanelVisible ? "InputFocusBorderBrush" : "InputBorderBrush");
            HistoryToolsEntryButton.Foreground = ThemeBrush(historyPanelVisible ? "SelectedRowTextBrush" : "PrimaryTextBrush");
        }
    }





    private static void SetBrush(string key, int red, int green, int blue)
    {
        SetBrush(key, System.Windows.Media.Color.FromRgb((byte)red, (byte)green, (byte)blue));
    }

    private static void SetBrush(string key, System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        System.Windows.Application.Current.Resources[key] = brush;
    }

    private static System.Windows.Media.Color ParseThemeColor(string? value, System.Windows.Media.Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
    }

    private static System.Windows.Media.Color Blend(System.Windows.Media.Color source, System.Windows.Media.Color target, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(source.R + (target.R - source.R) * amount),
            (byte)Math.Round(source.G + (target.G - source.G) * amount),
            (byte)Math.Round(source.B + (target.B - source.B) * amount));
    }

    private static System.Windows.Media.Color GetReadableTextColor(System.Windows.Media.Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255d;
        return luminance > 0.62
            ? System.Windows.Media.Color.FromRgb(0x1F, 0x23, 0x2A)
            : Colors.White;
    }


    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryKey);
            if (key == null)
            {
                return false;
            }
            var value = key.GetValue(AppsUseLightThemeValueName);
            if (value is int intValue)
            {
                return intValue != 0;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }



    private void RefreshSystemThemeIfNeeded()
    {
        if (_isFollowingSystemTheme)
        {
            ApplyTheme("System", save: false);
        }
    }

    private void TrackerService_SessionChanged(object? sender, SessionChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var previousProcess = _activeSession?.ProcessName;
            var previousTitle = _activeSession?.WindowTitle;
            _activeSession = e.ActiveSession;

            if (e.ClosedSession is not null && e.ClosedSession.StartTime.Date == _selectedDate.Date)
            {
                var searchText = _sessionSearchText.Trim();
                if (MatchesSessionSearch(e.ClosedSession, searchText))
                {
                    _sessions.Insert(0, e.ClosedSession);
                    InvalidateTimeDistributionCache();
                }
            }

            if (_selectedDate.Date != DateTime.Today)
            {
                _activeSession = null;
            }

            CurrentProcessText.Text = _activeSession?.ProcessName ?? (_selectedDate.Date == DateTime.Today ? "空闲" : "历史日期");
            UpdateCompactSessionWindow();

            var activeChanged = !string.Equals(previousProcess, _activeSession?.ProcessName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previousTitle, _activeSession?.WindowTitle, StringComparison.Ordinal);
            if (e.ClosedSession is not null || activeChanged)
            {
                InvalidateDashboardMetricCache();
                RequestDashboardRefresh(force: true);
            }
        });
    }

    // 用于在数据加载完成后执行的回调
    private Action? _onSessionsLoadedCallback;

    private async Task LoadSessionsForSelectedDateAsync(Action? onLoaded = null)
    {
        var selectedDate = _selectedDate.Date;
        var searchText = _sessionSearchText.Trim();
        var loadVersion = ++_sessionLoadVersion;
        var tcs = new TaskCompletionSource();
        _onSessionsLoadedCallback = () =>
        {
            onLoaded?.Invoke();
            tcs.TrySetResult();
        };
        SelectedDateSummaryText.Text = selectedDate == DateTime.Today
            ? "当前查看：今天（正在加载）"
            : $"当前查看：{FormatDateWithWeekday(selectedDate)}（正在加载）";
        RefreshDateRangeHint();

        _ = Task.Run(async () =>
        {
            try
            {
                var records = await _trackerService.QuerySessionsByDateAsync(selectedDate);
                var sessions = records
                    .Select(ToUsageSession)
                    .Where(session => MatchesSessionSearch(session, searchText))
                    .ToList();

                await Dispatcher.InvokeAsync(() =>
                {
                    if (loadVersion != _sessionLoadVersion || _selectedDate.Date != selectedDate)
                    {
                        tcs.TrySetResult();
                        return;
                    }

                    _sessions.Clear();
                    foreach (var session in sessions)
                    {
                        _sessions.Add(session);
                    }
                    InvalidateTimeDistributionCache();
                    InvalidateDashboardMetricCache();
                    _ = PreloadTimeDistributionAsync();

                    _activeSession = selectedDate == DateTime.Today ? _trackerService.ActiveSession : null;
                    CurrentProcessText.Text = _activeSession?.ProcessName ?? (selectedDate == DateTime.Today ? "空闲" : "历史日期");
                    SelectedDateSummaryText.Text = selectedDate == DateTime.Today
                        ? "当前查看：今天（含实时更新）"
                        : $"当前查看：{FormatDateWithWeekday(selectedDate)}（历史数据）";
                    RequestDashboardRefresh(force: true);

                    var callback = _onSessionsLoadedCallback;
                    _onSessionsLoadedCallback = null;
                    callback?.Invoke();
                }, DispatcherPriority.ContextIdle);
            }
            catch
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SelectedDateSummaryText.Text = selectedDate == DateTime.Today
                        ? "当前查看：今天（加载失败）"
                        : $"当前查看：{FormatDateWithWeekday(selectedDate)}（加载失败）";
                    tcs.TrySetResult();
                }, DispatcherPriority.ContextIdle);
            }
        });

        await tcs.Task;
    }

    private void LoadSessionsForSelectedDate(Action? onLoaded = null)
    {
        _ = LoadSessionsForSelectedDateAsync(onLoaded);
    }


    private void LoadSubjectOptions()
    {
        _majorSubjectOptions.Clear();
        _subjectOptions.Clear();
        _parentSubjectOptions.Clear();
        _keywordRuleSubjects.Clear();
        _subjectSearchGroups.Clear();
        _timeDistributionSubjectGroups.Clear();

        var definitions = _trackerService.GetSubjectDefinitions();
        _subjectPathMap = BuildSubjectPathMap(definitions);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                continue;
            }

            var majorDisplay = FormatSubjectDisplay("大类", definition.Name, 0);
            _majorSubjectOptions.Add(definition.Name);
            _subjectOptions.Add(majorDisplay);
            _keywordRuleSubjects.Add(definition.Name);
            AddSubjectSearchGroup(definition.Name, definition.GetAllSubjectNames());
            AddTimeDistributionSubjectGroup(definition.Name, definition.GetAllSubjectNames());
            foreach (var parent in definition.Parents)
            {
                if (string.IsNullOrWhiteSpace(parent.Name))
                {
                    continue;
                }

                var parentDisplay = FormatSubjectDisplay("父类", parent.Name, 1);
                _subjectOptions.Add(parentDisplay);
                _keywordRuleSubjects.Add(parent.Name);
                AddSubjectSearchGroup(parent.Name, parent.GetAllSubjectNames());
                AddTimeDistributionSubjectGroup(parent.Name, parent.GetAllSubjectNames());
                foreach (var child in parent.Children)
                {
                    if (!string.IsNullOrWhiteSpace(child))
                    {
                        var childDisplay = FormatSubjectDisplay("子类", child, 2);
                        _subjectOptions.Add(childDisplay);
                        _keywordRuleSubjects.Add(child);
                        AddSubjectSearchGroup(child, [child]);
                        AddTimeDistributionSubjectGroup(child, [child]);
                    }
                }
            }
        }

        SubjectClassifier.SetSubjectDefinitions(definitions);
        if (ParentSubjectComboBox.Items.Count > 0 && ParentSubjectComboBox.SelectedIndex < 0)
        {
            ParentSubjectComboBox.SelectedIndex = 0;
        }

        RefreshChildSubjectOptions();
        KeywordRuleSubjectButton.Content = string.IsNullOrWhiteSpace(_selectedKeywordRuleSubject) ? "选择分类 ▾" : $"{_selectedKeywordRuleSubject} ▾";
        RefreshKeywordRules();
    }

    private void BuildSessionContextMenu()
    {
        var contextMenu = CreateDarkContextMenu();
        var definitions = _trackerService.GetSubjectDefinitions();

        var hideItem = CreateDarkMenuItem("删除选中记录（可撤销，Del/Backspace）");
        hideItem.Click += DeleteSelectedSession_Click;
        contextMenu.Items.Add(hideItem);
        contextMenu.Items.Add(new Separator());

        foreach (var definition in definitions)
        {
            var majorItem = CreateDarkMenuItem(definition.Name);
            var useMajorItem = CreateDarkMenuItem($"直接归到{definition.Name}", definition.Name);
            useMajorItem.Click += SetSessionSubject_Click;
            majorItem.Items.Add(useMajorItem);
            if (definition.Parents.Count > 0)
            {
                majorItem.Items.Add(new Separator());
            }

            foreach (var parent in definition.Parents)
            {
                var parentItem = CreateDarkMenuItem(parent.Name);
                var useParentItem = CreateDarkMenuItem($"直接归到{parent.Name}", parent.Name);
                useParentItem.Click += SetSessionSubject_Click;
                parentItem.Items.Add(useParentItem);
                if (parent.Children.Count > 0)
                {
                    parentItem.Items.Add(new Separator());
                }

                foreach (var child in parent.Children)
                {
                    var childItem = CreateDarkMenuItem(child, child);
                    childItem.Click += SetSessionSubject_Click;
                    parentItem.Items.Add(childItem);
                }

                majorItem.Items.Add(parentItem);
            }

            contextMenu.Items.Add(majorItem);
        }

        contextMenu.Items.Add(new Separator());
        var nullItem = CreateDarkMenuItem("设为空分类", NullSubjectMenuTag);
        nullItem.Click += SetSessionSubject_Click;
        contextMenu.Items.Add(nullItem);

        var resetItem = CreateDarkMenuItem($"设置为{ManualUnclassifiedLabel}", ManualUnclassifiedLabel);
        resetItem.Click += SetSessionSubject_Click;
        contextMenu.Items.Add(resetItem);
        contextMenu.Opened += SessionContextMenu_Opened;
        SessionsGrid.ContextMenu = contextMenu;
    }

    private void TimeDistributionFilterButton_Click(object sender, RoutedEventArgs e)
    {
        var contextMenu = CreateDarkContextMenu();

        var allItem = CreateDarkMenuItem("全部分类", string.Empty);
        allItem.Click += SetTimeDistributionFilter_Click;
        contextMenu.Items.Add(allItem);
        contextMenu.Items.Add(new Separator());

        foreach (var definition in _trackerService.GetSubjectDefinitions())
        {
            var majorItem = CreateDarkMenuItem(definition.Name);
            var useMajorItem = CreateDarkMenuItem($"仅{definition.Name}", definition.Name);
            useMajorItem.Click += SetTimeDistributionFilter_Click;
            majorItem.Items.Add(useMajorItem);
            if (definition.Parents.Count > 0)
            {
                majorItem.Items.Add(new Separator());
            }

            foreach (var parent in definition.Parents)
            {
                var parentItem = CreateDarkMenuItem(parent.Name);
                var useParentItem = CreateDarkMenuItem($"仅{parent.Name}", parent.Name);
                useParentItem.Click += SetTimeDistributionFilter_Click;
                parentItem.Items.Add(useParentItem);
                if (parent.Children.Count > 0)
                {
                    parentItem.Items.Add(new Separator());
                }

                foreach (var child in parent.Children)
                {
                    var childItem = CreateDarkMenuItem(child, child);
                    childItem.Click += SetTimeDistributionFilter_Click;
                    parentItem.Items.Add(childItem);
                }

                majorItem.Items.Add(parentItem);
            }

            contextMenu.Items.Add(majorItem);
        }

        contextMenu.PlacementTarget = TimeDistributionFilterButton;
        contextMenu.IsOpen = true;
    }

    private void AverageDailyMetricCard_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var contextMenu = CreateDarkContextMenu();
        AddAverageDailyRangeMenuItem(contextMenu, "最近（七天）", AverageDailyRange.RecentSevenDays);
        AddAverageDailyRangeMenuItem(contextMenu, "本周", AverageDailyRange.CurrentWeek);
        AddAverageDailyRangeMenuItem(contextMenu, "本月", AverageDailyRange.CurrentMonth);
        AddAverageDailyRangeMenuItem(contextMenu, "全部", AverageDailyRange.All);
        contextMenu.PlacementTarget = AverageDailyMetricCard;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void AddAverageDailyRangeMenuItem(ContextMenu contextMenu, string header, AverageDailyRange range)
    {
        var item = CreateDarkMenuItem(header);
        item.Tag = range;
        item.IsCheckable = true;
        item.IsChecked = _averageDailyRange == range;
        item.Click += SetAverageDailyRange_Click;
        contextMenu.Items.Add(item);
    }

    private void SetAverageDailyRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AverageDailyRange range })
        {
            return;
        }

        _averageDailyRange = range;
        InvalidateDashboardMetricCache();
        _ = RefreshDashboardAsync();
    }

    private void SetTimeDistributionFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        _timeDistributionSubjectFilter = string.IsNullOrWhiteSpace(menuItem.Tag as string) ? null : menuItem.Tag as string;
        TimeDistributionFilterButton.Content = _timeDistributionSubjectFilter is null ? "全部分类 ▾" : $"{_timeDistributionSubjectFilter} ▾";
        InvalidateTimeDistributionCache();
        RequestDashboardRefresh(force: true);
    }

    private void DeleteSelectedSession_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedSessions();
    }

    private void DeleteSelectedSessions()
    {
        var selectedSessions = SessionsGrid.SelectedItems.OfType<UsageSession>().ToList();
        if (selectedSessions.Count == 0 && SessionsGrid.SelectedItem is UsageSession selectedSession)
        {
            selectedSessions.Add(selectedSession);
        }

        if (selectedSessions.Count == 0)
        {
            return;
        }

        var deletedSessions = new List<UsageSessionRecord>();
        foreach (var session in selectedSessions)
        {
            var deletedSession = _trackerService.DeleteSession(session);
            if (deletedSession is not null)
            {
                deletedSessions.Add(deletedSession);
            }
        }

        if (deletedSessions.Count == 0)
        {
            return;
        }

        SetUndoSessionAction(() =>
        {
            foreach (var deletedSession in deletedSessions)
            {
                _trackerService.RestoreSession(deletedSession);
            }

            LoadSessionsForSelectedDate();
            _ = RefreshDashboardAsync();
        });
        LoadSessionsForSelectedDate();
        _ = RefreshDashboardAsync();
        RefreshSessionSelectionSummary();
    }



    private void UndoSessionAction_Click(object sender, RoutedEventArgs e)
    {
        UndoLastSessionAction();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Z && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            UndoLastSessionAction();
            e.Handled = true;
            return;
        }

        if ((e.Key == System.Windows.Input.Key.Delete || e.Key == System.Windows.Input.Key.Back) && !IsTextInputFocused())
        {
            DeleteSelectedSessions();
            e.Handled = true;
            return;
        }

        if (MatchesManualIdleShortcut(e))
        {
            EnterManualIdle();
            e.Handled = true;
        }
    }

    private void SetUndoSessionAction(Action undoAction)
    {
        _undoSessionAction = undoAction;
        UndoSessionActionButton.IsEnabled = true;
    }

    private void UndoLastSessionAction()
    {
        var undoAction = _undoSessionAction;
        if (undoAction is null)
        {
            return;
        }

        _undoSessionAction = null;
        UndoSessionActionButton.IsEnabled = false;
        undoAction();
    }

    private static bool SessionMatches(UsageSession session, string processName, string windowTitle)
    {
        return session.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)
            && session.WindowTitle.Equals(windowTitle, StringComparison.Ordinal);
    }


    private static ContextMenu CreateDarkContextMenu()
    {
        return new ContextMenu();
    }

    private static MenuItem CreateDarkMenuItem(string header, string? tag = null)
    {
        return new MenuItem
        {
            Header = header,
            Tag = tag,
            Padding = new Thickness(12, 8, 12, 8),
            ItemContainerStyle = (Style)System.Windows.Application.Current.Resources[typeof(MenuItem)]
        };
    }

    private async Task RefreshDashboardAsync(bool includeHeavy = true)
    {
        var now = DateTime.Now;
        if (_selectedDate.Date == DateTime.Today && _activeSession is not null)
        {
            _activeSession.EndTime = now;
        }

        var effectiveSessions = _sessions
            .OrderByDescending(x => x.StartTime)
            .ToList();

        if (_selectedDate.Date == DateTime.Today && _activeSession is not null)
        {
            var searchText = _sessionSearchText.Trim();
            if (MatchesSessionSearch(_activeSession, searchText))
            {
                var activeStart = _activeSession.StartTime;
                var activeProcess = _activeSession.ProcessName;
                var existingActive = effectiveSessions.FirstOrDefault(s =>
                    s.ProcessName == activeProcess
                    && Math.Abs((s.StartTime - activeStart).TotalSeconds) < 2);
                if (existingActive is not null)
                {
                    effectiveSessions.Remove(existingActive);
                }
                effectiveSessions.Insert(0, _activeSession);
            }
        }

        var anchorDate = _selectedDate.Date;
        await UpdateMetricCardsAsync(anchorDate, effectiveSessions);
        UpdateSessionHeader(effectiveSessions);

        if (!includeHeavy)
        {
            return;
        }

        UpdateSubjectSummaries(effectiveSessions);
        var distSessions = await GetTimeDistributionSessionsAsync();
        QueueTimeDistributionUpdate(distSessions);
    }

    private async Task UpdateMetricCardsAsync(DateTime anchorDate, List<UsageSession> effectiveSessions)
    {
        var snapshot = await GetDashboardMetricSnapshotAsync(anchorDate);
        var week = snapshot.Week;
        if (anchorDate == DateTime.Today && _activeSession is not null)
        {
            week += _activeSession.Duration;
        }

        TotalFocusText.Text = FormatDuration(snapshot.Total);
        AverageDailyFocusText.Text = FormatDuration(snapshot.AverageDaily);
        TodayFocusText.Text = FormatDuration(TimeSpan.FromTicks(effectiveSessions.Sum(x => x.Duration.Ticks)));
        WeekFocusText.Text = FormatDuration(week);
        SessionCountText.Text = effectiveSessions.Count.ToString();
    }

    private async Task<DashboardMetricSnapshot> GetDashboardMetricSnapshotAsync(DateTime anchorDate)
    {
        var normalizedDate = anchorDate.Date;
        if (_cachedMetricSnapshot is { } cached
            && _cachedMetricSnapshotDate == normalizedDate
            && _cachedMetricSnapshotRange == _averageDailyRange)
        {
            SetAverageDailyRangeTitleScopes(normalizedDate);
            return cached;
        }

        var rangeSessionsTask = GetSessionsForAverageDailyRangeAsync(normalizedDate);
        var weekTask = CalculateWeekUsageAsync(normalizedDate);
        await Task.WhenAll(rangeSessionsTask, weekTask);

        var rangeSessions = rangeSessionsTask.Result;
        var total = TimeSpan.FromTicks(rangeSessions.Sum(session => session.Duration.Ticks));
        var dailyTicks = rangeSessions
            .Where(session => session.Duration.Ticks > 0)
            .GroupBy(session => session.StartTime.Date)
            .Select(group => group.Sum(session => session.Duration.Ticks))
            .ToList();
        var averageDaily = dailyTicks.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)dailyTicks.Average());
        var week = weekTask.Result;

        SetAverageDailyRangeTitleScopes(normalizedDate);
        var snapshot = new DashboardMetricSnapshot(total, averageDaily, week);
        _cachedMetricSnapshot = snapshot;
        _cachedMetricSnapshotDate = normalizedDate;
        _cachedMetricSnapshotRange = _averageDailyRange;
        return snapshot;
    }

    private void SetAverageDailyRangeTitleScopes(DateTime anchorDate)
    {
        var scopeText = _averageDailyRange switch
        {
            AverageDailyRange.RecentSevenDays => $"（截至{FormatDateWithWeekday(anchorDate)}最近七天）",
            AverageDailyRange.CurrentWeek => $"（{FormatDateWithWeekday(anchorDate)}所在周）",
            AverageDailyRange.CurrentMonth => $"（{FormatMonthLabel(anchorDate)}）",
            _ => $"（截至{FormatDateWithWeekday(anchorDate)}全部）"
        };
        SetMetricTitleScope(TotalTitleText, scopeText);
        SetMetricTitleScope(AverageDailyTitleText, scopeText);
    }

    private void InvalidateDashboardMetricCache()
    {
        _cachedMetricSnapshot = null;
        _cachedMetricSnapshotDate = DateTime.MinValue;
    }

    private readonly record struct DashboardMetricSnapshot(TimeSpan Total, TimeSpan AverageDaily, TimeSpan Week);

    private static UsageSession ToUsageSession(UsageSessionRecord record)
    {
        return new UsageSession
        {
            Id = record.Id,
            ProcessName = record.ProcessName,
            WindowTitle = record.WindowTitle,
            StartTime = record.StartTime,
            EndTime = record.EndTime ?? record.StartTime,
            ManualSubject = record.ManualSubject,
            ParallelActivities = record.ParallelActivities?.Select(x => x.Clone()).ToList() ?? []
        };
    }

    private void UpdateSubjectSummaries(List<UsageSession> effectiveSessions)
    {
        var grouped = effectiveSessions
            .GroupBy(x => x.ProcessName)
            .Select(g => new ProcessSummary
            {
                ProcessName = g.Key,
                TotalDuration = TimeSpan.FromTicks(g.Sum(x => x.Duration.Ticks))
            })
            .OrderByDescending(x => x.TotalDuration)
            .ToList();

        _summaries.Clear();
        var max = Math.Max(1d, grouped.FirstOrDefault()?.TotalDuration.TotalMilliseconds ?? 1d);
        foreach (var item in grouped)
        {
            item.BarRatio = item.TotalDuration.TotalMilliseconds / max;
            _summaries.Add(item);
        }

        var majorOrder = _subjectSummaries
            .Select((x, index) => new { x.SubjectName, index })
            .ToDictionary(x => x.SubjectName, x => x.index, StringComparer.OrdinalIgnoreCase);
        var parentOrder = _subjectSummaries
            .SelectMany(x => x.Children.Select((child, index) => new { Key = $"{x.SubjectName}|{child.SubjectName}", index }))
            .ToDictionary(x => x.Key, x => x.index, StringComparer.OrdinalIgnoreCase);
        var expandedState = _subjectSummaries.ToDictionary(x => x.SubjectName, x => x.IsExpanded, StringComparer.OrdinalIgnoreCase);
        var parentExpandedState = _subjectSummaries
            .SelectMany(x => x.Children)
            .ToDictionary(x => $"{x.MajorName}|{x.SubjectName}", x => x.IsExpanded, StringComparer.OrdinalIgnoreCase);
        var subjectPathMap = _subjectPathMap ??= BuildSubjectPathMap(_trackerService.GetSubjectDefinitions());

        var subjectGrouped = effectiveSessions
            .Select(session =>
            {
                var subject = session.ManualSubject;
                if (string.IsNullOrWhiteSpace(subject) || subject == ManualUnclassifiedLabel || !subjectPathMap.TryGetValue(subject, out var path))
                {
                    return null;
                }

                return new SubjectUsageItem(session, path.Major, path.Parent, path.Child);
            })
            .Where(x => x is not null)
            .Cast<SubjectUsageItem>()
            .GroupBy(x => x.Major)
            .Select(group =>
            {
                var majorItems = group.ToList();
                var totalDuration = TimeSpan.FromTicks(majorItems.Sum(x => x.Session.Duration.Ticks));
                var parentItems = majorItems
                    .Where(x => x.Parent is not null)
                    .GroupBy(x => x.Parent!)
                    .Select(parentGroup =>
                    {
                        var parentGroupItems = parentGroup.ToList();
                        var grandChildren = parentGroupItems
                            .Where(x => x.Child is not null)
                            .GroupBy(x => x.Child!)
                            .Select(childGroup => new GrandChildSubjectSummary
                            {
                                SubjectName = childGroup.Key,
                                TotalDuration = TimeSpan.FromTicks(childGroup.Sum(x => x.Session.Duration.Ticks)),
                                SessionCount = childGroup.Count(),
                                ProcessCount = childGroup.Select(x => x.Session.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                                AccentBrush = GetAccentBrush(childGroup.Key)
                            })
                            .OrderByDescending(x => x.TotalDuration)
                            .ToList();

                        var parentSummary = new ChildSubjectSummary
                        {
                            MajorName = group.Key,
                            SubjectName = parentGroup.Key,
                            TotalDuration = TimeSpan.FromTicks(parentGroupItems.Sum(x => x.Session.Duration.Ticks)),
                            SessionCount = parentGroupItems.Count,
                            ProcessCount = parentGroupItems.Select(x => x.Session.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                            AccentBrush = GetAccentBrush(parentGroup.Key),
                            Children = new ObservableCollection<GrandChildSubjectSummary>(grandChildren)
                        };
                        parentSummary.IsExpanded = grandChildren.Count > 0 && (!parentExpandedState.TryGetValue($"{group.Key}|{parentGroup.Key}", out var wasParentExpanded) || wasParentExpanded);
                        return parentSummary;
                    })
                    .OrderByDescending(x => x.TotalDuration)
                    .ThenBy(x => parentOrder.TryGetValue($"{group.Key}|{x.SubjectName}", out var previousOrder) ? previousOrder : int.MaxValue)
                    .ToList();

                return new SubjectTreeSummary
                {
                    SubjectName = group.Key,
                    TotalDuration = totalDuration,
                    SessionCount = majorItems.Count,
                    ProcessCount = majorItems.Select(x => x.Session.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Children = new ObservableCollection<ChildSubjectSummary>(parentItems),
                    IsExpanded = parentItems.Count() > 0 && (!expandedState.TryGetValue(group.Key, out var wasExpanded) || wasExpanded)
                };
            })
            .OrderByDescending(x => x.TotalDuration)
            .ToList();

        _subjectSummaries.Clear();
        var maxSubject = Math.Max(1d, subjectGrouped.FirstOrDefault()?.TotalDuration.TotalMilliseconds ?? 1d);
        foreach (var item in subjectGrouped.Take(12))
        {
            item.BarRatio = item.TotalDuration.TotalMilliseconds / maxSubject;
            _subjectSummaries.Add(item);
        }
    }

    private static Dictionary<string, (string Major, string? Parent, string? Child)> BuildSubjectPathMap(IEnumerable<SubjectDefinition> definitions)
    {
        var map = new Dictionary<string, (string Major, string? Parent, string? Child)>(StringComparer.OrdinalIgnoreCase);
        foreach (var major in definitions)
        {
            if (string.IsNullOrWhiteSpace(major.Name))
            {
                continue;
            }

            map.TryAdd(major.Name, (major.Name, null, null));
            foreach (var parent in major.Parents)
            {
                if (string.IsNullOrWhiteSpace(parent.Name))
                {
                    continue;
                }

                map.TryAdd(parent.Name, (major.Name, parent.Name, null));
                foreach (var child in parent.Children.Where(child => !string.IsNullOrWhiteSpace(child)))
                {
                    map.TryAdd(child, (major.Name, parent.Name, child));
                }
            }
        }

        return map;
    }

    private void UpdateSessionHeader(List<UsageSession> effectiveSessions)
    {
        var selectedDate = _selectedDate.Date;
        CurrentProcessText.Text = _activeSession?.ProcessName ?? (selectedDate == DateTime.Today ? "空闲" : "历史日期");
        ProcessCountText.Text = effectiveSessions.Select(x => x.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString();
        UpdateCompactSessionWindow();
    }


    private static void SetMetricTitleScope(TextBlock textBlock, string text)
    {
        textBlock.Text = text;
        textBlock.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

        if (textBlock.Parent is not Canvas canvas)
        {
            return;
        }

        textBlock.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(textBlock, 0);
        var overflow = textBlock.DesiredSize.Width - canvas.ActualWidth;
        if (overflow <= 2)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = 0,
            To = -overflow,
            Duration = TimeSpan.FromSeconds(Math.Clamp(overflow / 18d, 3d, 8d)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(0.8),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        textBlock.BeginAnimation(Canvas.LeftProperty, animation);
    }

    private async Task<TimeSpan> CalculateTotalForAverageDailyRangeAsync(DateTime anchorDate)
    {
        var snapshot = await GetDashboardMetricSnapshotAsync(anchorDate);
        return snapshot.Total;
    }

    private async Task<TimeSpan> CalculateAverageDailyAsync(DateTime anchorDate)
    {
        var snapshot = await GetDashboardMetricSnapshotAsync(anchorDate);
        return snapshot.AverageDaily;
    }

    private async Task<TimeSpan> CalculateWeekUsageAsync(DateTime anchorDate)
    {
        var weekStart = GetWeekStart(anchorDate);
        var weekEndExclusive = weekStart.AddDays(7);
        var records = await _trackerService.QuerySessionsInRangeAsync(weekStart, weekEndExclusive);
        return TimeSpan.FromTicks(records.Sum(r =>
        {
            var dur = (r.EndTime ?? r.StartTime) - r.StartTime;
            return dur.Ticks > 0 ? dur.Ticks : 0;
        }));
    }

    private async Task<List<UsageSession>> GetSessionsForAverageDailyRangeAsync(DateTime anchorDate)
    {
        var endExclusive = anchorDate.Date.AddDays(1);
        var startInclusive = _averageDailyRange switch
        {
            AverageDailyRange.RecentSevenDays => anchorDate.Date.AddDays(-6),
            AverageDailyRange.CurrentWeek => GetWeekStart(anchorDate),
            AverageDailyRange.CurrentMonth => new DateTime(anchorDate.Year, anchorDate.Month, 1),
            _ => DateTime.MinValue
        };

        var records = await _trackerService.QuerySessionsInRangeAsync(
            startInclusive == DateTime.MinValue ? DateTime.MinValue : startInclusive,
            endExclusive);

        return records.Select(ToUsageSession).ToList();
    }

    private void Window_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // 如果鼠标在分布图区域，让 TimeDistributionControl 自行处理所有滚轮事件（含 Ctrl+缩放）
        // 使用 IsMouseOverTimeDistribution() 按鼠标位置判断，比 OriginalSource 更可靠
        // （因为 DrawingVisual 不在可视树中，OriginalSource 可能无法追溯到 TimeDistributionControl）
        if (IsMouseOverTimeDistribution() || IsMouseWheelFromNestedScrollArea(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            e.Handled = true;
        }
    }

    private void MainScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // 如果鼠标在分布图区域，让 TimeDistributionControl 自行处理所有滚轮事件（含 Ctrl+缩放）
        if (IsMouseOverTimeDistribution() || IsMouseWheelFromNestedScrollArea(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            return;
        }

        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        SmoothScrollVertical(scrollViewer, e.Delta, 0.16d);
        e.Handled = true;
    }

    private void MainScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
    }

    private void CloseOpenComboBoxDropDowns()
    {
        ParentSubjectList.IsDropDownOpen = false;
        ParentSubjectComboBox.IsDropDownOpen = false;
        ChildSubjectList.IsDropDownOpen = false;
        GrandChildParentComboBox.IsDropDownOpen = false;
        GrandChildSubjectList.IsDropDownOpen = false;
    }





    private void KeywordRuleList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(KeywordRuleList);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0d)
        {
            e.Handled = true;
            return;
        }

        SmoothScrollVertical(scrollViewer, e.Delta, 0.12d);
        e.Handled = true;
    }

    private void KeywordRuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingKeywordRuleSelection)
        {
            return;
        }

        var keywords = KeywordRuleList.SelectedItems.OfType<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        DeleteKeywordRuleTextBox.Text = string.Join(",", keywords);
        DeleteKeywordRuleTextBox.CaretIndex = DeleteKeywordRuleTextBox.Text.Length;
        KeywordRuleListButton.Content = keywords.Count == 0 ? "选择规则 ▾" : $"已选 {keywords.Count} 项 ▾";
        RefreshKeywordRuleDeleteSelectionSummary();
    }

    private void SyncKeywordRuleSelectionFromDeleteText()
    {
        if (_isSyncingKeywordRuleSelection)
        {
            return;
        }

        _isSyncingKeywordRuleSelection = true;
        try
        {
            var keywords = SplitSubjects(DeleteKeywordRuleTextBox.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            KeywordRuleList.SelectedItems.Clear();
            foreach (var keyword in _keywordRules)
            {
                if (keywords.Contains(keyword))
                {
                    KeywordRuleList.SelectedItems.Add(keyword);
                }
            }

            KeywordRuleListButton.Content = keywords.Count == 0 ? "选择规则 ▾" : $"已选 {keywords.Count} 项 ▾";
        }
        finally
        {
            _isSyncingKeywordRuleSelection = false;
        }
    }

    private bool IsMouseWheelFromNestedScrollArea(DependencyObject? source)
    {
        return IsDescendantOf(source, SessionsGrid)
            || IsDescendantOf(source, SummaryItemsControl)
            || IsDescendantOf(source, SubjectStatsScrollViewer)
            || IsDescendantOf(source, TimeDistributionControl)
            || IsDescendantOf(source, KeywordRuleList);
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void SessionsGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _sessionsScrollViewer = SessionsOuterScrollViewer;
        if (_sessionsScrollViewer is not null)
        {
            _sessionsScrollViewer.ScrollChanged -= SessionsScrollViewer_ScrollChanged;
            _sessionsScrollViewer.ScrollChanged += SessionsScrollViewer_ScrollChanged;
            UpdateSessionsCustomScrollThumb();
        }

        SessionsSelectionOverlay.Width = SessionsOuterScrollViewer.ActualWidth;
        SessionsSelectionOverlay.Height = SessionsOuterScrollViewer.ActualHeight;
    }

    private void SessionsGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isDraggingSessionsScrollThumb)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<DataGridRow>(source) is not null || FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(source) is not null || FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is not null)
        {
            return;
        }

        _isDraggingSessionSelection = true;
        _sessionsSelectionStartPoint = e.GetPosition(SessionsGrid);
        _sessionsSelectionRect = new Rect(_sessionsSelectionStartPoint, _sessionsSelectionStartPoint);
        SessionsGrid.UnselectAll();
        _sessionsSelectionAnchor = new HashSet<UsageSession>(ReferenceEqualityComparer.Instance);
        SessionsGrid.CaptureMouse();
        UpdateSessionsSelectionRectangle(_sessionsSelectionRect);
        ApplySessionsSelectionRect();
        e.Handled = true;
    }

    private void SessionsGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not UsageSession session)
        {
            return;
        }

        if (!SessionsGrid.SelectedItems.Contains(session))
        {
            SessionsGrid.UnselectAll();
            SessionsGrid.SelectedItems.Add(session);
            SessionsGrid.CurrentItem = session;
        }
    }

    private void SessionsGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)

    {
        if (!_isDraggingSessionSelection)
        {
            return;
        }

        var currentPoint = e.GetPosition(SessionsGrid);
        _sessionsSelectionRect = new Rect(_sessionsSelectionStartPoint, currentPoint);
        UpdateSessionsSelectionRectangle(_sessionsSelectionRect);
        ApplySessionsSelectionRect();
        e.Handled = true;
    }

    private void SessionsGrid_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isDraggingSessionSelection)
        {
            return;
        }

        EndSessionsSelectionDrag();
        e.Handled = true;
    }

    private void EndSessionsSelectionDrag()
    {
        _isDraggingSessionSelection = false;
        SessionsGrid.ReleaseMouseCapture();
        SessionsSelectionRectangle.Visibility = Visibility.Collapsed;
    }

    private void UpdateSessionsSelectionRectangle(Rect rect)
    {
        SessionsSelectionOverlay.Width = SessionsOuterScrollViewer.ActualWidth;
        SessionsSelectionOverlay.Height = SessionsOuterScrollViewer.ActualHeight;

        var normalized = NormalizeRect(rect);
        SessionsSelectionRectangle.Visibility = normalized.Width < 2d && normalized.Height < 2d ? Visibility.Collapsed : Visibility.Visible;
        SessionsSelectionRectangle.Width = normalized.Width;
        SessionsSelectionRectangle.Height = normalized.Height;
        Canvas.SetLeft(SessionsSelectionRectangle, normalized.Left);
        Canvas.SetTop(SessionsSelectionRectangle, normalized.Top);
    }

    private void ApplySessionsSelectionRect()
    {
        if (_sessionsSelectionRectUpdateQueued)
        {
            return;
        }

        _sessionsSelectionRectUpdateQueued = true;
        _pendingSessionsSelectionRect = _sessionsSelectionRect;
        Dispatcher.InvokeAsync(() =>
        {
            _sessionsSelectionRectUpdateQueued = false;
            ApplySessionsSelectionRectCore(_pendingSessionsSelectionRect);
        }, DispatcherPriority.Render);
    }

    private void ApplySessionsSelectionRectCore(Rect selectionRect)
    {
        var normalized = NormalizeRect(selectionRect);
        var selected = new HashSet<UsageSession>(_sessionsSelectionAnchor, ReferenceEqualityComparer.Instance);

        foreach (var row in FindVisualChildren<DataGridRow>(SessionsGrid))
        {
            if (row.Item is not UsageSession item)
            {
                continue;
            }

            var rowBounds = GetElementBounds(row, SessionsGrid);
            if (rowBounds.IntersectsWith(normalized))
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
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
        var topLeft = element.TranslatePoint(new System.Windows.Point(0, 0), relativeTo);
        return new Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T matched)
            {
                return matched;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void SessionsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isDraggingSessionsScrollThumb || (_sessionsScrollViewer is not null && _smoothScrollStates.TryGetValue(_sessionsScrollViewer, out var state) && state.IsAnimating))
        {
            return;
        }

        UpdateSessionsCustomScrollThumb();
    }

    private void UpdateSessionsCustomScrollThumb()
    {
        if (_sessionsScrollViewer is null)
        {
            return;
        }

        const double thumbHeight = 80d;
        const double topMargin = 42d;
        const double bottomMargin = 8d;
        var trackHeight = Math.Max(thumbHeight, SessionsOuterScrollViewer.ActualHeight - topMargin - bottomMargin);
        var maxThumbOffset = Math.Max(0d, trackHeight - thumbHeight);
        var ratio = _sessionsScrollViewer.ScrollableHeight <= 0d ? 0d : _sessionsScrollViewer.VerticalOffset / _sessionsScrollViewer.ScrollableHeight;
        SessionsCustomScrollThumb.Height = thumbHeight;
        SessionsCustomScrollThumb.Margin = new Thickness(0d, topMargin, 0d, 0d);
        SetSessionsCustomScrollThumbOffset(maxThumbOffset * ratio);
    }

    private void SetSessionsCustomScrollThumbOffset(double offset)
    {
        if (SessionsCustomScrollThumb.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            SessionsCustomScrollThumb.RenderTransform = transform;
        }

        transform.Y = offset;
    }

    private double GetSessionsCustomScrollThumbOffset()
    {
        return SessionsCustomScrollThumb.RenderTransform is TranslateTransform transform ? transform.Y : 0d;
    }

    private void SessionsCustomScrollThumb_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        SessionsCustomScrollThumb.Background = ThemeBrush("InputHoverBorderBrush");
        SessionsCustomScrollThumb.Width = 12d;
    }

    private void SessionsCustomScrollThumb_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingSessionsScrollThumb)
        {
            SessionsCustomScrollThumb.Background = ThemeBrush("MenuSelectedBrush");
            SessionsCustomScrollThumb.Width = 10d;
        }
    }

    private void SessionsCustomScrollThumb_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_sessionsScrollViewer is null)
        {
            return;
        }

        // 拖拽开始时，终止可能正在进行的平滑滚轮动画，避免两者同时驱动同一个 ScrollViewer
        ResetSmoothScrollState(_sessionsScrollViewer);

        _isDraggingSessionsScrollThumb = true;
        _sessionsScrollThumbDragStartY = e.GetPosition(this).Y;
        _sessionsScrollViewerDragStartOffset = _sessionsScrollViewer.VerticalOffset;
        _sessionsDragThumbVisualTop = GetSessionsCustomScrollThumbOffset();
        _sessionsDragThumbVisualOffset = _sessionsDragThumbVisualTop;
        _sessionsDragThumbTargetOffset = _sessionsDragThumbVisualTop;
        _sessionsDragScrollTargetOffset = _sessionsScrollViewer.VerticalOffset;
        _hasSessionsDragScrollTarget = false;
        _lastSessionsDragFrameTime = TimeSpan.Zero;
        CompositionTarget.Rendering -= SessionsDragScroll_Rendering;
        CompositionTarget.Rendering += SessionsDragScroll_Rendering;
        SessionsCustomScrollThumb.CaptureMouse();
        SessionsCustomScrollThumb.Background = ThemeBrush("InputFocusBorderBrush");
        SessionsCustomScrollThumb.Width = 12d;
        e.Handled = true;
    }

    private void SessionsCustomScrollThumb_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingSessionsScrollThumb || _sessionsScrollViewer is null)
        {
            return;
        }

        const double thumbHeight = 80d;
        const double topMargin = 42d;
        const double bottomMargin = 8d;
        var trackHeight = Math.Max(thumbHeight, SessionsOuterScrollViewer.ActualHeight - topMargin - bottomMargin);
        var maxThumbOffset = Math.Max(1d, trackHeight - thumbHeight);
        var deltaY = e.GetPosition(this).Y - _sessionsScrollThumbDragStartY;
        var thumbOffset = Math.Clamp(_sessionsDragThumbVisualTop + deltaY, 0d, maxThumbOffset);
        _sessionsDragThumbTargetOffset = thumbOffset;
        _sessionsDragScrollTargetOffset = Math.Clamp(thumbOffset / maxThumbOffset * _sessionsScrollViewer.ScrollableHeight, 0d, _sessionsScrollViewer.ScrollableHeight);
        _hasSessionsDragScrollTarget = true;
        e.Handled = true;
    }

    private void SessionsDragScroll_Rendering(object? sender, EventArgs e)
    {
        if (!_isDraggingSessionsScrollThumb || _sessionsScrollViewer is null)
        {
            CompositionTarget.Rendering -= SessionsDragScroll_Rendering;
            _hasSessionsDragScrollTarget = false;
            return;
        }

        var deltaSeconds = 1d / 60d;
        if (e is RenderingEventArgs renderingEventArgs)
        {
            deltaSeconds = _lastSessionsDragFrameTime == TimeSpan.Zero
                ? 1d / 60d
                : Math.Clamp((renderingEventArgs.RenderingTime - _lastSessionsDragFrameTime).TotalSeconds, 1d / 240d, 1d / 20d);
            _lastSessionsDragFrameTime = renderingEventArgs.RenderingTime;
        }

        var visualDistance = _sessionsDragThumbTargetOffset - _sessionsDragThumbVisualOffset;
        if (Math.Abs(visualDistance) < 0.4d)
        {
            _sessionsDragThumbVisualOffset = _sessionsDragThumbTargetOffset;
        }
        else
        {
            var visualStep = 1d - Math.Pow(0.001d, deltaSeconds / 0.06d);
            _sessionsDragThumbVisualOffset += visualDistance * visualStep;
        }

        SetSessionsCustomScrollThumbOffset(_sessionsDragThumbVisualOffset);

        if (!_hasSessionsDragScrollTarget)
        {
            return;
        }

        var contentDistance = _sessionsDragScrollTargetOffset - _sessionsScrollViewer.VerticalOffset;
        if (Math.Abs(contentDistance) < 1d)
        {
            _sessionsScrollViewer.ScrollToVerticalOffset(_sessionsDragScrollTargetOffset);
            return;
        }

        var contentStep = 1d - Math.Pow(0.001d, deltaSeconds / 0.16d);
        _sessionsScrollViewer.ScrollToVerticalOffset(_sessionsScrollViewer.VerticalOffset + contentDistance * contentStep);
    }

    private void SessionsCustomScrollThumb_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isDraggingSessionsScrollThumb)
        {
            return;
        }

        _isDraggingSessionsScrollThumb = false;
        CompositionTarget.Rendering -= SessionsDragScroll_Rendering;
        if (_sessionsScrollViewer is not null && _hasSessionsDragScrollTarget)
        {
            SetSessionsCustomScrollThumbOffset(_sessionsDragThumbTargetOffset);
            _sessionsScrollViewer.ScrollToVerticalOffset(_sessionsDragScrollTargetOffset);
            ResetSmoothScrollState(_sessionsScrollViewer);
        }

        _hasSessionsDragScrollTarget = false;
        SessionsCustomScrollThumb.ReleaseMouseCapture();
        SessionsCustomScrollThumb.Background = ThemeBrush("MenuSelectedBrush");
        SessionsCustomScrollThumb.Width = 10d;
        e.Handled = true;
    }

    private void SessionsGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            return;
        }

        _sessionsScrollViewer ??= SessionsOuterScrollViewer;
        if (_sessionsScrollViewer is null)
        {
            return;
        }

        SmoothScrollVertical(_sessionsScrollViewer, e.Delta, 0.18d);
        e.Handled = true;
    }

    private void StatsScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 || sender is not ScrollViewer scrollViewer)
        {
            e.Handled = true;
            return;
        }

        SmoothScrollVertical(scrollViewer, e.Delta, 0.16d);
        e.Handled = true;
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
        if (!_smoothScrollStates.TryGetValue(scrollViewer, out var state))
        {
            state = new SmoothScrollState(scrollViewer.VerticalOffset);
            _smoothScrollStates[scrollViewer] = state;
        }
        else if (!state.IsAnimating)
        {
            state.TargetOffset = scrollViewer.VerticalOffset;
            state.LastFrameTime = TimeSpan.Zero;
        }

        state.ResponseSeconds = Math.Max(0.04d, responseSeconds);

        var current = scrollViewer.VerticalOffset;
        var effectiveWheelDelta = maxWheelDelta is null
            ? wheelDelta
            : Math.Clamp(wheelDelta, -maxWheelDelta.Value, maxWheelDelta.Value);
        var targetOffset = Math.Clamp(state.TargetOffset - effectiveWheelDelta * speed, 0d, scrollViewer.ScrollableHeight);
        if (maxTargetDistance is not null)
        {
            targetOffset = Math.Clamp(targetOffset, current - maxTargetDistance.Value, current + maxTargetDistance.Value);
        }
        if (immediateResponseRatio > 0d)
        {
            var immediateOffset = current + (targetOffset - current) * Math.Clamp(immediateResponseRatio, 0d, 1d);
            scrollViewer.ScrollToVerticalOffset(Math.Clamp(immediateOffset, 0d, scrollViewer.ScrollableHeight));
            current = scrollViewer.VerticalOffset;
        }

        state.TargetOffset = Math.Clamp(targetOffset, 0d, scrollViewer.ScrollableHeight);
        if (state.IsAnimating)
        {
            return;
        }

        if (Math.Abs(state.TargetOffset - current) < 0.35d)
        {
            scrollViewer.ScrollToVerticalOffset(state.TargetOffset);
            state.IsAnimating = false;
            state.LastFrameTime = TimeSpan.Zero;
            return;
        }

        state.IsAnimating = true;
        state.LastFrameTime = TimeSpan.Zero;
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
        else
        {
            _smoothScrollStates[scrollViewer] = new SmoothScrollState(scrollViewer.VerticalOffset);
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

            // 如果正在拖拽 sessions 滚动条，跳过该 ScrollViewer 的平滑滚动动画
            if (_isDraggingSessionsScrollThumb && ReferenceEquals(scrollViewer, _sessionsScrollViewer))
            {
                continue;
            }

            if (!state.IsAnimating)
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
            scrollViewer.ScrollToVerticalOffset(current + distance * stepRatio);
            if (ReferenceEquals(scrollViewer, _sessionsScrollViewer))
            {
                UpdateSessionsCustomScrollThumb();
            }

            anyAnimating = true;
        }

        if (!anyAnimating)
        {
            CompositionTarget.Rendering -= SmoothScroll_Rendering;
            _smoothScrollRenderingHooked = false;
        }
    }

    private sealed class SmoothScrollState
    {
        public SmoothScrollState(double targetOffset)
        {
            TargetOffset = targetOffset;
            ResponseSeconds = DefaultSmoothScrollResponseSeconds;
        }

        public double TargetOffset { get; set; }
        public bool IsAnimating { get; set; }
        public TimeSpan LastFrameTime { get; set; }
        public double ResponseSeconds { get; set; }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private bool IsMouseOverTimeDistribution()
    {
        if (TimeDistributionControl is null) return false;
        var point = System.Windows.Input.Mouse.GetPosition(TimeDistributionControl);
        return point.X >= 0 && point.X <= TimeDistributionControl.ActualWidth && point.Y >= 0 && point.Y <= TimeDistributionControl.ActualHeight;
    }

    private void QueueTimeDistributionUpdate(IReadOnlyCollection<UsageSession> sessions)
    {
        _pendingTimeDistributionSessions = sessions;
        if (_timeDistributionRenderQueued)
        {
            return;
        }

        _timeDistributionRenderQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _timeDistributionRenderQueued = false;
            var pendingSessions = _pendingTimeDistributionSessions;
            _pendingTimeDistributionSessions = null;
            if (pendingSessions is not null)
            {
                UpdateTimeDistribution(pendingSessions);
            }
        }, DispatcherPriority.Render);
    }

    private void UpdateTimeDistribution(IReadOnlyCollection<UsageSession> sessions)
    {
        if (TimeDistributionControl is null) return;

        var visibleDates = GetTimeDistributionDates().ToList();

        var filteredSessions = sessions
            .Where(x => x.Duration > System.TimeSpan.Zero)
            .Where(MatchesTimeDistributionSubjectFilter)
            .ToList();

        TimeDistributionControl.Sessions = filteredSessions;
        TimeDistributionControl.VisibleDates = visibleDates;
        TimeDistributionControl.UpdateRender();
    }

    private async Task<IReadOnlyList<UsageSession>> GetTimeDistributionSessionsAsync(bool includeActiveSession = true)
    {
        var dates = GetTimeDistributionDates().ToList();
        if (dates.Count == 0)
        {
            return [];
        }

        var start = GetTimeDistributionRangeStart(dates.Min());
        var end = GetTimeDistributionRangeEnd(dates.Max());
        var includesToday = dates.Contains(GetTimeDistributionDate(DateTime.Now));
        if (_cachedTimeDistributionSessions is null
            || _cachedTimeDistributionStart != start
            || _cachedTimeDistributionEnd != end
            || _cachedTimeDistributionIncludesToday != includesToday)
        {
            _cachedTimeDistributionStart = start;
            _cachedTimeDistributionEnd = end;
            _cachedTimeDistributionIncludesToday = includesToday;
            var records = await _trackerService.QuerySessionsInRangeAsync(start, end);
            _cachedTimeDistributionSessions = records.Select(ToUsageSession).ToList();
        }

        if (!includeActiveSession || !includesToday || _activeSession is null)
        {
            return _cachedTimeDistributionSessions;
        }

        var sessions = _cachedTimeDistributionSessions.ToList();
        sessions.Add(_activeSession);
        return sessions;
    }

    private void InvalidateTimeDistributionCache()
    {
        _cachedTimeDistributionSessions = null;
    }

    /// <summary>
    /// 异步预加载分布图数据（不阻塞 UI）
    /// </summary>
    private async Task PreloadTimeDistributionAsync()
    {
        try
        {
            var dates = await _trackerService.QueryActiveDatesAsync(
                DateTime.Today.AddMonths(-1), DateTime.Today.AddDays(1));
            // 数据已缓存于 _cachedTimeDistributionSessions 中（在 GetTimeDistributionSessions 按需加载）
            // 此方法仅触发日期列表的预热
        }
        catch
        {
            // 预加载失败不影响主流程
        }
    }

    private IEnumerable<DateTime> GetTimeDistributionDates()
    {
        if (_isTimeDistributionMonthView)
        {
            var firstDay = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            var days = DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month);
            return Enumerable.Range(0, days)
                .Select(offset => firstDay.AddDays(offset))
                .Reverse();
        }

        return Enumerable.Range(0, 7)
            .Select(offset => DateTime.Today.AddDays(-offset));
    }

    private static DateTime GetTimeDistributionRangeStart(DateTime date)
    {
        return date.Date.AddHours(4);
    }

    private static DateTime GetTimeDistributionRangeEnd(DateTime date)
    {
        return GetTimeDistributionRangeStart(date).AddDays(1).AddMilliseconds(-1);
    }

    private static DateTime GetTimeDistributionDate(DateTime time)
    {
        var date = time.Date;
        return time.TimeOfDay < TimeSpan.FromHours(4) ? date.AddDays(-1) : date;
    }

    private static DateTime GetWeekStart(DateTime date)
    {
        var day = date.Date;
        return day.DayOfWeek == DayOfWeek.Sunday
            ? day.AddDays(-6)
            : day.AddDays(-(int)day.DayOfWeek + (int)DayOfWeek.Monday);
    }

    private static string FormatMonthLabel(DateTime date)
    {
        return date.Year == DateTime.Today.Year
            ? $"{date.Month}月"
            : $"{date.Year}年{date.Month}月";
    }

    private static string FormatTimeDistributionDateLabel(DateTime date)
    {
        if (date.Date == DateTime.Today)
        {
            return $"今天（{GetChineseWeekday(date)}）";
        }

        if (date.Date == DateTime.Today.AddDays(-1))
        {
            return $"昨天（{GetChineseWeekday(date)}）";
        }

        return FormatDateWithWeekday(date);
    }

    private void RefreshDateRangeHint()
    {
        var earliestDate = _trackerService.EarliestSessionDate;
        var earliestText = earliestDate is null ? "暂无历史数据" : FormatDateWithWeekday(earliestDate.Value);
        DateRangeHintText.Text = $"日期格式：2026-05-04；最早可查看：{earliestText}；最多保留 {_trackerService.DataRetentionDays} 天数据";
    }

    private static string FormatDateWithWeekday(DateTime date)
    {
        var dateText = date.Year == DateTime.Today.Year
            ? $"{date.Month}月{date.Day}日"
            : $"{date.Year}年{date.Month}月{date.Day}日";
        return $"{dateText}（{GetChineseWeekday(date)}）";
    }

    private static string GetChineseWeekday(DateTime date)
    {
        return date.DayOfWeek switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周天"
        };
    }

    private System.Windows.Media.Brush ThemeBrush(string key)
    {
        return (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[key];
    }

    private System.Windows.Controls.ToolTip CreateTimeDistributionToolTip(UsageSession session)
    {
        var tooltipStack = new System.Windows.Controls.StackPanel();
        tooltipStack.Children.Add(CreateTooltipText(session.ProcessName, 13, FontWeights.SemiBold));
        tooltipStack.Children.Add(CreateTooltipText(session.WindowTitle, 11, FontWeights.Normal, new Thickness(0, 6, 0, 0)));
        tooltipStack.Children.Add(CreateTooltipText($"{session.StartText} - {session.EndText}", 11, FontWeights.Normal, new Thickness(0, 4, 0, 0)));
        tooltipStack.Children.Add(CreateTooltipText(session.DurationText, 11, FontWeights.SemiBold, new Thickness(0, 6, 0, 0)));

        return new System.Windows.Controls.ToolTip
        {
            Content = new Border
            {
                Background = ThemeBrush("MenuBackgroundBrush"),
                BorderBrush = ThemeBrush("MenuBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                MaxWidth = 300,
                Child = tooltipStack
            },
            HasDropShadow = false,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };
    }

    private TextBlock CreateTooltipText(string text, double fontSize, FontWeight fontWeight, Thickness margin = default)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = ThemeBrush(fontWeight == FontWeights.SemiBold ? "PrimaryTextBrush" : "SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = margin
        };
    }

    private void RecentSevenDaysView_Click(object sender, RoutedEventArgs e)
    {
        _isTimeDistributionMonthView = false;
        RefreshTimeDistributionViewState();
        RequestDashboardRefresh(force: true);
    }

    private void MonthView_Click(object sender, RoutedEventArgs e)
    {
        _isTimeDistributionMonthView = true;
        RefreshTimeDistributionViewState();
        RequestDashboardRefresh(force: true);
        
        // 滚动到今天的位置
        Dispatcher.InvokeAsync(() =>
        {
            var today = DateTime.Today;
            var dates = GetTimeDistributionDates().ToList();
            var todayIndex = dates.IndexOf(today);

            if (todayIndex >= 0 && TimeDistributionControl is not null)
            {
                const double rowHeight = 38d;
                var targetOffset = todayIndex * rowHeight;
                TimeDistributionControl.OffsetY = targetOffset;
            }
        }, DispatcherPriority.Loaded);
    }

    private void RefreshTimeDistributionViewState()
    {
        TimeDistributionTitleText.Text = LocalizationService.Instance.Get("TimeDist.UsageDurationDistribution");
        RecentSevenDaysButton.Background = _isTimeDistributionMonthView ? ThemeBrush("ButtonBackgroundBrush") : ThemeBrush("MenuSelectedBrush");
        RecentSevenDaysButton.BorderBrush = _isTimeDistributionMonthView ? ThemeBrush("ButtonBorderBrush") : ThemeBrush("InputFocusBorderBrush");
        RecentSevenDaysButton.Foreground = _isTimeDistributionMonthView ? ThemeBrush("PrimaryTextBrush") : ThemeBrush("AccentTextBrush");
        MonthViewButton.Background = _isTimeDistributionMonthView ? ThemeBrush("MenuSelectedBrush") : ThemeBrush("ButtonBackgroundBrush");
        MonthViewButton.BorderBrush = _isTimeDistributionMonthView ? ThemeBrush("InputFocusBorderBrush") : ThemeBrush("ButtonBorderBrush");
        MonthViewButton.Foreground = _isTimeDistributionMonthView ? ThemeBrush("AccentTextBrush") : ThemeBrush("PrimaryTextBrush");
        TimeDistributionFilterButton.Background = ThemeBrush("ButtonBackgroundBrush");
        TimeDistributionFilterButton.BorderBrush = ThemeBrush("ButtonBorderBrush");
        TimeDistributionFilterButton.Foreground = ThemeBrush("PrimaryTextBrush");

        // 切换视图时重置缩放
        if (TimeDistributionControl is not null)
        {
            if (!_isTimeDistributionMonthView)
            {
                TimeDistributionControl.ResetView();
            }
        }
    }

    private static double GetTimeDistributionStepMinutes(double zoom)
    {
        if (zoom >= 7.2d)
        {
            return 10d;
        }

        if (zoom >= 4.8d)
        {
            return 20d;
        }

        if (zoom >= 3d)
        {
            return 30d;
        }

        if (zoom >= 1.6d)
        {
            return 60d;
        }

        return 120d;
    }

    private bool MatchesTimeDistributionSubjectFilter(UsageSession session)
    {
        if (string.IsNullOrWhiteSpace(_timeDistributionSubjectFilter))
        {
            return true;
        }

        var subject = string.IsNullOrWhiteSpace(session.ManualSubject)
            ? EmptySubjectLabel
            : session.ManualSubject;

        if (string.Equals(_timeDistributionSubjectFilter, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_timeDistributionSubjectFilter, EmptySubjectLabel, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(subject, EmptySubjectLabel, StringComparison.OrdinalIgnoreCase);
        }

        if (!_timeDistributionSubjectGroups.TryGetValue(_timeDistributionSubjectFilter, out var subjects))
        {
            subjects = new HashSet<string>([_timeDistributionSubjectFilter], StringComparer.OrdinalIgnoreCase);
        }

        return subjects.Contains(subject);
    }

    private void TimeDistributionControl_SessionClicked(object sender, UsageSession session)
    {
        SelectSessionFromTimeDistribution(session);
    }

    private void SelectSessionFromTimeDistribution(UsageSession session)
    {
        var highlightVersion = ++_highlightSessionVersion;
        var sessionDate = GetTimeDistributionDate(session.StartTime);
        var needsDateChange = _selectedDate.Date != sessionDate;
        
        // 保存会话信息，用于在数据加载后找到对应的会话
        var targetStartTime = session.StartTime;
        var targetEndTime = session.EndTime;
        var targetProcessName = session.ProcessName;
        var targetWindowTitle = session.WindowTitle;

        // 高亮会话的方法
        Action highlightSession = () =>
        {
            // 重新在加载后的列表中找到对应的会话 - 使用更宽松的匹配条件
            var target = _sessions.FirstOrDefault(item =>
                item.StartTime == targetStartTime
                && string.Equals(item.ProcessName, targetProcessName, StringComparison.OrdinalIgnoreCase));
            
            if (target is null)
            {
                // 如果完全匹配找不到，尝试只匹配开始时间
                target = _sessions.FirstOrDefault(item => item.StartTime == targetStartTime);
            }
            
            if (target is null)
            {
                return;
            }

            SessionsGrid.SelectedItem = target;
            if (_sessionsScrollViewer is not null)
            {
                ResetSmoothScrollState(_sessionsScrollViewer);
            }

            HighlightSessionRow(target, highlightVersion);
        };
        
        // 切换到会话对应的日期，用于显示会话列表
        if (needsDateChange)
        {
            _selectedDate = sessionDate;
            DatePickerTextBox.Text = _selectedDate.ToString("yyyy-MM-dd");
            LoadSessionsForSelectedDate(highlightSession);
        }
        else
        {
            // 不需要切换日期，直接高亮
            highlightSession();
        }
    }

    private void HighlightSessionRow(UsageSession target, int highlightVersion)
    {
        if (highlightVersion != _highlightSessionVersion)
        {
            return;
        }

        ClearHighlightedSessionRow(invalidatePendingHighlight: false);
        _highlightedSession = target;
        target.IsHighlighted = true;

        if (SessionsGrid.ItemContainerGenerator.ContainerFromItem(target) is DataGridRow row)
        {
            row.BringIntoView();
            UpdateSessionsCustomScrollThumb();
        }
        else
        {
            SessionsGrid.Dispatcher.InvokeAsync(() =>
            {
                if (highlightVersion != _highlightSessionVersion) return;
                SessionsGrid.ScrollIntoView(target);
                if (SessionsGrid.ItemContainerGenerator.ContainerFromItem(target) is DataGridRow generatedRow)
                {
                    if (_sessionsScrollViewer is not null) ResetSmoothScrollState(_sessionsScrollViewer);
                    UpdateSessionsCustomScrollThumb();
                }
            }, DispatcherPriority.Background);
        }
    }

    private void ClearHighlightedSessionRow(bool invalidatePendingHighlight = true)
    {
        if (invalidatePendingHighlight)
        {
            _highlightSessionVersion++;
        }

        if (_highlightedSession is not null)
        {
            _highlightedSession.IsHighlighted = false;
            _highlightedSession = null;
        }

        foreach (var session in _sessions.Where(item => item.IsHighlighted).ToList())
        {
            session.IsHighlighted = false;
        }
    }

    private void ClearTimeDistributionSessionSelection()
    {
        ClearHighlightedSessionRow();
        SessionsGrid.SelectedItem = null;
        // 不再重置日期，避免日期来回跳变
        System.Windows.Input.Keyboard.ClearFocus();
    }

    private void RestoreSessionsToCurrentMoment()
    {
        var changedDate = _selectedDate.Date != DateTime.Today;
        if (changedDate)
        {
            _selectedDate = DateTime.Today;
            DatePickerTextBox.Text = _selectedDate.ToString("yyyy-MM-dd");
            LoadSessionsForSelectedDate();
        }

        Dispatcher.InvokeAsync(() =>
        {
            _sessionsScrollViewer ??= SessionsOuterScrollViewer;
            if (_sessionsScrollViewer is not null)
            {
                _sessionsScrollViewer.ScrollToVerticalOffset(0d);
                ResetSmoothScrollState(_sessionsScrollViewer);
            }

            UpdateSessionsCustomScrollThumb();
            if (!changedDate)
            {
                RequestDashboardRefresh(force: true);
            }
        }, DispatcherPriority.Loaded);
    }

    private void SessionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSessionSelectionSummary();
    }

    private void RefreshSessionSelectionSummary()
    {
        var selectedSessions = SessionsGrid.SelectedItems.OfType<UsageSession>().ToList();
        if (selectedSessions.Count == 0)
        {
            SessionSelectionSummaryText.Text = LocalizationService.Instance.Get("Sessions.SelectedNone");
        }
        else
        {
            SessionSelectionSummaryText.Text = string.Format(LocalizationService.Instance.Get("Sessions.Selected"), selectedSessions.Count);
        }

        var currentDateMode = _sessionSubjectScope == SessionSubjectScope.CurrentDate;
        SessionScopeTodayText.FontSize = currentDateMode ? 16d : 12d;
        SessionScopeTodayText.FontWeight = currentDateMode ? FontWeights.Bold : FontWeights.Normal;
        SessionScopeTodayText.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[currentDateMode ? "PrimaryTextBrush" : "SecondaryTextBrush"];
        SessionScopeHistoryText.FontSize = currentDateMode ? 12d : 16d;
        SessionScopeHistoryText.FontWeight = currentDateMode ? FontWeights.Normal : FontWeights.Bold;
        SessionScopeHistoryText.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[currentDateMode ? "SecondaryTextBrush" : "PrimaryTextBrush"];
    }

    private void SessionSubjectScopeButton_Click(object sender, RoutedEventArgs e)
    {
        _sessionSubjectScope = _sessionSubjectScope == SessionSubjectScope.CurrentDate
            ? SessionSubjectScope.AllHistory
            : SessionSubjectScope.CurrentDate;
        RefreshSessionSelectionSummary();
    }

    private void SetSessionSubject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string subject)
        {
            return;
        }

        var manualSubject = subject == NullSubjectMenuTag ? null : subject;
        if (SessionsGrid.SelectedItems.OfType<UsageSession>().Count() > 1)
        {
            ApplyBatchSessionSubject(manualSubject);
            return;
        }

        if (SessionsGrid.SelectedItem is not UsageSession session)
        {
            return;
        }

        ApplySingleSessionSubject(session, manualSubject);
    }

    private void ApplySingleSessionSubject(UsageSession session, string? manualSubject)
    {
        var previousSubject = session.ManualSubject;
        var previousActiveSubject = _activeSession is not null && SessionMatches(_activeSession, session.ProcessName, session.WindowTitle) && string.Equals(_activeSession.Id, session.Id, StringComparison.OrdinalIgnoreCase)
            ? _activeSession.ManualSubject
            : null;

        session.ManualSubject = manualSubject;
        if (_activeSession is not null && string.Equals(_activeSession.Id, session.Id, StringComparison.OrdinalIgnoreCase))
        {
            _activeSession.ManualSubject = manualSubject;
        }

        _trackerService.SetManualSubjectForDate(session, manualSubject, _selectedDate.Date);
        SetUndoSessionAction(() =>
        {
            session.ManualSubject = previousSubject;

            if (_activeSession is not null && string.Equals(_activeSession.Id, session.Id, StringComparison.OrdinalIgnoreCase))
            {
                _activeSession.ManualSubject = previousActiveSubject;
            }

            _trackerService.SetManualSubjectForDate(session, previousSubject, _selectedDate.Date);
            LoadSessionsForSelectedDate();
            _ = RefreshDashboardAsync();
        });

        _ = RefreshDashboardAsync();
    }






    private void SessionContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (SessionsGrid.SelectedItems.Count == 0 && SessionsGrid.CurrentItem is UsageSession currentSession)
        {
            SessionsGrid.SelectedItem = currentSession;
        }
    }

    private void ApplyBatchSessionSubject(string? manualSubject)
    {
        var selectedSessions = SessionsGrid.SelectedItems.OfType<UsageSession>().ToList();
        if (selectedSessions.Count == 0)
        {
            return;
        }

        var affectedRecords = new List<(UsageSession Session, string? PreviousSubject)>();

        foreach (var selected in selectedSessions)
        {
            affectedRecords.Add((selected, selected.ManualSubject));
            selected.ManualSubject = manualSubject;

            if (_activeSession is not null && string.Equals(_activeSession.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                _activeSession.ManualSubject = manualSubject;
            }

            _trackerService.SetManualSubjectForDate(selected, manualSubject, _selectedDate.Date);
        }

        SetUndoSessionAction(() =>
        {
            foreach (var (session, previousSubject) in affectedRecords)
            {
                session.ManualSubject = previousSubject;
                _trackerService.SetManualSubjectForDate(session, previousSubject, _selectedDate.Date);
            }

            LoadSessionsForSelectedDate();
            _ = RefreshDashboardAsync();
        });

        _ = RefreshDashboardAsync();
        RefreshSessionSelectionSummary();
    }

    private void RefreshSessions_Click(object sender, RoutedEventArgs e)
    {
        LoadSessionsForSelectedDate();
        _ = RefreshDashboardAsync();
    }


    private void SearchSessions_Click(object sender, RoutedEventArgs e)
    {
        ApplySessionSearch();
    }

    private void SearchSessionsButton_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var contextMenu = CreateDarkContextMenu();
        AddSearchModeMenuItem(contextMenu, "分类", SessionSearchMode.Subject);
        AddSearchModeMenuItem(contextMenu, "标题", SessionSearchMode.Title);
        AddSearchModeMenuItem(contextMenu, "进程", SessionSearchMode.Process);
        AddSearchModeMenuItem(contextMenu, "全盘", SessionSearchMode.All);
        contextMenu.PlacementTarget = SearchSessionsButton;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void AddSearchModeMenuItem(ContextMenu contextMenu, string header, SessionSearchMode mode)
    {
        var item = CreateDarkMenuItem(header, mode.ToString());
        item.Click += SetSessionSearchMode_Click;
        contextMenu.Items.Add(item);
    }

    private void SetSessionSearchMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<SessionSearchMode>(tag, out var mode))
        {
            return;
        }

        _sessionSearchMode = mode;
        SearchSessionsButton.Content = GetSessionSearchButtonText(mode);
        ApplySessionSearch();
    }

    private static string GetSessionSearchButtonText(SessionSearchMode mode)
    {
        return mode switch
        {
            SessionSearchMode.Subject => "分类查找",
            SessionSearchMode.Title => "标题查找",
            SessionSearchMode.Process => "进程查找",
            _ => "全盘查找"
        };
    }

    private void SessionSearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            ApplySessionSearch();
        }
    }

    private void ApplySessionSearch()
    {
        _sessionSearchText = SessionSearchTextBox.Text?.Trim() ?? string.Empty;
        LoadSessionsForSelectedDate();
        _ = RefreshDashboardAsync();
    }

    private void AddSubjectSearchGroup(string subject, IEnumerable<string> subjects)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        if (!_subjectSearchGroups.TryGetValue(subject, out var group))
        {
            group = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _subjectSearchGroups[subject] = group;
        }

        foreach (var item in subjects.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            group.Add(item);
        }
    }

    private void AddTimeDistributionSubjectGroup(string subject, IEnumerable<string> subjects)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        if (!_timeDistributionSubjectGroups.TryGetValue(subject, out var group))
        {
            group = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _timeDistributionSubjectGroups[subject] = group;
        }

        foreach (var item in subjects.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            group.Add(item);
        }
    }

    private bool MatchesSubjectSearch(UsageSession session, string searchText)
    {
        if (!_subjectSearchGroups.TryGetValue(searchText, out var subjects))
        {
            subjects = new HashSet<string>([searchText], StringComparer.OrdinalIgnoreCase);
        }

        var subject = string.IsNullOrWhiteSpace(session.ManualSubject)
            ? EmptySubjectLabel
            : session.ManualSubject;
        if (string.Equals(searchText, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(searchText, EmptySubjectLabel, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(subject, EmptySubjectLabel, StringComparison.OrdinalIgnoreCase);
        }

        return subjects.Contains(subject);
    }

    private bool MatchesSessionSearch(UsageSession session, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return SearchExpressionMatcher.IsMatch(searchText, term => MatchesSessionSearchTerm(session, term));
    }

    private bool MatchesSessionSearchTerm(UsageSession session, string searchText)
    {
        if (TryMatchScopedSearchTerm(session, searchText, out var scopedMatch))
        {
            return scopedMatch;
        }

        return _sessionSearchMode switch
        {
            SessionSearchMode.Subject => MatchesSubjectSearch(session, searchText),
            SessionSearchMode.Title => session.WindowTitle.Contains(searchText, StringComparison.OrdinalIgnoreCase),
            SessionSearchMode.Process => session.ProcessName.Contains(searchText, StringComparison.OrdinalIgnoreCase),
            _ => MatchesSubjectSearch(session, searchText)
                || session.WindowTitle.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || session.ProcessName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
        };
    }

    private bool TryMatchScopedSearchTerm(UsageSession session, string searchText, out bool isMatch)
    {
        isMatch = false;
        var separatorIndex = searchText.IndexOfAny([':', '：']);
        if (separatorIndex <= 0 || separatorIndex >= searchText.Length - 1)
        {
            return false;
        }

        var scope = searchText[..separatorIndex].Trim();
        var value = searchText[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var scopedMode = ResolveScopedSearchMode(scope);
        if (scopedMode is null)
        {
            return false;
        }

        if (_sessionSearchMode != SessionSearchMode.All && _sessionSearchMode != scopedMode.Value)
        {
            return false;
        }

        isMatch = scopedMode.Value switch
        {
            SessionSearchMode.Subject => MatchesSubjectSearch(session, value),
            SessionSearchMode.Title => session.WindowTitle.Contains(value, StringComparison.OrdinalIgnoreCase),
            SessionSearchMode.Process => session.ProcessName.Contains(value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        return true;
    }

    private static SessionSearchMode? ResolveScopedSearchMode(string scope)
    {
        return scope.ToLowerInvariant() switch
        {
            "分类" or "科目" or "subject" => SessionSearchMode.Subject,
            "标题" or "窗口" or "title" => SessionSearchMode.Title,
            "进程" or "程序" or "process" or "proc" => SessionSearchMode.Process,
            _ => null
        };
    }


    private void RunOnEnter(System.Windows.Input.KeyEventArgs e, Action action)
    {
        if (e.Key != System.Windows.Input.Key.Enter && e.Key != System.Windows.Input.Key.Return)
        {
            return;
        }

        e.Handled = true;
        action();
    }


    private void NewSubjectTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        RunOnEnter(e, () => AddSubject_Click(sender, e));
    }

    private void DeleteSubjectTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        RunOnEnter(e, () => RemoveSubject_Click(sender, e));
    }

    private void DeleteSubjectTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshDeleteSelectionSummary();
    }

    private void NewChildSubjectTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        RunOnEnter(e, () => AddChildSubject_Click(sender, e));
    }

    private void NewGrandChildSubjectTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        RunOnEnter(e, () => AddGrandChildSubject_Click(sender, e));
    }

    private void DeleteChildSubjectTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        RunOnEnter(e, () => RemoveChildSubject_Click(sender, e));
    }

    private void DeleteChildSubjectTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshChildDeleteSelectionSummary();
    }

    private void DeleteGrandChildSubjectTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        RunOnEnter(e, () => RemoveGrandChildSubject_Click(sender, e));
    }

    private void DeleteGrandChildSubjectTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshGrandChildDeleteSelectionSummary();
    }

    private void KeywordRuleTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        RunOnEnter(e, () => AddKeywordRule_Click(sender, e));
    }

    private void DeleteKeywordRuleTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        RunOnEnter(e, () => RemoveKeywordRule_Click(sender, e));
    }

    private void DeleteKeywordRuleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshKeywordRuleDeleteSelectionSummary();
        SyncKeywordRuleSelectionFromDeleteText();
    }

    private void AddSubject_Click(object sender, RoutedEventArgs e)
    {
        var subjects = SplitSubjects(NewSubjectTextBox.Text);
        var changed = false;
        foreach (var subject in subjects)
        {
            changed |= _trackerService.AddSubject(subject);
        }

        if (changed)
        {
            LoadSubjectOptions();
            BuildSessionContextMenu();
            NewSubjectTextBox.Clear();
            _ = RefreshDashboardAsync();
        }
    }

    private void RemoveSubject_Click(object sender, RoutedEventArgs e)
    {
        var subjects = SplitSubjects(DeleteSubjectTextBox.Text);
        var changed = false;
        foreach (var subject in subjects)
        {
            changed |= _trackerService.RemoveSubject(subject);
        }

        if (changed)
        {
            LoadSubjectOptions();
            BuildSessionContextMenu();
            DeleteSubjectTextBox.Clear();
            RefreshDeleteSelectionSummary();
            RefreshChildDeleteSelectionSummary();
            RefreshGrandChildDeleteSelectionSummary();
            _ = RefreshDashboardAsync();
        }
    }

    private void AddChildSubject_Click(object sender, RoutedEventArgs e)
    {
        if (ParentSubjectComboBox.SelectedItem is not string parentSubject)
        {
            return;
        }

        var children = SplitSubjects(NewChildSubjectTextBox.Text);
        var changed = false;
        foreach (var child in children)
        {
            changed |= _trackerService.AddChildSubject(parentSubject, child);
        }

        if (changed)
        {
            LoadSubjectOptions();
            BuildSessionContextMenu();
            NewChildSubjectTextBox.Clear();
            _ = RefreshDashboardAsync();
        }
    }

    private void RemoveChildSubject_Click(object sender, RoutedEventArgs e)
    {
        var parentSubject = GetSelectedSubjectValue(ParentSubjectComboBox);
        if (string.IsNullOrWhiteSpace(parentSubject))
        {
            return;
        }

        var children = SplitSubjects(DeleteChildSubjectTextBox.Text);
        if (children.Count == 0)
        {
            return;
        }

        var promoteToParent = AskPromoteRemovedSubject($"删除父类后，原属于这些父类及其子类的记录要归为上一层大类“{parentSubject}”吗？\n\n选择“是”：归为“{parentSubject}”\n选择“否”：按现有规则自动重新分类");
        if (promoteToParent is null)
        {
            return;
        }

        var changed = false;
        foreach (var child in children)
        {
            changed |= _trackerService.RemoveChildSubject(parentSubject, child, promoteToParent.Value);
        }

        if (changed)
        {
            LoadSubjectOptions();
            BuildSessionContextMenu();
            DeleteChildSubjectTextBox.Clear();
            RefreshChildDeleteSelectionSummary();
            _ = RefreshDashboardAsync();
        }
    }

    private void AddGrandChildSubject_Click(object sender, RoutedEventArgs e)
    {
        var majorSubject = GetSelectedSubjectValue(ParentSubjectComboBox);
        var parentSubject = GetSelectedSubjectValue(GrandChildParentComboBox);
        if (string.IsNullOrWhiteSpace(majorSubject) || string.IsNullOrWhiteSpace(parentSubject))
        {
            return;
        }

        var changed = false;
        foreach (var child in SplitSubjects(NewGrandChildSubjectTextBox.Text))
        {
            changed |= _trackerService.AddGrandChildSubject(majorSubject, parentSubject, child);
        }

        if (changed)
        {
            LoadSubjectOptions();
            BuildSessionContextMenu();
            NewGrandChildSubjectTextBox.Clear();
            _ = RefreshDashboardAsync();
        }
    }


    private void RemoveGrandChildSubject_Click(object sender, RoutedEventArgs e)
    {
        var majorSubject = GetSelectedSubjectValue(ParentSubjectComboBox);
        var parentSubject = GetSelectedSubjectValue(GrandChildParentComboBox);
        if (string.IsNullOrWhiteSpace(majorSubject) || string.IsNullOrWhiteSpace(parentSubject))
        {
            return;
        }

        var children = SplitSubjects(DeleteGrandChildSubjectTextBox.Text);
        if (children.Count == 0)
        {
            return;
        }

        var promoteToParent = AskPromoteRemovedSubject($"删除子类后，原属于这些子类的记录要归为上一层父类“{parentSubject}”吗？\n\n选择“是”：归为“{parentSubject}”\n选择“否”：按现有规则自动重新分类");
        if (promoteToParent is null)
        {
            return;
        }

        var changed = false;
        foreach (var child in children)
        {
            changed |= _trackerService.RemoveGrandChildSubject(majorSubject, parentSubject, child, promoteToParent.Value);
        }

        if (changed)
        {
            LoadSubjectOptions();
            BuildSessionContextMenu();
            DeleteGrandChildSubjectTextBox.Clear();
            RefreshGrandChildDeleteSelectionSummary();
            _ = RefreshDashboardAsync();
        }
    }

    private void KeywordRuleSubjectButton_Click(object sender, RoutedEventArgs e)
    {
        var contextMenu = CreateDarkContextMenu();

        foreach (var definition in _trackerService.GetSubjectDefinitions())
        {
            var majorItem = CreateDarkMenuItem(definition.Name);
            var useMajorItem = CreateDarkMenuItem($"选择{definition.Name}", definition.Name);
            useMajorItem.Click += SetKeywordRuleSubject_Click;
            majorItem.Items.Add(useMajorItem);
            if (definition.Parents.Count > 0)
            {
                majorItem.Items.Add(new Separator());
            }

            foreach (var parent in definition.Parents)
            {
                var parentItem = CreateDarkMenuItem(parent.Name);
                var useParentItem = CreateDarkMenuItem($"选择{parent.Name}", parent.Name);
                useParentItem.Click += SetKeywordRuleSubject_Click;
                parentItem.Items.Add(useParentItem);
                if (parent.Children.Count > 0)
                {
                    parentItem.Items.Add(new Separator());
                }

                foreach (var child in parent.Children)
                {
                    var childItem = CreateDarkMenuItem(child, child);
                    childItem.Click += SetKeywordRuleSubject_Click;
                    parentItem.Items.Add(childItem);
                }

                majorItem.Items.Add(parentItem);
            }

            contextMenu.Items.Add(majorItem);
        }

        contextMenu.PlacementTarget = KeywordRuleSubjectButton;
        contextMenu.IsOpen = true;
    }

    private void SetKeywordRuleSubject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string subject || string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        _selectedKeywordRuleSubject = subject;
        KeywordRuleSubjectButton.Content = $"{subject} ▾";
        RefreshKeywordRules();
        e.Handled = true;
    }



    private void AddKeywordRule_Click(object sender, RoutedEventArgs e)
    {
        var subject = _selectedKeywordRuleSubject;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        var changed = false;
        foreach (var keyword in SplitSubjects(KeywordRuleTextBox.Text))
        {
            changed |= _trackerService.AddSubjectKeywordRule(subject, keyword);
        }

        if (changed)
        {
            KeywordRuleTextBox.Clear();
            RefreshKeywordRules();
            LoadSessionsForSelectedDate();
            _ = RefreshDashboardAsync();
        }
    }

    private void RemoveKeywordRule_Click(object sender, RoutedEventArgs e)
    {
        var subject = _selectedKeywordRuleSubject;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        var changed = false;
        foreach (var keyword in SplitSubjects(DeleteKeywordRuleTextBox.Text))
        {
            changed |= _trackerService.RemoveSubjectKeywordRule(subject, keyword);
        }

        if (changed)
        {
            DeleteKeywordRuleTextBox.Clear();
            RefreshKeywordRules();
            RefreshKeywordRuleDeleteSelectionSummary();
            LoadSessionsForSelectedDate();
            _ = RefreshDashboardAsync();
        }
    }

    private void KeywordRuleListButton_Click(object sender, RoutedEventArgs e)
    {
        KeywordRuleListPanel.Visibility = KeywordRuleListPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }



    private void RefreshKeywordRules()
    {
        _keywordRules.Clear();
        var subject = _selectedKeywordRuleSubject;
        if (string.IsNullOrWhiteSpace(subject))
        {
            SyncKeywordRuleSelectionFromDeleteText();
            return;
        }

        foreach (var keyword in _trackerService.GetSubjectKeywordRules(subject))
        {
            _keywordRules.Add(keyword);
        }

        SyncKeywordRuleSelectionFromDeleteText();
    }


    private void ParentSubjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ParentSubjectList.SelectedItem is not string subject || string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        var subjects = SplitSubjects(DeleteSubjectTextBox.Text);
        if (!subjects.Contains(subject, StringComparer.OrdinalIgnoreCase))
        {
            subjects.Add(subject);
            DeleteSubjectTextBox.Text = string.Join(",", subjects);
            DeleteSubjectTextBox.CaretIndex = DeleteSubjectTextBox.Text.Length;
        }

        RefreshDeleteSelectionSummary();
        ParentSubjectList.SelectedItem = null;
    }

    private void ChildSubjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChildSubjectList.SelectedItem is not string subject || string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        var subjects = SplitSubjects(DeleteChildSubjectTextBox.Text);
        if (!subjects.Contains(subject, StringComparer.OrdinalIgnoreCase))
        {
            subjects.Add(subject);
            DeleteChildSubjectTextBox.Text = string.Join(",", subjects);
            DeleteChildSubjectTextBox.CaretIndex = DeleteChildSubjectTextBox.Text.Length;
        }

        RefreshChildDeleteSelectionSummary();
        ChildSubjectList.SelectedItem = null;
    }

    private void GrandChildSubjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var subject = GetSelectedSubjectValue(GrandChildSubjectList);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        var subjects = SplitSubjects(DeleteGrandChildSubjectTextBox.Text);
        if (!subjects.Contains(subject, StringComparer.OrdinalIgnoreCase))
        {
            subjects.Add(subject);
            DeleteGrandChildSubjectTextBox.Text = string.Join(",", subjects);
            DeleteGrandChildSubjectTextBox.CaretIndex = DeleteGrandChildSubjectTextBox.Text.Length;
        }

        RefreshGrandChildDeleteSelectionSummary();
        GrandChildSubjectList.SelectedItem = null;
    }

    private void ParentSubjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshChildSubjectOptions();
    }

    private void GrandChildParentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshGrandChildSubjectOptions();
    }

    private void RefreshChildSubjectOptions()
    {
        _parentSubjectOptions.Clear();
        GrandChildSubjectList.ItemsSource = null;
        var parentSubject = GetSelectedSubjectValue(ParentSubjectComboBox);
        if (string.IsNullOrWhiteSpace(parentSubject))
        {
            return;
        }

        var definition = _trackerService.GetSubjectDefinitions().FirstOrDefault(x => string.Equals(x.Name, parentSubject, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return;
        }

        foreach (var parent in definition.Parents)
        {
            _parentSubjectOptions.Add(parent.Name);
        }

        if (GrandChildParentComboBox.Items.Count > 0 && GrandChildParentComboBox.SelectedIndex < 0)
        {
            GrandChildParentComboBox.SelectedIndex = 0;
        }

        RefreshGrandChildSubjectOptions();
    }

    private void RefreshGrandChildSubjectOptions()
    {
        GrandChildSubjectList.ItemsSource = null;
        if (ParentSubjectComboBox.SelectedItem is not string majorSubject || GrandChildParentComboBox.SelectedItem is not string parentSubject)
        {
            return;
        }

        var parent = _trackerService.GetSubjectDefinitions()
            .FirstOrDefault(x => string.Equals(x.Name, majorSubject, StringComparison.OrdinalIgnoreCase))?
            .Parents.FirstOrDefault(x => string.Equals(x.Name, parentSubject, StringComparison.OrdinalIgnoreCase));
        GrandChildSubjectList.ItemsSource = parent?.Children ?? new List<string>();
    }

    private void ClearDeleteSubjects_Click(object sender, RoutedEventArgs e)
    {
        DeleteSubjectTextBox.Clear();
        RefreshDeleteSelectionSummary();
    }

    private void ClearDeleteChildSubjects_Click(object sender, RoutedEventArgs e)
    {
        DeleteChildSubjectTextBox.Clear();
        RefreshChildDeleteSelectionSummary();
    }

    private void ClearDeleteGrandChildSubjects_Click(object sender, RoutedEventArgs e)
    {
        DeleteGrandChildSubjectTextBox.Clear();
        RefreshGrandChildDeleteSelectionSummary();
    }

    private void ClearDeleteKeywordRules_Click(object sender, RoutedEventArgs e)
    {
        DeleteKeywordRuleTextBox.Clear();
        RefreshKeywordRuleDeleteSelectionSummary();
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var enabled = StartupCheckBox.IsChecked == true;
        var changed = _trackerService.SetStartWithWindows(enabled);
        if (!changed)
        {
            System.Windows.MessageBox.Show(LocalizationService.Instance.Get("Msg.AutoStartFailed"), LocalizationService.Instance.Get("Msg.SettingsFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshStartupState(enableByDefault: enabled);
    }

    private enum TransferPayloadKind
    {
        Usage,
        Settings,
        Full
    }

    private async void ExportData_Click(object sender, RoutedEventArgs e)
    {
        await ExportJsonAsync(TransferPayloadKind.Full);
    }

    private async void ImportData_Click(object sender, RoutedEventArgs e)
    {
        await ImportJsonAsync(TransferPayloadKind.Full);
    }

    private void ExportMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTransferMenu(ExportMenuPopup, ExportMenuFlyout, ExportMenuFlyoutTransform);
    }

    private void ImportMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleTransferMenu(ImportMenuPopup, ImportMenuFlyout, ImportMenuFlyoutTransform);
    }

    private async void TransferMenuItem_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string tag }) return;
        e.Handled = true;
        HideTransferMenu();

        switch (tag)
        {
            case "ExportUsage":
                await ExportJsonAsync(TransferPayloadKind.Usage);
                break;
            case "ExportSettings":
                await ExportJsonAsync(TransferPayloadKind.Settings);
                break;
            case "ExportFull":
                await ExportJsonAsync(TransferPayloadKind.Full);
                break;
            case "ImportUsage":
                await ImportJsonAsync(TransferPayloadKind.Usage);
                break;
            case "ImportSettings":
                await ImportJsonAsync(TransferPayloadKind.Settings);
                break;
            case "ImportFull":
                await ImportJsonAsync(TransferPayloadKind.Full);
                break;
        }
    }

    private void ToggleTransferMenu(Popup popup, Border flyout, TranslateTransform transform)
    {
        if (popup.IsOpen)
        {
            HideTransferMenu();
            return;
        }

        HideTransferMenu();
        _transferMenuCloseTimer.Stop();
        _activeTransferPopup = popup;
        _activeTransferFlyout = flyout;
        popup.IsOpen = true;
        AnimateTransferMenu(flyout, transform, show: true);
    }

    private void HideTransferMenu()
    {
        _transferMenuCloseTimer.Stop();
        if (_activeTransferPopup is null || _activeTransferFlyout is null)
        {
            return;
        }

        var popup = _activeTransferPopup;
        var flyout = _activeTransferFlyout;
        var transform = flyout.RenderTransform as TranslateTransform;
        _activeTransferPopup = null;
        _activeTransferFlyout = null;

        if (transform is null)
        {
            popup.IsOpen = false;
            return;
        }

        AnimateTransferMenu(flyout, transform, show: false, () => popup.IsOpen = false);
    }

    private void AnimateTransferMenu(Border flyout, TranslateTransform transform, bool show, Action? completed = null)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = easing
        };
        var yAnimation = new DoubleAnimation
        {
            To = show ? 0 : -8,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = easing
        };
        if (completed is not null)
        {
            opacityAnimation.Completed += (_, _) => completed();
        }

        flyout.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        transform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
    }

    private void TransferMenuFlyout_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _transferMenuCloseTimer.Stop();
    }

    private void TransferMenuFlyout_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_activeTransferPopup?.IsOpen == true)
        {
            _transferMenuCloseTimer.Stop();
            _transferMenuCloseTimer.Start();
        }
    }

    private void TransferMenuCloseTimer_Tick(object? sender, EventArgs e)
    {
        _transferMenuCloseTimer.Stop();
        if (_activeTransferFlyout is not null && !IsMouseWithin(_activeTransferFlyout) && !IsMouseWithin(ExportMenuButton) && !IsMouseWithin(ImportMenuButton))
        {
            HideTransferMenu();
        }
    }

    private async Task ExportJsonAsync(TransferPayloadKind kind)
    {
        if (_trackerService is null) return;

        var exportDirectory = _trackerService.GetDefaultExportDirectory();
        var prefix = kind switch
        {
            TransferPayloadKind.Usage => "usage-data",
            TransferPayloadKind.Settings => "settings",
            _ => "full-backup"
        };
        var title = kind switch
        {
            TransferPayloadKind.Usage => "导出时迹数据",
            TransferPayloadKind.Settings => "导出时迹配置",
            _ => "导出完整备份"
        };
        var fileName = prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = "JSON 文件 (*.json)|*.json",
            InitialDirectory = exportDirectory,
            FileName = fileName,
            AddExtension = true,
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            switch (kind)
            {
                case TransferPayloadKind.Usage:
                    await _trackerService.ExportUsageDataAsync(dialog.FileName);
                    break;
                case TransferPayloadKind.Settings:
                    await _trackerService.ExportSettingsDataAsync(dialog.FileName);
                    break;
                default:
                    await _trackerService.ExportFullBackupAsync(dialog.FileName);
                    break;
            }
            System.Windows.MessageBox.Show(string.Format(LocalizationService.Instance.Get("Msg.ExportComplete"), dialog.FileName), LocalizationService.Instance.Get("Msg.ExportCompleteTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(string.Format(LocalizationService.Instance.Get("Msg.ExportFailed"), ex.Message), LocalizationService.Instance.Get("Msg.ExportFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ImportJsonAsync(TransferPayloadKind kind)
    {
        if (_trackerService is null) return;

        var title = kind switch
        {
            TransferPayloadKind.Usage => LocalizationService.Instance.Get("Msg.ImportUsageData"),
            TransferPayloadKind.Settings => LocalizationService.Instance.Get("Msg.ImportSettingsDsg"),
            _ => LocalizationService.Instance.Get("Msg.ImportFullBackup")
        };
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "JSON 文件 (*.json)|*.json",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ImportConflictStrategy conflictStrategy = ImportConflictStrategy.KeepLocal;
            ImportPreview? preview = null;
            if (kind != TransferPayloadKind.Settings)
            {
                preview = await _trackerService.PreviewImportDataAsync(dialog.FileName);
                if (preview.ConflictCount > 0)
                {
                    var choice = System.Windows.MessageBox.Show(
                        string.Format(LocalizationService.Instance.Get("Msg.ImportConflictDetected"), preview.TotalCount, preview.NonConflictCount, preview.ConflictCount),
                        LocalizationService.Instance.Get("Msg.ImportConflictTitle"),
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question,
                        MessageBoxResult.Yes);

                    if (choice == MessageBoxResult.Cancel)
                    {
                        return;
                    }

                    conflictStrategy = choice == MessageBoxResult.No
                        ? ImportConflictStrategy.UseIncoming
                        : ImportConflictStrategy.KeepLocal;
                }
            }

            var result = kind switch
            {
                TransferPayloadKind.Usage => await _trackerService.ImportUsageDataAsync(dialog.FileName, conflictStrategy),
                TransferPayloadKind.Settings => await _trackerService.ImportSettingsDataAsync(dialog.FileName),
                _ => await _trackerService.ImportFullBackupAsync(dialog.FileName, conflictStrategy)
            };

            await Dispatcher.InvokeAsync(() =>
            {
                ApplyTheme(_trackerService.Theme, save: false);
                LoadSubjectOptions();
                BuildSessionContextMenu();
                LoadSessionsForSelectedDate();
                _ = RefreshDashboardAsync();
            }, DispatcherPriority.ContextIdle);

            if (kind == TransferPayloadKind.Settings)
            {
                System.Windows.MessageBox.Show(LocalizationService.Instance.Get("Msg.ImportSettingsComplete"), LocalizationService.Instance.Get("Msg.ImportCompleteTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var conflictLine = conflictStrategy == ImportConflictStrategy.UseIncoming
                ? $"冲突覆盖导入：{result.OverwrittenCount}"
                : $"冲突保留本地：{result.ConflictCount}";
            System.Windows.MessageBox.Show(string.Format(LocalizationService.Instance.Get("Msg.ImportComplete"), result.ImportedCount, conflictLine, result.TotalCount), LocalizationService.Instance.Get("Msg.ImportCompleteTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(string.Format(LocalizationService.Instance.Get("Msg.ImportFailed"), ex.Message), LocalizationService.Instance.Get("Msg.ImportFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private void RefreshStartupState(bool enableByDefault = true)
    {
        var isEnabled = _trackerService.IsStartWithWindowsEnabled();
        if (!isEnabled && enableByDefault)
        {
            isEnabled = _trackerService.SetStartWithWindows(true) && _trackerService.IsStartWithWindowsEnabled();
        }

        StartupCheckBox.IsChecked = isEnabled;
        StartupStatusText.Text = isEnabled
            ? $"开机自启：已启用（{_trackerService.GetStartupCommand()}）"
            : "开机自启：未启用";
    }

    private void RefreshIdleTimeoutState()
    {
        IdleTimeoutMinutesTextBox.Text = _trackerService.IdleTimeoutMinutes.ToString();
        ManualIdleShortcutTextBox.Text = _trackerService.ManualIdleShortcutText;
        _manualIdleShortcut = TryParseManualIdleShortcut(_trackerService.ManualIdleShortcutText, showWarning: false);
        IdleTimeoutStatusText.Text = $"当前：{_trackerService.IdleTimeoutMinutes} 分钟无操作进入空闲；手动空闲快捷键：{_trackerService.ManualIdleShortcutText}";
    }

    private void ApplyIdleTimeout_Click(object sender, RoutedEventArgs e)
    {
        ApplyIdleTimeoutFromInput();
    }

    private void IdleTimeoutMinutesTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            ApplyIdleTimeoutFromInput();
        }
    }

    private void ApplyIdleTimeoutFromInput()
    {
        if (!int.TryParse(IdleTimeoutMinutesTextBox.Text.Trim(), out var minutes))
        {
            System.Windows.MessageBox.Show(LocalizationService.Instance.Get("Msg.InvalidMinutes"), LocalizationService.Instance.Get("Msg.SettingsFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshIdleTimeoutState();
            return;
        }

        _trackerService.SetIdleTimeoutMinutes(minutes);
        RefreshIdleTimeoutState();
    }

    private void EnterManualIdle_Click(object sender, RoutedEventArgs e)
    {
        EnterManualIdle();
    }

    private void EnterManualIdle()
    {
        _trackerService.EnterManualIdle();
        LoadSessionsForSelectedDate();
        _ = RefreshDashboardAsync();
    }

    private void ApplyManualIdleShortcut_Click(object sender, RoutedEventArgs e)
    {
        ApplyManualIdleShortcutFromInput();
    }

    private void ManualIdleShortcutTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var key = GetGestureKey(e);
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.System or System.Windows.Input.Key.None)
        {
            ManualIdleShortcutTextBox.Text = FormatModifiers(modifiers);
            return;
        }

        if (modifiers == System.Windows.Input.ModifierKeys.None)
        {
            System.Windows.MessageBox.Show(LocalizationService.Instance.Get("Msg.ShortcutInstructions"), LocalizationService.Instance.Get("Msg.ShortcutSettingTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshIdleTimeoutState();
            return;
        }

        var shortcut = new System.Windows.Input.KeyGesture(key, modifiers);
        _manualIdleShortcut = shortcut;
        _trackerService.SetManualIdleShortcutText(FormatKeyGesture(shortcut));
        RegisterManualIdleHotkey();
        RefreshIdleTimeoutState();
    }

    private void ApplyManualIdleShortcutFromInput()
    {
        var shortcutText = ManualIdleShortcutTextBox.Text.Trim();
        var shortcut = TryParseManualIdleShortcut(shortcutText, showWarning: true);
        if (shortcut is null)
        {
            RefreshIdleTimeoutState();
            return;
        }

        _manualIdleShortcut = shortcut;
        _trackerService.SetManualIdleShortcutText(FormatKeyGesture(shortcut));
        RegisterManualIdleHotkey();
        RefreshIdleTimeoutState();
    }

    private bool MatchesManualIdleShortcut(System.Windows.Input.KeyEventArgs e)
    {
        if (_manualIdleShortcut is null || IsTextInputFocused())
        {
            return false;
        }

        return GetGestureKey(e) == _manualIdleShortcut.Key
            && (System.Windows.Input.Keyboard.Modifiers & _manualIdleShortcut.Modifiers) == _manualIdleShortcut.Modifiers;
    }

    private static System.Windows.Input.Key GetGestureKey(System.Windows.Input.KeyEventArgs e)
    {
        return e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
    }

    private void RegisterManualIdleHotkey()
    {
        UnregisterManualIdleHotkey();
        if (_windowHandle == IntPtr.Zero || _manualIdleShortcut is null)
        {
            return;
        }

        var modifiers = ToHotkeyModifiers(_manualIdleShortcut.Modifiers);
        var virtualKey = System.Windows.Input.KeyInterop.VirtualKeyFromKey(_manualIdleShortcut.Key);
        if (modifiers == 0 || virtualKey == 0)
        {
            return;
        }

        _manualIdleHotkeyRegistered = RegisterHotKey(_windowHandle, ManualIdleHotkeyId, modifiers, (uint)virtualKey);
        if (!_manualIdleHotkeyRegistered)
        {
            IdleTimeoutStatusText.Text = $"当前：{_trackerService.IdleTimeoutMinutes} 分钟无操作进入空闲；快捷键 {_trackerService.ManualIdleShortcutText} 注册失败，可能已被占用";
        }
    }

    private void UnregisterManualIdleHotkey()
    {
        if (!_manualIdleHotkeyRegistered || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        UnregisterHotKey(_windowHandle, ManualIdleHotkeyId);
        _manualIdleHotkeyRegistered = false;
    }

    private static uint ToHotkeyModifiers(System.Windows.Input.ModifierKeys modifiers)
    {
        uint result = 0;
        if ((modifiers & System.Windows.Input.ModifierKeys.Alt) != 0)
        {
            result |= 0x0001;
        }
        if ((modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            result |= 0x0002;
        }
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
        {
            result |= 0x0004;
        }
        if ((modifiers & System.Windows.Input.ModifierKeys.Windows) != 0)
        {
            result |= 0x0008;
        }

        return result;
    }

    private static string FormatModifiers(System.Windows.Input.ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }
        if ((modifiers & System.Windows.Input.ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }
        if ((modifiers & System.Windows.Input.ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
    }

    private static bool IsTextInputFocused()
    {
        var focusedElement = System.Windows.Input.Keyboard.FocusedElement;
        return focusedElement is System.Windows.Controls.TextBox or PasswordBox or System.Windows.Controls.ComboBox;
    }

    private System.Windows.Input.KeyGesture? TryParseManualIdleShortcut(string shortcutText, bool showWarning)
    {
        if (string.IsNullOrWhiteSpace(shortcutText))
        {
            shortcutText = "Ctrl+Alt+I";
        }

        shortcutText = shortcutText.Replace("\\u002B", "+").Replace("\u002B", "+");

        try
        {
            var converter = new System.Windows.Input.KeyGestureConverter();
            if (converter.ConvertFromInvariantString(shortcutText) is System.Windows.Input.KeyGesture gesture
                && gesture.Key is not System.Windows.Input.Key.None)
            {
                return gesture;
            }
        }
        catch
        {
        }

        if (showWarning)
        {
            System.Windows.MessageBox.Show(LocalizationService.Instance.Get("Msg.InvalidShortcut"), LocalizationService.Instance.Get("Msg.SettingsFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return null;
    }

    private static string FormatKeyGesture(System.Windows.Input.KeyGesture gesture)
    {
        var parts = new List<string>();
        if ((gesture.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }
        if ((gesture.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }
        if ((gesture.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }
        if ((gesture.Modifiers & System.Windows.Input.ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(gesture.Key.ToString());
        return string.Join("+", parts);
    }

    private void RefreshDeleteSelectionSummary()
    {
        var count = SplitSubjects(DeleteSubjectTextBox.Text).Count;
        DeleteSelectionSummaryText.Text = $"待删除分类：{count} 项";
    }

    private void RefreshChildDeleteSelectionSummary()
    {
        var count = SplitSubjects(DeleteChildSubjectTextBox.Text).Count;
        DeleteChildSelectionSummaryText.Text = $"待删除父类：{count} 项";
    }

    private void RefreshGrandChildDeleteSelectionSummary()
    {
        var count = SplitSubjects(DeleteGrandChildSubjectTextBox.Text).Count;
        DeleteGrandChildSelectionSummaryText.Text = $"待删除子类：{count} 项";
    }

    private void RefreshKeywordRuleDeleteSelectionSummary()
    {
        var count = SplitSubjects(DeleteKeywordRuleTextBox.Text).Count;
        DeleteKeywordRuleSelectionSummaryText.Text = $"待删除规则：{count} 项";
    }

    private void ToggleSubjectExpand_Click(object sender, RoutedEventArgs e)
    {
        switch (sender)
        {
            case System.Windows.Controls.Button { Tag: SubjectTreeSummary summary }:
                summary.IsExpanded = !summary.IsExpanded;
                break;
            case System.Windows.Controls.Button { Tag: ChildSubjectSummary summary }:
                summary.IsExpanded = !summary.IsExpanded;
                break;
        }
    }

    private void LoadSelectedDate_Click(object sender, RoutedEventArgs e)
    {
        if (!DateTime.TryParse(DatePickerTextBox.Text, out var selectedDate))
        {
            return;
        }

        _selectedDate = selectedDate.Date;
        RefreshDateRangeHint();
        RefreshTimeDistributionViewState();
        LoadSessionsForSelectedDate();
    }

    private void DatePickerTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        RunOnEnter(e, () => LoadSelectedDate_Click(sender, e));
    }

    private void ViewToday_Click(object sender, RoutedEventArgs e)
    {
        _selectedDate = DateTime.Today;
        DatePickerTextBox.Text = _selectedDate.ToString("yyyy-MM-dd");
        RefreshDateRangeHint();
        RefreshTimeDistributionViewState();
        LoadSessionsForSelectedDate();
    }

    private sealed record SubjectUsageItem(UsageSession Session, string Major, string? Parent, string? Child);

    private bool? AskPromoteRemovedSubject(string message)
    {
        return DeletionChoiceDialog.Show(
            this,
            "选择删除后的分类方式",
            message);
    }

    private static System.Windows.Media.Brush GetAccentBrush(string subjectName)
    {
        var palette = new[]
        {
            "#A8D95C", "#59D2C8", "#C455D7", "#FFB14A", "#6FA8FF", "#FF7E8A", "#8FD36B"
        };
        var index = Math.Abs(subjectName.GetHashCode()) % palette.Length;
        return (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(palette[index])!;
    }

    private static string FormatSubjectDisplay(string subjectType, string subjectName, int level)
    {
        return level switch
        {
            0 => $"▾ {subjectName}",
            1 => $"　├ {subjectName}",
            _ => $"　│　└ {subjectName}"
        };
    }

    private static string GetSubjectValueFromDisplay(string displayText)
    {
        return displayText
            .Trim()
            .TrimStart('▾', '├', '└', '│', '─', ' ', '　')
            .Trim();
    }

    private static string? GetSelectedSubjectValue(System.Windows.Controls.ComboBox comboBox)
    {
        return comboBox.SelectedItem switch
        {
            string text => GetSubjectValueFromDisplay(text),
            ComboBoxItem item when item.Content is string text => GetSubjectValueFromDisplay(text),
            _ => null
        };
    }

    private static List<string> SplitSubjects(string? input)
    {
        return (input ?? string.Empty)
            .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ToggleCompactMode_Click(object sender, RoutedEventArgs e)
    {
        if (_compactSessionWindow?.IsVisible == true)
        {
            HideCompactSessionWindow();
            return;
        }

        ShowCompactSessionWindow();
    }

    private void UpdateCompactSessionWindow()
    {
        if (_compactSessionWindow is null)
        {
            return;
        }

        var title = _activeSession?.WindowTitle ?? (_selectedDate.Date == DateTime.Today ? "空闲" : "历史日期");
        var duration = FormatDuration(_activeSession?.Duration ?? TimeSpan.Zero);
        _compactSessionWindow.UpdateSession(title, duration);
    }

    public void ShowCompactSessionWindow()
    {
        if (_compactSessionWindow is null)
        {
            _compactSessionWindow = new CompactSessionWindow();
            _compactSessionWindow.BackToMainRequested += CompactSessionWindow_BackToMainRequested;
            _compactSessionWindow.ManualIdleRequested += CompactSessionWindow_ManualIdleRequested;
            _compactSessionWindow.Closed += (_, _) =>
            {
                if (_compactSessionWindow is not null)
                {
                    _compactSessionWindow.BackToMainRequested -= CompactSessionWindow_BackToMainRequested;
                    _compactSessionWindow.ManualIdleRequested -= CompactSessionWindow_ManualIdleRequested;
                }

                _compactSessionWindow = null;
                CompactModeButton.Content = "◱";
            };
        }

        UpdateCompactSessionWindow();
        if (!_compactSessionWindow.IsVisible)
        {
            _compactSessionWindow.PositionCompactWindow();
            _compactSessionWindow.Show();
        }

        _compactSessionWindow.Activate();
        CompactModeButton.Content = "◰";
        WindowState = WindowState.Minimized;
    }


    public void HideCompactSessionWindow()
    {
        _compactSessionWindow?.Close();
    }

    private void CompactSessionWindow_BackToMainRequested(object? sender, EventArgs e)
    {
        HideCompactSessionWindow();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void CompactSessionWindow_ManualIdleRequested(object? sender, EventArgs e)
    {
        EnterManualIdle();
    }

    private bool _initialized = false;
    
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 防止无限循环：如果已初始化，直接返回
            if (_initialized)
            {
                return;
            }
            
            App.LogStartupMessage("MainWindow.Loaded", "begin");
            _isLightTheme = false;
            _isFollowingSystemTheme = false;
            LoadThemeWithDelay();
            App.LogStartupMessage("MainWindow.Loaded", "before DatePickerTextBox");
            App.LogStartupMessage("MainWindow.Loaded", "entering try block");
            try
            {
                App.LogStartupMessage("MainWindow.Loaded", "setting DatePickerTextBox.Text - STEP 1");
                App.LogStartupMessage("MainWindow.Loaded", "setting DatePickerTextBox.Text - STEP 2");
                App.LogStartupMessage("MainWindow.Loaded", "setting DatePickerTextBox.Text - STEP 3");
                App.LogStartupMessage("MainWindow.Loaded", "skipping DatePickerTextBox.Text assignment to avoid crash");
                App.LogStartupMessage("MainWindow.Loaded", "setting DatePickerTextBox.Text - STEP 4");
                App.LogStartupMessage("MainWindow.Loaded", "DatePickerTextBox.Text set successfully - BEFORE CATCH");
            }
            catch (Exception ex)
            {
                App.LogStartupException("MainWindow.Loaded.DatePickerTextBox", ex);
                App.LogStartupMessage("MainWindow.Loaded", "Failed to set DatePickerTextBox.Text, continuing anyway");
            }
            App.LogStartupMessage("MainWindow.Loaded", "after DatePickerTextBox - AFTER CATCH");
            App.LogStartupMessage("MainWindow.Loaded", "date text set");
            App.LogStartupMessage("MainWindow.Loaded", "before _uiTimer.Start");
            _uiTimer.Start();
            App.LogStartupMessage("MainWindow.Loaded", "after _uiTimer.Start");
            App.LogStartupMessage("MainWindow.Loaded", "timer started");
            App.LogStartupMessage("MainWindow.Loaded", "before AddWindowMessageHook");
            AddWindowMessageHook();
            App.LogStartupMessage("MainWindow.Loaded", "after AddWindowMessageHook");
            App.LogStartupMessage("MainWindow.Loaded", "hook added");
            App.LogStartupMessage("MainWindow.Loaded", "timer and hook started");

            App.LogStartupMessage("MainWindow.Loaded", "initial ui ready");

            App.LogStartupMessage("MainWindow.Loaded", "calling InitializeAppAsync");
            InitializeAppAsync();
            App.LogStartupMessage("MainWindow.Loaded", "InitializeAppAsync returned");
        }
        catch (Exception ex)
        {
            App.LogStartupException("MainWindow.Loaded", ex);
            App.LogStartupMessage("MainWindow.Loaded", "Exception caught in Loaded event");
        }
    }
    
    private async void InitializeAppAsync()
    {
        if (_initialized)
        {
            App.LogStartupMessage("MainWindow.InitializeAppAsync", "already initialized, skipping");
            return;
        }
        
        _initialized = true;
        _loadingState.Phase = DataLoadPhase.Loading;
        _loadingState.Message = "正在初始化...";
        
        try
        {
            App.LogStartupMessage("MainWindow.InitializeAppAsync", "begin");
            if (_trackerService == null)
            {
                App.LogStartupMessage("MainWindow.InitializeAppAsync", "trackerService is null, this should not happen");
                _loadingState.Phase = DataLoadPhase.Error;
                return;
            }
            
            App.LogStartupMessage("MainWindow.InitializeAppAsync", "calling trackerService.InitializeAsync");
            await _trackerService.InitializeAsync();
            App.LogStartupMessage("MainWindow.InitializeAppAsync", "service initialized");
            _trackerService.SessionChanged += TrackerService_SessionChanged;
            _trackerService.Start();
            App.LogStartupMessage("MainWindow.InitializeAppAsync", "tracking started");
            
            LoadSubjectOptions();
            BuildSessionContextMenu();
            
            // 阶段1: 并行加载今日数据
            _loadingState.Message = "正在加载今日数据...";
            await LoadSessionsForSelectedDateAsync();
            
            _loadingState.Phase = DataLoadPhase.Loaded;
            _loadingState.Message = null;
            
            RefreshStartupState(enableByDefault: false);
            RefreshIdleTimeoutState();
            
            // 阶段2: 后台预热
            _ = PreloadTimeDistributionAsync();
            
            App.LogStartupMessage("MainWindow.InitializeAppAsync", "completed");
        }
        catch (Exception ex)
        {
            App.LogStartupException("MainWindow.InitializeAppAsync", ex);
            _loadingState.Phase = DataLoadPhase.Error;
            _loadingState.Message = ex.Message;
            try
            {
                _trackerService?.Start();
                _ = LoadSessionsForSelectedDateAsync();
            }
            catch (Exception fallbackEx)
            {
                App.LogStartupException("MainWindow.InitializeAppAsync.FallbackStart", fallbackEx);
            }
        }
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_initialized)
        {
            App.LogStartupMessage("MainWindow.ContentRendered", "already initialized, skipping");
            return;
        }
        
        try
        {
            App.LogStartupMessage("MainWindow.ContentRendered", "begin");
            InitializeAppAsync();
        }
        catch (Exception ex)
        {
            App.LogStartupException("MainWindow.ContentRendered", ex);
        }
    }

    private async Task InitializeMainWindowAsync()
    {
        try
        {
            App.LogStartupMessage("MainWindow.InitializeAsync", "begin");
            await Task.Run(() =>
            {
                App.LogStartupMessage("MainWindow.InitializeAsync", "background work");
            });
            await Dispatcher.InvokeAsync(() =>
            {
                LoadSubjectOptions();
                LoadSessionsForSelectedDate();
            }, DispatcherPriority.ContextIdle);
            App.LogStartupMessage("MainWindow.InitializeAsync", "completed");
        }
        catch (Exception ex)
        {
            App.LogStartupException("MainWindow.InitializeAsync", ex);
            await Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(string.Format(LocalizationService.Instance.Get("App.BackendInitFailed"), ex.Message), LocalizationService.Instance.Get("App.Name"), MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
    }

    private void AddWindowMessageHook()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _windowHandle = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        RegisterManualIdleHotkey();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == ManualIdleHotkeyId)
        {
            EnterManualIdle();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmSettingChange)
        {
            RefreshSystemThemeIfNeeded();
            return IntPtr.Zero;
        }

        if (msg != WmMouseHWheel || !IsMouseOverTimeDistribution())
        {
            return IntPtr.Zero;
        }

        var delta = GetWheelDeltaWParam(wParam);
        if (delta == 0)
        {
            return IntPtr.Zero;
        }

        // 触摸板水平滚动 → 委托给控件
        if (TimeDistributionControl is not null)
        {
            TimeDistributionControl.OffsetX -= delta * 0.5 / TimeDistributionControl.Zoom;
            TimeDistributionControl.ClampOffset();
            TimeDistributionControl.UpdateRender();
        }

        handled = true;
        return IntPtr.Zero;
    }

    private static int GetWheelDeltaWParam(IntPtr wParam)
    {
        return (short)((wParam.ToInt64() >> 16) & 0xffff);
    }

    private const int WmMouseHWheel = 0x020E;
    private const int WmSettingChange = 0x001A;

    private void ApplyTitleBarTheme(bool isLight)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var useDarkMode = isLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, DwmWindowAttributeUseImmersiveDarkMode, ref useDarkMode, sizeof(int));

        var captionColor = isLight
            ? ColorToBgr(0xFF, 0xFF, 0xFF)
            : ColorToBgr(0x15, 0x17, 0x1E);
        DwmSetWindowAttribute(hwnd, DwmWindowAttributeCaptionColor, ref captionColor, sizeof(int));

        var textColor = isLight
            ? ColorToBgr(0x17, 0x20, 0x33)
            : ColorToBgr(0xF4, 0xF7, 0xFB);
        DwmSetWindowAttribute(hwnd, DwmWindowAttributeTextColor, ref textColor, sizeof(int));
    }

    private static int ColorToBgr(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int DwmWindowAttributeUseImmersiveDarkMode = 20;
    private const int DwmWindowAttributeCaptionColor = 35;
    private const int DwmWindowAttributeTextColor = 36;

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)

    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed && IsMouseClickInHeader(e.OriginalSource as DependencyObject))
        {
            DragMove();
        }
    }

    private void Window_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isDraggingSessionSelection)
        {
            EndSessionsSelectionDrag();
        }

        CollapseKeywordRuleListPanelIfClickedOutside(e.OriginalSource as DependencyObject);
        if (_activeTransferPopup?.IsOpen == true)
        {
            var clickedExport = IsDescendantOf(e.OriginalSource as DependencyObject, ExportMenuButton) || IsDescendantOf(e.OriginalSource as DependencyObject, ExportMenuFlyout);
            var clickedImport = IsDescendantOf(e.OriginalSource as DependencyObject, ImportMenuButton) || IsDescendantOf(e.OriginalSource as DependencyObject, ImportMenuFlyout);
            if (!clickedExport && !clickedImport)
            {
                HideTransferMenu();
            }
        }

        var source = e.OriginalSource as DependencyObject;

        if (_highlightedSession is not null)
        {
            if (IsDescendantOf(source, SessionsSection))
            {
                if (IsSessionsGridBlankArea(source))
                {
                    ClearTimeDistributionSessionSelection();
                }

                return;
            }

            ClearTimeDistributionSessionSelection();
            return;
        }

        if (IsSessionsGridBlankArea(source) || (!IsDescendantOf(source, SessionsSection) && SessionsGrid.SelectedItems.Count > 0))
        {
            SessionsGrid.UnselectAll();
            System.Windows.Input.Keyboard.ClearFocus();
        }
    }

    private void CollapseKeywordRuleListPanelIfClickedOutside(DependencyObject? source)
    {
        if (KeywordRuleListPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        if (IsDescendantOf(source, KeywordRuleListPanel) || IsDescendantOf(source, KeywordRuleListButton))
        {
            return;
        }

        KeywordRuleListPanel.Visibility = Visibility.Collapsed;
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_isDraggingSessionSelection)
        {
            EndSessionsSelectionDrag();
        }

        if (_highlightedSession is not null)
        {
            ClearTimeDistributionSessionSelection();
        }
        else if (SessionsGrid.SelectedItems.Count > 0)
        {
            SessionsGrid.UnselectAll();
        }

        KeywordRuleListPanel.Visibility = Visibility.Collapsed;
        HideTransferMenu();
    }

    private bool IsSessionsGridBlankArea(DependencyObject? source)
    {
        return IsDescendantOf(source, SessionsGrid)
            && FindAncestor<DataGridRow>(source) is null
            && FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(source) is null
            && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) is null;
    }

    private bool IsMouseClickInHeader(DependencyObject? source)
    {

        while (source is not null)
        {
            if (source is Border border && Grid.GetRow(border) == 0)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            UnregisterManualIdleHotkey();
            HideCompactSessionWindow();
            _trackerService.Dispose();
            System.Windows.Application.Current.Shutdown();
            return;
        }

        e.Cancel = true;
        WindowState = WindowState.Minimized;
        Hide();
    }

    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ExitFromTray()
    {
        _allowClose = true;
        Close();
    }

    private async void LoadThemeWithDelay()
    {
        await Task.Delay(100);
        try
        {
            var theme = _trackerService.Theme;
            ApplyTheme(theme, save: false);
            App.LogStartupMessage("MainWindow.Loaded", "theme applied successfully");
        }
        catch (Exception ex)
        {
            App.LogStartupException("MainWindow.LoadThemeWithDelay", ex);
            App.LogStartupMessage("MainWindow.Loaded", "theme application failed, using default");
        }
    }

    internal static string FormatDuration(TimeSpan span)

    {
        var totalMinutes = span.TotalMinutes;
        if (totalMinutes < 60)
        {
            return $"{totalMinutes:F1}分钟";
        }

        var hours = (int)(totalMinutes / 60);
        var minutes = (int)Math.Round(totalMinutes - hours * 60, MidpointRounding.AwayFromZero);
        if (minutes == 60)
        {
            hours++;
            minutes = 0;
        }

        return $"{hours}h{minutes}m";
    }

    private static string FormatDurationShort(TimeSpan span)
    {
        return FormatDuration(span);
    }



}

public enum SessionSearchMode
{
    Subject,
    Title,
    Process,
    All
}

public sealed class UsageSession : INotifyPropertyChanged
{
    private const string EmptySubjectLabel = "空分类";
    private string _id = string.Empty;
    private string _processName = string.Empty;
    private string _windowTitle = string.Empty;
    private DateTime _startTime;
    private DateTime _endTime;
    private string? _manualSubject;
    private IReadOnlyList<ParallelActivitySnapshot> _parallelActivities = [];
    private bool _isHighlighted;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetField(ref _isHighlighted, value);
    }

    public string ProcessName
    {
        get => _processName;
        set => SetField(ref _processName, value);
    }

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetField(ref _windowTitle, value);
    }

    public DateTime StartTime
    {
        get => _startTime;
        set
        {
            if (SetField(ref _startTime, value))
            {
                OnPropertyChanged(nameof(StartText));
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public DateTime EndTime
    {
        get => _endTime;
        set
        {
            if (SetField(ref _endTime, value))
            {
                OnPropertyChanged(nameof(EndText));
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public string? ManualSubject
    {
        get => _manualSubject;
        set
        {
            if (SetField(ref _manualSubject, value))
            {
                OnPropertyChanged(nameof(SubjectText));
            }
        }
    }

    public IReadOnlyList<ParallelActivitySnapshot> ParallelActivities
    {
        get => _parallelActivities;
        set
        {
            var normalized = value ?? [];
            if (SetField(ref _parallelActivities, normalized))
            {
                OnPropertyChanged(nameof(HasParallelActivities));
                OnPropertyChanged(nameof(ParallelSummaryText));
            }
        }
    }

    public bool HasParallelActivities => ParallelActivities.Count > 0;
    public string ParallelSummaryText => HasParallelActivities
        ? "并行：" + string.Join("、", ParallelActivities.Take(2).Select(x => x.DisplayText)) + (ParallelActivities.Count > 2 ? $" 等 {ParallelActivities.Count} 项" : string.Empty)
        : string.Empty;

    public string SubjectText => ManualSubject ?? EmptySubjectLabel;
    public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : DateTime.Now - StartTime;
    public string StartText => StartTime.ToString("MM-dd HH:mm:ss");
    public string EndText => EndTime > StartTime ? EndTime.ToString("MM-dd HH:mm:ss") : "进行中";
    public string DurationText => FormatDuration(Duration);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatDuration(TimeSpan span)
    {
        return MainWindow.FormatDuration(span);
    }
}

public sealed class ProcessSummary : INotifyPropertyChanged
{
    private string _processName = string.Empty;
    private TimeSpan _totalDuration;
    private double _barRatio;

    public string ProcessName
    {
        get => _processName;
        set => SetField(ref _processName, value);
    }

    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set
        {
            if (SetField(ref _totalDuration, value))
            {
                OnPropertyChanged(nameof(TotalDurationText));
            }
        }
    }

    public string TotalDurationText => MainWindow.FormatDuration(TotalDuration);

    public double BarRatio
    {
        get => _barRatio;
        set => SetField(ref _barRatio, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class GrandChildSubjectSummary : INotifyPropertyChanged
{
    private string _subjectName = string.Empty;
    private TimeSpan _totalDuration;
    private int _sessionCount;
    private int _processCount;
    private System.Windows.Media.Brush _accentBrush = System.Windows.Media.Brushes.CadetBlue;

    public string SubjectName
    {
        get => _subjectName;
        set => SetField(ref _subjectName, value);
    }

    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set
        {
            if (SetField(ref _totalDuration, value))
            {
                OnPropertyChanged(nameof(TotalDurationText));
            }
        }
    }

    public int SessionCount
    {
        get => _sessionCount;
        set
        {
            if (SetField(ref _sessionCount, value))
            {
                OnPropertyChanged(nameof(SessionCountText));
            }
        }
    }

    public int ProcessCount
    {
        get => _processCount;
        set
        {
            if (SetField(ref _processCount, value))
            {
                OnPropertyChanged(nameof(ProcessCountText));
            }
        }
    }

    public System.Windows.Media.Brush AccentBrush
    {
        get => _accentBrush;
        set => SetField(ref _accentBrush, value);
    }

    public string TotalDurationText => MainWindow.FormatDuration(TotalDuration);
    public string SessionCountText => $"{SessionCount} 段";
    public string ProcessCountText => $"{ProcessCount} 进程";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ChildSubjectSummary : INotifyPropertyChanged
{
    private string _majorName = string.Empty;
    private string _subjectName = string.Empty;
    private TimeSpan _totalDuration;
    private int _sessionCount;
    private int _processCount;
    private bool _isExpanded = true;
    private ObservableCollection<GrandChildSubjectSummary> _children = new();
    private System.Windows.Media.Brush _accentBrush = System.Windows.Media.Brushes.CadetBlue;

    public string MajorName
    {
        get => _majorName;
        set => SetField(ref _majorName, value);
    }

    public string SubjectName
    {
        get => _subjectName;
        set => SetField(ref _subjectName, value);
    }

    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set
        {
            if (SetField(ref _totalDuration, value))
            {
                OnPropertyChanged(nameof(TotalDurationText));
            }
        }
    }

    public int SessionCount
    {
        get => _sessionCount;
        set
        {
            if (SetField(ref _sessionCount, value))
            {
                OnPropertyChanged(nameof(SessionCountText));
            }
        }
    }

    public int ProcessCount
    {
        get => _processCount;
        set
        {
            if (SetField(ref _processCount, value))
            {
                OnPropertyChanged(nameof(ProcessCountText));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandGlyph));
                OnPropertyChanged(nameof(ChildrenVisibility));
            }
        }
    }

    public ObservableCollection<GrandChildSubjectSummary> Children
    {
        get => _children;
        set
        {
            if (SetField(ref _children, value))
            {
                OnPropertyChanged(nameof(ChildrenVisibility));
                OnPropertyChanged(nameof(ExpandButtonVisibility));
                OnPropertyChanged(nameof(ExpandGlyph));
            }
        }
    }

    public System.Windows.Media.Brush AccentBrush
    {
        get => _accentBrush;
        set => SetField(ref _accentBrush, value);
    }

    public string TotalDurationText => MainWindow.FormatDuration(TotalDuration);
    public string SessionCountText => $"{SessionCount} 段";
    public string ProcessCountText => $"{ProcessCount} 进程";
    public string ExpandGlyph => Children.Count == 0 ? string.Empty : (IsExpanded ? "▲" : "▼");
    public Visibility ExpandButtonVisibility => Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChildrenVisibility => IsExpanded && Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class SubjectTreeSummary : INotifyPropertyChanged
{
    private string _subjectName = string.Empty;
    private TimeSpan _totalDuration;
    private int _sessionCount;
    private int _processCount;
    private double _barRatio;
    private bool _isExpanded = true;
    private ObservableCollection<ChildSubjectSummary> _children = new();

    public string SubjectName
    {
        get => _subjectName;
        set => SetField(ref _subjectName, value);
    }

    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set
        {
            if (SetField(ref _totalDuration, value))
            {
                OnPropertyChanged(nameof(TotalDurationText));
            }
        }
    }

    public int SessionCount
    {
        get => _sessionCount;
        set
        {
            if (SetField(ref _sessionCount, value))
            {
                OnPropertyChanged(nameof(SessionCountText));
            }
        }
    }

    public int ProcessCount
    {
        get => _processCount;
        set
        {
            if (SetField(ref _processCount, value))
            {
                OnPropertyChanged(nameof(ProcessCountText));
            }
        }
    }

    public double BarRatio
    {
        get => _barRatio;
        set => SetField(ref _barRatio, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandGlyph));
                OnPropertyChanged(nameof(ChildrenVisibility));
            }
        }
    }

    public ObservableCollection<ChildSubjectSummary> Children
    {
        get => _children;
        set
        {
            if (SetField(ref _children, value))
            {
                OnPropertyChanged(nameof(ChildrenVisibility));
                OnPropertyChanged(nameof(ExpandButtonVisibility));
                OnPropertyChanged(nameof(ExpandGlyph));
            }
        }
    }

    public string TotalDurationText => MainWindow.FormatDuration(TotalDuration);
    public string SessionCountText => $"{SessionCount} 段";
    public string ProcessCountText => $"{ProcessCount} 进程";
    public string ExpandGlyph => Children.Count == 0 ? string.Empty : (IsExpanded ? "▲" : "▼");
    public Visibility ExpandButtonVisibility => Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChildrenVisibility => IsExpanded && Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public static class SubjectClassifier
{
    private static List<SubjectDefinition> _subjectDefinitions =
    [
        new() { Name = "数学", Children = ["高数", "线代", "概率论"] },
        new() { Name = "英语", Children = ["单词", "阅读", "听力"] },
        new() { Name = "语文", Children = ["作文", "古诗文"] },
        new() { Name = "物理", Children = ["力学", "电磁学"] },
        new() { Name = "化学", Children = ["有机", "无机"] },
        new() { Name = "生物", Children = ["细胞", "遗传"] },
        new() { Name = "编程", Children = ["数据结构", "算法", "C#"] }
    ];

    public static void SetSubjectDefinitions(IEnumerable<SubjectDefinition> subjectDefinitions)
    {
        _subjectDefinitions = subjectDefinitions.Select(x => x.Clone()).ToList();
    }

    public static string? GetSubjectName(UsageSession session)
    {
        return session.ManualSubject;
    }

    public static string? GetSubjectPathText(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        foreach (var major in _subjectDefinitions)
        {
            if (string.Equals(major.Name, subject, StringComparison.OrdinalIgnoreCase))
            {
                return major.Name;
            }

            foreach (var parent in major.Parents)
            {
                if (string.Equals(parent.Name, subject, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{major.Name} / {parent.Name}";
                }

                var child = parent.Children.FirstOrDefault(child => string.Equals(child, subject, StringComparison.OrdinalIgnoreCase));
                if (child is not null)
                {
                    return $"{major.Name} / {parent.Name} / {child}";
                }
            }
        }

        return subject;
    }
}
