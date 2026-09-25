using System.Diagnostics;
using System.Net.Http.Json;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsageTrackerWeb;

public sealed class TrayForm : Form
{
    private static readonly Color Accent = Color.FromArgb(85, 190, 168);
    private static readonly Color AccentHover = Color.FromArgb(67, 169, 148);
    private static readonly Color Background = Color.FromArgb(247, 249, 251);
    private static readonly Color Panel = Color.White;
    private static readonly Color Border = Color.FromArgb(226, 231, 236);
    private static readonly Color TextPrimary = Color.FromArgb(43, 51, 62);
    private static readonly Color TextSecondary = Color.FromArgb(112, 122, 134);
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "时迹Web";

    private readonly IHost _host;
    private readonly string _url;
    private readonly NotifyIcon _notifyIcon = new();
    private readonly System.Windows.Forms.Timer _trayClickTimer = new();
    private TrayMenuForm? _trayMenu;
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly Label _uptimeLabel = new();
    private readonly Label _nativeStatusLabel = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly ToggleSwitch _startupSwitch = new();
    private readonly Button _nativeButton = new();
    private bool _nativeVisible;
    private DateTime _lastBrowserLaunchAt = DateTime.MinValue;
    private bool _closingForExit;
    private int? _ownedNativeProcessId;

    public TrayForm(IHost host, string url, bool showWindow = false)
    {
        _host = host;
        _url = url;
        InitializeComponent();
        if (showWindow)
        {
            Show();
        }
    }

