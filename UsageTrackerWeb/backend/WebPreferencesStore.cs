using System.Text.Json;

namespace UsageTrackerWeb;

public sealed class WebPreferencesStore
{
    private readonly string _path;
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WebPreferencesStore(string dataDirectory)
    {
        // 配置存固定用户目录，不随数据目录迁移而丢失
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "时迹",
            "web-preferences.json");
        MigrateLegacyConfig(dataDirectory);
    }

    /// <summary>历史目录的偏好配置一次性迁移到固定目录（取最新的一份）。</summary>
    private void MigrateLegacyConfig(string dataDirectory)
    {
        if (File.Exists(_path)) return;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var source = new[] { "UsageTrackerNative_v2", "UsageTrackerNative" }
            .Select(name => Path.Combine(localAppData, name, "web-preferences.json"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        if (source is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.Copy(source, _path);
        }
        catch
        {
            // 迁移失败不影响启动，用户重新设置即可
        }
    }

    public WebPreferencesSnapshot Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new WebPreferencesSnapshot();
                }

                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<WebPreferencesSnapshot>(json, JsonOptions)
                    ?? new WebPreferencesSnapshot();
            }
            catch
            {
                return new WebPreferencesSnapshot();
            }
        }
    }

    public WebPreferencesSnapshot SaveRange(string from, string to)
    {
        // 先 Load 再改：新建快照整文件覆盖会清掉热键等其他偏好。
        var snapshot = Load();
        snapshot.OverviewRange = new SavedOverviewRange(from, to);
        Save(snapshot);
        return snapshot;
    }

    public void ClearRange()
    {
        var snapshot = Load();
        snapshot.OverviewRange = null;
        Save(snapshot);
    }

    public void SaveHotkey(uint modifiers, uint key, string gesture)
    {
        var snapshot = Load();
        snapshot.BrowserHotkey = new SavedBrowserHotkey
        {
            Modifiers = modifiers,
            Key = key,
            Gesture = gesture,
        };
        Save(snapshot);
    }

    /// <summary>持久化“手动空闲”快捷键（启动器自定义槽）。</summary>
    public void SaveManualIdleHotkey(uint modifiers, uint key, string gesture)
    {
        var snapshot = Load();
        snapshot.ManualIdleHotkey = new SavedBrowserHotkey
        {
            Modifiers = modifiers,
            Key = key,
            Gesture = gesture,
        };
        Save(snapshot);
    }

    private void Save(WebPreferencesSnapshot snapshot)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions));
                File.Move(temporaryPath, _path, true);
            }
            catch
            {
                // 配置写入失败不应影响网页统计展示。
            }
        }
    }
}

public sealed class WebPreferencesSnapshot
{
    public SavedOverviewRange? OverviewRange { get; set; }
    public SavedBrowserHotkey? BrowserHotkey { get; set; }
    public SavedBrowserHotkey? ManualIdleHotkey { get; set; }
}

public sealed class SavedBrowserHotkey
{
    public uint Modifiers { get; set; }
    public uint Key { get; set; }
    public string Gesture { get; set; } = string.Empty;
}

public sealed class SavedOverviewRange
{
    public SavedOverviewRange()
    {
    }

    public SavedOverviewRange(string from, string to)
    {
        From = from;
        To = to;
    }

    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}
