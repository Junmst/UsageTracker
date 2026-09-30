using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace UsageTrackerWeb;

internal sealed class DashboardForm : Form
{
    private readonly string _url;
    private readonly Func<Task> _fallback;
    private readonly WebView2 _webView = new();
    private readonly Label _errorLabel = new();
    private readonly SettingsReader _settingsReader = new(string.Empty);
    private readonly System.Windows.Forms.Timer _themeDebounce = new() { Interval = 300 };
    private FileSystemWatcher? _settingsWatcher;
    private bool _allowClose;
    private bool _initialized;

    public DashboardForm(string url, Func<Task> fallback)
    {
        _url = url;
        _fallback = fallback;
        Text = "时迹 · 网页看板";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        // 默认窗口长宽各为 2560×1640 的 2/3（约 1707×1093），超出屏幕时按工作区收敛
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1707, 1093);
        var targetWidth = Math.Clamp(1707, 960, workArea.Width - 40);
        var targetHeight = Math.Clamp(1093, 640, workArea.Height - 40);
        Size = new Size(targetWidth, targetHeight);
        ShowInTaskbar = true;
        Icon = TryLoadIcon();
        _webView.Dock = DockStyle.Fill;
        _errorLabel.Dock = DockStyle.Fill;
        _errorLabel.Text = "正在加载网页看板…";
        _errorLabel.TextAlign = ContentAlignment.MiddleCenter;
        _errorLabel.ForeColor = Color.DimGray;
        _errorLabel.Visible = true;
        Controls.Add(_webView);
        Controls.Add(_errorLabel);
        Shown += DashboardForm_Shown;
        FormClosing += DashboardForm_FormClosing;
        // 窗口标题栏/加载底色跟随 Web 深浅色：设置保存到 settings.json，监听其变化即时切换
        _themeDebounce.Tick += (_, _) =>
        {
            _themeDebounce.Stop();
            ApplyTitleBarTheme();
        };
        NativeTheme.Changed += (_, _) => ApplyTitleBarTheme();
        StartSettingsWatcher();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 看板固定到所有虚拟桌面，与小窗始终同屏
        VirtualDesktopHelper.PinToAllDesktops(Handle);
        ApplyTitleBarTheme();
    }

    private void StartSettingsWatcher()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "时迹");
            _settingsWatcher = new FileSystemWatcher(dir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _settingsWatcher.Changed += (_, _) => ScheduleThemeApply();
            _settingsWatcher.Created += (_, _) => ScheduleThemeApply();
            _settingsWatcher.Renamed += (_, _) => ScheduleThemeApply();
        }
        catch
        {
            // 监听失败不影响窗口本身，仅在下次打开时应用主题
        }
    }

    private void ScheduleThemeApply()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _themeDebounce.Stop();
                _themeDebounce.Start();
            });
        }
        catch
        {
            // 窗口已销毁
        }
    }

    /// <summary>Web 端主题（dark/light/system）对应的有效深浅色：system 时跟随 Windows 系统主题。</summary>
    private bool IsWebDarkMode()
    {
        var theme = _settingsReader.Load().Theme;
        if (string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)) return false;
        return NativeTheme.Current.IsDark;
    }

    private void ApplyTitleBarTheme()
    {
        if (IsDisposed || !IsHandleCreated) return;
        var dark = IsWebDarkMode();
        var useDark = dark ? 1 : 0;
        // 深色标题栏（Win10 1809+；19 为旧版属性号兜底）
        if (DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkModeLegacy, ref useDark, sizeof(int));
        }
        // WebView2 与加载提示的底色也跟随，避免深色模式下白闪
        _webView.DefaultBackgroundColor = dark ? Color.Black : Color.White;
        _errorLabel.BackColor = dark ? Color.Black : SystemColors.Control;
        _errorLabel.ForeColor = dark ? Color.Gainsboro : Color.DimGray;
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    private async void DashboardForm_Shown(object? sender, EventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "时迹",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    _errorLabel.Visible = false;
                    _webView.Visible = true;
                }
                else
                {
                    ShowError($"网页加载失败（{args.WebErrorStatus}），将打开系统浏览器…");
                    _ = _fallback();
                }
            };
            _webView.Visible = true;
            _webView.Source = new Uri(_url);
        }
        catch (Exception exception)
        {
            ShowError($"内嵌看板初始化失败：{exception.Message}");
            await _fallback();
        }
    }

    private void DashboardForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void ShowError(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowError(message));
            return;
        }
        _errorLabel.Text = message;
        _errorLabel.Visible = true;
        _webView.Visible = false;
    }

    public void ShowDashboard()
    {
        if (IsDisposed) return;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        ApplyTitleBarTheme();
        _webView.Focus();
    }

    public void CloseDashboard()
    {
        if (IsDisposed) return;
        _allowClose = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _themeDebounce.Stop();
            _themeDebounce.Dispose();
            if (_settingsWatcher is not null)
            {
                _settingsWatcher.EnableRaisingEvents = false;
                _settingsWatcher.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    private static Icon? TryLoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
    }
}
