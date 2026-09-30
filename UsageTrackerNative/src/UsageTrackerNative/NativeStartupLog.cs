namespace UsageTrackerNative;

internal static class NativeStartupLog
{
    public static System.Diagnostics.Stopwatch StartupStopwatch => NativeLogger.StartupStopwatch;

    public static void LogStartupMessage(string source, string? message)
        => NativeLogger.LogStartupMessage(source, message);

    public static void LogStartupException(string source, Exception? exception)
        => NativeLogger.LogStartupException(source, exception);
}
