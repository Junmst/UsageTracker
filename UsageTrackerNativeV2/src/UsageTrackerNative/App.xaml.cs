using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
    private ShellWindow? _shellWindow;
    private NativeControlPipeServer? _controlPipeServer;
    private Mutex? _singleInstanceMutex;
    private static readonly string StartupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        UsageTrackerService.DataDirectoryName,
        "startup.log");

    internal static Stopwatch StartupStopwatch = Stopwatch.StartNew();

    // startup.log 只保留最近内容，避免长期运行后无限膨胀（历史上曾涨到 170MB+）
    private const long StartupLogMaxBytes = 2L * 1024 * 1024;
    private const int MaxLogMessageLength = 8000;
    private static readonly object StartupLogLock = new();
    private static readonly HashSet<string> ReportedRuntimeErrors = new(StringComparer.Ordinal);
    private static DateTime _lastLogSizeCheckAt = DateTime.MinValue;

    protected override async void OnStartup(StartupEventArgs e)
    {
        LogStartupMessage("App.OnStartup", $"Entry: {StartupStopwatch.ElapsedMilliseconds}ms, ProcessStartTime: {Process.GetCurrentProcess().StartTime:O}");
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        try
        {
            base.OnStartup(e);
            _singleInstanceMutex = new Mutex(true, "Shiji.Native.SingleInstance", out var isFirstInstance);
            if (!isFirstInstance)
            {
                if (!e.Args.Any(arg => string.Equals(arg, UsageTrackerService.StartupCompactArgument, StringComparison.OrdinalIgnoreCase)))
                {
                    await NativeControlPipeClient.TrySendAsync("show");
                }
                Shutdown(0);
                return;
            }
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
            MainWindow = mainWindow;
            _controlPipeServer = new NativeControlPipeServer(Dispatcher, mainWindow);
            LogStartupMessage("App.OnStartup", $"Native control pipe ready: {StartupStopwatch.ElapsedMilliseconds}ms");

            if (e.Args.Any(arg => string.Equals(arg, UsageTrackerService.StartupCompactArgument, StringComparison.OrdinalIgnoreCase)))
            {
                mainWindow.HideFromLauncher();
                return;
            }

            mainWindow.Show();
            mainWindow.Activate();
            LogStartupMessage("App.OnStartup", $"Visible standalone startup complete: {StartupStopwatch.ElapsedMilliseconds}ms");
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
        e.Handled = true;
        LogStartupException("DispatcherUnhandledException", e.Exception);
        TryShowRuntimeErrorDialog(e.Exception);
    }

    /// <summary>
    /// 同一类错误每个进程只弹一次（最多 5 类），避免后台轮询反复失败时弹窗刷屏，
    /// 也避免弹窗本身再次抛异常导致连锁崩溃。
    /// </summary>
    private static void TryShowRuntimeErrorDialog(Exception? exception)
    {
        var signature = exception is null
            ? "unknown"
            : (exception.GetType().FullName ?? exception.GetType().Name) + "|" + SafeMessage(exception);

        lock (ReportedRuntimeErrors)
        {
            if (ReportedRuntimeErrors.Count >= 5 || !ReportedRuntimeErrors.Add(signature))
            {
                return;
            }
        }

        try
        {
            Forms.MessageBox.Show(
                string.Format(LocalizationService.Instance.Get("App.RuntimeError"), SafeMessage(exception), StartupLogPath),
                LocalizationService.Instance.Get("App.Name"),
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
        catch
        {
        }
    }

    private static string SafeMessage(Exception? exception)
    {
        try
        {
            return exception?.Message ?? string.Empty;
        }
        catch
        {
            return "(异常信息读取失败)";
        }
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
        var body = TruncateLogText(message);
        var logMessage = $"[{DateTime.Now:O}] {source}\r\n{body}\r\n\r\n";

        try
        {
            lock (StartupLogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
                RotateStartupLogIfNeeded();
                File.AppendAllText(StartupLogPath, logMessage);
            }
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }
    }

    /// <summary>
    /// 日志超过上限时滚动为 startup.log.1（只保留一份历史），
    /// 避免长期运行后日志无限增长拖垮磁盘与安全软件扫描。
    /// </summary>
    private static void RotateStartupLogIfNeeded()
    {
        var now = DateTime.Now;
        if (now - _lastLogSizeCheckAt < TimeSpan.FromSeconds(20))
        {
            return;
        }

        _lastLogSizeCheckAt = now;
        try
        {
            var info = new FileInfo(StartupLogPath);
            if (!info.Exists || info.Length < StartupLogMaxBytes)
            {
                return;
            }

            File.Move(StartupLogPath, StartupLogPath + ".1", overwrite: true);
        }
        catch
        {
            // 文件被占用等极端情况：直接清空，宁可少一份历史也不要无限增长
            try
            {
                File.WriteAllText(StartupLogPath, string.Empty);
            }
            catch
            {
            }
        }
    }

    internal static void LogStartupException(string source, Exception exception)
    {
        LogStartupMessage(source, DescribeException(exception));
    }

    /// <summary>
    /// 安全地把异常转成字符串：内存极度紧张时 <c>Exception.ToString()</c> 会因反射解析堆栈
    /// 再抛 OutOfMemoryException，这里逐段兜底，保证任何时候都能记下关键信息。
    /// </summary>
    private static string DescribeException(Exception? exception)
    {
        if (exception is null)
        {
            return "(no exception)";
        }

        var builder = new StringBuilder(512);
        var current = exception;
        for (var depth = 0; current is not null && depth < 4; depth++)
        {
            if (depth > 0)
            {
                builder.Append("--- InnerException ---\r\n");
            }

            AppendSafely(builder, () => current.GetType().FullName ?? current.GetType().Name);
            builder.Append(": ");
            AppendSafely(builder, () => current.Message);
            builder.Append("\r\n");
            AppendSafely(builder, () => current.StackTrace);
            current = current.InnerException;
        }

        return builder.ToString();
    }

    private static void AppendSafely(StringBuilder builder, Func<string?> valueFactory)
    {
        try
        {
            var value = valueFactory();
            if (!string.IsNullOrEmpty(value))
            {
                builder.Append(value);
            }
        }
        catch
        {
            builder.Append("(异常信息读取失败)");
        }
    }

    private static string TruncateLogText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= MaxLogMessageLength
            ? value
            : value[..MaxLogMessageLength] + "\r\n...(日志过长已截断)";
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
        _controlPipeServer?.Dispose();
        _controlPipeServer = null;
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

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