    private void InitializeComponent()
    {
        Text = "时迹";
        Size = new Size(760, 860);
        MinimumSize = new Size(760, 860);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ShowInTaskbar = true;
        MinimizeBox = true;
        MaximizeBox = false;
        MaximumSize = new Size(760, 860);
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
            BackColor = Background,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
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
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _nativeButton.Text = "打开时迹";
        StyleButton(_nativeButton, primary: true);
        _nativeButton.Margin = new Padding(0, 0, 7, 7);
        _nativeButton.Click += async (_, _) => await ToggleNativeAsync();
        actions.Controls.Add(_nativeButton, 0, 0);
        var webButton = new Button { Text = "打开网页看板" };
        StyleButton(webButton, primary: false);
        webButton.Margin = new Padding(7, 0, 0, 7);
        webButton.Click += async (_, _) => await OpenBrowserAsync();
        actions.Controls.Add(webButton, 1, 0);
        var hideButton = new Button { Text = "隐藏时迹窗口" };
        StyleButton(hideButton, primary: false);
        hideButton.Margin = new Padding(0, 7, 7, 0);
        hideButton.Click += async (_, _) => await SendNativeCommandAsync("hide", "已隐藏时迹窗口");
        actions.Controls.Add(hideButton, 0, 1);
        var compactButton = new Button { Text = "显示专注小窗" };
        StyleButton(compactButton, primary: false);
        compactButton.Margin = new Padding(7, 7, 0, 0);
        compactButton.Click += async (_, _) => await SendNativeCommandAsync("compact", "已切换到专注小窗");
        actions.Controls.Add(compactButton, 1, 1);
        root.Controls.Add(actions, 0, 2);

        var info = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            Padding = new Padding(14, 10, 14, 9),
            BackColor = Panel,
            Margin = new Padding(0, 0, 0, 12),
        };
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
        Shown += async (_, _) =>
        {
            await StartNativeInBackgroundAsync();
        };
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
            this,
            ShowLauncher,
            () => _ = OpenBrowserAsync(),
            () => _ = SendNativeCommandAsync("show", "时迹主窗口已打开"),
            () => _ = SendNativeCommandAsync("hide", "已隐藏时迹窗口"),
            () => _ = ExitAsync());
        _trayMenu.ShowFromTray(_notifyIcon);
    }

    private static void StyleButton(Button button, bool primary)
    {
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
        _nativeStatusLabel.Text = "正在启动桌面记录服务…";
        var ok = await NativeControlClient.EnsureStartedAndSendAsync("hide", processId => _ownedNativeProcessId = processId);
        _nativeVisible = false;
        _nativeStatusLabel.Text = ok ? "时迹已在后台运行，正在记录使用时长" : "时迹尚未启动，可点击打开时迹重试";
        UpdateNativeButton();
    }

    private async Task ToggleNativeAsync()
    {
        var command = _nativeVisible ? "hide" : "show";
        await SendNativeCommandAsync(command, _nativeVisible ? "已隐藏时迹窗口" : "时迹主窗口已打开");
    }

    private async Task SendNativeCommandAsync(string command, string successMessage)
    {
        _nativeButton.Enabled = false;
        _nativeStatusLabel.Text = "正在连接时迹…";
        var ok = await NativeControlClient.EnsureStartedAndSendAsync(command, processId => _ownedNativeProcessId = processId);
        _nativeButton.Enabled = true;
        if (!ok)
        {
            _nativeStatusLabel.Text = "未能启动时迹，请检查程序文件是否完整";
            return;
        }

        _nativeVisible = command is "show" or "compact";
        _nativeStatusLabel.Text = successMessage;
        UpdateNativeButton();
    }

    private async Task RefreshNativeStatusAsync()
    {
        var running = await NativeControlClient.SendAsync("status");
        _nativeVisible = false;
        _nativeStatusLabel.Text = running ? "时迹在后台运行，记录服务正常" : "时迹尚未启动，打开时会自动启动";
        UpdateNativeButton();
    }

    private void UpdateNativeButton()
    {
        _nativeButton.Text = _nativeVisible ? "隐藏时迹" : "打开时迹";
    }

    private void ShowLauncher()
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            Activate();
            return;
        }

        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task OpenBrowserAsync()
    {
        if ((DateTime.UtcNow - _lastBrowserLaunchAt).TotalMilliseconds < 900)
        {
            return;
        }

        var presence = await GetBrowserPresenceAsync();
        if (presence)
        {
            BrowserWindowHelper.TryActivateBrowserWindow();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            _lastBrowserLaunchAt = DateTime.UtcNow;
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
        await NativeControlClient.SendAsync("exit");
        await WaitForNativeExitAsync();
        _timer.Stop();
        _notifyIcon.Visible = false;
        await _host.StopAsync();
        Close();
        Application.Exit();
    }

    private static int? FindRunningNativeProcessId()
    {
        try
        {
            return Process.GetProcessesByName("时迹")
                .Where(process =>
                {
                    try
                    {
                        return process.MainModule?.FileName?.Contains("UsageTrackerWeb\\publish", StringComparison.OrdinalIgnoreCase) == true;
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
            _trayMenu?.CloseAnimated();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record BrowserPresenceResponse(bool Alive);

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
            using var brush = new SolidBrush(Color.FromArgb(225, 244, 240));
            using var border = new Pen(Color.FromArgb(187, 226, 218), 1);
            e.Graphics.FillEllipse(brush, circle);
            e.Graphics.DrawEllipse(border, circle);
            using var hand = new Pen(AccentHover, 2.2F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var center = new PointF(Width / 2F, Height / 2F);
            e.Graphics.DrawLine(hand, center, new PointF(center.X, center.Y - 10));
            e.Graphics.DrawLine(hand, center, new PointF(center.X + 8, center.Y + 5));
            using var dot = new SolidBrush(AccentHover);
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
            using var trackBrush = new SolidBrush(Checked ? Accent : Color.FromArgb(209, 216, 222));
            e.Graphics.FillPath(trackBrush, path);
            var x = Checked ? track.Right - ThumbSize - 2 : track.Left + 2;
            var thumb = new Rectangle(x, track.Top + 2, ThumbSize, ThumbSize);
            using var thumbBrush = new SolidBrush(Color.White);
            e.Graphics.FillEllipse(thumbBrush, thumb);
        }
    }
}
