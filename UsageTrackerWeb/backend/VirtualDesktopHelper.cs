using System.Runtime.InteropServices;

namespace UsageTrackerWeb;

/// <summary>
/// Windows 虚拟桌面（任务视图 / Win+Tab）相关操作。
/// 把小窗、启动器、看板固定到所有虚拟桌面，并支持把窗口召唤到小窗所在的桌面。
/// 24H2（Build 26100）起 Pin 接口从“窗口”改为“ApplicationView”，这里两套都实现，按系统版本选择。
/// 服务按需懒解析并在窗口刚创建时重试，避免进程启动早期 Shell 服务/窗口 View 尚未就绪导致永久失败。
/// 所有调用均吞掉异常、静默降级：接口不可用时窗口仅留在当前桌面。
/// </summary>
internal static class VirtualDesktopHelper
{
    private static readonly int Build = Environment.OSVersion.Version.Build;
    private static readonly object InitLock = new();
    private static long _lastInitAttemptTick;

    private static IVirtualDesktopManager? _manager;
    private static IShellServiceProvider? _shell;
    private static IVirtualDesktopPinnedAppsWin11? _pinnedWin11;
    private static IApplicationViewCollection? _viewCollection;
    private static IVirtualDesktopPinnedAppsLegacy? _pinnedLegacy;

    /// <summary>把窗口固定到所有虚拟桌面（每个桌面都显示，等价于任务视图里的“在所有桌面上显示此窗口”）。</summary>
    public static void PinToAllDesktops(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        // 窗口句柄刚创建时 Shell 侧的 View 可能还查不到，在 UI 线程上异步重试若干次
        TryPinOnce(hwnd);
        ScheduleRetries(hwnd, attempt: 0);

        static async void ScheduleRetries(IntPtr handle, int attempt)
        {
            const int maxAttempts = 10;
            for (var i = 0; i < maxAttempts; i++)
            {
                try
                {
                    await Task.Delay(250).ConfigureAwait(true);
                    if (TryPinOnce(handle)) return;
                }
                catch
                {
                    // 忽略，继续重试
                }
            }
        }
    }

