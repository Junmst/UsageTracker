﻿﻿﻿﻿using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace UsageTrackerNative.Modules.TimeDistribution;

public partial class TimeDistributionPage : System.Windows.Controls.UserControl
{
    private readonly Shell.V2AppContext _context;
    private bool _isMonthView;
    private long _refreshSerial;
    private UsageSession? _selectedSession;
    private string? _subjectFilter;
    private Dictionary<string, HashSet<string>> _subjectFilterGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool _isSubjectFilterMenuOpen;
    private Window? _subjectFilterHostWindow;
    private System.Windows.Threading.DispatcherTimer? _subjectFilterCloseTimer;
    private static readonly Duration SubjectFilterMenuAnimationDuration = new(TimeSpan.FromMilliseconds(240));
    private static readonly Duration ButtonPressAnimationDuration = new(TimeSpan.FromMilliseconds(120));

    public TimeDistributionPage(Shell.V2AppContext context)
    {
        _context = context;
        InitializeComponent();
        _subjectFilterCloseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _subjectFilterCloseTimer.Tick += SubjectFilterCloseTimer_Tick;
        DistributionControl.SessionClicked += DistributionControl_SessionClicked;
        Loaded += TimeDistributionPage_Loaded;
        Unloaded += TimeDistributionPage_Unloaded;
        _context.SelectedDateChanged += Context_SelectedDateChanged;
        _context.PreviewModeChanged += Context_PreviewModeChanged;
        _context.DataChanged += Context_DataChanged;
        UpdateViewModeButtons();
        UpdateSubjectFilterButtonText();
    }

    public void RefreshTheme()
    {
        UpdateViewModeButtons();
        UpdateSubjectFilterButtonText();
        DistributionControl.UpdateTheme();
    }

