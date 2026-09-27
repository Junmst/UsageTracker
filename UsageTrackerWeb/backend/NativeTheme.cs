using Microsoft.Win32;

namespace UsageTrackerWeb;

internal sealed record NativeThemePalette(
    bool IsDark,
    Color Background,
    Color Panel,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color Accent,
    Color AccentHover,
    Color AccentSoft,
    Color Danger,
    Color DangerSoft);

internal static class NativeTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static event EventHandler? Changed;

    static NativeTheme()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static NativeThemePalette Current => CreatePalette(IsDarkMode());

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static NativeThemePalette CreatePalette(bool dark) => dark
        ? new NativeThemePalette(
            true,
            Color.FromArgb(16, 16, 16),
            Color.Black,
            Color.FromArgb(80, 80, 80),
            Color.White,
            Color.FromArgb(190, 190, 190),
            Color.White,
            Color.FromArgb(220, 220, 220),
            Color.FromArgb(48, 48, 48),
            Color.White,
            Color.FromArgb(48, 48, 48))
        : new NativeThemePalette(
            false,
            Color.FromArgb(247, 249, 251),
            Color.White,
            Color.FromArgb(226, 231, 236),
            Color.FromArgb(43, 51, 62),
            Color.FromArgb(112, 122, 134),
            Color.FromArgb(32, 32, 32),
            Color.FromArgb(0, 0, 0),
            Color.FromArgb(235, 235, 235),
            Color.FromArgb(32, 32, 32),
            Color.FromArgb(235, 235, 235));
}
