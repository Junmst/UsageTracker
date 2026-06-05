using System.Windows;
using System.Windows.Media;

namespace UsageTrackerNative.Modules.Stats;

public partial class StatsPage : System.Windows.Controls.UserControl
{
    private readonly Shell.V2AppContext _context;
    private readonly StatsKind _kind;
    private readonly HashSet<string> _collapsedMajorKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedParentKeys = new(StringComparer.OrdinalIgnoreCase);
    private string _lastRowsHash = "";
    private System.Windows.Threading.DispatcherTimer? _sessionChangedDebounce;
    private static readonly Duration SubjectExpandAnimationDuration = new(TimeSpan.FromMilliseconds(240));

    public StatsPage(Shell.V2AppContext context, StatsKind kind)
    {
        _context = context;
        _kind = kind;
        InitializeComponent();
        _sessionChangedDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _sessionChangedDebounce.Tick += async (_, _) => { _sessionChangedDebounce.Stop(); await RefreshAsync(); };
        TitleText.Text = kind == StatsKind.Process ? "进程统计" : "分类统计";
        Loaded += StatsPage_Loaded;
        Unloaded += StatsPage_Unloaded;
    }

    private void EnsureSubscriptions()
    {
        _context.SelectedDateChanged -= Context_SelectedDateChanged;
        _context.TrackerService.SessionChanged -= TrackerService_SessionChanged;
        _context.DataChanged -= Context_DataChanged;
        _context.SelectedDateChanged += Context_SelectedDateChanged;
        _context.TrackerService.SessionChanged += TrackerService_SessionChanged;
        _context.DataChanged += Context_DataChanged;
    }

