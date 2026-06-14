using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using UsageTrackerNative.Shell;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;


namespace UsageTrackerNative;

public partial class App : Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private ITrayHost? _trayHost;
    private Window? _trayMenuWindow;
    private Border? _trayMenuFlyout;
    private TranslateTransform? _trayMenuTransform;
    private DispatcherTimer? _trayMenuCloseTimer;
    private bool _isTrayMenuClosing;
    private ShellWindow? _shellWindow;
    private static readonly Duration TrayMenuAnimationDuration = new(TimeSpan.FromMilliseconds(240));
    private const double TrayMenuOffsetY = -4d;
    private static readonly string StartupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        UsageTrackerService.DataDirectoryName,
        "startup.log");

    internal static Stopwatch StartupStopwatch = Stopwatch.StartNew();

    protected override void OnStartup(StartupEventArgs e)
    {
        LogStartupMessage("App.OnStartup", $"Entry: {StartupStopwatch.ElapsedMilliseconds}ms, ProcessStartTime: {Process.GetCurrentProcess().StartTime:O}");
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        try
        {
            base.OnStartup(e);
            GlobalSmoothScroll.Enable();
            LogStartupMessage("App.OnStartup", $"After base.OnStartup: {StartupStopwatch.ElapsedMilliseconds}ms");

            LogStartupMessage("App.OnStartup", $"Before LoadPersistedThemeSnapshot: {StartupStopwatch.ElapsedMilliseconds}ms");
            var persistedTheme = UsageTrackerService.LoadPersistedThemeSnapshot();
            LogStartupMessage("App.OnStartup", $"After LoadPersistedThemeSnapshot: {StartupStopwatch.ElapsedMilliseconds}ms");
            var isLight = string.Equals(persistedTheme.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                || string.Equals(persistedTheme.Theme, "System", StringComparison.OrdinalIgnoreCase) && global::UsageTrackerNative.MainWindow.IsSystemLightTheme();
            global::UsageTrackerNative.MainWindow.ApplyThemePaletteToApplication(isLight, persistedTheme.ThemeAccentColor ?? "#C62828");
            LogStartupMessage("App.OnStartup", $"After ApplyThemePaletteToApplication: {StartupStopwatch.ElapsedMilliseconds}ms");

            var mainWindow = new ShellWindow();
            _shellWindow = mainWindow;
            LogStartupMessage("App.OnStartup", $"After new ShellWindow(): {StartupStopwatch.ElapsedMilliseconds}ms");

            // 初始化语言（从 settings.json 读取偏好，切换 MergedDictionaries）
            var persistedLanguage = UsageTrackerService.LoadPersistedLanguage();
            LocalizationService.Instance.SetLanguage(persistedLanguage);
            LogStartupMessage("App.OnStartup", $"Language initialized: {persistedLanguage}");

            _trayHost = mainWindow;
            MainWindow = mainWindow;
            LogStartupMessage("App.OnStartup", $"Before ConfigureTray: {StartupStopwatch.ElapsedMilliseconds}ms");
            ConfigureTray();
            LogStartupMessage("App.OnStartup", $"After ConfigureTray: {StartupStopwatch.ElapsedMilliseconds}ms");

            if (e.Args.Any(arg => string.Equals(arg, UsageTrackerService.StartupCompactArgument, StringComparison.OrdinalIgnoreCase)))
            {
                mainWindow.ShowCompactSessionWindow();
                return;
            }

            mainWindow.RestoreFromTray();
            LogStartupMessage("App.OnStartup", $"After RestoreFromTray (OnStartup complete): {StartupStopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            LogStartupException("OnStartup", ex);
            Forms.MessageBox.Show(string.Format(LocalizationService.Instance.Get("App.StartupFailed"), ex.Message, StartupLogPath), LocalizationService.Instance.Get("App.Name"), Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            Shutdown(1);
        }
    }

    private static void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogStartupException("DispatcherUnhandledException", e.Exception);
        Forms.MessageBox.Show(string.Format(LocalizationService.Instance.Get("App.RuntimeError"), e.Exception.Message, StartupLogPath), LocalizationService.Instance.Get("App.Name"), Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
        e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogStartupException("UnhandledException", ex);
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogStartupException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    internal static void LogStartupMessage(string source, string message)
    {
        var logMessage = $"[{DateTime.Now:O}] {source}\r\n{message}\r\n\r\n";
        
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            File.AppendAllText(StartupLogPath, logMessage);
        }
        catch
        {
        }
    }

    internal static void LogStartupException(string source, Exception exception)
    {
        LogStartupMessage(source, exception.ToString());
    }


    private void ConfigureTray()
    {
        System.Drawing.Icon trayIcon;
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app-icon.ico"));
            if (streamInfo is not null)
            {
                using (streamInfo.Stream)
                    trayIcon = new System.Drawing.Icon(streamInfo.Stream);
            }
            else
            {
                trayIcon = SystemIcons.Application;
            }
        }
        catch
        {
            trayIcon = SystemIcons.Application;
        }

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "时迹",
            Icon = trayIcon,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                TryTrayAction(() => _trayHost?.RestoreFromTray());
            }
        };
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                Dispatcher.BeginInvoke(ShowTrayMenu, DispatcherPriority.Input);
            }
        };
    }

    private void ShowTrayMenu()
    {
        try
        {
            LogStartupMessage("TrayMenu", $"Show requested at {Forms.Cursor.Position}");
            ShowTrayMenuCore();
            LogStartupMessage("TrayMenu", "Show completed");
        }
        catch (Exception ex)
        {
            LogStartupException("TrayMenu.ShowTrayMenu", ex);
        }
    }

    private void ShowTrayMenuCore()
    {
        CloseTrayMenu(immediate: true);

        _trayMenuCloseTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _trayMenuCloseTimer.Tick -= TrayMenuCloseTimer_Tick;
        _trayMenuCloseTimer.Tick += TrayMenuCloseTimer_Tick;

        var content = new StackPanel();
        content.Children.Add(CreateTrayMenuItem("打开主界面", () => TryTrayAction(() => _trayHost?.RestoreFromTray())));
        content.Children.Add(CreateTrayMenuItem("小窗模式", () => TryTrayAction(() => _trayHost?.ShowCompactSessionWindow())));
        content.Children.Add(CreateTrayMenuSeparator());
        content.Children.Add(CreateTrayMenuItem("退出", () => TryTrayAction(() =>
        {
            _trayHost?.ExitFromTray();
            Shutdown();
        })));

        _trayMenuTransform = new TranslateTransform { Y = TrayMenuOffsetY };
        _trayMenuFlyout = new Border
        {
            Width = 120,
            Padding = new Thickness(0, 4, 0, 4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Opacity = 0,
            RenderTransform = _trayMenuTransform,
            Child = content
        };
        _trayMenuFlyout.SetResourceReference(Border.BackgroundProperty, "ButtonBackgroundBrush");
        _trayMenuFlyout.SetResourceReference(Border.BorderBrushProperty, "ButtonBorderBrush");
        _trayMenuFlyout.MouseEnter += TrayMenuArea_MouseEnter;
        _trayMenuFlyout.MouseLeave += TrayMenuArea_MouseLeave;

        var position = GetTrayMenuDipPosition();
        _trayMenuWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            Content = _trayMenuFlyout,
            Left = position.X - 120,
            Top = position.Y - 18,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false
        };
        _trayMenuWindow.Show();
        EnsureTrayMenuWithinScreen();
        AnimateTrayMenu(show: true);
    }

    private Border CreateTrayMenuItem(string text, Action action)
    {
        var item = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(4, 2, 4, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        ((TextBlock)item.Child).SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        item.MouseEnter += (_, _) =>
        {
            _trayMenuCloseTimer?.Stop();
            item.SetResourceReference(Border.BackgroundProperty, "DataGridRowHoverBrush");
        };
        item.MouseLeave += (_, _) =>
        {
            item.Background = System.Windows.Media.Brushes.Transparent;
            StartTrayMenuCloseCheck();
        };
        item.MouseLeftButtonDown += (_, _) => item.Opacity = 0.82;
        item.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            item.Opacity = 1;
            CloseTrayMenu(immediate: true);
            action();
        };
        return item;
    }

    private static Separator CreateTrayMenuSeparator()
    {
        var separator = new Separator { Margin = new Thickness(6, 3, 6, 3) };
        separator.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "ButtonBorderBrush");
        return separator;
    }

    private System.Windows.Point GetTrayMenuDipPosition()
    {
        var cursor = Forms.Cursor.Position;
        var source = MainWindow is not null ? PresentationSource.FromVisual(MainWindow) : null;
        if (source?.CompositionTarget is not null)
        {
            return source.CompositionTarget.TransformFromDevice.Transform(new System.Windows.Point(cursor.X, cursor.Y));
        }

        return new System.Windows.Point(cursor.X, cursor.Y);
    }

    private Rect GetCurrentScreenDipWorkingArea()
    {
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var source = MainWindow is not null ? PresentationSource.FromVisual(MainWindow) : null;
        if (source?.CompositionTarget is not null)
        {
            var topLeft = source.CompositionTarget.TransformFromDevice.Transform(new System.Windows.Point(screen.Left, screen.Top));
            var bottomRight = source.CompositionTarget.TransformFromDevice.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        return new Rect(screen.Left, screen.Top, screen.Width, screen.Height);
    }

    private void EnsureTrayMenuWithinScreen()
    {
        if (_trayMenuWindow is null)
        {
            return;
        }

        var screen = GetCurrentScreenDipWorkingArea();
        _trayMenuWindow.UpdateLayout();
        var width = _trayMenuWindow.ActualWidth;
        var height = _trayMenuWindow.ActualHeight;
        if (_trayMenuWindow.Left + width > screen.Right)
        {
            _trayMenuWindow.Left = screen.Right - width - 4;
        }
        if (_trayMenuWindow.Left < screen.Left)
        {
            _trayMenuWindow.Left = screen.Left + 4;
        }
        if (_trayMenuWindow.Top + height > screen.Bottom)
        {
            var cursor = GetTrayMenuDipPosition();
            _trayMenuWindow.Top = cursor.Y - height + 8;
        }
        if (_trayMenuWindow.Top < screen.Top)
        {
            _trayMenuWindow.Top = screen.Top + 4;
        }
    }

    private void TrayMenuArea_MouseEnter(object sender, MouseEventArgs e)
    {
        _trayMenuCloseTimer?.Stop();
    }

    private void TrayMenuArea_MouseLeave(object sender, MouseEventArgs e)
    {
        StartTrayMenuCloseCheck();
    }

    private void StartTrayMenuCloseCheck()
    {
        if (_trayMenuWindow?.IsVisible != true)
        {
            return;
        }
        _trayMenuCloseTimer?.Stop();
        _trayMenuCloseTimer?.Start();
    }

    private void TrayMenuCloseTimer_Tick(object? sender, EventArgs e)
    {
        _trayMenuCloseTimer?.Stop();
        if (_trayMenuWindow?.IsVisible != true)
        {
            return;
        }
        if (IsMouseWithin(_trayMenuFlyout))
        {
            return;
        }
        CloseTrayMenu();
    }

    private void CloseTrayMenu(bool immediate = false)
    {
        _trayMenuCloseTimer?.Stop();
        if (_trayMenuWindow is null)
        {
            return;
        }

        if (immediate || _trayMenuFlyout is null || _trayMenuTransform is null)
        {
            _trayMenuWindow.Close();
            ClearTrayMenuRefs();
            return;
        }

        if (_isTrayMenuClosing)
        {
            return;
        }

        _isTrayMenuClosing = true;
        var window = _trayMenuWindow;
        AnimateTrayMenu(show: false, () =>
        {
            window.Close();
            ClearTrayMenuRefs();
        });
    }

    private void AnimateTrayMenu(bool show, Action? completed = null)
    {
        if (_trayMenuFlyout is null || _trayMenuTransform is null)
        {
            completed?.Invoke();
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TrayMenuAnimationDuration,
            EasingFunction = easing
        };
        var yAnimation = new DoubleAnimation
        {
            To = show ? 0 : TrayMenuOffsetY,
            Duration = TrayMenuAnimationDuration,
            EasingFunction = easing
        };
        if (completed is not null)
        {
            opacityAnimation.Completed += (_, _) => completed();
        }
        _trayMenuFlyout.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        _trayMenuTransform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
    }

    private void ClearTrayMenuRefs()
    {
        if (_trayMenuFlyout is not null)
        {
            _trayMenuFlyout.MouseEnter -= TrayMenuArea_MouseEnter;
            _trayMenuFlyout.MouseLeave -= TrayMenuArea_MouseLeave;
        }
        _trayMenuWindow = null;
        _trayMenuFlyout = null;
        _trayMenuTransform = null;
        _isTrayMenuClosing = false;
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

    private static void TryTrayAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogStartupException("TrayAction", ex);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // 系统关机/注销时，同步截断 ActiveSession 并写入 DB，防止重启后记录异常时长
        LogStartupMessage("App.OnSessionEnding", $"Reason={e.ReasonSessionEnding}");
        DisposeTrackerService();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogStartupMessage("App.OnExit", $"ExitCode={e.ApplicationExitCode}, ShutdownMode={ShutdownMode}, DispatcherShutdownStarted={Dispatcher.HasShutdownStarted}, MainWindow={MainWindow?.GetType().Name ?? "null"}");
        DisposeTrackerService();
        CloseTrayMenu(immediate: true);
        _trayMenuCloseTimer?.Stop();
        if (_trayMenuCloseTimer is not null)
        {
            _trayMenuCloseTimer.Tick -= TrayMenuCloseTimer_Tick;
        }
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.OnExit(e);
    }

    private void DisposeTrackerService()
    {
        try
        {
            _shellWindow?.DisposeTrackerService();
        }
        catch (Exception ex)
        {
            LogStartupException("App.DisposeTrackerService", ex);
        }
    }
}

