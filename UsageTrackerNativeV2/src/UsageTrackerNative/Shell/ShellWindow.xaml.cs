using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using MediaColor = System.Windows.Media.Color;

namespace UsageTrackerNative.Shell;

public partial class ShellWindow : Window, ITrayHost
{
    private readonly IReadOnlyList<AppModuleDefinition> _modules;
    private readonly V2AppContext _context = new();
    private CompactSessionWindow? _compactSessionWindow;
    private DispatcherTimer? _compactSessionTimer;
    private DispatcherTimer? _headerDateTimer;
    private DispatcherTimer? _systemThemeChangedDebounceTimer;
    private DispatcherTimer? _themeModeCloseTimer;
    private KeyGesture? _manualIdleShortcut;
    private IntPtr _windowHandle;
    private bool _manualIdleHotkeyRegistered;
    private bool _isClosed;
    private bool _isExitRequested;
    private const int ManualIdleHotkeyId = 0x534A;
    private const int WmHotkey = 0x0312;
    private const int WmSettingChange = 0x001A;
    private const int WmThemeChanged = 0x031A;
    private const int WmDwmColorizationColorChanged = 0x0320;
    private string _themeMode = "System";
    private string _accentColor = "#C62828";
    private bool _isThemeModePopupClosing;
    private static readonly Duration ThemeModePopupAnimationDuration = new(TimeSpan.FromMilliseconds(240));
    private const double ThemeModePopupOffsetY = -4d;
    private const double ThemeModePopupScaleX = 0.99d;
    private const double ThemeModePopupScaleY = 0.985d;