    private void TimeDistributionPage_Unloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _context.SelectedDateChanged -= Context_SelectedDateChanged;
        _context.PreviewModeChanged -= Context_PreviewModeChanged;
        _context.DataChanged -= Context_DataChanged;
        _subjectFilterCloseTimer?.Stop();
        DetachSubjectFilterWindowHandlers();
        HideSubjectFilterMenu(immediate: true);
    }

    private async void TimeDistributionPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        AttachSubjectFilterWindowHandlers();
        UpdateViewModeButtons();
        await RefreshAsync();
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
        _subjectFilter = null;
        UpdateSubjectFilterButtonText();
        await RefreshAsync();
    }

    private async void SevenDaysButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _isMonthView = false;
        UpdateViewModeButtons();
        await RefreshAsync();
    }

    private async void MonthButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _isMonthView = true;
        UpdateViewModeButtons();
        await RefreshAsync();
    }

    private void UpdateViewModeButtons()
    {
        SetViewModeButtonState(SevenDaysButton, !_isMonthView);
        SetViewModeButtonState(MonthButton, _isMonthView);
    }

    private static void SetViewModeButtonState(Border button, bool selected)
    {
        button.Tag = selected ? "Selected" : "Normal";
        button.SetResourceReference(Border.BackgroundProperty, selected ? "MenuSelectedBrush" : "ButtonBackgroundBrush");
        button.SetResourceReference(Border.BorderBrushProperty, selected ? "MenuSelectedBrush" : "ButtonBorderBrush");
        if (button.Child is TextBlock text)
        {
            text.SetResourceReference(TextBlock.ForegroundProperty, selected ? "AccentTextBrush" : "PrimaryTextBrush");
        }
    }

    private void DistributionFilterButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyButtonHoverState(border, hover: true);
        }
    }

    private void DistributionFilterButton_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left || sender is not Border border)
        {
            return;
        }

        AnimateButtonPressedState(border, pressed: true);
    }

    private void DistributionFilterButton_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            AnimateButtonPressedState(border, pressed: false);
            ApplyButtonHoverState(border, hover: border.IsMouseOver);
        }
    }

    private void DistributionFilterButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border border)
        {
            AnimateButtonPressedState(border, pressed: false);
            ApplyButtonHoverState(border, hover: false);
        }
    }

    private static bool IsSelectedButton(Border border)
    {
        return string.Equals(border.Tag as string, "Selected", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyButtonHoverState(Border border, bool hover)
    {
        if (hover)
        {
            border.SetResourceReference(Border.BorderBrushProperty, "InputFocusBorderBrush");
            return;
        }

        border.SetResourceReference(Border.BorderBrushProperty, IsSelectedButton(border) ? "MenuSelectedBrush" : "ButtonBorderBrush");
    }

    private void AnimateButtonPressedState(Border border, bool pressed)
    {
        AnimateBrush(border, Border.BackgroundProperty, GetCurrentBrushColor(border.Background, IsSelectedButton(border) ? "MenuSelectedBrush" : "ButtonBackgroundBrush"), pressed);
        AnimateBrush(border, Border.BorderBrushProperty, GetCurrentBrushColor(border.BorderBrush, IsSelectedButton(border) ? "MenuSelectedBrush" : "ButtonBorderBrush"), pressed);
    }

    private static void AnimateBrush(Border border, DependencyProperty property, System.Windows.Media.Color baseColor, bool pressed)
    {
        var targetColor = pressed ? DarkenColor(baseColor, 0.14) : baseColor;
        var currentColor = property == Border.BackgroundProperty
            ? GetCurrentBrushColor(border.Background, null)
            : GetCurrentBrushColor(border.BorderBrush, null);
        var brush = new SolidColorBrush(currentColor);
        border.SetValue(property, brush);
        var animation = new System.Windows.Media.Animation.ColorAnimation
        {
            To = targetColor,
            Duration = ButtonPressAnimationDuration,
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        if (!pressed)
        {
            animation.Completed += (_, _) =>
            {
                border.SetResourceReference(property, property == Border.BackgroundProperty
                    ? (IsSelectedButton(border) ? "MenuSelectedBrush" : "ButtonBackgroundBrush")
                    : (border.IsMouseOver ? "InputFocusBorderBrush" : (IsSelectedButton(border) ? "MenuSelectedBrush" : "ButtonBorderBrush")));
            };
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private static System.Windows.Media.Color GetCurrentBrushColor(Brush? brush, string? fallbackResourceKey)
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

    private static System.Windows.Media.Color DarkenColor(System.Windows.Media.Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return System.Windows.Media.Color.FromArgb(
            color.A,
            (byte)Math.Round(color.R * (1 - amount)),
            (byte)Math.Round(color.G * (1 - amount)),
            (byte)Math.Round(color.B * (1 - amount)));
    }

    private async void SubjectFilterButtonText_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        await RefreshAsync();
    }

    private void SubjectFilterDropDown_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleSubjectFilterMenu();
    }

    private void SubjectFilterHost_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleSubjectFilterMenu();
    }

    private void ToggleSubjectFilterMenu()
    {
        if (_isSubjectFilterMenuOpen)
        {
            HideSubjectFilterMenu();
        }
        else
        {
            ShowSubjectFilterMenu();
        }
    }

    private void ShowSubjectFilterMenu()
    {
        _subjectFilterCloseTimer?.Stop();
        AttachSubjectFilterWindowHandlers();
        BuildSubjectFilterMenu();
        SubjectFilterPopup.IsOpen = true;
        SubjectFilterPopup.HorizontalOffset = 0;
        SubjectFilterPopup.VerticalOffset = 8;
        _isSubjectFilterMenuOpen = true;
        AnimateSubjectFilterMenu(show: true);
    }

    private void HideSubjectFilterMenu(bool immediate = false)
    {
        if (!_isSubjectFilterMenuOpen && !SubjectFilterPopup.IsOpen)
        {
            return;
        }

        _subjectFilterCloseTimer?.Stop();
        _isSubjectFilterMenuOpen = false;
        if (immediate)
        {
            SubjectFilterFlyout.BeginAnimation(UIElement.OpacityProperty, null);
            SubjectFilterFlyoutTransform.BeginAnimation(TranslateTransform.YProperty, null);
            SubjectFilterFlyout.Opacity = 0;
            SubjectFilterFlyoutTransform.Y = -8;
            SubjectFilterPopup.IsOpen = false;
            return;
        }

        AnimateSubjectFilterMenu(show: false);
    }

    private void AnimateSubjectFilterMenu(bool show)
    {
        var easing = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var opacityAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = SubjectFilterMenuAnimationDuration,
            EasingFunction = easing
        };
        var yAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 0 : -8,
            Duration = SubjectFilterMenuAnimationDuration,
            EasingFunction = easing
        };
        if (!show)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                if (!_isSubjectFilterMenuOpen)
                {
                    SubjectFilterPopup.IsOpen = false;
                }
            };
        }
        SubjectFilterFlyout.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        SubjectFilterFlyoutTransform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
    }

    private void BuildSubjectFilterMenu()
    {
        SubjectFilterMenuPanel.Children.Clear();
        _subjectFilterGroups = BuildSubjectFilterGroups(_context.GetSubjectDefinitions());
        SubjectFilterMenuPanel.Children.Add(CreateSubjectFilterItem("全部分类", null, null));
        foreach (var definition in _context.GetSubjectDefinitions())
        {
            SubjectFilterMenuPanel.Children.Add(CreateSubjectFilterItem(definition.Name, definition.Name, BuildParentSubjectFilterMenu(definition)));
        }
        UpdateSubjectFilterMenuVisuals(SubjectFilterMenuPanel);
    }

    private StackPanel BuildParentSubjectFilterMenu(SubjectDefinition definition)
    {
        var panel = new StackPanel();
        foreach (var parent in definition.Parents)
        {
            panel.Children.Add(CreateSubjectFilterItem(parent.Name, parent.Name, parent.Children.Count > 0 ? BuildChildSubjectFilterMenu(parent) : null));
        }
        return panel;
    }

    private StackPanel BuildChildSubjectFilterMenu(SubjectParentDefinition parent)
    {
        var panel = new StackPanel();
        foreach (var child in parent.Children)
        {
            panel.Children.Add(CreateSubjectFilterItem(child, child, null));
        }
        return panel;
    }

    private Border CreateSubjectFilterItem(string text, string? subject, StackPanel? submenu)
    {
        var item = new Border { Tag = subject };
        item.Style = TryFindResource("SubjectFilterItemStyle") as Style;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        grid.Children.Add(label);
        if (submenu is not null)
        {
            var arrow = new TextBlock { Text = "›", FontSize = 14, FontWeight = FontWeights.Black, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);
            var popup = CreateSubjectFilterSubmenuPopup(item, submenu);
            arrow.Tag = popup;
            item.MouseEnter += (_, _) =>
            {
                popup.IsOpen = true;
                if (popup.Child is Border flyout && flyout.RenderTransform is TranslateTransform transform)
                {
                    AnimateFlyout(flyout, transform, show: true);
                }
            };
            item.MouseLeave += (_, _) =>
            {
                if (popup.IsMouseOver)
                {
                    return;
                }
                if (popup.Child is Border flyout && flyout.RenderTransform is TranslateTransform transform)
                {
                    AnimateFlyout(flyout, transform, show: false, () => popup.IsOpen = false);
                }
            };
        }
        item.Child = grid;
        item.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            _subjectFilter = subject;
            UpdateSubjectFilterButtonText();
            HideSubjectFilterMenu();
            await RefreshAsync();
        };
        return item;
    }

    private Popup CreateSubjectFilterSubmenuPopup(FrameworkElement owner, StackPanel content)
    {
        var transform = new TranslateTransform { Y = -8 };
        var flyout = new Border
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
        var popup = new Popup
        {
            PlacementTarget = owner,
            Placement = PlacementMode.Right,
            HorizontalOffset = 6,
            AllowsTransparency = true,
            StaysOpen = true,
            Child = flyout
        };
        flyout.MouseEnter += (_, _) => _subjectFilterCloseTimer?.Stop();
        flyout.MouseLeave += (_, _) => StartSubjectFilterCloseCheck();
        return popup;
    }

    private void UpdateSubjectFilterMenuVisuals(System.Windows.Controls.Panel panel)
    {
        foreach (var item in panel.Children.OfType<Border>())
        {
            var subject = item.Tag as string;
            var active = string.Equals(_subjectFilter, subject, StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrWhiteSpace(_subjectFilter) && subject is null);
            if (item.Child is Grid grid && grid.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
            {
                label.FontWeight = active ? FontWeights.Black : FontWeights.SemiBold;
                label.FontSize = active ? 14.5 : 13.5;
                label.SetResourceReference(TextBlock.ForegroundProperty, active ? "AccentRedBrush" : "PrimaryTextBrush");
            }
        }
    }

    private static void AnimateFlyout(Border flyout, TranslateTransform transform, bool show, Action? completed = null)
    {
        var easing = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var opacityAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = SubjectFilterMenuAnimationDuration,
            EasingFunction = easing
        };
        var yAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 0 : -8,
            Duration = SubjectFilterMenuAnimationDuration,
            EasingFunction = easing
        };
        if (completed is not null)
        {
            opacityAnimation.Completed += (_, _) => completed();
        }
        flyout.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        transform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
    }

    private void TimeDistributionPage_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isSubjectFilterMenuOpen)
        {
            return;
        }

        if (IsMouseWithin(SubjectFilterHost) || IsMouseWithin(SubjectFilterFlyout))
        {
            return;
        }

        HideSubjectFilterMenu();
    }

    private void SubjectFilterMenuArea_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _subjectFilterCloseTimer?.Stop();
    }

    private void SubjectFilterMenuArea_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        StartSubjectFilterCloseCheck();
    }

    private void StartSubjectFilterCloseCheck()
    {
        if (!_isSubjectFilterMenuOpen)
        {
            return;
        }

        _subjectFilterCloseTimer?.Stop();
        _subjectFilterCloseTimer?.Start();
    }

    private void SubjectFilterCloseTimer_Tick(object? sender, EventArgs e)
    {
        _subjectFilterCloseTimer?.Stop();
        if (!_isSubjectFilterMenuOpen)
        {
            return;
        }

        if (IsMouseWithin(SubjectFilterHost) || IsMouseWithin(SubjectFilterFlyout) || IsMouseWithinAnyOpenSubjectFilterSubmenu())
        {
            return;
        }

        HideSubjectFilterMenu();
    }

    private bool IsMouseWithinAnyOpenSubjectFilterSubmenu()
    {
        return HasOpenSubmenuMouseOver(SubjectFilterMenuPanel);
    }

    private static bool HasOpenSubmenuMouseOver(System.Windows.Controls.Panel panel)
    {
        foreach (var item in panel.Children.OfType<Border>())
        {
            if (item.Tag is Popup popup && popup.IsOpen)
            {
                if (popup.Child is FrameworkElement child && IsMouseWithin(child))
                {
                    return true;
                }

                if (popup.Child is Border { Child: System.Windows.Controls.Panel nestedPanel } && HasOpenSubmenuMouseOver(nestedPanel))
                {
                    return true;
                }
            }

            if (item.Child is Grid grid)
            {
                foreach (var child in grid.Children.OfType<FrameworkElement>())
                {
                    if (child.Tag is Popup childPopup && childPopup.IsOpen)
                    {
                        if (childPopup.Child is FrameworkElement popupChild && IsMouseWithin(popupChild))
                        {
                            return true;
                        }

                        if (childPopup.Child is Border { Child: System.Windows.Controls.Panel nestedPanel } && HasOpenSubmenuMouseOver(nestedPanel))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private void AttachSubjectFilterWindowHandlers()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(_subjectFilterHostWindow, window))
        {
            return;
        }

        DetachSubjectFilterWindowHandlers();
        _subjectFilterHostWindow = window;
        if (_subjectFilterHostWindow is null)
        {
            return;
        }

        _subjectFilterHostWindow.LocationChanged += SubjectFilterHostWindow_PositionChanged;
        _subjectFilterHostWindow.SizeChanged += SubjectFilterHostWindow_PositionChanged;
        _subjectFilterHostWindow.Deactivated += SubjectFilterHostWindow_Deactivated;
    }

    private void DetachSubjectFilterWindowHandlers()
    {
        if (_subjectFilterHostWindow is null)
        {
            return;
        }

        _subjectFilterHostWindow.LocationChanged -= SubjectFilterHostWindow_PositionChanged;
        _subjectFilterHostWindow.SizeChanged -= SubjectFilterHostWindow_PositionChanged;
        _subjectFilterHostWindow.Deactivated -= SubjectFilterHostWindow_Deactivated;
        _subjectFilterHostWindow = null;
    }

    private void SubjectFilterHostWindow_PositionChanged(object? sender, EventArgs e)
    {
        HideSubjectFilterMenu();
    }

    private void SubjectFilterHostWindow_Deactivated(object? sender, EventArgs e)
    {
        HideSubjectFilterMenu();
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

    private static Dictionary<string, HashSet<string>> BuildSubjectFilterGroups(IReadOnlyList<SubjectDefinition> definitions)
    {
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var majorSet = GetOrCreateGroup(groups, definition.Name);
            majorSet.Add(definition.Name);
            foreach (var parent in definition.Parents)
            {
                majorSet.Add(parent.Name);
                var parentSet = GetOrCreateGroup(groups, parent.Name);
                parentSet.Add(parent.Name);
                foreach (var child in parent.Children)
                {
                    majorSet.Add(child);
                    parentSet.Add(child);
                    GetOrCreateGroup(groups, child).Add(child);
                }
            }
        }
        return groups;
    }

    private static HashSet<string> GetOrCreateGroup(Dictionary<string, HashSet<string>> groups, string subject)
    {
        if (!groups.TryGetValue(subject, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            groups[subject] = set;
        }
        return set;
    }

    private void UpdateSubjectFilterButtonText()
    {
        SubjectFilterButtonText.Text = string.IsNullOrWhiteSpace(_subjectFilter) ? "全部分类" : _subjectFilter;
        SubjectFilterHost.ToolTip = null;
    }

    private async Task RefreshAsync()
    {
        var serial = System.Threading.Interlocked.Increment(ref _refreshSerial);
        var stopwatch = Stopwatch.StartNew();
        await _context.Initialization;
        var initMs = stopwatch.ElapsedMilliseconds;
        var dates = GetVisibleDates().ToList();
        if (dates.Count == 0)
        {
            DistributionControl.VisibleDates = [];
            DistributionControl.Sessions = [];
            DistributionControl.UpdateRender();
            App.LogStartupMessage("TimeDistribution.Refresh", $"#{serial} empty dates, total={stopwatch.ElapsedMilliseconds}ms");
            return;
        }

        var start = dates.Min().Date.AddHours(4);
        var end = dates.Max().Date.AddHours(4).AddDays(1);
        var queryStartMs = stopwatch.ElapsedMilliseconds;
        var records = await _context.QuerySessionsInRangeAsync(start, end);
        var queryMs = stopwatch.ElapsedMilliseconds - queryStartMs;
        var mapStartMs = stopwatch.ElapsedMilliseconds;
        _subjectFilterGroups = BuildSubjectFilterGroups(_context.GetSubjectDefinitions());
        var sessions = records
            .Select(ToUsageSession)
            .Where(x => x.Duration > TimeSpan.Zero)
            .Where(MatchesSubjectFilter)
            .ToList();
        var mapMs = stopwatch.ElapsedMilliseconds - mapStartMs;
        var renderStartMs = stopwatch.ElapsedMilliseconds;

        DistributionControl.VisibleDates = dates;
        DistributionControl.Sessions = sessions;
        DistributionControl.UpdateRender();
        var renderMs = stopwatch.ElapsedMilliseconds - renderStartMs;
        App.LogStartupMessage(
            "TimeDistribution.Refresh",
            $"#{serial} view={(_isMonthView ? "month" : "sevenDays")}, dates={dates.Count}, records={records.Count}, sessions={sessions.Count}, init={initMs}ms, query={queryMs}ms, map={mapMs}ms, render={renderMs}ms, total={stopwatch.ElapsedMilliseconds}ms");
    }

    private bool MatchesSubjectFilter(UsageSession session)
    {
        if (string.IsNullOrWhiteSpace(_subjectFilter)) return true;
        var subject = string.IsNullOrWhiteSpace(session.ManualSubject) ? session.SubjectText : session.ManualSubject;
        if (string.IsNullOrWhiteSpace(subject)) return false;
        return _subjectFilterGroups.TryGetValue(_subjectFilter, out var group)
            ? group.Contains(subject)
            : string.Equals(subject, _subjectFilter, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<DateTime> GetVisibleDates()
    {
        if (_isMonthView)
        {
            var selected = _context.SelectedDate;
            var firstDay = new DateTime(selected.Year, selected.Month, 1);
            var days = DateTime.DaysInMonth(selected.Year, selected.Month);
            return Enumerable.Range(0, days).Select(offset => firstDay.AddDays(offset)).Reverse();
        }

        return Enumerable.Range(0, 7).Select(offset => _context.SelectedDate.Date.AddDays(-offset));
    }

    private static UsageSession ToUsageSession(UsageSessionRecord record)
    {
        record.EnsureId();
        return new UsageSession
        {
            Id = record.Id,
            ProcessName = record.ProcessName,
            WindowTitle = record.WindowTitle,
            StartTime = record.StartTime,
            EndTime = record.EndTime ?? DateTime.Now,
            ManualSubject = record.ManualSubject,
            ParallelActivities = record.ParallelActivities?.Select(x => x.Clone()).ToList() ?? []
        };
    }

    private void DistributionControl_SessionClicked(object? sender, UsageSession session)
    {
        _selectedSession = session;
        var displayTitle = string.IsNullOrWhiteSpace(session.WindowTitle) ? "无标题" : session.WindowTitle;
        SelectedSessionTitleText.Text = $"已选会话：{displayTitle}";
        var parallelText = session.HasParallelActivities
            ? $" · {session.ParallelSummaryText}（不计入主时长）"
            : string.Empty;
        SelectedSessionMetaText.Text = $"{session.StartText} → {session.EndText} · {session.DurationText} · 进程：{session.ProcessName} · 分类：{session.SubjectText}{parallelText}";
    }

    private void ViewDetailsButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedSession is null)
        {
            return;
        }

        _context.RequestNavigate("sessions", BuildSessionNavigationTarget(_selectedSession));
    }

    private static string BuildSessionNavigationTarget(UsageSession session)
    {
        static string Escape(string? value) => Uri.EscapeDataString(value ?? string.Empty);
        return string.Join("|",
            Escape(session.Id),
            Escape(session.ProcessName),
            Escape(session.WindowTitle),
            session.StartTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            session.EndTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Shell.V2AppContext.NormalizeToDateBoundary(session.StartTime).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}


