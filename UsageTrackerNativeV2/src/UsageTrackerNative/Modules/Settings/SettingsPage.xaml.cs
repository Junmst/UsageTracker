namespace UsageTrackerNative.Modules.Settings;

public partial class SettingsPage : System.Windows.Controls.UserControl
{
    private readonly Shell.V2AppContext _context;
    private enum TransferPayloadKind
    {
        Usage,
        Settings,
        Full
    }

    private readonly System.Windows.Threading.DispatcherTimer _transferMenuCloseTimer;
    private System.Windows.Controls.Primitives.Popup? _activeTransferPopup;
    private System.Windows.Controls.Border? _activeTransferFlyout;
    private System.Windows.Media.TranslateTransform? _activeTransferTransform;
    private System.Windows.FrameworkElement? _activeTransferHost;
    private System.Windows.Window? _transferMenuOwnerWindow;
    private string _lastStatus = "就绪";
    private double _lastAccentHue = 0;
    private double _lastAccentSaturation = 1.0;
    private double _lastAccentLightness = 0.55;
    private string _previewAccentColor = "#C62828";
    private bool _isDraggingAccentPlane;
    private bool _isDraggingAccentShade;
    private bool _isDraggingAccentSaturation;
    private System.Windows.Controls.Button[]? _accentSlotButtons;

    public SettingsPage(Shell.V2AppContext context)
    {
        _context = context;
        _transferMenuCloseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _transferMenuCloseTimer.Tick += TransferMenuCloseTimer_Tick;
        InitializeComponent();
        RegisterAccentSlotMouseHandlers();
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    private void SettingsPage_Unloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        DetachTransferMenuOwnerWindow();
        HideTransferMenu();
    }

