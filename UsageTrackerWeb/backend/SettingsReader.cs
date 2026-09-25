using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageTrackerWeb;

/// <summary>
/// 只读读取桌面版 settings.json。
/// 网页版只取展示与主题所需字段，绝不回写（修改仍由桌面版负责）。
/// </summary>
public sealed class SettingsReader
{
    private readonly string _settingsPath;

    public SettingsReader(string dataDirectory)
    {
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SettingsSnapshot Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new SettingsSnapshot();
        }

        try
        {
            using var stream = File.OpenRead(_settingsPath);
            return JsonSerializer.Deserialize<SettingsSnapshot>(stream, Options) ?? new SettingsSnapshot();
        }
        catch
        {
            return new SettingsSnapshot();
        }
    }
}

public sealed class SettingsSnapshot
{
    public string? Theme { get; set; }
    public string? Language { get; set; }
    public string? ThemeAccentColor { get; set; }
    public List<string>? ThemeAccentRecentColors { get; set; }
    public List<string>? ThemeAccentSlots { get; set; }
    public int? IdleTimeoutMinutes { get; set; }
    public List<SubjectDefinitionDto>? SubjectDefinitions { get; set; }
}

public sealed class SubjectDefinitionDto
{
    public string Name { get; set; } = string.Empty;
    public List<SubjectParentDefinitionDto>? Parents { get; set; }
    public List<string>? Children { get; set; }
}

public sealed class SubjectParentDefinitionDto
{
    public string Name { get; set; } = string.Empty;
    public List<string>? Children { get; set; }
}