    public ShellWindow()
    {
        InitializeComponent();
        var theme = UsageTrackerService.LoadPersistedThemeSnapshot();
        _themeMode = theme.Theme ?? "System";
        _accentColor = theme.ThemeAccentColor ?? "#C62828";
        InitializeSystemThemeChangedDebounceTimer();
        InitializeThemeModeCloseTimer();
        ApplyThemeResources(IsLightMode(), _accentColor);
        Loaded += ShellWindow_Loaded;
        PreviewMouseDown += ShellWindow_PreviewMouseDown;
        Closing += ShellWindow_Closing;
        Closed += ShellWindow_Closed;
        _ = _context.InitializeAsync();
        _context.ManualIdleShortcutChanged += Context_ManualIdleShortcutChanged;
        _context.NavigateRequested += Context_NavigateRequested;
        _context.PreviewModeChanged += Context_PreviewModeChanged;
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _context.RequestUndo()), new KeyGesture(Key.Z, ModifierKeys.Control)));
        StartHeaderDateTimer();
        _modules = ModuleRegistry.CreateDefaultModules().OrderBy(x => x.Order).ToList();
        NavigationGroupsControl.ItemsSource = _modules
            .GroupBy(x => x.Group)
            .Select(x => new ModuleGroup(x.Key, x.OrderBy(m => m.Order).ToList()))
            .ToList();
        NavigateTo(_modules.First());
    }

    private void ModuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AppModuleDefinition module })
        {
            NavigateTo(module);
        }
    }

    private void NavigateTo(AppModuleDefinition module, string? targetId = null)
    {
        var view = module.CreateView(_context);
        ModuleContent.Content = view;
        if (!string.IsNullOrWhiteSpace(targetId) && view is INavigationTarget navigationTarget)
        {
            navigationTarget.ApplyNavigationTarget(targetId);
        }
    }

    private void Context_NavigateRequested(object? sender, NavigateRequestedEventArgs e)
    {
        var module = _modules.FirstOrDefault(x => string.Equals(x.Id, e.ModuleId, StringComparison.OrdinalIgnoreCase));
        if (module is not null)
        {
            NavigateTo(module, e.TargetId);
        }
    }

    private void Context_PreviewModeChanged(object? sender, EventArgs e)
    {
        UpdatePreviewModeHeader();
        RefreshCurrentModuleTheme();
    }

    private void UpdatePreviewModeHeader()
    {
        if (PreviewModeHeaderBanner is null || PreviewModeHeaderText is null)
        {
            return;
        }

        PreviewModeHeaderBanner.Visibility = _context.IsPreviewMode ? Visibility.Visible : Visibility.Collapsed;
        PreviewModeHeaderText.Text = _context.PreviewContext is null
            ? string.Empty
            : $"仅查看：{_context.PreviewContext.DisplayName}";
    }

    private void ExitPreviewModeHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        _context.ExitPreviewMode();
    }

    private void ShellWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SelectThemeButtonText(_themeMode);
        UpdateHeaderDateText();
        UpdatePreviewModeHeader();
        ApplyTitleBarTheme(IsLightMode());
        AddWindowMessageHook();
    }

    private void StartHeaderDateTimer()
    {
        UpdateHeaderDateText();
        _headerDateTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _headerDateTimer.Tick -= HeaderDateTimer_Tick;
        _headerDateTimer.Tick += HeaderDateTimer_Tick;
        _headerDateTimer.Start();
    }

    private void HeaderDateTimer_Tick(object? sender, EventArgs e)
    {
        UpdateHeaderDateText();
    }

    private void StopHeaderDateTimer()
    {
        _headerDateTimer?.Stop();
    }

    private void InitializeThemeModeCloseTimer()
    {
        _themeModeCloseTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _themeModeCloseTimer.Tick -= ThemeModeCloseTimer_Tick;
        _themeModeCloseTimer.Tick += ThemeModeCloseTimer_Tick;
    }

    private void StopThemeModeCloseTimer()
    {
        _themeModeCloseTimer?.Stop();
        if (_themeModeCloseTimer is not null)
        {
            _themeModeCloseTimer.Tick -= ThemeModeCloseTimer_Tick;
        }
    }

    private void UpdateHeaderDateText()
    {
        if (HeaderDateText is null)
        {
            return;
        }

        HeaderDateText.Text = DateTime.Now.ToString("yyyy-MM-dd");
    }

    private void ShellWindow_Closing(object? sender, CancelEventArgs e)
    {
        CloseThemeModePopup(immediate: true);
        if (_isExitRequested || System.Windows.Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown && System.Windows.Application.Current.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        e.Cancel = true;
        HideCompactSessionWindow();
        Hide();
        App.LogStartupMessage("ShellWindow.Closing", "Main window close was converted to tray hide.");
    }

    private void ShellWindow_Closed(object? sender, EventArgs e)
    {
        CloseThemeModePopup(immediate: true);
        _isClosed = true;
        PreviewMouseDown -= ShellWindow_PreviewMouseDown;
        _context.ManualIdleShortcutChanged -= Context_ManualIdleShortcutChanged;
        _context.NavigateRequested -= Context_NavigateRequested;
        _context.PreviewModeChanged -= Context_PreviewModeChanged;
        UnregisterManualIdleHotkey();
        if (_windowHandle != IntPtr.Zero)
        {
            HwndSource.FromHwnd(_windowHandle)?.RemoveHook(WndProc);
            _windowHandle = IntPtr.Zero;
        }
        HideCompactSessionWindow();
        StopCompactSessionTimer();
        StopHeaderDateTimer();
        StopThemeModeCloseTimer();
        StopSystemThemeChangedDebounceTimer();
    }

    private void InitializeSystemThemeChangedDebounceTimer()
    {
        _systemThemeChangedDebounceTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _systemThemeChangedDebounceTimer.Tick -= SystemThemeChangedDebounceTimer_Tick;
        _systemThemeChangedDebounceTimer.Tick += SystemThemeChangedDebounceTimer_Tick;
    }

    private void StopSystemThemeChangedDebounceTimer()
    {
        _systemThemeChangedDebounceTimer?.Stop();
        if (_systemThemeChangedDebounceTimer is not null)
        {
            _systemThemeChangedDebounceTimer.Tick -= SystemThemeChangedDebounceTimer_Tick;
        }
    }

    private void SystemThemeChangedDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _systemThemeChangedDebounceTimer?.Stop();
        ApplySystemThemeIfNeeded();
    }

    private void ScheduleSystemThemeRefresh()
    {
        if (!string.Equals(_themeMode, "System", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        InitializeSystemThemeChangedDebounceTimer();
        _systemThemeChangedDebounceTimer?.Stop();
        _systemThemeChangedDebounceTimer?.Start();
    }

    private void ApplySystemThemeIfNeeded()
    {
        if (!string.Equals(_themeMode, "System", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isLight = IsSystemLightTheme();
        ApplyThemeResources(isLight, _accentColor);
        ApplyTitleBarTheme(isLight);
        RefreshCurrentModuleTheme();
        InvalidateVisual();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void Context_ManualIdleShortcutChanged(object? sender, EventArgs e)
    {
        LoadManualIdleShortcut();
        RegisterManualIdleHotkey();
    }

    private void ShellWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ThemeModePopup.IsOpen
            && !IsMouseWithin(ThemeModeButton)
            && !IsMouseWithin(ThemeModeFlyout))
        {
            _themeModeCloseTimer?.Stop();
            CloseThemeModePopupWithAnimation();
        }
    }

    private void ThemeModeMenuArea_MouseEnter(object sender, MouseEventArgs e)
    {
        _themeModeCloseTimer?.Stop();
    }

    private void ThemeModeMenuArea_MouseLeave(object sender, MouseEventArgs e)
    {
        StartThemeModeCloseCheck();
    }

    private void StartThemeModeCloseCheck()
    {
        if (ThemeModePopup?.IsOpen != true)
        {
            return;
        }

        _themeModeCloseTimer?.Stop();
        _themeModeCloseTimer?.Start();
    }

    private void ThemeModeCloseTimer_Tick(object? sender, EventArgs e)
    {
        _themeModeCloseTimer?.Stop();
        if (ThemeModePopup?.IsOpen != true)
        {
            return;
        }

        if (IsMouseWithin(ThemeModeButton) || IsMouseWithin(ThemeModeFlyout))
        {
            return;
        }

        _themeModeCloseTimer?.Stop();
        CloseThemeModePopupWithAnimation();
    }

    private static bool IsMouseWithin(FrameworkElement? element)
    {
        if (element is null || !element.IsVisible)
        {
            return false;
        }

        var point = Mouse.GetPosition(element);
        return point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
    }

    private void ThemeModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ThemeModePopup.IsOpen)
        {
            _themeModeCloseTimer?.Stop();
            CloseThemeModePopupWithAnimation();
            return;
        }

        _isThemeModePopupClosing = false;
        ThemeModeFlyout.Opacity = 0;
        ThemeModeFlyoutTransform.Y = ThemeModePopupOffsetY;
        ThemeModeFlyoutScale.ScaleX = ThemeModePopupScaleX;
        ThemeModeFlyoutScale.ScaleY = ThemeModePopupScaleY;
        ThemeModePopup.IsOpen = true;
        AnimateThemeModePopup(show: true);
    }

    private void ThemeModePopupItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string mode })
        {
            e.Handled = true;
            ApplyThemeMode(mode);
            _themeModeCloseTimer?.Stop();
            CloseThemeModePopupWithAnimation();
        }
    }

    private void CloseThemeModePopup(bool immediate = false)
    {
        if (!ThemeModePopup.IsOpen)
        {
            return;
        }

        if (immediate)
        {
            ThemeModePopup.IsOpen = false;
            ResetThemeModePopupVisual();
            _isThemeModePopupClosing = false;
            return;
        }

        _themeModeCloseTimer?.Stop();
        CloseThemeModePopupWithAnimation();
    }

    private void CloseThemeModePopupWithAnimation()
    {
        if (!ThemeModePopup.IsOpen || _isThemeModePopupClosing)
        {
            return;
        }

        _isThemeModePopupClosing = true;
        AnimateThemeModePopup(show: false, () =>
        {
            ThemeModePopup.IsOpen = false;
            ResetThemeModePopupVisual();
            _isThemeModePopupClosing = false;
        });
    }

    private void AnimateThemeModePopup(bool show, Action? completed = null)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnimation = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = ThemeModePopupAnimationDuration,
            EasingFunction = easing
        };
        var yAnimation = new DoubleAnimation
        {
            To = show ? 0 : ThemeModePopupOffsetY,
            Duration = ThemeModePopupAnimationDuration,
            EasingFunction = easing
        };
        var scaleXAnimation = new DoubleAnimation
        {
            To = show ? 1 : ThemeModePopupScaleX,
            Duration = ThemeModePopupAnimationDuration,
            EasingFunction = easing
        };
        var scaleYAnimation = new DoubleAnimation
        {
            To = show ? 1 : ThemeModePopupScaleY,
            Duration = ThemeModePopupAnimationDuration,
            EasingFunction = easing
        };

        if (completed is not null)
        {
            opacityAnimation.Completed += (_, _) => completed();
        }

        ThemeModeFlyout.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        ThemeModeFlyoutTransform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
        ThemeModeFlyoutScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnimation);
        ThemeModeFlyoutScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation);
    }

    private void ResetThemeModePopupVisual()
    {
        ThemeModeFlyout.BeginAnimation(UIElement.OpacityProperty, null);
        ThemeModeFlyoutTransform.BeginAnimation(TranslateTransform.YProperty, null);
        ThemeModeFlyoutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ThemeModeFlyoutScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ThemeModeFlyout.Opacity = 0;
        ThemeModeFlyoutTransform.Y = ThemeModePopupOffsetY;
        ThemeModeFlyoutScale.ScaleX = ThemeModePopupScaleX;
        ThemeModeFlyoutScale.ScaleY = ThemeModePopupScaleY;
    }

    private void ApplyThemeMode(string mode)
    {
        _themeMode = mode;
        ThemeModeButtonText.Text = mode;
        ApplyThemeResources(IsLightMode(), _accentColor);
        ApplyTitleBarTheme(IsLightMode());
        RefreshCurrentModuleTheme();
        _context.TrackerService.SetTheme(mode);
    }

    public static void SetAccentColor(string accentColor)
    {
        if (System.Windows.Application.Current.MainWindow is ShellWindow shell)
        {
            shell._accentColor = accentColor;
            ApplyThemeResources(shell.IsLightMode(), accentColor);
            shell.ApplyTitleBarTheme(shell.IsLightMode());
            shell.RefreshCurrentModuleTheme();
            shell._context.TrackerService.SetThemeAccentColor(accentColor);
        }
    }

    private void RefreshCurrentModuleTheme()
    {
        if (ModuleContent?.Content is UsageTrackerNative.Modules.TimeDistribution.TimeDistributionPage distributionPage)
        {
            distributionPage.RefreshTheme();
        }
        if (ModuleContent?.Content is System.Windows.UIElement element)
        {
            element.InvalidateVisual();
        }
    }

    private void SelectThemeButtonText(string mode)
    {
        if (ThemeModeButtonText is null)
        {
            return;
        }

        ThemeModeButtonText.Text = mode;
    }

    private bool IsLightMode()
    {
        if (string.Equals(_themeMode, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(_themeMode, "System", StringComparison.OrdinalIgnoreCase))
        {
            return IsSystemLightTheme();
        }

        return false;
    }

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value != 0;
            }
        }
        catch
        {
            // Ignore registry access failures and prefer a readable light theme fallback.
        }

        return true;
    }

    private static void ApplyThemeResources(bool isLight, string accentColor)
    {
        var accent = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(accentColor);
        SetBrush("WindowBackgroundBrush", isLight ? MediaColor.FromRgb(0xF7, 0xF7, 0xF8) : MediaColor.FromRgb(0x11, 0x12, 0x17));
        SetBrush("PanelBrush", isLight ? Colors.White : MediaColor.FromRgb(0x1A, 0x1C, 0x24));
        SetBrush("PanelBrushAlt", isLight ? MediaColor.FromRgb(0xFB, 0xFB, 0xFC) : MediaColor.FromRgb(0x17, 0x19, 0x21));
        SetBrush("BorderBrush", isLight ? MediaColor.FromRgb(0xE5, 0xE7, 0xEB) : MediaColor.FromRgb(0x2A, 0x2E, 0x39));
        SetBrush("PrimaryTextBrush", isLight ? MediaColor.FromRgb(0x1F, 0x23, 0x2A) : MediaColor.FromRgb(0xF7, 0xF9, 0xFF));
        SetBrush("SecondaryTextBrush", isLight ? MediaColor.FromRgb(0x6B, 0x72, 0x80) : MediaColor.FromRgb(0xC2, 0xC8, 0xD8));
        SetBrush("HeaderBackgroundBrush", isLight ? Colors.White : MediaColor.FromRgb(0x15, 0x17, 0x1E));
        SetBrush("HeaderBorderBrush", isLight ? MediaColor.FromRgb(0xEA, 0xEA, 0xEE) : MediaColor.FromRgb(0x20, 0x23, 0x2D));
        SetBrush("InputBackgroundBrush", isLight ? MediaColor.FromRgb(0xF4, 0xF6, 0xF8) : MediaColor.FromRgb(0x17, 0x1A, 0x22));
        SetBrush("InputBorderBrush", isLight ? MediaColor.FromRgb(0xDD, 0xE0, 0xE6) : MediaColor.FromRgb(0x2B, 0x31, 0x40));
        SetBrush("InputHoverBorderBrush", Blend(accent, isLight ? Colors.White : Colors.Black, isLight ? 0.35 : 0.20));
        SetBrush("InputFocusBorderBrush", accent);
        SetBrush("ButtonBackgroundBrush", isLight ? Colors.White : MediaColor.FromRgb(0x20, 0x24, 0x2E));
        SetBrush("ButtonBorderBrush", isLight ? MediaColor.FromRgb(0xDD, 0xE0, 0xE6) : MediaColor.FromRgb(0x2B, 0x31, 0x40));
        SetBrush("MenuBorderBrush", isLight ? MediaColor.FromRgb(0xE5, 0xE7, 0xEB) : MediaColor.FromRgb(0x31, 0x37, 0x48));
        SetBrush("MenuHoverBrush", isLight ? MediaColor.FromRgb(0xE5, 0xE5, 0xE5) : MediaColor.FromRgb(0x3E, 0x3E, 0x42));
        SetBrush("ButtonHoverBackgroundBrush", isLight ? MediaColor.FromRgb(0xE5, 0xE5, 0xE5) : MediaColor.FromRgb(0x3E, 0x3E, 0x42));
        SetBrush("MenuSelectedBrush", accent);
        SetBrush("AccentBlueBrush", accent);
        SetBrush("AccentGreenBrush", accent);
        SetBrush("AccentRedBrush", accent);
        SetBrush("AccentTextBrush", Colors.White);
        SetBrush("MetricValueBrush", isLight ? MediaColor.FromRgb(0x1F, 0x23, 0x2A) : MediaColor.FromRgb(0xF7, 0xF9, 0xFF));
        var sessionHover = isLight ? MediaColor.FromRgb(0xF5, 0xF5, 0xF5) : MediaColor.FromRgb(0x2D, 0x2D, 0x30);
        var sessionSelected = MediaColor.FromArgb(26, accent.R, accent.G, accent.B);
        SetBrush("DataGridRowHoverBrush", sessionHover);
        SetBrush("DataGridRowSelectedBrush", sessionSelected);
        SetBrush("DataGridRowSelectedInactiveBrush", sessionSelected);
        SetBrush("SessionRowHoverBrush", sessionHover);
        SetBrush("SessionRowSelectedBackgroundBrush", sessionSelected);
        SetBrush("SessionRowSelectedInactiveBackgroundBrush", sessionSelected);
        SetBrush("SessionRowSelectedIndicatorBrush", accent);
        SetBrush("SessionRowDividerBrush", isLight ? MediaColor.FromRgb(0xE5, 0xE5, 0xE5) : MediaColor.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        SetBrush("SessionSubjectTagBackgroundBrush", isLight ? MediaColor.FromRgb(0xF2, 0xF3, 0xF5) : MediaColor.FromRgb(0x3D, 0x3D, 0x40));
        SetBrush("SessionSubjectTagTextBrush", isLight ? MediaColor.FromRgb(0x86, 0x90, 0x9C) : MediaColor.FromRgb(0xCC, 0xCC, 0xCC));
        SetBrush("SelectedRowTextBrush", isLight ? MediaColor.FromRgb(0x1F, 0x23, 0x2A) : Colors.White);
    }

    private void ApplyTitleBarTheme(bool isLight)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var useDarkMode = isLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int));
        var captionColor = isLight ? ColorToBgr(0xFF, 0xFF, 0xFF) : ColorToBgr(0x15, 0x17, 0x1E);
        DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
        var textColor = isLight ? ColorToBgr(0x17, 0x20, 0x33) : ColorToBgr(0xF4, 0xF7, 0xFB);
        DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));
    }

    private static MediaColor Blend(MediaColor foreground, MediaColor background, double backgroundAmount)
    {
        backgroundAmount = Math.Clamp(backgroundAmount, 0d, 1d);
        var foregroundAmount = 1d - backgroundAmount;
        return MediaColor.FromRgb(
            (byte)Math.Round(foreground.R * foregroundAmount + background.R * backgroundAmount),
            (byte)Math.Round(foreground.G * foregroundAmount + background.G * backgroundAmount),
            (byte)Math.Round(foreground.B * foregroundAmount + background.B * backgroundAmount));
    }

    private static int ColorToBgr(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    private static void SetBrush(string key, MediaColor color)
    {
        System.Windows.Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static void SetBrush(string key, SolidColorBrush brush)
    {
        System.Windows.Application.Current.Resources[key] = brush;
    }

    public void RestoreFromTray()
    {
        if (_isClosed)
        {
            App.LogStartupMessage("ShellWindow.RestoreFromTray", "Ignored restore request because ShellWindow is already closed.");
            return;
        }

        try
        {
            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }
        catch (InvalidOperationException ex)
        {
            App.LogStartupException("ShellWindow.RestoreFromTray", ex);
        }
    }

    public void ExitFromTray()
    {
        _isExitRequested = true;
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// 在程序退出（正常关闭或系统关机）时，同步截断 ActiveSession 并写入 DB。
    /// 幂等：TrackerService.Dispose() 内部有 _isDisposed 守卫，安全多次调用。
    /// </summary>
    public void DisposeTrackerService()
    {
        App.LogStartupMessage("ShellWindow.DisposeTrackerService", "begin");
        _context.TrackerService.Dispose();
        App.LogStartupMessage("ShellWindow.DisposeTrackerService", "done");
    }

    public void ShowCompactSessionWindow()
    {
        if (_isClosed)
        {
            App.LogStartupMessage("ShellWindow.ShowCompactSessionWindow", "Ignored compact mode request because ShellWindow is already closed.");
            return;
        }

        if (_compactSessionWindow is null)
        {
            _compactSessionWindow = new CompactSessionWindow();
            _compactSessionWindow.BackToMainRequested += (_, _) => RestoreFromCompactWindow();
            _compactSessionWindow.ManualIdleRequested += (_, _) =>
            {
                _context.TrackerService.EnterManualIdle();
                UpdateCompactSessionWindow();
            };
            _compactSessionWindow.Closed += (_, _) =>
            {
                StopCompactSessionTimer();
                _compactSessionWindow = null;
            };
        }

        StartCompactSessionTimer();
        UpdateCompactSessionWindow();
        _compactSessionWindow.PositionCompactWindow();
        _compactSessionWindow.Show();
        _compactSessionWindow.Activate();
        Hide();
    }

    public void HideCompactSessionWindow()
    {
        if (_compactSessionWindow is not null)
        {
            _compactSessionWindow.Hide();
        }

        StopCompactSessionTimer();
    }

    private void CompactModeButton_Click(object sender, RoutedEventArgs e)
    {
        ShowCompactSessionWindow();
    }

    private void EnterManualIdleFromShortcut()
    {
        var closed = _context.TrackerService.EnterManualIdle();
        App.LogStartupMessage("ManualIdleHotkey.Trigger", closed is null
            ? "Entered manual idle; no active session was closed."
            : $"Entered manual idle; closed {closed.ProcessName} from {closed.StartTime:HH:mm:ss}.");
        UpdateCompactSessionWindow();
    }

    private void AddWindowMessageHook()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || hwnd == _windowHandle)
        {
            return;
        }

        _windowHandle = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        LoadManualIdleShortcut();
        RegisterManualIdleHotkey();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSettingChange || msg == WmThemeChanged || msg == WmDwmColorizationColorChanged)
        {
            ScheduleSystemThemeRefresh();
        }

        if (msg == WmHotkey && wParam.ToInt32() == ManualIdleHotkeyId)
        {
            EnterManualIdleFromShortcut();
            handled = true;
        }
        else if (_manualIdleShortcut is not null && IsManualIdleFallbackKeyDown(msg, wParam, lParam))
        {
            EnterManualIdleFromShortcut();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void LoadManualIdleShortcut()
    {
        var shortcutText = _context.TrackerService.ManualIdleShortcutText;
        _manualIdleShortcut = TryParseManualIdleShortcut(shortcutText);
        App.LogStartupMessage("ManualIdleHotkey.Load", _manualIdleShortcut is null
            ? $"No valid shortcut loaded from '{shortcutText}'."
            : $"Loaded {_manualIdleShortcut.Modifiers}+{_manualIdleShortcut.Key} from '{shortcutText}'.");
    }

    private void RegisterManualIdleHotkey()
    {
        UnregisterManualIdleHotkey();
        if (_windowHandle == IntPtr.Zero || _manualIdleShortcut is null)
        {
            return;
        }

        var modifiers = ToHotkeyModifiers(_manualIdleShortcut.Modifiers);
        var virtualKey = ToVirtualKey(_manualIdleShortcut.Key);
        if (modifiers == 0 || virtualKey == 0)
        {
            return;
        }

        _manualIdleHotkeyRegistered = RegisterHotKey(_windowHandle, ManualIdleHotkeyId, modifiers, (uint)virtualKey);
        App.LogStartupMessage("ManualIdleHotkey", _manualIdleHotkeyRegistered
            ? $"Registered {_manualIdleShortcut.Modifiers}+{_manualIdleShortcut.Key} vk={virtualKey}"
            : $"RegisterHotKey failed {_manualIdleShortcut.Modifiers}+{_manualIdleShortcut.Key} vk={virtualKey}, win32={Marshal.GetLastWin32Error()}");
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

    private static uint ToHotkeyModifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            result |= 0x0001;
        }
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            result |= 0x0002;
        }
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            result |= 0x0004;
        }
        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            result |= 0x0008;
        }

        return result;
    }

    private static uint ToVirtualKey(Key key)
    {
        key = key switch
        {
            Key.Return => Key.Enter,
            _ => key
        };
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0)
        {
            return (uint)virtualKey;
        }

        return key switch
        {
            >= Key.A and <= Key.Z => (uint)(0x41 + key - Key.A),
            >= Key.D0 and <= Key.D9 => (uint)(0x30 + key - Key.D0),
            >= Key.NumPad0 and <= Key.NumPad9 => (uint)(0x60 + key - Key.NumPad0),
            >= Key.F1 and <= Key.F12 => (uint)(0x70 + key - Key.F1),
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            _ => 0
        };
    }

    private static KeyGesture? TryParseManualIdleShortcut(string shortcutText)
    {
        if (string.IsNullOrWhiteSpace(shortcutText))
        {
            return null;
        }

        shortcutText = shortcutText.Replace("\\u002B", "+").Replace("\u002B", "+").Trim();
        try
        {
            var parts = shortcutText.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var modifiers = ModifierKeys.None;
                foreach (var part in parts.Take(parts.Length - 1))
                {
                    if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Control;
                    else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Alt;
                    else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Shift;
                    else if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase) || string.Equals(part, "Windows", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Windows;
                }

                var key = ParseShortcutKey(parts[^1]);
                if (modifiers != ModifierKeys.None && key != Key.None)
                {
                    return new KeyGesture(key, modifiers);
                }
            }

            var converter = new KeyGestureConverter();
            if (converter.ConvertFromInvariantString(shortcutText) is KeyGesture gesture
                && gesture.Key is not Key.None)
            {
                return gesture;
            }
        }
        catch
        {
            // Invalid persisted shortcuts are ignored; settings validation prevents new invalid values.
        }

        return null;
    }

    private static Key ParseShortcutKey(string keyText)
    {
        if (keyText.Length == 1)
        {
            var ch = keyText[0];
            if (ch >= 'A' && ch <= 'Z') return Key.A + (ch - 'A');
            if (ch >= 'a' && ch <= 'z') return Key.A + (ch - 'a');
            if (ch >= '0' && ch <= '9') return Key.D0 + (ch - '0');
        }

        return keyText.ToLowerInvariant() switch
        {
            "esc" => Key.Escape,
            "space" => Key.Space,
            "enter" => Key.Return,
            "return" => Key.Return,
            "back" => Key.Back,
            "delete" => Key.Delete,
            "insert" => Key.Insert,
            "home" => Key.Home,
            "end" => Key.End,
            "pageup" => Key.PageUp,
            "pagedown" => Key.PageDown,
            "up" => Key.Up,
            "down" => Key.Down,
            "left" => Key.Left,
            "right" => Key.Right,
            "+" => Key.OemPlus,
            "-" => Key.OemMinus,
            "," => Key.OemComma,
            "." => Key.OemPeriod,
            "/" => Key.OemQuestion,
            ";" => Key.OemSemicolon,
            "'" => Key.OemQuotes,
            "[" => Key.OemOpenBrackets,
            "]" => Key.OemCloseBrackets,
            "\\" => Key.OemPipe,
            _ => Enum.TryParse<Key>(keyText, ignoreCase: true, out var parsed) ? parsed : Key.None
        };
    }

    private void RestoreFromCompactWindow()
    {
        HideCompactSessionWindow();
        RestoreFromTray();
    }

    private void UpdateCompactSessionWindow()
    {
        if (_compactSessionWindow is null)
        {
            return;
        }

        var active = _context.TrackerService.ActiveSession;
        var title = active is null ? "空闲" : $"{active.ProcessName} · {active.WindowTitle}";
        var duration = active is null ? "--" : FormatDuration(DateTime.Now - active.StartTime);
        _compactSessionWindow.UpdateSession(title, duration);
    }

    private void StartCompactSessionTimer()
    {
        if (_compactSessionTimer is null)
        {
            _compactSessionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _compactSessionTimer.Tick += (_, _) => UpdateCompactSessionWindow();
        }

        _compactSessionTimer.Start();
    }

    private void StopCompactSessionTimer()
    {
        _compactSessionTimer?.Stop();
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}小时 {value.Minutes}分钟";
        }

        return $"{Math.Max(0, value.Minutes)}分钟";
    }

    private bool IsManualIdleFallbackKeyDown(int msg, IntPtr wParam, IntPtr lParam)
    {
        const int wmKeyDown = 0x0100;
        const int wmSysKeyDown = 0x0104;
        if (msg != wmKeyDown && msg != wmSysKeyDown || _manualIdleShortcut is null)
        {
            return false;
        }

        var repeatCount = lParam.ToInt64() & 0xFFFF;
        if (repeatCount > 1)
        {
            return false;
        }

        var required = _manualIdleShortcut.Modifiers;
        if (required == ModifierKeys.None)
        {
            return false;
        }

        var current = Keyboard.Modifiers;
        if ((current & required) != required)
        {
            return false;
        }

        return (uint)wParam.ToInt32() == ToVirtualKey(_manualIdleShortcut.Key);
    }

    private sealed record ModuleGroup(string Group, IReadOnlyList<AppModuleDefinition> Modules);
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute(parameter);
}
