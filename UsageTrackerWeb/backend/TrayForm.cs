using System.Diagnostics;
using System.Net.Http.Json;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsageTrackerWeb;

public sealed class TrayForm : Form
{
    private static Color Accent => NativeTheme.Current.Accent;
    private static Color AccentHover => NativeTheme.Current.AccentHover;
    private static Color Background => NativeTheme.Current.Background;
    private static Color Panel => NativeTheme.Current.Panel;
    private static Color Border => NativeTheme.Current.Border;
    private static Color TextPrimary => NativeTheme.Current.TextPrimary;
    private static Color TextSecondary => NativeTheme.Current.TextSecondary;
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "时迹Web";
    private const int BrowserHotkeyId = 0x5742;
    private const int ManualIdleHotkeyId = 0x5743;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const int WmHotkey = 0x0312;

    private readonly IHost _host;
    private readonly string _url;
    private readonly WebPreferencesStore _preferences;
    private readonly HotkeyTextBox _hotkeyBox = new();
    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private bool _hotkeyRegistered;
    private readonly HotkeyTextBox _manualIdleHotkeyBox = new();
    private uint _manualIdleHotkeyModifiers;
    private uint _manualIdleHotkeyKey;
    private bool _manualIdleHotkeyRegistered;
    private readonly NotifyIcon _notifyIcon = new();
    private readonly System.Windows.Forms.Timer _trayClickTimer = new();
    private TrayMenuForm? _trayMenu;
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly Label _uptimeLabel = new();
    private readonly Label _nativeStatusLabel = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly ToggleSwitch _startupSwitch = new();
    private DateTime _lastBrowserLaunchAt = DateTime.MinValue;
    private bool _closingForExit;
    private bool _launcherBoundsApplied;
    /// <summary>是否允许启动器窗口可见。Application.Run(Form) 会自动 Show()，
    /// 用此标志在启动阶段拦截，实现隐式启动（默认只显示小窗+托盘）。
    /// --show 参数或用户从托盘菜单"打开启动器"时才置为 true。</summary>
    private bool _launcherVisible;
    private DashboardForm? _dashboard;
    private CompactStatusForm? _statusWindow;
    private int? _ownedNativeProcessId;

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int valueSize);

    public TrayForm(IHost host, string url, bool showWindow = false, WebPreferencesStore? preferences = null)
    {
        _host = host;
        _url = url;
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        InitializeComponent();
        NativeTheme.Changed += NativeTheme_Changed;
        ApplyNativeTheme();
        LoadHotkey();
        RegisterBrowserHotkey();
        LoadManualIdleHotkey();
        RegisterManualIdleHotkey();
        if (showWindow)
        {
            _launcherVisible = true;
            Show();
            _ = OpenBrowserAsync();
        }
        // 无论启动器是否显示，消息循环启动后必定拉起 Native 后台记录程序。
        // 之前绑在 Shown 事件上，隐式启动时 Shown 不触发会导致 Native 永不启动。
        BeginInvoke(async () => await StartNativeInBackgroundAsync());
    }

    /// <summary>Application.Run(this) 会自动调用 Show() 让窗口可见。
    /// 启动阶段（隐式启动）时 _launcherVisible=false，拦截 Show 避免启动器弹出。
    /// 注意：不能依赖 IsHandleCreated 判断，因为构造函数里 RegisterHotKey 已创建句柄。</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (value && !_launcherVisible)
        {
            value = false;
        }
        base.SetVisibleCore(value);
    }

    private void InitializeComponent()
    {
        Text = "时迹";
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1421, 888);
        var currentWidth = Math.Min(980, Math.Max(860, workArea.Width - 40));
        var launcherWidth = Math.Max(645, currentWidth * 3 / 4);
        var launcherHeight = Math.Min(960, Math.Max(820, workArea.Height - 48));
        Size = new Size(launcherWidth, launcherHeight);
        MinimumSize = new Size(Math.Min(760, launcherWidth), Math.Min(760, launcherHeight));
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ShowInTaskbar = true;
        MinimizeBox = true;
        MaximizeBox = false;
        MaximumSize = new Size(launcherWidth, launcherHeight);
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = false;
        DoubleBuffered = true;
        BackColor = Background;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        Padding = new Padding(0);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
        Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22, 20, 22, 18),
            ColumnCount = 1,
            RowCount = 5,
            AutoScroll = true,
            BackColor = Background,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 420));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 56,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 2),
            BackColor = Background,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var mark = new IconMark { Size = new Size(42, 42), Margin = new Padding(0, 2, 12, 0) };
        header.Controls.Add(mark, 0, 0);
        var heading = new Label
        {
            Text = "时迹",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
            ForeColor = TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 2),
            BackColor = Background,
        };
        header.Controls.Add(heading, 1, 0);
        root.Controls.Add(header, 0, 0);

        var statePanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 100,
            BackColor = Panel,
            Padding = new Padding(20, 16, 20, 14),
            Margin = new Padding(0, 0, 0, 18),
        };
        statePanel.Paint += (_, e) => DrawPanelBorder(e.Graphics, statePanel.ClientRectangle);
        var stateDot = new StatusDot { Location = new Point(20, 22), Size = new Size(12, 12), Color = Accent };
        statePanel.Controls.Add(stateDot);
        statePanel.Controls.Add(new Label
        {
            Text = "桌面记录",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = TextPrimary,
            Location = new Point(42, 16),
        });
        _nativeStatusLabel.Text = "正在检查时迹状态…";
        _nativeStatusLabel.AutoSize = true;
        _nativeStatusLabel.ForeColor = TextSecondary;
        _nativeStatusLabel.Location = new Point(42, 52);
        statePanel.Controls.Add(_nativeStatusLabel);
        root.Controls.Add(statePanel, 0, 1);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Background,
            Margin = new Padding(0, 0, 0, 14),
            Padding = new Padding(24, 18, 24, 18),
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var webButton = new Button { Text = "打开网页看板" };
        StyleButton(webButton, primary: true);
        webButton.Margin = new Padding(0, 0, 7, 7);
        webButton.Click += async (_, _) => await OpenBrowserAsync();
        actions.Controls.Add(webButton, 0, 0);
        var statusButton = new Button { Text = "刷新后台状态" };
        StyleButton(statusButton, primary: false);
        statusButton.Margin = new Padding(7, 0, 0, 7);
        statusButton.Click += async (_, _) => await RefreshNativeStatusAsync();
        actions.Controls.Add(statusButton, 1, 0);
        var hideButton = new Button { Text = "隐藏启动器" };
        StyleButton(hideButton, primary: false);
        hideButton.Margin = new Padding(0, 7, 7, 0);
        hideButton.Click += (_, _) => Hide();
        actions.Controls.Add(hideButton, 0, 1);
        var statusWindowButton = new Button { Text = "打开状态小窗" };
        StyleButton(statusWindowButton, primary: false);
        statusWindowButton.Margin = new Padding(7, 7, 0, 0);
        statusWindowButton.Click += (_, _) => ShowStatusWindow();
        actions.Controls.Add(statusWindowButton, 1, 1);
        root.Controls.Add(actions, 0, 2);

        var info = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 5,
            AutoSize = true,
            Padding = new Padding(14, 10, 14, 9),
            BackColor = Panel,
            Margin = new Padding(0, 0, 0, 12),
        };
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.Paint += (_, e) => DrawPanelBorder(e.Graphics, info.ClientRectangle);
        var dbLabel = new Label
        {
            Text = $"数据位置  {GetDatabasePath()}",
            Dock = DockStyle.Top,
            AutoEllipsis = true,
            Height = 23,
            ForeColor = TextSecondary,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            Margin = new Padding(0, 0, 0, 4),
        };
        info.Controls.Add(dbLabel, 0, 0);
        _uptimeLabel.Text = "启动器运行  00:00:00";
        _uptimeLabel.AutoSize = true;
        _uptimeLabel.ForeColor = TextSecondary;
        _uptimeLabel.Font = new Font("Microsoft YaHei UI", 8.5F);
        _uptimeLabel.Margin = new Padding(0, 1, 0, 4);
        info.Controls.Add(_uptimeLabel, 0, 1);
        var urlLabel = new LinkLabel
        {
            Text = _url,
            AutoSize = true,
            LinkColor = AccentHover,
            ActiveLinkColor = AccentHover,
            VisitedLinkColor = AccentHover,
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold),
            Margin = new Padding(0),
        };
        urlLabel.LinkClicked += async (_, _) => await OpenBrowserAsync();
        info.Controls.Add(urlLabel, 0, 2);
        var hotkeyLabel = new Label
        {
            Text = "网页快捷键",
            AutoSize = false,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(132, 28),
            ForeColor = TextSecondary,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 8, 0),
            AutoEllipsis = false,
        };
        _hotkeyBox.Dock = DockStyle.Left;
        _hotkeyBox.Width = 110;
        _hotkeyBox.Height = 28;
        _hotkeyBox.Margin = new Padding(30, 2, 0, 0);
        _hotkeyBox.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
        _hotkeyBox.HotkeyCaptured += (_, hotkey) => SaveHotkey(hotkey.Modifiers, hotkey.Key);
        var hotkeyRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0),
        };
        hotkeyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        hotkeyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hotkeyRow.Controls.Add(hotkeyLabel, 0, 0);
        hotkeyRow.Controls.Add(_hotkeyBox, 1, 0);
        info.Controls.Add(hotkeyRow, 0, 3);

        // 手动空闲快捷键自定义槽：默认显示“未设置”，用户按下任意修饰键+字母/数字/F键即注册为全局热键。
        var manualIdleHotkeyLabel = new Label
        {
            Text = "空闲快捷键",
            AutoSize = false,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(132, 28),
            ForeColor = TextSecondary,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 2, 8, 0),
            AutoEllipsis = false,
        };
        _manualIdleHotkeyBox.Dock = DockStyle.Left;
        _manualIdleHotkeyBox.Width = 110;
        _manualIdleHotkeyBox.Height = 28;
        _manualIdleHotkeyBox.Margin = new Padding(30, 2, 0, 0);
        _manualIdleHotkeyBox.Text = FormatManualIdleHotkey(_manualIdleHotkeyModifiers, _manualIdleHotkeyKey);
        _manualIdleHotkeyBox.HotkeyCaptured += (_, hotkey) => SaveManualIdleHotkey(hotkey.Modifiers, hotkey.Key);
        var manualIdleHotkeyRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0),
        };
        manualIdleHotkeyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        manualIdleHotkeyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        manualIdleHotkeyRow.Controls.Add(manualIdleHotkeyLabel, 0, 0);
        manualIdleHotkeyRow.Controls.Add(_manualIdleHotkeyBox, 1, 0);
        info.Controls.Add(manualIdleHotkeyRow, 0, 4);
        root.Controls.Add(info, 0, 3);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            Height = 40,
            BackColor = Background,
            Margin = new Padding(0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var startupPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Background,
            Margin = new Padding(0),
        };
        MigrateNativeStartupEntry();
        _startupSwitch.Checked = IsStartupEnabled();
        _startupSwitch.CheckedChanged += (_, _) => SetStartupEnabled(_startupSwitch.Checked);
        startupPanel.Controls.Add(_startupSwitch);
        startupPanel.Controls.Add(new Label
        {
            Text = "登录后启动",
            AutoSize = true,
            ForeColor = TextSecondary,
            Font = new Font("Microsoft YaHei UI", 9F),
            Margin = new Padding(8, 4, 0, 0),
        });
        footer.Controls.Add(startupPanel, 0, 0);
        var quitButton = new Button { Text = "退出" };
        StyleButton(quitButton, primary: false);
        quitButton.Size = new Size(80, 34);
        quitButton.Margin = new Padding(8, 0, 0, 0);
        quitButton.Click += async (_, _) => await ExitAsync();
        footer.Controls.Add(quitButton, 1, 0);
        root.Controls.Add(footer, 0, 4);

        _notifyIcon.Text = "时迹 · 网页控制台";
        _notifyIcon.Icon = Icon;
        _notifyIcon.Visible = true;
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        _trayClickTimer.Interval = 260;
        _trayClickTimer.Tick += (_, _) =>
        {
            _trayClickTimer.Stop();
            _ = OpenBrowserAsync();
        };

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && !_closingForExit)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon.Visible = true;
                _notifyIcon.ShowBalloonTip(1800, "时迹", "启动器已隐藏到托盘，双击图标重新打开", ToolTipIcon.Info);
            }
        };
        _timer.Interval = 1000;
        _timer.Tick += (_, _) => UpdateUptime();
        _timer.Start();
        UpdateUptime();
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _trayClickTimer.Stop();
        _trayClickTimer.Start();
    }

    private void NotifyIcon_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _trayClickTimer.Stop();
        ShowLauncher();
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
            ShowLauncher,
            () => _ = OpenBrowserAsync(),
            () => _ = ExitAsync());
        _trayMenu.ShowFromTray();
    }

    private void NativeTheme_Changed(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(ApplyNativeTheme);
            return;
        }
        ApplyNativeTheme();
    }

    private void ApplyNativeTheme()
    {
        var palette = NativeTheme.Current;
        BackColor = palette.Background;
        ApplyNativeTheme(this, palette);
        ApplyDarkTitleBar(palette.IsDark);
        Invalidate(true);
    }

    private static void ApplyNativeTheme(Control control, NativeThemePalette palette)
    {
        if (control is Button button)
        {
            var primary = Equals(button.Tag, "primary");
            button.BackColor = primary ? palette.Accent : palette.Panel;
            button.ForeColor = primary ? (palette.IsDark ? Color.Black : Color.White) : palette.TextPrimary;
            button.FlatAppearance.BorderColor = palette.Border;
            button.FlatAppearance.MouseOverBackColor = primary ? palette.AccentHover : Color.FromArgb(
                Math.Min(255, palette.Panel.R + 10),
                Math.Min(255, palette.Panel.G + 10),
                Math.Min(255, palette.Panel.B + 10));
            button.FlatAppearance.MouseDownBackColor = primary ? palette.AccentHover : palette.AccentSoft;
        }
        else if (control is LinkLabel link)
        {
            link.LinkColor = palette.AccentHover;
            link.ActiveLinkColor = palette.AccentHover;
            link.VisitedLinkColor = palette.AccentHover;
            link.ForeColor = palette.AccentHover;
        }
        else if (control is HotkeyTextBox hotkeyBox)
        {
            hotkeyBox.BackColor = palette.Panel;
            hotkeyBox.ForeColor = palette.TextPrimary;
        }
        else if (control is Label label)
        {
            label.ForeColor = label.Text is "时迹" or "桌面记录" ? palette.TextPrimary : palette.TextSecondary;
            var parentIsPanel = label.Parent is Panel || label.Parent is TableLayoutPanel;
            label.BackColor = parentIsPanel ? palette.Panel : palette.Background;
        }
        else if (control is ToggleSwitch toggle)
        {
            toggle.BackColor = palette.Background;
            toggle.Invalidate();
        }
        else if (control is StatusDot statusDot)
        {
            statusDot.Color = palette.Accent;
            statusDot.Invalidate();
        }

        if (control is Form or TableLayoutPanel or FlowLayoutPanel)
        {
            control.BackColor = palette.Background;
        }
        else if (control is Panel panel)
        {
            panel.BackColor = palette.Panel;
        }

        foreach (Control child in control.Controls)
        {
            ApplyNativeTheme(child, palette);
        }
    }

    private void ApplyDarkTitleBar(bool dark)
    {
        if (!OperatingSystem.IsWindows() || !IsHandleCreated) return;
        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(Handle, 20, ref value, sizeof(int));
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.Tag = primary ? "primary" : "secondary";
        button.Dock = DockStyle.Fill;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = primary ? Accent : Panel;
        button.ForeColor = primary ? Color.White : TextPrimary;
        button.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Color.FromArgb(244, 248, 248);
        button.FlatAppearance.MouseDownBackColor = primary ? AccentHover : Color.FromArgb(236, 243, 242);
    }

    private static void DrawPanelBorder(Graphics graphics, Rectangle bounds)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = GetRoundedRect(Rectangle.Inflate(bounds, -1, -1), 12);
        using var pen = new Pen(Border, 1);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath GetRoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string GetDatabasePath()
    {
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageTrackerNative");
        var path = Path.Combine(dataDirectory, "usage-tracker.db");
        return File.Exists(path) ? path : $"{dataDirectory}（未找到数据库）";
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
            var value = key?.GetValue(StartupValueName) as string;
            return !string.IsNullOrWhiteSpace(value)
                && value.Contains("时迹Web.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void MigrateNativeStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            key?.DeleteValue("时迹", false);
        }
        catch
        {
        }
    }

    private static void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryRunKey);
            if (key is null) return;
            if (enabled)
            {
                key.SetValue(StartupValueName, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }
        }
        catch
        {
        }
    }

    private void UpdateUptime()
    {
        var elapsed = DateTime.Now - _startedAt;
        _uptimeLabel.Text = $"启动器运行  {elapsed:hh\\:mm\\:ss}";
    }

    private async Task StartNativeInBackgroundAsync()
    {
        _ownedNativeProcessId = FindRunningNativeProcessId();
        _nativeStatusLabel.Text = "正在启动后台记录服务…";
        var response = await NativeControlClient.EnsureStartedAndGetStatusAsync();
        _nativeStatusLabel.Text = response?.Ok == true
            ? "后台记录服务正在运行，Web 是唯一用户界面"
            : "后台记录服务未响应，请检查发布文件";
    }

    private async Task RefreshNativeStatusAsync()
    {
        var response = await NativeControlClient.GetStatusAsync();
        _nativeStatusLabel.Text = response?.Status is { Running: true } status
            ? status.Tracking ? "后台记录服务正在记录，Web 是唯一用户界面" : "后台记录服务已启动但当前未记录"
            : "后台记录服务未运行";
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyNativeTheme();
        if (_launcherBoundsApplied) return;
        _launcherBoundsApplied = true;
        var workArea = Screen.FromControl(this).WorkingArea;
        var currentWidth = Math.Min(980, Math.Max(860, workArea.Width - 40));
        var width = Math.Max(645, currentWidth * 3 / 4);
        var height = Math.Min(960, Math.Max(820, workArea.Height - 48));
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, width, height, SwpNoMove | SwpNoZOrder | SwpNoActivate);
    }

    internal void ShowStatusWindowFromStartup()
    {
        ShowStatusWindow();
    }

    private void ShowStatusWindow()
    {
        if (_statusWindow is null || _statusWindow.IsDisposed)
        {
            _statusWindow = new CompactStatusForm(ShowLauncher);
        }
        _statusWindow.ShowStatus();
    }

    private void ShowLauncher()
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            Activate();
            return;
        }

        _launcherVisible = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void LoadHotkey()
    {
        var hotkey = _preferences.Load().BrowserHotkey;
        if (hotkey is null || hotkey.Modifiers == 0 || hotkey.Key == 0)
        {
            _hotkeyModifiers = ModControl | ModAlt;
            _hotkeyKey = (uint)Keys.W;
        }
        else
        {
            _hotkeyModifiers = hotkey.Modifiers;
            _hotkeyKey = hotkey.Key;
        }

        _hotkeyBox.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
    }

    private void SaveHotkey(uint modifiers, uint key)
    {
        if ((modifiers & (ModControl | ModAlt | ModShift | ModWin)) == 0) return;
        UnregisterBrowserHotkey();
        _hotkeyModifiers = modifiers;
        _hotkeyKey = key;
        _preferences.SaveHotkey(modifiers, key, FormatHotkey(modifiers, key));
        RegisterBrowserHotkey();
        _hotkeyBox.Text = FormatHotkey(modifiers, key);
    }

    private void RegisterBrowserHotkey()
    {
        _hotkeyRegistered = RegisterHotKey(Handle, BrowserHotkeyId, _hotkeyModifiers, _hotkeyKey);
        if (!_hotkeyRegistered)
        {
            _hotkeyBox.Text = $"{FormatHotkey(_hotkeyModifiers, _hotkeyKey)}（不可用）";
        }
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

    // 手动空闲快捷键：用户未设置时不注册全局热键，输入框显示“未设置”。
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
        _manualIdleHotkeyBox.Text = FormatManualIdleHotkey(_manualIdleHotkeyModifiers, _manualIdleHotkeyKey);
    }

    private void SaveManualIdleHotkey(uint modifiers, uint key)
    {
        if ((modifiers & (ModControl | ModAlt | ModShift | ModWin)) == 0) return;
        UnregisterManualIdleHotkey();
        _manualIdleHotkeyModifiers = modifiers;
        _manualIdleHotkeyKey = key;
        _preferences.SaveManualIdleHotkey(modifiers, key, FormatManualIdleHotkey(modifiers, key));
        RegisterManualIdleHotkey();
        _manualIdleHotkeyBox.Text = FormatManualIdleHotkey(modifiers, key);
    }

    private void RegisterManualIdleHotkey()
    {
        if (_manualIdleHotkeyModifiers == 0 || _manualIdleHotkeyKey == 0) return;
        _manualIdleHotkeyRegistered = RegisterHotKey(Handle, ManualIdleHotkeyId, _manualIdleHotkeyModifiers, _manualIdleHotkeyKey);
        if (!_manualIdleHotkeyRegistered)
        {
            _manualIdleHotkeyBox.Text = $"{FormatManualIdleHotkey(_manualIdleHotkeyModifiers, _manualIdleHotkeyKey)}（不可用）";
        }
    }

    private void UnregisterManualIdleHotkey()
    {
        if (!_manualIdleHotkeyRegistered) return;
        UnregisterHotKey(Handle, ManualIdleHotkeyId);
        _manualIdleHotkeyRegistered = false;
    }

    private static string FormatManualIdleHotkey(uint modifiers, uint key)
    {
        if (modifiers == 0 || key == 0) return "未设置";
        return FormatHotkey(modifiers, key);
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
                _ = OpenBrowserAsync();
            }
            else if (id == ManualIdleHotkeyId)
            {
                _ = TriggerManualIdleAsync();
            }
        }
        base.WndProc(ref message);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterBrowserHotkey();
        UnregisterManualIdleHotkey();
        base.OnHandleDestroyed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private async Task OpenBrowserAsync()
    {
        if ((DateTime.UtcNow - _lastBrowserLaunchAt).TotalMilliseconds < 900)
        {
            return;
        }

        _lastBrowserLaunchAt = DateTime.UtcNow;
        _dashboard ??= new DashboardForm(_url, OpenExternalBrowserAsync);
        _dashboard.ShowDashboard();
        await Task.CompletedTask;
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

    private async Task ExitAsync()
    {
        if (_closingForExit) return;
        _closingForExit = true;
        await NativeControlClient.StopAsync();
        await WaitForNativeExitAsync();
        _timer.Stop();
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
            _timer.Stop();
            _timer.Dispose();
            _trayClickTimer.Stop();
            _trayClickTimer.Dispose();
            NativeTheme.Changed -= NativeTheme_Changed;
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

    private sealed class HotkeyTextBox : TextBox
    {
        public event EventHandler<HotkeyGesture>? HotkeyCaptured;

        public HotkeyTextBox()
        {
            ReadOnly = true;
            TabStop = true;
            BackColor = Panel;
            ForeColor = TextPrimary;
            BorderStyle = BorderStyle.FixedSingle;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            TextAlign = HorizontalAlignment.Center;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
            {
                return;
            }

            var modifiers = 0u;
            if (e.Control) modifiers |= ModControl;
            if (e.Alt) modifiers |= ModAlt;
            if (e.Shift) modifiers |= ModShift;
            if (modifiers == 0) return;
            HotkeyCaptured?.Invoke(this, new HotkeyGesture(modifiers, (uint)e.KeyCode));
        }
    }

    private sealed record HotkeyGesture(uint Modifiers, uint Key);

    private sealed class IconMark : Control
    {
        public IconMark()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var circle = new Rectangle(4, 4, Width - 9, Height - 9);
            var palette = NativeTheme.Current;
            using var brush = new SolidBrush(palette.AccentSoft);
            using var border = new Pen(palette.Border, 1);
            e.Graphics.FillEllipse(brush, circle);
            e.Graphics.DrawEllipse(border, circle);
            using var hand = new Pen(palette.AccentHover, 2.2F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var center = new PointF(Width / 2F, Height / 2F);
            e.Graphics.DrawLine(hand, center, new PointF(center.X, center.Y - 10));
            e.Graphics.DrawLine(hand, center, new PointF(center.X + 8, center.Y + 5));
            using var dot = new SolidBrush(NativeTheme.Current.AccentHover);
            e.Graphics.FillEllipse(dot, center.X - 2, center.Y - 2, 4, 4);
        }
    }

    private sealed class StatusDot : Control
    {
        public Color Color { get; set; } = Accent;
        public StatusDot()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = System.Drawing.Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            using var brush = new SolidBrush(Color);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(brush, 1, 1, Width - 2, Height - 2);
        }
    }

    private sealed class ToggleSwitch : CheckBox
    {
        private const int TrackWidth = 40;
        private const int TrackHeight = 21;
        private const int ThumbSize = 17;
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
            Size = new Size(TrackWidth + 2, TrackHeight + 2);
            BackColor = Background;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? Background);
            var track = new Rectangle((Width - TrackWidth) / 2, (Height - TrackHeight) / 2, TrackWidth, TrackHeight);
            using var path = GetRoundedRect(track, TrackHeight / 2);
            var palette = NativeTheme.Current;
            using var trackBrush = new SolidBrush(Checked ? palette.Accent : palette.Border);
            e.Graphics.FillPath(trackBrush, path);
            var x = Checked ? track.Right - ThumbSize - 2 : track.Left + 2;
            var thumb = new Rectangle(x, track.Top + 2, ThumbSize, ThumbSize);
            using var thumbBrush = new SolidBrush(Color.White);
            e.Graphics.FillEllipse(thumbBrush, thumb);
        }
    }
}