    private void SettingsPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        AttachTransferMenuOwnerWindow();
        RefreshRuntimeSettings();
    }

    private void AttachTransferMenuOwnerWindow()
    {
        var ownerWindow = System.Windows.Window.GetWindow(this);
        if (ReferenceEquals(ownerWindow, _transferMenuOwnerWindow))
        {
            return;
        }

        DetachTransferMenuOwnerWindow();
        _transferMenuOwnerWindow = ownerWindow;
        if (_transferMenuOwnerWindow is not null)
        {
            _transferMenuOwnerWindow.PreviewMouseDown += TransferMenuOwnerWindow_PreviewMouseDown;
            _transferMenuOwnerWindow.Deactivated += TransferMenuOwnerWindow_Deactivated;
        }
    }

    private void DetachTransferMenuOwnerWindow()
    {
        if (_transferMenuOwnerWindow is null)
        {
            return;
        }

        _transferMenuOwnerWindow.PreviewMouseDown -= TransferMenuOwnerWindow_PreviewMouseDown;
        _transferMenuOwnerWindow.Deactivated -= TransferMenuOwnerWindow_Deactivated;
        _transferMenuOwnerWindow = null;
    }

    private void RefreshRuntimeSettings()
    {
        StartupStateText.Text = _context.TrackerService.IsStartWithWindowsEnabled()
            ? LocalizationService.Instance.Get("Settings.Enabled")
            : LocalizationService.Instance.Get("Settings.Disabled");
        IdleTimeoutInput.Text = _context.TrackerService.IdleTimeoutMinutes.ToString();
        ManualIdleShortcutInput.Text = _context.TrackerService.ManualIdleShortcutText;
        RefreshLanguageButtons(_context.TrackerService.Language);
        var accentColor = _context.TrackerService.ThemeAccentColor;
        UpdateAccentStateFromHex(accentColor);
        _previewAccentColor = accentColor;
        UpdateAccentShadeBrush();
        UpdateAccentSaturationBrush();
        UpdateAccentPreview(accentColor);
        UpdateAccentThumbs();
        RefreshAccentSlotButtons();
        StatusText.Text = _lastStatus;
    }

    private void RefreshLanguageButtons(string language)
    {
        var isEn = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        SetLangButtonActive(LangZhButton, LangZhText, !isEn);
        SetLangButtonActive(LangEnButton, LangEnText, isEn);
    }

    private void SetLangButtonActive(System.Windows.Controls.Border button, System.Windows.Controls.TextBlock label, bool active)
    {
        if (active)
        {
            button.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["AccentRedBrush"];
            button.BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["AccentRedBrush"];
            label.Foreground = System.Windows.Media.Brushes.White;
        }
        else
        {
            button.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["ButtonBackgroundBrush"];
            button.BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["ButtonBorderBrush"];
            label.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PrimaryTextBrush"];
        }
    }

    private void LangZhButton_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        _context.TrackerService.SetLanguage("zh");
        LocalizationService.Instance.SetLanguage("zh-CN");
        RefreshLanguageButtons("zh");
        RefreshRuntimeSettings();
        SetStatus("语言已切换为中文。");
    }

    private void LangEnButton_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        _context.TrackerService.SetLanguage("en");
        LocalizationService.Instance.SetLanguage("en-US");
        RefreshLanguageButtons("en");
        RefreshRuntimeSettings();
        SetStatus("Language switched to English.");
    }

    private void SystemSettingsScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

    private void StartupRow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var enabled = !_context.TrackerService.IsStartWithWindowsEnabled();
        _context.TrackerService.SetStartWithWindows(enabled);
        RefreshRuntimeSettings();
    }

    private void SaveIdleTimeoutButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!int.TryParse(IdleTimeoutInput.Text.Trim(), out var minutes))
        {
            SetStatus("空闲判定必须是 1-1440 之间的分钟数。", isError: true);
            RefreshRuntimeSettings();
            return;
        }

        if (minutes < 1 || minutes > 1440)
        {
            SetStatus("空闲判定范围是 1-1440 分钟。", isError: true);
            RefreshRuntimeSettings();
            return;
        }

        _context.TrackerService.SetIdleTimeoutMinutes(minutes);
        SetStatus($"已保存空闲判定：{minutes} 分钟。");
        RefreshRuntimeSettings();
    }

    private void IdleTimeoutInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Return)
        {
            e.Handled = true;
            SaveIdleTimeoutButton_Click(sender, e);
        }
    }

    private void ManualIdleShortcutInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Tab)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Return)
        {
            SaveShortcutButton_Click(sender, e);
            return;
        }

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        key = NormalizeShortcutKey(key);
        if (IsModifierOnlyKey(key) || key == System.Windows.Input.Key.None)
        {
            return;
        }

        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        var parts = new List<string>();
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(FormatShortcutKeyName(key));
        ManualIdleShortcutInput.Text = string.Join("+", parts);
        ManualIdleShortcutInput.CaretIndex = ManualIdleShortcutInput.Text.Length;
    }

    private static bool IsModifierOnlyKey(System.Windows.Input.Key key)
    {
        return key == System.Windows.Input.Key.LeftCtrl || key == System.Windows.Input.Key.RightCtrl || key == System.Windows.Input.Key.LeftAlt || key == System.Windows.Input.Key.RightAlt ||
               key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift || key == System.Windows.Input.Key.LWin || key == System.Windows.Input.Key.RWin;
    }

    private static System.Windows.Input.Key NormalizeShortcutKey(System.Windows.Input.Key key)
    {
        return key switch
        {
            System.Windows.Input.Key.ImeProcessed => System.Windows.Input.Keyboard.PrimaryDevice.ActiveSource is null ? key : System.Windows.Input.KeyInterop.KeyFromVirtualKey(System.Windows.Input.KeyInterop.VirtualKeyFromKey(key)),
            _ => key
        };
    }

    private static string FormatShortcutKeyName(System.Windows.Input.Key key)
    {
        return key switch
        {
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)(key - System.Windows.Input.Key.D0)).ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => ((int)(key - System.Windows.Input.Key.NumPad0)).ToString(),
            System.Windows.Input.Key.OemPlus or System.Windows.Input.Key.Add => "+",
            System.Windows.Input.Key.OemMinus or System.Windows.Input.Key.Subtract => "-",
            System.Windows.Input.Key.OemComma => ",",
            System.Windows.Input.Key.OemPeriod or System.Windows.Input.Key.Decimal => ".",
            System.Windows.Input.Key.OemQuestion or System.Windows.Input.Key.Divide => "/",
            System.Windows.Input.Key.OemSemicolon => ";",
            System.Windows.Input.Key.OemQuotes => "'",
            System.Windows.Input.Key.OemOpenBrackets => "[",
            System.Windows.Input.Key.OemCloseBrackets => "]",
            System.Windows.Input.Key.OemPipe => "\\",
            System.Windows.Input.Key.Space => "Space",
            System.Windows.Input.Key.Escape => "Esc",
            System.Windows.Input.Key.Return or System.Windows.Input.Key.Enter => "Enter",
            System.Windows.Input.Key.Back => "Back",
            System.Windows.Input.Key.Delete => "Delete",
            System.Windows.Input.Key.Insert => "Insert",
            System.Windows.Input.Key.Home => "Home",
            System.Windows.Input.Key.End => "End",
            System.Windows.Input.Key.PageUp => "PageUp",
            System.Windows.Input.Key.PageDown => "PageDown",
            System.Windows.Input.Key.Up => "Up",
            System.Windows.Input.Key.Down => "Down",
            System.Windows.Input.Key.Left => "Left",
            System.Windows.Input.Key.Right => "Right",
            _ => key.ToString()
        };
    }

    private void SaveShortcutButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var shortcut = ManualIdleShortcutInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            _context.SetManualIdleShortcutText(string.Empty);
            SetStatus("已清空手动空闲快捷键；不会注册默认热键。");
            RefreshRuntimeSettings();
            return;
        }

        if (!IsValidShortcut(shortcut))
        {
            SetStatus("快捷键格式不正确，例如 Ctrl+L、Ctrl+Alt+I、Ctrl+Shift+F12。", isError: true);
            RefreshRuntimeSettings();
            return;
        }

        _context.SetManualIdleShortcutText(shortcut);
        SetStatus($"已保存手动空闲快捷键：{shortcut}。全局热键已重新注册。");
        RefreshRuntimeSettings();
    }

    private void ApplyAccentColorButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ApplyAccentColorPreset(_previewAccentColor);
    }

    private void ExportMenuButton_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleTransferMenu(ExportMenuPopup, ExportMenuFlyout, ExportMenuFlyoutTransform, ExportMenuHost);
    }

    private void ImportMenuButton_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleTransferMenu(ImportMenuPopup, ImportMenuFlyout, ImportMenuFlyoutTransform, ImportMenuHost);
    }

    private void SettingsRoot_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HideTransferMenuIfOutside();
    }

    private void TransferMenuOwnerWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HideTransferMenuIfOutside();
    }

    private void TransferMenuOwnerWindow_Deactivated(object? sender, EventArgs e)
    {
        HideTransferMenu();
    }

    private void HideTransferMenuIfOutside()
    {
        if (_activeTransferPopup?.IsOpen != true)
        {
            return;
        }

        if (IsMouseWithinActiveTransferSafeArea())
        {
            return;
        }

        HideTransferMenu();
    }

    private async void TransferMenuItem_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border { Tag: string tag }) return;
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

    private void ToggleTransferMenu(System.Windows.Controls.Primitives.Popup popup, System.Windows.Controls.Border flyout, System.Windows.Media.TranslateTransform transform, System.Windows.FrameworkElement host)
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
        _activeTransferTransform = transform;
        _activeTransferHost = host;
        popup.IsOpen = true;
        AnimateTransferMenu(flyout, transform, show: true);
    }

    private void HideTransferMenu()
    {
        _transferMenuCloseTimer.Stop();
        if (_activeTransferPopup is null || _activeTransferFlyout is null || _activeTransferTransform is null)
        {
            return;
        }

        var popup = _activeTransferPopup;
        var flyout = _activeTransferFlyout;
        var transform = _activeTransferTransform;
        _activeTransferPopup = null;
        _activeTransferFlyout = null;
        _activeTransferTransform = null;
        _activeTransferHost = null;
        AnimateTransferMenu(flyout, transform, show: false, () => popup.IsOpen = false);
    }

    private static void AnimateTransferMenu(System.Windows.Controls.Border flyout, System.Windows.Media.TranslateTransform transform, bool show, Action? completed = null)
    {
        var easing = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var opacityAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = easing
        };
        var yAnimation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = show ? 0 : -8,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = easing
        };
        if (completed is not null)
        {
            opacityAnimation.Completed += (_, _) => completed();
        }

        flyout.BeginAnimation(System.Windows.UIElement.OpacityProperty, opacityAnimation);
        transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, yAnimation);
    }

    private void TransferMenuSafeArea_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _transferMenuCloseTimer.Stop();
    }

    private void TransferMenuSafeArea_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
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
        if (_activeTransferFlyout is not null && !IsMouseWithinActiveTransferSafeArea())
        {
            HideTransferMenu();
        }
    }

    private bool IsMouseWithinActiveTransferSafeArea()
    {
        if (_activeTransferFlyout is null || _activeTransferHost is null)
        {
            return false;
        }

        if (IsMouseWithin(_activeTransferHost) || IsMouseWithin(_activeTransferFlyout))
        {
            return true;
        }

        return IsMouseWithinVerticalConnector(_activeTransferHost, _activeTransferFlyout);
    }

    private static bool IsMouseWithinVerticalConnector(System.Windows.FrameworkElement host, System.Windows.FrameworkElement flyout)
    {
        if (!host.IsVisible || !flyout.IsVisible)
        {
            return false;
        }

        var mouseInHost = System.Windows.Input.Mouse.GetPosition(host);
        if (mouseInHost.X < 0 || mouseInHost.X > host.ActualWidth)
        {
            return false;
        }

        var mouseInFlyout = System.Windows.Input.Mouse.GetPosition(flyout);
        var hostBottomInFlyout = host.TranslatePoint(new System.Windows.Point(0, host.ActualHeight), flyout).Y;
        var flyoutTopInHost = flyout.TranslatePoint(new System.Windows.Point(0, 0), host).Y;

        return mouseInFlyout.Y >= hostBottomInFlyout && mouseInHost.Y <= flyoutTopInHost;
    }

    private static bool IsMouseWithin(System.Windows.FrameworkElement? element)
    {
        if (element is null || !element.IsVisible)
        {
            return false;
        }

        var point = System.Windows.Input.Mouse.GetPosition(element);
        return point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
    }

    private async Task ExportJsonAsync(TransferPayloadKind kind)
    {
        var exportDirectory = _context.TrackerService.GetDefaultExportDirectory();
        var prefix = kind switch
        {
            TransferPayloadKind.Usage => "usage-data",
            TransferPayloadKind.Settings => "settings",
            _ => "full-backup"
        };
        var extension = kind == TransferPayloadKind.Settings ? "json" : "zip";
        var filePath = System.IO.Path.Combine(exportDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");

        switch (kind)
        {
            case TransferPayloadKind.Usage:
                await _context.TrackerService.ExportUsageDataAsync(filePath);
                break;
            case TransferPayloadKind.Settings:
                await _context.TrackerService.ExportSettingsDataAsync(filePath);
                break;
            default:
                await _context.TrackerService.ExportFullBackupAsync(filePath);
                break;
        }

        SetStatus($"已导出：{filePath}");
    }

    private async Task ImportJsonAsync(TransferPayloadKind kind)
    {
        var filter = kind == TransferPayloadKind.Settings
            ? "JSON 配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
            : "ZIP 备份文件 (*.zip)|*.zip|JSON 旧版数据文件 (*.json)|*.json|所有文件 (*.*)|*.*";
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = filter,
            InitialDirectory = _context.TrackerService.GetDefaultExportDirectory(),
            Title = kind switch
            {
                TransferPayloadKind.Usage => "选择要导入的数据文件",
                TransferPayloadKind.Settings => "选择要导入的配置文件",
                _ => "选择要导入的完整备份"
            }
        };

        if (dialog.ShowDialog() != true)
        {
            SetStatus("已取消导入。");
            return;
        }

        var payloadKind = kind switch
        {
            TransferPayloadKind.Usage => ImportPayloadKind.Usage,
            TransferPayloadKind.Settings => ImportPayloadKind.Settings,
            _ => ImportPayloadKind.Full
        };
        var preview = await _context.TrackerService.PreviewImportPackageAsync(dialog.FileName, payloadKind);
        var owner = System.Windows.Window.GetWindow(this);
        var options = ImportOptionsDialog.Show(owner, preview, payloadKind);
        if (options is null)
        {
            SetStatus("已取消导入。");
            return;
        }

        if (!options.ShouldApply)
        {
            _context.EnterPreviewMode(preview);
            SetStatus($"已进入仅查看模式：{System.IO.Path.GetFileName(dialog.FileName)}。");
            return;
        }

        var result = payloadKind switch
        {
            ImportPayloadKind.Usage when options.SelectedDataMode == ImportDataMode.Replace => await _context.TrackerService.ReplaceUsageDataAsync(preview),
            ImportPayloadKind.Usage => await _context.TrackerService.ImportUsageDataAsync(dialog.FileName, options.SelectedConflictStrategy),
            ImportPayloadKind.Settings => await _context.TrackerService.ImportSettingsDataAsync(preview, options.SelectedSettingsMode),
            _ => await _context.TrackerService.ImportFullBackupAsync(preview, options.SelectedDataMode, options.SelectedConflictStrategy, options.SelectedSettingsMode)
        };

        RefreshRuntimeSettings();
        SetStatus(BuildImportResultStatus(payloadKind, result));
        if (payloadKind != ImportPayloadKind.Settings)
        {
            _context.RequestNavigate("overview");
        }
    }

    private static string BuildImportResultStatus(ImportPayloadKind kind, ImportResult result)
    {
        var label = kind switch
        {
            ImportPayloadKind.Usage => "数据导入完成",
            ImportPayloadKind.Settings => "配置导入完成",
            _ => "完整备份导入完成"
        };
        var parts = new List<string> { label };
        if (kind != ImportPayloadKind.Settings)
        {
            parts.Add($"新增 {result.ImportedCount}");
            parts.Add($"冲突/跳过 {result.ConflictCount}");
            parts.Add($"覆盖 {result.OverwrittenCount}");
            parts.Add($"总计 {result.TotalCount}");
        }
        if (result.SettingsChangedCount > 0)
        {
            parts.Add($"配置变更 {result.SettingsChangedCount}");
        }
        if (!string.IsNullOrWhiteSpace(result.BackupPath))
        {
            parts.Add($"已备份：{result.BackupPath}");
        }
        return string.Join("，", parts) + "。";
    }

    private void AccentSlot_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (TryGetAccentSlotIndex(sender, out var index))
        {
            ApplyAccentSlot(index);
        }
    }

    private void RegisterAccentSlotMouseHandlers()
    {
        AccentSlotButton1.AddHandler(System.Windows.UIElement.PreviewMouseDownEvent, new System.Windows.Input.MouseButtonEventHandler(AccentSlotButton_PreviewMouseDown), true);
        AccentSlotButton2.AddHandler(System.Windows.UIElement.PreviewMouseDownEvent, new System.Windows.Input.MouseButtonEventHandler(AccentSlotButton_PreviewMouseDown), true);
        AccentSlotButton3.AddHandler(System.Windows.UIElement.PreviewMouseDownEvent, new System.Windows.Input.MouseButtonEventHandler(AccentSlotButton_PreviewMouseDown), true);
    }

    private void AccentSlotButton_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Right)
        {
            return;
        }

        if (TryGetAccentSlotIndex(sender, out var index))
        {
            SaveCurrentAccentToSlot(index);
            e.Handled = true;
        }
    }

    private static bool TryGetAccentSlotIndex(object? sender, out int index)
    {
        index = -1;
        if (sender is not System.Windows.Controls.Button button)
        {
            return false;
        }

        return button.Tag switch
        {
            int value when value is >= 0 and <= 2 => SetIndex(value, out index),
            string text when int.TryParse(text, out var value) && value is >= 0 and <= 2 => SetIndex(value, out index),
            _ => false
        };
    }

    private static bool SetIndex(int value, out int index)
    {
        index = value;
        return true;
    }

    private void ApplyAccentSlot(int index)
    {
        var color = _context.TrackerService.GetThemeAccentSlot(index);
        ApplyAccentColorPreset(color);
        SetStatus($"已应用颜色槽 {index + 1}：{color}。");
    }

    private void SaveCurrentAccentToSlot(int index)
    {
        var color = _previewAccentColor;
        if (!IsValidHexColor(color))
        {
            color = _context.TrackerService.ThemeAccentColor;
        }

        _context.TrackerService.SetThemeAccentSlot(index, color);
        RefreshAccentSlotButtons();
        SetStatus($"已将当前主题色 {color} 保存到颜色槽 {index + 1}。");
    }

    private void RefreshAccentSlotButtons()
    {
        _accentSlotButtons ??= [AccentSlotButton1, AccentSlotButton2, AccentSlotButton3];
        for (var i = 0; i < _accentSlotButtons.Length; i++)
        {
            var button = _accentSlotButtons[i];
            var color = _context.TrackerService.GetThemeAccentSlot(i);
            if (TryColorBrush(color, out var brush))
            {
                button.Background = brush;
                button.BorderBrush = brush;
                button.Foreground = GetReadableForegroundBrush(color);
            }
            button.Content = string.Empty;
            button.ToolTip = $"左键应用 {color}；右键保存当前主题色到槽 {i + 1}";
        }
    }

    private static System.Windows.Media.Brush GetReadableForegroundBrush(string color)
    {
        if (!TryHexToRgb(color, out var r, out var g, out var b))
        {
            return System.Windows.Media.Brushes.White;
        }

        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return luminance > 0.58 ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
    }

    private void AccentPlane_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement element) return;
        _isDraggingAccentPlane = true;
        element.CaptureMouse();
        UpdateAccentFromPlane(element, e.GetPosition(element), apply: true);
    }

    private void AccentPlane_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingAccentPlane || sender is not System.Windows.FrameworkElement element) return;
        UpdateAccentFromPlane(element, e.GetPosition(element), apply: false);
    }

    private void AccentPlane_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement element) return;
        if (_isDraggingAccentPlane)
        {
            UpdateAccentFromPlane(element, e.GetPosition(element), apply: true);
        }
        _isDraggingAccentPlane = false;
        element.ReleaseMouseCapture();
    }

    private void AccentShadeBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement element) return;
        _isDraggingAccentShade = true;
        element.CaptureMouse();
        UpdateAccentFromShade(element, e.GetPosition(element), apply: true);
    }

    private void AccentShadeBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingAccentShade || sender is not System.Windows.FrameworkElement element) return;
        UpdateAccentFromShade(element, e.GetPosition(element), apply: false);
    }

    private void AccentShadeBar_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement element) return;
        if (_isDraggingAccentShade)
        {
            UpdateAccentFromShade(element, e.GetPosition(element), apply: true);
        }
        _isDraggingAccentShade = false;
        element.ReleaseMouseCapture();
    }

    private void AccentShadeBar_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        AdjustAccentShadeByWheel(e.Delta);
        e.Handled = true;
    }

    private void AccentSaturationBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement element) return;
        _isDraggingAccentSaturation = true;
        element.CaptureMouse();
        UpdateAccentFromSaturation(element, e.GetPosition(element), apply: true);
    }

    private void AccentSaturationBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingAccentSaturation || sender is not System.Windows.FrameworkElement element) return;
        UpdateAccentFromSaturation(element, e.GetPosition(element), apply: false);
    }

    private void AccentSaturationBar_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement element) return;
        if (_isDraggingAccentSaturation)
        {
            UpdateAccentFromSaturation(element, e.GetPosition(element), apply: true);
        }
        _isDraggingAccentSaturation = false;
        element.ReleaseMouseCapture();
    }

    private void AccentSaturationBar_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        AdjustAccentSaturationByWheel(e.Delta);
        e.Handled = true;
    }

    private void AdjustAccentShadeByWheel(int delta)
    {
        var step = delta > 0 ? 0.03 : -0.03;
        _lastAccentLightness = Math.Clamp(_lastAccentLightness + step, 0, 1);
        var color = HslToHex(_lastAccentHue, _lastAccentSaturation, _lastAccentLightness);
        PreviewAccentColor(color);
        UpdateAccentShadeBrush();
        UpdateAccentSaturationBrush();
        UpdateShadeThumbFromLightness();
        UpdateSaturationThumbFromSaturation();
        Shell.ShellWindow.SetAccentColor(color);
        SetStatus($"已滚轮调节明暗：{color}。");
    }

    private void AdjustAccentSaturationByWheel(int delta)
    {
        var step = delta > 0 ? 0.03 : -0.03;
        _lastAccentSaturation = Math.Clamp(_lastAccentSaturation + step, 0, 1);
        var color = HslToHex(_lastAccentHue, _lastAccentSaturation, _lastAccentLightness);
        PreviewAccentColor(color);
        UpdateAccentSaturationBrush();
        UpdateSaturationThumbFromSaturation();
        Shell.ShellWindow.SetAccentColor(color);
        SetStatus($"已滚轮调节饱和度：{color}。");
    }

    private void UpdateAccentFromPlane(System.Windows.FrameworkElement element, System.Windows.Point pos, bool apply)
    {
        var xRatio = Math.Clamp(pos.X / Math.Max(1, element.ActualWidth), 0, 1);
        var yRatio = Math.Clamp(pos.Y / Math.Max(1, element.ActualHeight), 0, 1);
        _lastAccentHue = xRatio * 360.0;
        _lastAccentSaturation = 1.0;
        _lastAccentLightness = 0.12 + yRatio * 0.76;
        var color = HslToHex(_lastAccentHue, _lastAccentSaturation, _lastAccentLightness);
        PreviewAccentColor(color);
        UpdatePlaneThumb(xRatio, yRatio);
        UpdateAccentShadeBrush();
        UpdateAccentSaturationBrush();
        UpdateShadeThumbFromLightness();
        UpdateSaturationThumbFromSaturation();
        if (apply)
        {
            ApplyAccentColorPreset(color);
        }
    }

    private void UpdateAccentFromShade(System.Windows.FrameworkElement element, System.Windows.Point pos, bool apply)
    {
        var ratio = Math.Clamp(pos.X / Math.Max(1, element.ActualWidth), 0, 1);
        _lastAccentLightness = ratio;
        var color = HslToHex(_lastAccentHue, _lastAccentSaturation, _lastAccentLightness);
        PreviewAccentColor(color);
        UpdateShadeThumb(ratio);
        if (apply)
        {
            ApplyAccentColorPreset(color);
        }
    }

    private void UpdateAccentFromSaturation(System.Windows.FrameworkElement element, System.Windows.Point pos, bool apply)
    {
        var ratio = Math.Clamp(pos.X / Math.Max(1, element.ActualWidth), 0, 1);
        _lastAccentSaturation = ratio;
        var color = HslToHex(_lastAccentHue, _lastAccentSaturation, _lastAccentLightness);
        PreviewAccentColor(color);
        UpdateAccentSaturationBrush();
        UpdateSaturationThumb(ratio);
        if (apply)
        {
            ApplyAccentColorPreset(color);
        }
    }

    private static string HslToHex(double h, double s, double l)
    {
        h = Math.Clamp(h, 0, 360);
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = l - c / 2;
        double r1, g1, b1;
        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }
        var r = (int)Math.Round((r1 + m) * 255);
        var g = (int)Math.Round((g1 + m) * 255);
        var b = (int)Math.Round((b1 + m) * 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private void DoneButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SetStatus("设置已保存。可继续使用左侧模块。");
    }

    private void ApplyAccentColorPreset(string color)
    {
        UpdateAccentStateFromHex(color);
        _previewAccentColor = color;
        UpdateAccentPreview(color);
        UpdateAccentShadeBrush();
        UpdateAccentSaturationBrush();
        UpdateAccentThumbs();
        Shell.ShellWindow.SetAccentColor(color);
        RefreshAccentSlotButtons();
        SetStatus($"已应用主题色：{color}。");
    }

    private void UpdateAccentStateFromHex(string color)
    {
        if (!TryHexToRgb(color, out var r, out var g, out var b))
        {
            return;
        }

        RgbToHsl(r, g, b, out var h, out var s, out var l);
        _lastAccentHue = h;
        _lastAccentSaturation = Math.Clamp(s, 0, 1);
        _lastAccentLightness = Math.Clamp(l, 0, 1);
    }

    private void UpdateAccentShadeBrush()
    {
        if (AccentShadeBrush is null)
        {
            return;
        }

        AccentShadeBrush.GradientStops.Clear();
        AccentShadeBrush.GradientStops.Add(new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(HslToHex(_lastAccentHue, _lastAccentSaturation, 0.0)), 0));
        AccentShadeBrush.GradientStops.Add(new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(HslToHex(_lastAccentHue, _lastAccentSaturation, 0.34)), 0.34));
        AccentShadeBrush.GradientStops.Add(new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(HslToHex(_lastAccentHue, _lastAccentSaturation, 0.58)), 0.62));
        AccentShadeBrush.GradientStops.Add(new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(HslToHex(_lastAccentHue, _lastAccentSaturation, 1.0)), 1));
    }

    private void UpdateAccentSaturationBrush()
    {
        if (AccentSaturationBrush is null)
        {
            return;
        }

        var gray = HslToHex(_lastAccentHue, 0, _lastAccentLightness);
        var pure = HslToHex(_lastAccentHue, 1, _lastAccentLightness);
        AccentSaturationBrush.GradientStops.Clear();
        AccentSaturationBrush.GradientStops.Add(new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(gray), 0));
        AccentSaturationBrush.GradientStops.Add(new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(pure), 1));
    }

    private void PreviewAccentColor(string color)
    {
        _previewAccentColor = color;
        UpdateAccentPreview(color);
    }

    private void UpdateAccentPreview(string color)
    {
        if (!TryColorBrush(color, out var brush)) return;
        PreviewColorSwatch.Background = brush;
        CurrentColorSwatch.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["AccentRedBrush"];
    }

    private void UpdateAccentThumbs()
    {
        UpdatePlaneThumb((_lastAccentHue % 360.0) / 360.0, Math.Clamp((_lastAccentLightness - 0.12) / 0.76, 0, 1));
        UpdateShadeThumbFromLightness();
        UpdateSaturationThumbFromSaturation();
    }

    private void UpdatePlaneThumb(double xRatio, double yRatio)
    {
        if (AccentPlaneThumb is null || AccentPlane is null) return;
        var x = Math.Clamp(xRatio, 0, 1) * Math.Max(0, AccentPlane.ActualWidth) - AccentPlaneThumb.Width / 2;
        var y = Math.Clamp(yRatio, 0, 1) * Math.Max(0, AccentPlane.ActualHeight) - AccentPlaneThumb.Height / 2;
        AccentPlaneThumb.Margin = new System.Windows.Thickness(x, y, 0, 0);
    }

    private void UpdateShadeThumbFromLightness()
    {
        UpdateShadeThumb(Math.Clamp(_lastAccentLightness, 0, 1));
    }

    private void UpdateShadeThumb(double ratio)
    {
        if (AccentShadeThumb is null || AccentShadeBar is null) return;
        var x = Math.Clamp(ratio, 0, 1) * Math.Max(0, AccentShadeBar.ActualWidth) - AccentShadeThumb.Width / 2;
        AccentShadeThumb.Margin = new System.Windows.Thickness(x, 0, 0, 0);
    }

    private void UpdateSaturationThumbFromSaturation()
    {
        UpdateSaturationThumb(Math.Clamp(_lastAccentSaturation, 0, 1));
    }

    private void UpdateSaturationThumb(double ratio)
    {
        if (AccentSaturationThumb is null || AccentSaturationBar is null) return;
        var x = Math.Clamp(ratio, 0, 1) * Math.Max(0, AccentSaturationBar.ActualWidth) - AccentSaturationThumb.Width / 2;
        AccentSaturationThumb.Margin = new System.Windows.Thickness(x, 0, 0, 0);
    }

    private static bool TryColorBrush(string color, out System.Windows.Media.SolidColorBrush brush)
    {
        brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent);
        if (!IsValidHexColor(color)) return false;
        brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        return true;
    }

    private static bool TryHexToRgb(string color, out double r, out double g, out double b)
    {
        r = g = b = 0;
        if (!IsValidHexColor(color))
        {
            return false;
        }

        r = Convert.ToInt32(color.Substring(1, 2), 16) / 255.0;
        g = Convert.ToInt32(color.Substring(3, 2), 16) / 255.0;
        b = Convert.ToInt32(color.Substring(5, 2), 16) / 255.0;
        return true;
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (Math.Abs(max - min) < 0.0001)
        {
            h = 0;
            s = 0;
            return;
        }

        var d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        h = Math.Abs(max - r) < 0.0001
            ? 60 * (((g - b) / d + 6) % 6)
            : Math.Abs(max - g) < 0.0001
                ? 60 * ((b - r) / d + 2)
                : 60 * ((r - g) / d + 4);
    }

    private void SetStatus(string message, bool isError = false)
    {
        _lastStatus = message;
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? System.Windows.Media.Brushes.OrangeRed
            : (System.Windows.Application.Current.Resources["SecondaryTextBrush"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray);
    }

    private static bool IsValidHexColor(string value)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$");
    }

    private static bool IsValidShortcut(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var key = parts[^1];
        if (!IsSupportedShortcutKeyName(key))
        {
            return false;
        }

        return parts.Take(parts.Length - 1).All(part =>
            string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase)
            || string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSupportedShortcutKeyName(string key)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9]$|^F([1-9]|1[0-2])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return true;
        }

        return new[]
        {
            "Space", "Esc", "Enter", "Tab", "Back", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
            "Up", "Down", "Left", "Right", "+", "-", ",", ".", "/", ";", "'", "[", "]", "\\"
        }.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
    }
}
