using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsageTrackerWeb;

/// <summary>
/// 托盘宿主 + 消息循环 + 全局热键。窗口本身永不显示：
/// 界面由系统托盘菜单、桌面小窗（CompactStatusForm）与网页看板（DashboardForm）组成。
/// </summary>
public sealed class TrayForm : Form
{
    private const int BrowserHotkeyId = 0x5742;
    private const int ManualIdleHotkeyId = 0x5743;
    private const int StatusWindowHotkeyId = 0x5744;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const int WmHotkey = 0x0312;

    /// <summary>网页快捷键默认值（从未设置过时生效；用户清除后不再回落默认值）。</summary>
    internal const uint DefaultBrowserHotkeyModifiers = ModControl | ModAlt;
    internal const uint DefaultBrowserHotkeyKey = (uint)Keys.W;
    internal static string DefaultBrowserHotkeyGesture => FormatHotkey(DefaultBrowserHotkeyModifiers, DefaultBrowserHotkeyKey);

    private readonly IHost _host;
    private readonly string _url;
    private readonly WebPreferencesStore _preferences;
    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private bool _hotkeyRegistered;
    private uint _manualIdleHotkeyModifiers;
    private uint _manualIdleHotkeyKey;
    private bool _manualIdleHotkeyRegistered;
    private uint _statusWindowHotkeyModifiers;
    private uint _statusWindowHotkeyKey;
    private bool _statusWindowHotkeyRegistered;
    private readonly NotifyIcon _notifyIcon = new();
    private TrayMenuForm? _trayMenu;
    private DateTime _lastBrowserLaunchAt = DateTime.MinValue;
    private bool _closingForExit;
    private DashboardForm? _dashboard;
    private CompactStatusForm? _statusWindow;
    private int? _ownedNativeProcessId;

    /// <summary>供 API 端点跨线程访问（热键注册必须在 UI 线程执行）。</summary>
    internal static TrayForm? Instance { get; private set; }

    public TrayForm(IHost host, string url, bool openDashboard, WebPreferencesStore preferences)
    {
        _host = host;
        _url = url;
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        Instance = this;

        Text = "时迹";
        ShowInTaskbar = false;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
        Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        _notifyIcon.Text = "时迹 · 网页控制台";
        _notifyIcon.Icon = Icon;
        _notifyIcon.Visible = true;
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;

        LoadHotkey();
        RegisterBrowserHotkey();
        LoadManualIdleHotkey();
        RegisterManualIdleHotkey();
        LoadStatusWindowHotkey();
        RegisterStatusWindowHotkey();

        // 无论哪种启动方式都显示托盘 + 小窗；--show 额外自动打开网页看板。
        BeginInvoke(() =>
        {
            ShowStatusWindow();
            if (openDashboard) _ = OpenDashboardAsync();
        });
        // 消息循环启动后必定拉起 Native 后台记录程序（不能依赖 Shown，窗口永不显示）。
        BeginInvoke(async () => await StartNativeInBackgroundAsync());
    }

