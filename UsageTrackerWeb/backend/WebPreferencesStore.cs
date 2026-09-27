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
        _path = Path.Combine(dataDirectory, "web-preferences.json");
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
        var snapshot = new WebPreferencesSnapshot
        {
            OverviewRange = new SavedOverviewRange(from, to),
        };
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
