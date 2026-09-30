using Windows.Media.Control;

namespace UsageTrackerNative;

/// <summary>
/// 基于 Windows 全局媒体会话（SMTC，即系统音量面板看到的播放控件）判断应用是否“真正在播放”。
/// 浏览器（Edge/Chrome/Firefox）的网页音视频、腾讯视频等播放器都会注册 SMTC 会话，
/// PlaybackStatus 能精确区分 Playing/Paused/Stopped——暂停视频时音频会话可能仍短暂存在，
/// 只有 SMTC 能可靠识别“暂停后不应再点亮呼吸灯”。
/// 查询全部异步执行、带超时、1 秒缓存；不可用时返回“未知”，由调用方回退到音频会话检测。
/// </summary>
internal static class GlobalMediaPlaybackMonitor
{
    private static readonly Dictionary<string, string[]> AppAumidKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge.exe"] = new[] { "msedge" },
        ["chrome.exe"] = new[] { "chrome" },
        ["firefox.exe"] = new[] { "firefox" },
        ["brave.exe"] = new[] { "brave" },
        ["opera.exe"] = new[] { "opera" },
        ["vivaldi.exe"] = new[] { "vivaldi" },
        ["QQLive.exe"] = new[] { "qqlive", "tencent" },
        ["PotPlayerMini64.exe"] = new[] { "potplayer" },
        ["PotPlayer.exe"] = new[] { "potplayer" },
        ["vlc.exe"] = new[] { "vlc" },
        ["mpv.exe"] = new[] { "mpv" },
        ["BiliBili.exe"] = new[] { "bilibili" },
    };

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);
    private static string? _cachedProcess;
    private static bool _cachedHasSession;
    private static bool _cachedPlaying;
    private static DateTime _cachedAt;
    private static readonly object CacheGate = new();
    private static GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static readonly object ManagerGate = new();

    private static string? _cachedTitle;

    /// <summary>
    /// 查询指定前台应用的媒体播放状态。
    /// 返回 hasSession=false 表示该应用没有注册 SMTC 会话（调用方可回退到音频检测）；
    /// hasSession=true 时 playing 即真实播放/暂停状态；mediaTitle 为系统媒体控件显示的标题（全屏播放时依然准确）。
    /// </summary>
    public static bool TryGetAppState(string processName, out bool hasSession, out bool playing, out string? mediaTitle)
    {
        hasSession = false;
        playing = false;
        mediaTitle = null;
        var normalized = NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (!AppAumidKeys.ContainsKey(normalized))
        {
            normalized = normalized + ".exe";
            if (!AppAumidKeys.ContainsKey(normalized)) return false;
        }

        lock (CacheGate)
        {
            if (string.Equals(_cachedProcess, normalized, StringComparison.OrdinalIgnoreCase)
                && DateTime.Now - _cachedAt < CacheLifetime)
            {
                hasSession = _cachedHasSession;
                playing = _cachedPlaying;
                mediaTitle = _cachedTitle;
                return true;
            }
        }

        var result = Query(normalized);
        if (result is null) return false;
        var query = result.Value;

        lock (CacheGate)
        {
            _cachedProcess = normalized;
            _cachedHasSession = query.hasSession;
            _cachedPlaying = query.playing;
            _cachedTitle = query.title;
            _cachedAt = DateTime.Now;
        }
        hasSession = query.hasSession;
        playing = query.playing;
        mediaTitle = query.title;
        return true;
    }

    public static bool TryGetAppPlaying(string processName, out bool hasSession, out bool playing)
        => TryGetAppState(processName, out hasSession, out playing, out _);

    private static (bool hasSession, bool playing, string? title)? Query(string processName)
    {
        try
        {
            var task = Task.Run(() =>
            {
                var manager = EnsureManager();
                if (manager is null) return (false, false, (string?)null);

                var keys = AppAumidKeys[processName];
                bool anyMatched = false;
                bool anyPlaying = false;
                string? title = null;

                // “当前媒体会话”（最近产生播放事件的标签页）优先，其次遍历该应用的全部会话。
                var current = manager.GetCurrentSession();
                if (current is not null && MatchesApp(current.SourceAppUserModelId, keys))
                {
                    anyMatched = true;
                    var currentPlaying = current.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    if (currentPlaying)
                    {
                        anyPlaying = true;
                        title = TryGetTitle(current);
                    }
                }

                foreach (var session in manager.GetSessions())
                {
                    if (!MatchesApp(session.SourceAppUserModelId, keys)) continue;
                    anyMatched = true;
                    if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        anyPlaying = true;
                        title ??= TryGetTitle(session);
                    }
                    else if (!anyPlaying && string.IsNullOrWhiteSpace(title))
                    {
                        // 暂停状态也保留媒体标题：全屏暂停时标签栏隐藏，仍可作为精准标题来源。
                        title = TryGetTitle(session);
                    }
                }

                return (anyMatched, anyPlaying, string.IsNullOrWhiteSpace(title) ? null : title.Trim());
            });
            return task.Wait(TimeSpan.FromMilliseconds(500)) && task.IsCompletedSuccessfully ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    private static GlobalSystemMediaTransportControlsSessionManager? EnsureManager()
    {
        lock (ManagerGate)
        {
            if (_manager is not null) return _manager;
            try
            {
                var request = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
                if (request.Wait(TimeSpan.FromSeconds(2)))
                {
                    _manager = request.Result;
                }
            }
            catch
            {
                _manager = null;
            }
            return _manager;
        }
    }

    private static string? TryGetTitle(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var request = session.TryGetMediaPropertiesAsync().AsTask();
            return request.Wait(TimeSpan.FromMilliseconds(400)) ? request.Result?.Title : null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeProcessName(string processName)
    {
        var trimmed = processName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private static bool MatchesApp(string? aumid, string[] keys)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        foreach (var key in keys)
        {
            // 词边界匹配，禁止子串误命中："msedge" 绝不能匹配 "msedgewebview2.exe"
            // （腾讯视频等 WebView2 宿主的 AUMID），否则别的应用播放会被记到浏览器头上。
            var searchFrom = 0;
            while (searchFrom <= aumid.Length - key.Length)
            {
                var index = aumid.IndexOf(key, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var beforeChar = index == 0 ? '.' : aumid[index - 1];
                var afterPosition = index + key.Length;
                var afterChar = afterPosition >= aumid.Length ? '.' : aumid[afterPosition];
                if (!IsWordChar(beforeChar) && !IsWordChar(afterChar)) return true;
                searchFrom = index + 1;
            }
        }
        return false;
    }

    private static bool IsWordChar(char value)
        => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
