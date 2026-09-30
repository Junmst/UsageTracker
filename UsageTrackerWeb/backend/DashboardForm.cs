using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace UsageTrackerWeb;

internal sealed class DashboardForm : Form
{
    private readonly string _url;
    private readonly Func<Task> _fallback;
    private readonly WebView2 _webView = new();
    private readonly Label _errorLabel = new();
    private bool _allowClose;
    private bool _initialized;

    public DashboardForm(string url, Func<Task> fallback)
    {
        _url = url;
        _fallback = fallback;
        Text = "时迹 · 网页看板";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1280, 820);
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
    }

    private async void DashboardForm_Shown(object? sender, EventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UsageTrackerNative",
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
        _webView.Focus();
    }

    public void CloseDashboard()
    {
        if (IsDisposed) return;
        _allowClose = true;
        Close();
    }

    private static Icon? TryLoadIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
    }
}
