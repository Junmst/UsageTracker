using System.Threading;

namespace UsageTrackerNative;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;
    private static HeadlessAgentHost? _host;
    private static NativeControlPipeServer? _pipe;
    private static readonly ManualResetEventSlim ExitEvent = new(false);

    [STAThread]
    private static void Main(string[] args)
    {
        NativeStartupLog.LogStartupMessage("Program.Main", $"Entry: {NativeStartupLog.StartupStopwatch.ElapsedMilliseconds}ms");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) NativeStartupLog.LogStartupException("UnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { NativeStartupLog.LogStartupException("UnobservedTaskException", e.Exception); e.SetObserved(); };
        _singleInstanceMutex = new Mutex(true, "Shiji.Native.SingleInstance", out var firstInstance);
        if (!firstInstance) return;
        try
        {
            _host = new HeadlessAgentHost(ExitEvent.Set);
            _pipe = new NativeControlPipeServer(_host);
            NativeStartupLog.LogStartupMessage("Program.Main", $"Headless native host ready: {NativeStartupLog.StartupStopwatch.ElapsedMilliseconds}ms");
            ExitEvent.Wait();
        }
        catch (Exception ex) { NativeStartupLog.LogStartupException("Program.Main", ex); }
        finally
        {
            _pipe?.Dispose();
            _host?.Dispose();
            _singleInstanceMutex.Dispose();
            NativeStartupLog.LogStartupMessage("Program.Main", "Exit");
        }
    }
}