internal static class GlobalSmoothScroll
{
    private const double WheelSpeed = 0.16d;
    private const double ResponseSeconds = 0.20d;
    private const double ImmediateResponseRatio = 0d;
    private static readonly Dictionary<System.Windows.Controls.ScrollViewer, SmoothScrollState> States = new();
    private static bool _enabled;
    private static bool _renderingHooked;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.ScrollViewer),
            System.Windows.Controls.ScrollViewer.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.DataGrid),
            System.Windows.Controls.DataGrid.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(System.Windows.Controls.ListBox),
            System.Windows.Controls.ListBox.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: true);
    }

    private static void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject originalSource && ShouldBypass(originalSource))
        {
            return;
        }

        var scrollViewer = sender as System.Windows.Controls.ScrollViewer;
        scrollViewer ??= FindAncestor<System.Windows.Controls.ScrollViewer>(e.OriginalSource as DependencyObject);
        if (scrollViewer is null || !scrollViewer.IsLoaded || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        e.Handled = true;
        SmoothScrollVertical(scrollViewer, e.Delta);
    }

    private static bool ShouldBypass(DependencyObject source)
    {
        if (FindAncestor<UsageTrackerNative.TimeDistribution.TimeDistributionControl>(source) is not null)
        {
            return true;
        }

        if (FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null)
        {
            return true;
        }

        if (FindAncestor<System.Windows.Controls.ComboBox>(source) is { IsDropDownOpen: true })
        {
            return true;
        }

        return false;
    }

    private static void SmoothScrollVertical(System.Windows.Controls.ScrollViewer scrollViewer, int wheelDelta)
    {
        if (!States.TryGetValue(scrollViewer, out var state))
        {
            state = new SmoothScrollState(scrollViewer.VerticalOffset);
            States[scrollViewer] = state;
        }
        else if (!state.IsAnimating)
        {
            state.TargetOffset = scrollViewer.VerticalOffset;
            state.LastFrameTime = TimeSpan.Zero;
        }

        var target = Math.Clamp(state.TargetOffset - wheelDelta * WheelSpeed, 0d, scrollViewer.ScrollableHeight);
        state.TargetOffset = Math.Clamp(target, 0d, scrollViewer.ScrollableHeight);

        var immediate = (state.TargetOffset - scrollViewer.VerticalOffset) * ImmediateResponseRatio;
        if (Math.Abs(immediate) > 0.35d)
        {
            scrollViewer.ScrollToVerticalOffset(Math.Clamp(scrollViewer.VerticalOffset + immediate, 0d, scrollViewer.ScrollableHeight));
        }

        if (Math.Abs(state.TargetOffset - scrollViewer.VerticalOffset) < 0.35d)
        {
            scrollViewer.ScrollToVerticalOffset(state.TargetOffset);
            state.IsAnimating = false;
            return;
        }

        state.IsAnimating = true;
        EnsureRenderingHook();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T typed)
            {
                return typed;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static void EnsureRenderingHook()
    {
        if (_renderingHooked)
        {
            return;
        }

        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (e is not System.Windows.Media.RenderingEventArgs renderingEventArgs)
        {
            return;
        }

        var anyAnimating = false;
        foreach (var pair in States.ToList())
        {
            var scrollViewer = pair.Key;
            var state = pair.Value;
            if (!scrollViewer.IsLoaded)
            {
                state.IsAnimating = false;
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

            var stepRatio = 1d - Math.Pow(0.001d, deltaSeconds / ResponseSeconds);
            scrollViewer.ScrollToVerticalOffset(Math.Clamp(current + distance * stepRatio, 0d, scrollViewer.ScrollableHeight));
            anyAnimating = true;
        }

        if (!anyAnimating)
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    private sealed class SmoothScrollState(double targetOffset)
    {
        public double TargetOffset { get; set; } = targetOffset;
        public bool IsAnimating { get; set; }
        public TimeSpan LastFrameTime { get; set; }
    }
}