    private static bool TryPinOnce(IntPtr hwnd)
    {
        try
        {
            EnsureServices();
            if (Build >= 26100)
            {
                if (_pinnedWin11 is null || _viewCollection is null) return false;
                if (_viewCollection.GetViewForHwnd(hwnd, out var view) != 0 || view is null) return false;
                if (_pinnedWin11.IsViewPinned(view)) return true;
                _pinnedWin11.PinView(view);
                return _pinnedWin11.IsViewPinned(view);
            }
            else
            {
                if (_pinnedLegacy is null) return false;
                // 旧接口直接返回 HRESULT：S_OK(0)=已固定，S_FALSE(1)=未固定
                if (_pinnedLegacy.IsWindowPinned(hwnd) == 0) return true;
                _pinnedLegacy.PinWindow(hwnd);
                return _pinnedLegacy.IsWindowPinned(hwnd) == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureServices()
    {
        lock (InitLock)
        {
            var now = Environment.TickCount64;
            if (_shell is null && now - _lastInitAttemptTick < 1000) return;
            _lastInitAttemptTick = now;

            try
            {
                _shell ??= CreateShell();
            }
            catch
            {
                _shell = null;
            }

            if (_shell is null) return;

            try
            {
                if (Build >= 26100)
                {
                    _pinnedWin11 ??= QueryService<IVirtualDesktopPinnedAppsWin11>(
                        _shell,
                        new Guid("b5a399e7-1c87-46b8-88e9-fc5747b171bd"),
                        new Guid("4ce81583-1e4c-4632-a621-07a53543148f"));

                    _viewCollection ??= QueryService<IApplicationViewCollection>(
                        _shell,
                        new Guid("1841c6d7-4f9d-42c0-af41-8747538f10e5"),
                        new Guid("1841c6d7-4f9d-42c0-af41-8747538f10e5"));
                }
                else
                {
                    _pinnedLegacy ??= QueryService<IVirtualDesktopPinnedAppsLegacy>(
                        _shell,
                        new Guid("b4a54bf7-0b18-494a-8fca-f2e0b6e5f7a3"),
                        new Guid("4bcb57a6-92a3-4b90-9f99-35dbd17b056f"));
                }
            }
            catch
            {
                // 服务暂不可用，下次调用重试
            }
        }
    }

    private static IShellServiceProvider? CreateShell()
    {
        var type = Type.GetTypeFromCLSID(new Guid("c2f03a33-21f5-47fa-b4bb-156362a2f239")); // CLSID_ImmersiveShell
        return type is null ? null : (IShellServiceProvider?)Activator.CreateInstance(type);
    }

    private static T? QueryService<T>(IShellServiceProvider shell, Guid serviceId, Guid interfaceId) where T : class
    {
        try
        {
            return shell.QueryService(ref serviceId, ref interfaceId) as T;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 target 窗口移动到 source 窗口所在的虚拟桌面（用于从其他桌面“召唤”启动器/看板）。
    /// source 已固定到所有桌面时其桌面 ID 为空，此时不做处理。
    /// </summary>
    public static void BringToSameDesktop(IntPtr source, IntPtr target)
    {
        if (source == IntPtr.Zero || target == IntPtr.Zero) return;
        try
        {
            lock (InitLock)
            {
                if (_manager is null)
                {
                    var type = Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
                    _manager = type is null
                        ? null
                        : (IVirtualDesktopManager?)Activator.CreateInstance(type);
                }
            }
            if (_manager is null) return;
            var desktopId = _manager.GetWindowDesktopId(source);
            if (desktopId != Guid.Empty)
            {
                _manager.MoveWindowToDesktop(target, ref desktopId);
            }
        }
        catch
        {
            // 固定窗口或接口异常时忽略；PinToAllDesktops 已保证常规情况下窗口在所有桌面可见。
        }
    }

    [ComImport]
    [Guid("6d5140c1-7436-11ce-8034-00aa006009fa")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? QueryService(ref Guid serviceId, ref Guid interfaceId);
    }

    [ComImport]
    [Guid("a5cd92ff-29be-4f99-8dcd-5d82d4bb4bb7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

        Guid GetWindowDesktopId(IntPtr topLevelWindow);

        void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    // ---- 24H2（Build 26100）及之后：按 ApplicationView 固定 ----

    [ComImport]
    [Guid("372e1d3b-38d3-42e4-a15b-8ab2b178f513")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationView
    {
        // 仅作为不透明 COM 指针传给 PinView/IsViewPinned，从不调用其方法。
        // .NET 8 不支持 IInspectable marshal，而该接口本质是 IInspectable 的派生接口，
        // 这里以 IUnknown 方式拿同一 IID 的指针即可满足“不透明传参”。
    }

    [ComImport]
    [Guid("1841c6d7-4f9d-42c0-af41-8747538f10e5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        int GetViews(out IntPtr views);
        int GetViewsByZOrder(out IntPtr views);
        int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, out IntPtr views);

        int GetViewForHwnd(IntPtr hwnd, out IApplicationView? view);
    }

    [ComImport]
    [Guid("4ce81583-1e4c-4632-a621-07a53543148f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedAppsWin11
    {
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId);

        void PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        void UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsViewPinned(IApplicationView applicationView);

        void PinView(IApplicationView applicationView);

        void UnpinView(IApplicationView applicationView);
    }

    // ---- 24H2 之前（Win10 / Win11 21H2-23H2）：直接按窗口固定 ----

    [ComImport]
    [Guid("4bcb57a6-92a3-4b90-9f99-35dbd17b056f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedAppsLegacy
    {
        // 这些内部方法直接返回 HRESULT：S_OK(0)=是/成功，S_FALSE(1)=否
        [PreserveSig]
        int IsAppPinned([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId);

        [PreserveSig]
        int PinApp([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId);

        [PreserveSig]
        int UnpinApp([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId);

        [PreserveSig]
        int IsWindowPinned(IntPtr hwnd);

        [PreserveSig]
        int PinWindow(IntPtr hwnd);

        [PreserveSig]
        int UnpinWindow(IntPtr hwnd);
    }
}
