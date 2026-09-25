using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsageTrackerWeb;

internal static class BrowserWindowHelper
{
    private const int SwRestore = 9;
    private static readonly string[] BrowserProcesses = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi"];

    public static bool TryActivateBrowserWindow()
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0) return true;

            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (BrowserProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    match = handle;
                    return false;
                }
            }
            catch
            {
            }

            return true;
        }, IntPtr.Zero);

        if (match == IntPtr.Zero) return false;
        ShowWindow(match, SwRestore);
        SetForegroundWindow(match);
        return true;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);
}
