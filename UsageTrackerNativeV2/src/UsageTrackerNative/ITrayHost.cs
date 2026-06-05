namespace UsageTrackerNative;

public interface ITrayHost
{
    void RestoreFromTray();
    void ExitFromTray();
    void ShowCompactSessionWindow();
    void HideCompactSessionWindow();
}