    private async void StatsPage_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureSubscriptions();
        await RefreshAsync(force: true);
    }

    private void StatsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _sessionChangedDebounce?.Stop();
        _context.SelectedDateChanged -= Context_SelectedDateChanged;
        _context.TrackerService.SessionChanged -= TrackerService_SessionChanged;
        _context.DataChanged -= Context_DataChanged;
    }

    private async void Context_SelectedDateChanged(object? sender, EventArgs e)
    {
        await RefreshAsync(force: true);
    }

    private async void Context_DataChanged(object? sender, EventArgs e)
    {
        await RefreshAsync(force: true);
    }

    private void TrackerService_SessionChanged(object? sender, SessionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => TrackerService_SessionChanged(sender, e));
            return;
        }

        _sessionChangedDebounce?.Stop();
        _sessionChangedDebounce?.Start();
    }

    private sealed record StatsRow(string Name, long TotalTicks, int SessionCount, bool IsParallel = false);
    private sealed record SubjectNode(string Name, long TotalTicks, int SessionCount, List<SubjectNode> Children);

    private async Task RefreshAsync(bool force = false)
    {
        if (_kind == StatsKind.Process)
        {
            var raw = await GetProcessRowsAsync();
            RefreshProcessRows(raw, force);
            return;
        }

        var subjects = await GetSubjectTreeAsync();
        RefreshSubjectRows(subjects, force);
    }

    private async Task<List<StatsRow>> GetProcessRowsAsync()
    {
        var raw = (await _context.TrackerService.QueryProcessSummariesAsync(_context.SelectedDate))
            .Select(x => new StatsRow(x.ProcessName, x.TotalTicks, x.SessionCount))
            .ToList();

        var records = await _context.QuerySessionsByDateAsync(_context.SelectedDate);
        var parallelRows = records
            .SelectMany(record => (record.ParallelActivities ?? [])
                .Where(activity => !string.IsNullOrWhiteSpace(activity.ProcessName))
                .Select(activity => new
                {
                    activity.ProcessName,
                    Duration = activity.ObservedDuration > TimeSpan.Zero
                        ? activity.ObservedDuration
                        : ((record.EndTime ?? DateTime.Now) > record.StartTime ? (record.EndTime ?? DateTime.Now) - record.StartTime : TimeSpan.Zero)
                }))
            .Where(x => x.Duration > TimeSpan.Zero)
            .GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new StatsRow($"{g.Key}（并行）", g.Sum(x => x.Duration.Ticks), g.Count(), IsParallel: true))
            .ToList();

        raw.AddRange(parallelRows);
        return raw;
    }

    private void RefreshProcessRows(List<StatsRow> raw, bool force)
    {
        var hash = string.Join('|', raw.OrderByDescending(x => x.TotalTicks).Take(80).Select(x => $"{x.Name}:{x.TotalTicks}:{x.SessionCount}:{x.IsParallel}"));
        if (!force && hash == _lastRowsHash) return;
        _lastRowsHash = hash;

        RowsPanel.Children.Clear();
        var maxTicks = raw.Count == 0 ? 1d : Math.Max(1d, raw.Max(x => (double)x.TotalTicks));
        BadgeText.Text = $"{raw.Count} 个进程";

        foreach (var row in raw.OrderByDescending(x => x.TotalTicks).Take(80))
        {
            RowsPanel.Children.Add(CreateRow(row.Name, FormatDuration(TimeSpan.FromTicks(row.TotalTicks)), row.TotalTicks / maxTicks, row.IsParallel));
        }
    }

    private void RefreshSubjectRows(List<SubjectNode> subjects, bool force)
    {
        var flatHash = FlattenSubjectNodes(subjects).Take(120).Select(x => $"{x.Name}:{x.TotalTicks}:{x.SessionCount}");
        var hash = string.Join('|', flatHash);
        if (!force && hash == _lastRowsHash) return;
        _lastRowsHash = hash;

        RowsPanel.Children.Clear();
        BadgeText.Text = $"{subjects.Count} 个大类";
        var maxTicks = subjects.Count == 0 ? 1d : Math.Max(1d, subjects.Max(x => (double)x.TotalTicks));
        foreach (var major in subjects.OrderByDescending(x => x.TotalTicks))
        {
            RowsPanel.Children.Add(CreateSubjectMajorCard(major, major.TotalTicks / maxTicks));
        }
    }

    private System.Windows.Controls.Border CreateRow(string name, string value, double ratio, bool isParallel = false)
    {
        var root = new System.Windows.Controls.Border
        {
            Style = (Style)FindResource("StatsCardStyle")
        };

        var grid = new System.Windows.Controls.Grid();
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.Child = grid;

        var header = new System.Windows.Controls.Grid();
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(header);

        var title = new System.Windows.Controls.TextBlock { Text = name, FontSize = 19, FontWeight = FontWeights.Black };
        if (isParallel)
        {
            title.Foreground = (Brush)System.Windows.Application.Current.FindResource("SecondaryTextBrush");
        }
        header.Children.Add(title);

        var valueText = new System.Windows.Controls.TextBlock
        {
            Text = value,
            FontSize = 18,
            FontWeight = FontWeights.Black,
            Foreground = (Brush)System.Windows.Application.Current.FindResource("AccentRedBrush")
        };
        System.Windows.Controls.Grid.SetColumn(valueText, 1);
        header.Children.Add(valueText);

        var bar = CreateProgress(ratio, new Thickness(0, 18, 0, 0), 8, (Brush)System.Windows.Application.Current.FindResource("AccentRedBrush"));
        System.Windows.Controls.Grid.SetRow(bar, 1);
        grid.Children.Add(bar);

        return root;
    }

    private System.Windows.Controls.Border CreateSubjectMajorCard(SubjectNode major, double ratio)
    {
        var majorKey = major.Name;
        var expanded = !_collapsedMajorKeys.Contains(majorKey);
        var colors = GetSubjectLevelBrushes();
        var root = new System.Windows.Controls.Border { Style = (Style)FindResource("SubjectMajorCardStyle") };
        var panel = new System.Windows.Controls.StackPanel();
        root.Child = panel;

        var childrenPanel = new System.Windows.Controls.StackPanel { Visibility = expanded ? Visibility.Visible : Visibility.Collapsed, Opacity = expanded ? 1 : 0 };
        panel.Children.Add(CreateSubjectHeader(
            major.Name,
            FormatDuration(TimeSpan.FromTicks(major.TotalTicks)),
            $"{major.SessionCount} 次",
            17,
            FontWeights.Black,
            colors.Major,
            true,
            expanded,
            () => ToggleMajor(majorKey, childrenPanel, root)));
        panel.Children.Add(CreateProgress(ratio, new Thickness(0, 10, 0, 0), 10, colors.Major));
        foreach (var parent in major.Children.OrderByDescending(x => x.TotalTicks))
        {
            childrenPanel.Children.Add(CreateSubjectParentCard(parent, major.Name, major.TotalTicks, colors));
        }
        panel.Children.Add(childrenPanel);

        return root;
    }

    private System.Windows.Controls.Border CreateSubjectParentCard(SubjectNode parent, string majorName, long majorTicks, SubjectLevelBrushes colors)
    {
        var parentKey = $"{majorName}/{parent.Name}";
        var expanded = !_collapsedParentKeys.Contains(parentKey);
        var root = new System.Windows.Controls.Border { Style = (Style)FindResource("SubjectParentCardStyle"), Margin = new Thickness(18, 10, 0, 0) };
        var panel = new System.Windows.Controls.StackPanel();
        root.Child = panel;

        var childrenPanel = new System.Windows.Controls.StackPanel { Visibility = expanded ? Visibility.Visible : Visibility.Collapsed, Opacity = expanded ? 1 : 0 };
        panel.Children.Add(CreateSubjectHeader(
            parent.Name,
            FormatDuration(TimeSpan.FromTicks(parent.TotalTicks)),
            $"{parent.SessionCount} 次",
            14.5,
            FontWeights.SemiBold,
            colors.Parent,
            true,
            expanded,
            () => ToggleParent(parentKey, childrenPanel, root)));
        panel.Children.Add(CreateProgress(majorTicks <= 0 ? 0 : parent.TotalTicks / (double)majorTicks, new Thickness(0, 8, 0, 0), 8, colors.Parent));
        foreach (var child in parent.Children.OrderByDescending(x => x.TotalTicks))
        {
            childrenPanel.Children.Add(CreateSubjectChildCard(child, parent.TotalTicks, colors));
        }
        panel.Children.Add(childrenPanel);

        return root;
    }

    private System.Windows.Controls.Border CreateSubjectChildCard(SubjectNode child, long parentTicks, SubjectLevelBrushes colors)
    {
        var root = new System.Windows.Controls.Border { Style = (Style)FindResource("SubjectChildCardStyle"), Margin = new Thickness(28, 8, 0, 0) };
        var panel = new System.Windows.Controls.StackPanel();
        root.Child = panel;
        panel.Children.Add(CreateSubjectHeader(child.Name, FormatDuration(TimeSpan.FromTicks(child.TotalTicks)), $"{child.SessionCount} 次", 13, FontWeights.SemiBold, colors.Child, false, false, null));
        panel.Children.Add(CreateProgress(parentTicks <= 0 ? 0 : child.TotalTicks / (double)parentTicks, new Thickness(0, 7, 0, 0), 6, colors.Child));
        return root;
    }

    private System.Windows.Controls.Grid CreateSubjectHeader(string name, string duration, string count, double fontSize, FontWeight fontWeight, Brush markerBrush, bool collapsible, bool expanded, Action? toggle)
    {
        var header = new System.Windows.Controls.Grid { Cursor = collapsible ? System.Windows.Input.Cursors.Hand : null };
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

        var arrow = new System.Windows.Controls.TextBlock
        {
            Text = collapsible ? (expanded ? "▾" : "›") : "",
            Width = 20,
            FontSize = 14,
            FontWeight = FontWeights.Black,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = markerBrush,
            Margin = new Thickness(0, 0, 4, 0)
        };
        header.Children.Add(arrow);

        var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = markerBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        System.Windows.Controls.Grid.SetColumn(dot, 1);
        header.Children.Add(dot);

        var title = new System.Windows.Controls.TextBlock { Text = name, FontSize = fontSize, FontWeight = fontWeight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.Grid.SetColumn(title, 2);
        header.Children.Add(title);

        var durationBadge = CreateBadge(duration);
        System.Windows.Controls.Grid.SetColumn(durationBadge, 3);
        header.Children.Add(durationBadge);

        var countBadge = CreateBadge(count);
        countBadge.Margin = new Thickness(8, 0, 0, 0);
        System.Windows.Controls.Grid.SetColumn(countBadge, 4);
        header.Children.Add(countBadge);

        if (collapsible && toggle is not null)
        {
            header.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                toggle();
                arrow.Text = arrow.Text == "▾" ? "›" : "▾";
            };
        }

        return header;
    }

    private void ToggleMajor(string key, FrameworkElement childrenPanel, FrameworkElement layoutContainer)
    {
        if (_collapsedMajorKeys.Contains(key))
        {
            _collapsedMajorKeys.Remove(key);
            AnimateChildrenVisibility(childrenPanel, show: true, layoutContainer);
        }
        else
        {
            _collapsedMajorKeys.Add(key);
            AnimateChildrenVisibility(childrenPanel, show: false, layoutContainer);
        }
    }

    private void ToggleParent(string key, FrameworkElement childrenPanel, FrameworkElement layoutContainer)
    {
        if (_collapsedParentKeys.Contains(key))
        {
            _collapsedParentKeys.Remove(key);
            AnimateChildrenVisibility(childrenPanel, show: true, layoutContainer);
        }
        else
        {
            _collapsedParentKeys.Add(key);
            AnimateChildrenVisibility(childrenPanel, show: false, layoutContainer);
        }
    }

    private static void AnimateChildrenVisibility(FrameworkElement element, bool show, FrameworkElement? layoutContainer = null)
    {
        var easing = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        element.ClearValue(FrameworkElement.MaxHeightProperty);

        var scale = element.LayoutTransform as ScaleTransform;
        if (scale is null)
        {
            scale = new ScaleTransform(1, 1);
            element.LayoutTransform = scale;
        }
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (show)
        {
            element.Visibility = Visibility.Visible;
            element.Opacity = 0;
            scale.ScaleY = 0.001;
            RequestSubjectTreeLayoutRefresh(element, layoutContainer);

            var scaleAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.001,
                To = 1,
                Duration = SubjectExpandAnimationDuration,
                EasingFunction = easing
            };
            var opacityAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = SubjectExpandAnimationDuration,
                EasingFunction = easing
            };
            opacityAnimation.Completed += (_, _) =>
            {
                scale.ScaleY = 1;
                element.Opacity = 1;
                RequestSubjectTreeLayoutRefresh(element, layoutContainer);
            };
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            element.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        }
        else
        {
            var scaleAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = scale.ScaleY <= 0 ? 1 : scale.ScaleY,
                To = 0.001,
                Duration = SubjectExpandAnimationDuration,
                EasingFunction = easing
            };
            var opacityAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = element.Opacity,
                To = 0,
                Duration = SubjectExpandAnimationDuration,
                EasingFunction = easing
            };
            opacityAnimation.Completed += (_, _) =>
            {
                element.Visibility = Visibility.Collapsed;
                scale.ScaleY = 1;
                element.Opacity = 0;
                RequestSubjectTreeLayoutRefresh(element, layoutContainer);
            };
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            element.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        }
    }

    private static void RequestSubjectTreeLayoutRefresh(FrameworkElement element, FrameworkElement? layoutContainer)
    {
        static void Refresh(FrameworkElement target)
        {
            target.InvalidateMeasure();
            target.InvalidateArrange();
            target.UpdateLayout();
        }

        element.Dispatcher.BeginInvoke(new Action(() =>
        {
            var refreshed = new HashSet<FrameworkElement>();
            void RefreshOnce(FrameworkElement target)
            {
                if (refreshed.Add(target))
                {
                    Refresh(target);
                }
            }

            RefreshOnce(element);
            if (layoutContainer is not null)
            {
                RefreshOnce(layoutContainer);
            }

            var current = (System.Windows.DependencyObject?)layoutContainer ?? element;
            while (current is not null)
            {
                if (current is FrameworkElement frameworkElement)
                {
                    RefreshOnce(frameworkElement);
                }

                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private sealed record SubjectLevelBrushes(Brush Major, Brush Parent, Brush Child);

    private static SubjectLevelBrushes GetSubjectLevelBrushes()
    {
        var accentBrush = System.Windows.Application.Current.FindResource("AccentRedBrush") as SolidColorBrush;
        var panelBrush = System.Windows.Application.Current.FindResource("PanelBrushAlt") as SolidColorBrush;
        var accent = accentBrush?.Color ?? Colors.Red;
        var baseColor = panelBrush?.Color ?? Colors.Black;
        return new SubjectLevelBrushes(
            new SolidColorBrush(accent),
            new SolidColorBrush(BlendColor(accent, baseColor, 0.34)),
            new SolidColorBrush(BlendColor(accent, baseColor, 0.58)));
    }

    private static System.Windows.Media.Color BlendColor(System.Windows.Media.Color foreground, System.Windows.Media.Color background, double backgroundAmount)
    {
        backgroundAmount = Math.Clamp(backgroundAmount, 0d, 1d);
        var foregroundAmount = 1d - backgroundAmount;
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(foreground.R * foregroundAmount + background.R * backgroundAmount),
            (byte)Math.Round(foreground.G * foregroundAmount + background.G * backgroundAmount),
            (byte)Math.Round(foreground.B * foregroundAmount + background.B * backgroundAmount));
    }

    private static System.Windows.Controls.Border CreateBadge(string text)
    {
        return new System.Windows.Controls.Border
        {
            Background = (Brush)System.Windows.Application.Current.FindResource("InputBackgroundBrush"),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 4, 10, 4),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = (Brush)System.Windows.Application.Current.FindResource("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private System.Windows.Controls.ProgressBar CreateProgress(double ratio, Thickness margin, double height, Brush foreground)
    {
        return new System.Windows.Controls.ProgressBar
        {
            Minimum = 0d,
            Maximum = 1d,
            Value = Math.Clamp(ratio, 0d, 1d),
            Height = height,
            Margin = margin,
            Foreground = foreground,
            Style = (Style)System.Windows.Application.Current.FindResource("SummaryBarStyle")
        };
    }

    private static IEnumerable<SubjectNode> FlattenSubjectNodes(IEnumerable<SubjectNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in FlattenSubjectNodes(node.Children))
            {
                yield return child;
            }
        }
    }

    private async Task<List<SubjectNode>> GetSubjectTreeAsync()
    {
        var summaries = await _context.TrackerService.QuerySubjectSummariesAsync(_context.SelectedDate);
        var pathMap = BuildSubjectPathMap(_context.TrackerService.GetSubjectDefinitions());
        var items = summaries
            .Where(summary => !string.IsNullOrWhiteSpace(summary.Subject))
            .Select(summary =>
            {
                var found = pathMap.TryGetValue(summary.Subject, out var mapped);
                return (Subject: summary.Subject, Path: mapped, Found: found, summary.TotalTicks, summary.SessionCount);
            })
            .Where(x => x.Found)
            .ToList();

        var majorNodes = new List<SubjectNode>();
        foreach (var majorGroup in items.GroupBy(x => x.Path.Major).OrderByDescending(x => x.Sum(i => i.TotalTicks)))
        {
            var parentNodes = majorGroup
                .Where(x => x.Path.Parent is not null)
                .GroupBy(x => x.Path.Parent!)
                .OrderByDescending(x => x.Sum(i => i.TotalTicks))
                .Select(parentGroup =>
                {
                    var childNodes = parentGroup
                        .Where(x => x.Path.Child is not null)
                        .GroupBy(x => x.Path.Child!)
                        .OrderByDescending(x => x.Sum(i => i.TotalTicks))
                        .Select(childGroup => new SubjectNode(
                            childGroup.Key,
                            childGroup.Sum(x => x.TotalTicks),
                            childGroup.Sum(x => x.SessionCount),
                            []))
                        .ToList();

                    return new SubjectNode(
                        parentGroup.Key,
                        parentGroup.Sum(x => x.TotalTicks),
                        parentGroup.Sum(x => x.SessionCount),
                        childNodes);
                })
                .ToList();

            majorNodes.Add(new SubjectNode(
                majorGroup.Key,
                majorGroup.Sum(x => x.TotalTicks),
                majorGroup.Sum(x => x.SessionCount),
                parentNodes));
        }

        return majorNodes;
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

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            return "<1分钟";
        }

        var totalHours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        return totalHours > 0 ? $"{totalHours}h{minutes}m" : $"{(int)duration.TotalMinutes}分钟";
    }
}

public enum StatsKind
{
    Process,
    Subject
}
