using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace UsageTrackerNative;

/// <summary>
/// 通过 UI Automation 读取 Chromium/Gecko 浏览器“当前选中标签页”的真实页面标题。
/// Win32 GetWindowText 在全屏（F11）、Edge 工作区等场景下会退化成工作区名（如 "LEARNING"），
/// 选中标签页（TabItem）的 Name 才是精准标题；全屏时标签栏虽不可见，TabItem 仍在 UIA 树中且带 IsSelected。
/// TabItem.Name 会附带“选项卡组/内存使用率/睡眠”等噪音片段，需逐段清洗。
/// 所有调用均带超时与异常保护，失败时由调用方回退到 Win32 标题。
/// </summary>
internal static partial class BrowserTabTitleReader
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi"
    };

    // 同一窗口的短缓存，避免每秒对整棵 UIA 树做一次搜索。
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);
    // UIA 瞬时失败（播放视频时 Chromium 无障碍提供方偶尔繁忙超时）时沿用上一次成功标题的宽限。
    // 仅在 Win32 标题也退化（F11/工作区）时才启用，且只给 3 秒：
    // 时间过长会把已切换走的旧标签标题安到新标签上，属于无中生有的错误记录。
    private static readonly TimeSpan StickyLifetime = TimeSpan.FromSeconds(3);
    private static readonly string[] BrowserSuffixes =
    {
        " - Microsoft Edge", " - Google Chrome", " - Mozilla Firefox", " - Brave", " - Opera", " - Vivaldi"
    };
    private static IntPtr _cachedHandle;
    private static string? _cachedTitle;
    private static DateTime _cachedAt;
    // 按窗口句柄 / 按进程两层粘性：F11 全屏时前台 HWND 可能在主窗口与全屏辅助窗口间切换。
    private static IntPtr _stickyHandle;
    private static string? _stickyHandleTitle;
    private static DateTime _stickyHandleAt;
    private static string? _stickyProcess;
    private static string? _stickyProcessTitle;
    private static DateTime _stickyProcessAt;
    private static string? _lastEmittedTitle;
    private static readonly object CacheGate = new();

    // UIA 在视频播放时可能完全冻结（Chromium 无障碍提供方阻塞），但复杂页面（淘宝等）需要足够时间遍历。
    // 1200ms 是实测平衡值：正常页面 200-500ms 完成，视频冻结时最多阻塞 1.2s 后回退。
    private const int QueryTimeoutMs = 1200;

    // UIA 查询必须在专用 STA 线程上执行：headless 宿主的 MTA 线程池线程上
    // System.Windows.Automation 会瞬间抛 COM 异常（startup.log 实测每秒必现、从未成功）。
    private static readonly BlockingCollection<Action> UiaQueue = new();
    private static Thread? _uiaThread;

    // 同一窗口连续失败时对 FAILED 日志节流，避免每秒刷一条拖累磁盘。
    private static IntPtr _lastFailedHandle;
    private static DateTime _lastFailedLogAt;

    public static bool IsSupported(string processName) => BrowserProcesses.Contains(processName);

    /// <summary>
    /// 读取当前选中标签页标题。rawWin32Title 必须传入未经改写的 GetWindowText 原文：
    /// 窗口化时它形如“页面标题 - Microsoft Edge”，剥掉后缀就是实时精准标题（切标签立即变化），
    /// 用它校验缓存/否决粘性；F11/工作区时它退化成工作区名（如 LEARNING），才允许 UIA 失败后短粘性兜底。
    /// </summary>
    public static bool TryGetSelectedTabTitle(IntPtr handle, string processName, string? rawWin32Title, out string title)
    {
        title = string.Empty;
        if (handle == IntPtr.Zero || !IsSupported(processName)) return false;

        var win32Reliable = TryStripBrowserSuffix(rawWin32Title, out var win32Title);

        lock (CacheGate)
        {
            if (_cachedHandle == handle && _cachedTitle is { Length: > 0 } && DateTime.Now - _cachedAt < CacheLifetime)
            {
                // Win32 可靠时，它一旦变化就说明用户已切换标签，缓存立即作废，防止沿用旧标签标题。
                if (!win32Reliable || string.Equals(_cachedTitle, win32Title, StringComparison.Ordinal))
                {
                    title = _cachedTitle;
                    return true;
                }
            }
        }

        // 标题来源只认 UIA 选中标签页；不再使用 SMTC 媒体标题兜底——
        // 媒体会话可能属于后台标签甚至别的应用（实测腾讯视频 WebView2 的 AUMID 曾被子串误判成 Edge），
        // 把媒体标题写进记录就是“无中生有”。
        string? resolved = null;
        try
        {
            var task = RunOnUiaThread(() => ResolveTitle(handle));
            if (task.Wait(QueryTimeoutMs) && task.IsCompletedSuccessfully)
            {
                resolved = task.Result;
            }
            else
            {
                NativeLogger.LogStartupMessage("BrowserTitle", $"UIA query timeout ({QueryTimeoutMs}ms) for {processName}");
            }
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            NativeLogger.LogStartupMessage("BrowserTitle",
                $"UIA query exception: {ex.GetType().Name} / {inner.GetType().Name}: {inner.Message}");
            resolved = null;
        }

        string? normalized = IsUsableTitle(resolved) ? SanitizeTabName(resolved!) : null;
        var source = "uia";

        if (!IsUsableTitle(normalized) && !win32Reliable)
        {
            // 仅 F11/工作区（Win32 不可信）时允许极短粘性；窗口化时直接回退真实 Win32 标题。
            normalized = GetStickyTitle(handle, processName);
            if (IsUsableTitle(normalized)) source = "sticky";
        }

        if (!IsUsableTitle(normalized))
        {
            if (handle != _lastFailedHandle || DateTime.Now - _lastFailedLogAt > TimeSpan.FromSeconds(30))
            {
                _lastFailedHandle = handle;
                _lastFailedLogAt = DateTime.Now;
                NativeLogger.LogStartupMessage("BrowserTitle", $"FAILED: proc={processName}, handle={handle}, win32Reliable={win32Reliable}, rawWin32='{rawWin32Title}', uiaResolved='{resolved}'");
            }
            return false;
        }

        lock (CacheGate)
        {
            _cachedHandle = handle;
            _cachedTitle = normalized;
            _cachedAt = DateTime.Now;
            _stickyHandle = handle;
            _stickyHandleTitle = normalized;
            _stickyHandleAt = DateTime.Now;
            _stickyProcess = NormalizeProcessName(processName);
            _stickyProcessTitle = normalized;
            _stickyProcessAt = DateTime.Now;
        }
        if (!string.Equals(_lastEmittedTitle, normalized, StringComparison.Ordinal))
        {
            _lastEmittedTitle = normalized;
            NativeLogger.LogStartupMessage("BrowserTitle", $"source={source}, proc={processName}, title='{normalized}'");
        }
        title = normalized;
        return true;
    }

    /// <summary>原始 Win32 标题带浏览器后缀时视为可靠（窗口化实时标题），输出剥后缀的页面标题。</summary>
    private static bool TryStripBrowserSuffix(string? raw, out string pageTitle)
    {
        pageTitle = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        foreach (var suffix in BrowserSuffixes)
        {
            if (raw.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var stripped = raw[..^suffix.Length].Trim();
                if (stripped.Length > 0)
                {
                    pageTitle = stripped;
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    private static string? GetStickyTitle(IntPtr handle, string processName)
    {
        lock (CacheGate)
        {
            if (_stickyHandle == handle && _stickyHandleTitle is { Length: > 0 } && DateTime.Now - _stickyHandleAt < StickyLifetime)
            {
                return _stickyHandleTitle;
            }
            var normalized = NormalizeProcessName(processName);
            if (string.Equals(_stickyProcess, normalized, StringComparison.OrdinalIgnoreCase)
                && _stickyProcessTitle is { Length: > 0 }
                && DateTime.Now - _stickyProcessAt < StickyLifetime)
            {
                return _stickyProcessTitle;
            }
        }
        return null;
    }

    private static string NormalizeProcessName(string processName)
    {
        var trimmed = processName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private static Task<T> RunOnUiaThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        UiaQueue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        EnsureUiaThread();
        return tcs.Task;
    }

    private static void EnsureUiaThread()
    {
        if (_uiaThread is { IsAlive: true }) return;
        lock (UiaQueue)
        {
            if (_uiaThread is { IsAlive: true }) return;
            _uiaThread = new Thread(UiaWorkerLoop)
            {
                IsBackground = true,
                Name = "UiaTitleWorker",
            };
            _uiaThread.SetApartmentState(ApartmentState.STA);
            _uiaThread.Start();
        }
    }

    private static void UiaWorkerLoop()
    {
        foreach (var work in UiaQueue.GetConsumingEnumerable())
        {
            try { work(); }
            catch { }
        }
    }

    private static string? ResolveTitle(IntPtr handle)
    {
        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(handle);
        }
        catch (Exception ex)
        {
            var sb = new StringBuilder(256);
            sb.Append($"FromHandle threw: {ex.GetType().Name}: {ex.Message}");
            for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
                sb.Append($" -> {e.GetType().Name}: {e.Message}");
            NativeLogger.LogStartupMessage("BrowserTitle", sb.ToString());
            return null;
        }
        if (root is null)
        {
            NativeLogger.LogStartupMessage("BrowserTitle", $"FromHandle returned null for {handle}");
            return null;
        }

        // 1) 首选：选中的 TabItem（F11 全屏标签栏隐藏时仍可找到），兼容个别版本暴露为 Tab。
        var selected = FindSelectedElement(root, ControlType.TabItem) ?? FindSelectedElement(root, ControlType.Tab);
        var candidate = selected?.Current.Name;

        // 2) 兜底：网页 Document。
        if (!IsUsableTitle(candidate))
        {
            candidate = FindDocumentTitle(root);
        }

        return IsUsableTitle(candidate) ? candidate : null;
    }

    private static AutomationElement? FindSelectedElement(AutomationElement root, ControlType controlType)
    {
        AutomationElementCollection elements;
        try
        {
            elements = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
        }
        catch (Exception ex)
        {
            NativeLogger.LogStartupMessage("BrowserTitle", $"FindAll {controlType} exception: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        foreach (AutomationElement element in elements)
        {
            try
            {
                if (element.GetCurrentPattern(SelectionItemPattern.Pattern) is not SelectionItemPattern selectionItem)
                {
                    continue;
                }
                if (selectionItem.Current.IsSelected)
                {
                    return element;
                }
            }
            catch
            {
                // 元素在遍历期间失效，跳过。
            }
        }
        return null;
    }

    private static string? FindDocumentTitle(AutomationElement root)
    {
        try
        {
            var document = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            var name = document?.Current.Name;
            return IsUsableTitle(name) ? name : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清洗 Edge/Chrome TabItem.Name 附带的噪音片段。</summary>
    private static string SanitizeTabName(string value)
    {
        var normalized = value.Trim().Trim('*').Trim();

        foreach (var suffix in BrowserSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length].Trim();
                break;
            }
        }

        // 形如 "标题 - 选项卡组 网络 的一部分 - 内存使用率 - 275 MB - 睡眠"，逐段剔除噪音后保留标题本体。
        var segments = normalized.Split(" - ");
        var kept = segments.Where(segment => !IsNoiseSegment(segment.Trim())).ToArray();
        return string.Join(" - ", kept).Trim('*').Trim();
    }

    private static bool IsNoiseSegment(string segment)
    {
        if (segment.Length == 0) return true;
        if (segment.Equals("睡眠", StringComparison.Ordinal) || segment is "休眠" or "后台" or "已固定") return true;
        if (segment.StartsWith("选项卡组", StringComparison.Ordinal) && segment.EndsWith("的一部分", StringComparison.Ordinal)) return true;
        if (segment.StartsWith("内存使用率", StringComparison.Ordinal) || segment.StartsWith("CPU 使用率", StringComparison.Ordinal)) return true;
        // Edge 媒体徽标：“视频/音频正在播放”“画中画”“后台播放”等短提示片段。
        if (segment.Length <= 12
            && (segment.Contains("正在播放", StringComparison.Ordinal)
                || (segment.Contains("播放", StringComparison.Ordinal)
                    && (segment.Contains("视频", StringComparison.Ordinal)
                        || segment.Contains("音频", StringComparison.Ordinal)
                        || segment.Contains("媒体", StringComparison.Ordinal)))
                || segment is "画中画" or "后台播放"))
        {
            return true;
        }
        if (MemorySizeRegex().IsMatch(segment)) return true;
        return false;
    }

    [GeneratedRegex(@"^\d+(\.\d+)?\s*(KB|MB|GB|TB)$", RegexOptions.IgnoreCase)]
    private static partial Regex MemorySizeRegex();

    private static bool IsUsableTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed.Length < 1 || trimmed.Length > 300) return false;
        if (LooksLikeProcessName(trimmed)) return false;
        return true;
    }

    private static bool LooksLikeProcessName(string value)
    {
        var lower = value.Trim();
        return BrowserProcesses.Contains(lower) || lower.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }
}
