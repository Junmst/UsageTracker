using System.Diagnostics;
using System.IO;
using System.Text;

namespace UsageTrackerNative;

internal static class NativeLogger
{
    private const long StartupLogMaxBytes = 2L * 1024 * 1024;
    private const int MaxLogMessageLength = 8000;
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        UsageTrackerService.DataDirectoryName,
        "startup.log");
    private static DateTime _lastSizeCheck = DateTime.MinValue;

    public static Stopwatch StartupStopwatch { get; } = Stopwatch.StartNew();

    public static void LogStartupMessage(string source, string? message)
    {
        try
        {
            var body = Truncate(message);
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                RotateIfNeeded();
                File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {source}\r\n{body}\r\n\r\n");
            }
        }
        catch { }
    }

    public static void LogStartupException(string source, Exception? exception)
        => LogStartupMessage(source, Describe(exception));

    private static void RotateIfNeeded()
    {
        if (DateTime.Now - _lastSizeCheck < TimeSpan.FromSeconds(20)) return;
        _lastSizeCheck = DateTime.Now;
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < StartupLogMaxBytes) return;
            File.Move(LogPath, LogPath + ".1", true);
        }
        catch
        {
            try { File.WriteAllText(LogPath, string.Empty); } catch { }
        }
    }

    private static string Describe(Exception? exception)
    {
        if (exception is null) return "(no exception)";
        var builder = new StringBuilder(512);
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 4; depth++, current = current.InnerException)
        {
            if (depth > 0) builder.Append("--- InnerException ---\r\n");
            try { builder.Append(current.GetType().FullName ?? current.GetType().Name); } catch { builder.Append("Exception"); }
            builder.Append(": ");
            try { builder.Append(current.Message); } catch { builder.Append("(异常信息读取失败)"); }
            builder.Append("\r\n");
            try { builder.Append(current.StackTrace); } catch { }
        }
        return builder.ToString();
    }

    private static string Truncate(string? value)
        => string.IsNullOrEmpty(value) || value.Length <= MaxLogMessageLength
            ? value ?? string.Empty
            : value[..MaxLogMessageLength] + "\r\n...(日志过长已截断)";
}