    /// <summary>Application.Run(this) 会自动 Show()，拦截之：窗口永不显示。</summary>
    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(false);
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _ = OpenDashboardAsync();
    }

    private void NotifyIcon_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ShowTrayMenu();
        }
    }

    private void ShowTrayMenu()
    {
        _trayMenu?.CloseAnimated();
        _trayMenu = new TrayMenuForm(
            () => _ = OpenDashboardAsync(),
            ToggleStatusWindow,
            () => _ = ExitAsync(),
            IsStatusWindowVisible);
        _trayMenu.ShowFromTray();
    }

    internal bool IsStatusWindowVisible => _statusWindow is { IsDisposed: false };

    internal void ToggleStatusWindow()
    {
        if (IsStatusWindowVisible)
        {
            _statusWindow!.Close(); // 非模态窗口 Close 会一并 Dispose
            _statusWindow = null;
        }
        else
        {
            ShowStatusWindow();
        }
    }

    private void ShowStatusWindow()
    {
        if (_statusWindow is null || _statusWindow.IsDisposed)
        {
            _statusWindow = new CompactStatusForm(() => _ = OpenDashboardAsync());
        }
        _statusWindow.ShowStatus();
    }

    private void LoadHotkey()
    {
        var hotkey = _preferences.Load().BrowserHotkey;
        if (hotkey is null)
        {
            // 从未设置过：使用默认 Ctrl + Alt + W
            _hotkeyModifiers = DefaultBrowserHotkeyModifiers;
            _hotkeyKey = DefaultBrowserHotkeyKey;
        }
        else
        {
            // 已保存 0/0 表示用户主动清除，不再回落默认值
            _hotkeyModifiers = hotkey.Modifiers;
            _hotkeyKey = hotkey.Key;
        }
    }

    private void SaveHotkey(uint modifiers, uint key)
    {
        UnregisterBrowserHotkey();
        _hotkeyModifiers = modifiers;
        _hotkeyKey = key;
        _preferences.SaveHotkey(modifiers, key, FormatOptionalHotkey(modifiers, key));
        RegisterBrowserHotkey();
    }

    private void RegisterBrowserHotkey()
    {
        if (_hotkeyModifiers == 0 || _hotkeyKey == 0) return;
        _hotkeyRegistered = RegisterHotKey(Handle, BrowserHotkeyId, _hotkeyModifiers, _hotkeyKey);
    }

    private void UnregisterBrowserHotkey()
    {
        if (!_hotkeyRegistered) return;
        UnregisterHotKey(Handle, BrowserHotkeyId);
        _hotkeyRegistered = false;
    }

    private static string FormatHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(((Keys)key).ToString());
        return string.Join(" + ", parts);
    }

    // 手动空闲快捷键：用户未设置时不注册全局热键，显示“未设置”。
    private void LoadManualIdleHotkey()
    {
        var hotkey = _preferences.Load().ManualIdleHotkey;
        if (hotkey is null || hotkey.Modifiers == 0 || hotkey.Key == 0)
        {
            _manualIdleHotkeyModifiers = 0;
            _manualIdleHotkeyKey = 0;
        }
        else
        {
            _manualIdleHotkeyModifiers = hotkey.Modifiers;
            _manualIdleHotkeyKey = hotkey.Key;
        }
    }

    private void SaveManualIdleHotkey(uint modifiers, uint key)
    {
        UnregisterManualIdleHotkey();
        _manualIdleHotkeyModifiers = modifiers;
        _manualIdleHotkeyKey = key;
        _preferences.SaveManualIdleHotkey(modifiers, key, FormatOptionalHotkey(modifiers, key));
        RegisterManualIdleHotkey();
    }

    private void RegisterManualIdleHotkey()
    {
        if (_manualIdleHotkeyModifiers == 0 || _manualIdleHotkeyKey == 0) return;
        _manualIdleHotkeyRegistered = RegisterHotKey(Handle, ManualIdleHotkeyId, _manualIdleHotkeyModifiers, _manualIdleHotkeyKey);
    }

    private void UnregisterManualIdleHotkey()
    {
        if (!_manualIdleHotkeyRegistered) return;
        UnregisterHotKey(Handle, ManualIdleHotkeyId);
        _manualIdleHotkeyRegistered = false;
    }

    private static string FormatOptionalHotkey(uint modifiers, uint key)
    {
        if (modifiers == 0 || key == 0) return "未设置";
        return FormatHotkey(modifiers, key);
    }

    // 小窗显示/隐藏快捷键：用户未设置时不注册全局热键，显示“未设置”。
    private void LoadStatusWindowHotkey()
    {
        var hotkey = _preferences.Load().StatusWindowHotkey;
        if (hotkey is null || hotkey.Modifiers == 0 || hotkey.Key == 0)
        {
            _statusWindowHotkeyModifiers = 0;
            _statusWindowHotkeyKey = 0;
        }
        else
        {
            _statusWindowHotkeyModifiers = hotkey.Modifiers;
            _statusWindowHotkeyKey = hotkey.Key;
        }
    }

    private void SaveStatusWindowHotkey(uint modifiers, uint key)
    {
        UnregisterStatusWindowHotkey();
        _statusWindowHotkeyModifiers = modifiers;
        _statusWindowHotkeyKey = key;
        _preferences.SaveStatusWindowHotkey(modifiers, key, FormatOptionalHotkey(modifiers, key));
        RegisterStatusWindowHotkey();
    }

    private void RegisterStatusWindowHotkey()
    {
        if (_statusWindowHotkeyModifiers == 0 || _statusWindowHotkeyKey == 0) return;
        _statusWindowHotkeyRegistered = RegisterHotKey(Handle, StatusWindowHotkeyId, _statusWindowHotkeyModifiers, _statusWindowHotkeyKey);
    }

    private void UnregisterStatusWindowHotkey()
    {
        if (!_statusWindowHotkeyRegistered) return;
        UnregisterHotKey(Handle, StatusWindowHotkeyId);
        _statusWindowHotkeyRegistered = false;
    }

    private static bool HasModifier(uint modifiers)
        => (modifiers & (ModControl | ModAlt | ModShift | ModWin)) != 0;

    /// <summary>Web 设置页保存/清除网页快捷键：0/0 表示清除。</summary>
    internal (string Gesture, bool Registered) UpdateBrowserHotkey(uint modifiers, uint key)
    {
        if (modifiers == 0 || key == 0)
        {
            SaveHotkey(0, 0);
            return ("未设置", true);
        }
        if (!HasModifier(modifiers)) return (FormatOptionalHotkey(_hotkeyModifiers, _hotkeyKey), false);
        SaveHotkey(modifiers, key);
        return (FormatOptionalHotkey(_hotkeyModifiers, _hotkeyKey), _hotkeyRegistered);
    }

    /// <summary>Web 设置页保存/清除空闲快捷键：0/0 表示清除。</summary>
    internal (string Gesture, bool Registered) UpdateManualIdleHotkey(uint modifiers, uint key)
    {
        if (modifiers == 0 || key == 0)
        {
            SaveManualIdleHotkey(0, 0);
            return ("未设置", true);
        }
        if (!HasModifier(modifiers)) return (FormatOptionalHotkey(_manualIdleHotkeyModifiers, _manualIdleHotkeyKey), false);
        SaveManualIdleHotkey(modifiers, key);
        return (FormatOptionalHotkey(_manualIdleHotkeyModifiers, _manualIdleHotkeyKey), _manualIdleHotkeyRegistered);
    }

    /// <summary>Web 设置页保存/清除小窗快捷键：0/0 表示清除。</summary>
    internal (string Gesture, bool Registered) UpdateStatusWindowHotkey(uint modifiers, uint key)
    {
        if (modifiers == 0 || key == 0)
        {
            SaveStatusWindowHotkey(0, 0);
            return ("未设置", true);
        }
        if (!HasModifier(modifiers)) return (FormatOptionalHotkey(_statusWindowHotkeyModifiers, _statusWindowHotkeyKey), false);
        SaveStatusWindowHotkey(modifiers, key);
        return (FormatOptionalHotkey(_statusWindowHotkeyModifiers, _statusWindowHotkeyKey), _statusWindowHotkeyRegistered);
    }

    /// <summary>按下手动空闲热键后调用 /api/agent/idle，让 Native 进入手动空闲状态。</summary>
    private async Task TriggerManualIdleAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.PostAsync($"{_url.TrimEnd('/')}/api/agent/idle", null);
            if (!response.IsSuccessStatusCode)
            {
                _notifyIcon.ShowBalloonTip(2000, "时迹", "手动空闲触发失败", ToolTipIcon.Warning);
            }
        }
        catch
        {
            _notifyIcon.ShowBalloonTip(2000, "时迹", "手动空闲触发失败", ToolTipIcon.Warning);
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey)
        {
            var id = message.WParam.ToInt32();
            if (id == BrowserHotkeyId)
            {
                _ = OpenDashboardAsync();
            }
            else if (id == ManualIdleHotkeyId)
            {
                _ = TriggerManualIdleAsync();
            }
            else if (id == StatusWindowHotkeyId)
            {
                ToggleStatusWindow();
            }
        }
        base.WndProc(ref message);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterBrowserHotkey();
        UnregisterManualIdleHotkey();
        UnregisterStatusWindowHotkey();
        base.OnHandleDestroyed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>打开（或复用）网页看板窗口，并召唤到小窗所在的虚拟桌面。</summary>
    private async Task OpenDashboardAsync()
    {
        if ((DateTime.UtcNow - _lastBrowserLaunchAt).TotalMilliseconds < 900)
        {
            return;
        }

        _lastBrowserLaunchAt = DateTime.UtcNow;
        if (_dashboard is null || _dashboard.IsDisposed)
        {
            _dashboard = new DashboardForm(_url, OpenExternalBrowserAsync);
            // 看板关闭即销毁 WebView2 进程组；清空引用，下次打开时重新创建
            _dashboard.FormClosed += (_, _) => _dashboard = null;
        }
        _dashboard.ShowDashboard();
        VirtualDesktopHelper.BringToSameDesktop(_statusWindow?.Handle ?? IntPtr.Zero, _dashboard.Handle);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async Task OpenExternalBrowserAsync()
    {
        try
        {
            var presence = await GetBrowserPresenceAsync();
            if (presence && BrowserWindowHelper.TryActivateBrowserWindow()) return;
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private async Task<bool> GetBrowserPresenceAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            using var response = await client.GetAsync($"{_url}/api/browser-presence");
            if (!response.IsSuccessStatusCode) return false;
            var payload = await response.Content.ReadFromJsonAsync<BrowserPresenceResponse>();
            return payload?.Alive == true;
        }
        catch
        {
            return false;
        }
    }

    private async Task StartNativeInBackgroundAsync()
    {
        _ownedNativeProcessId = FindRunningNativeProcessId();
        await NativeControlClient.EnsureStartedAndGetStatusAsync();
    }

    private async Task ExitAsync()
    {
        if (_closingForExit) return;
        _closingForExit = true;
        await NativeControlClient.StopAsync();
        await WaitForNativeExitAsync();
        _notifyIcon.Visible = false;
        await _host.StopAsync();
        Close();
        Application.Exit();
    }

    private static int? FindRunningNativeProcessId()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            return Process.GetProcessesByName("时迹")
                .Where(process =>
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        return !string.IsNullOrWhiteSpace(path)
                            && path.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Select(process =>
                {
                    var id = (int?)process.Id;
                    process.Dispose();
                    return id;
                })
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task WaitForNativeExitAsync()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (_ownedNativeProcessId is null) return;
            try
            {
                using var process = Process.GetProcessById(_ownedNativeProcessId.Value);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Delay(100);
        }

        if (_ownedNativeProcessId is int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            _trayMenu?.CloseAnimated();
            _dashboard?.CloseDashboard();
            _statusWindow?.Close();
            _statusWindow?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record BrowserPresenceResponse(bool Alive);
}
