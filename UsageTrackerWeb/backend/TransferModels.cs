namespace UsageTrackerWeb;

public sealed record TransferPreviewRequest(string Path, string? Kind = null);
public sealed record TransferExportRequest(string Kind = "full");
public sealed record TransferImportRequest(
    string Path,
    string? DataMode = null,
    string? ConflictStrategy = null,
    string? SettingsMode = null);
