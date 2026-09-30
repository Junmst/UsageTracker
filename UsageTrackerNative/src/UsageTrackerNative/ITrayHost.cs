namespace UsageTrackerNative;

public interface ITrayHost
{
    void RestoreFromTray();
    void ExitFromTray();
    void ShowCompactSessionWindow();
    void HideCompactSessionWindow();
    AgentStatusSnapshot GetAgentStatus();
    bool EnterManualIdleFromWeb();
    WebCommandResult ExecuteWebCommand(string commandJson);
}

public sealed record WebCommandResult(bool Ok, string Error = "", object? Data = null);

public sealed record AgentStatusSnapshot(
    bool Running,
    bool Tracking,
    bool IsIdle,
    bool IsManualIdle,
    bool IsVideoPlayback,
    string? ActiveProcessName,
    string? ActiveWindowTitle,
    DateTime? ActiveStartTime,
    DateTime? LastCapturedAt);

