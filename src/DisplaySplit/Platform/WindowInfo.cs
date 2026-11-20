using DisplaySplit.Core;
using DisplaySplit.Native;

namespace DisplaySplit.Platform;

/// <summary>Cheap, non-blocking queries about foreign windows (none of these send messages to the target).</summary>
public static unsafe class WindowInfo
{
    static readonly HashSet<string> DeniedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "#32768", "#32769", "#32770_HIDDEN",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow", "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd",
        "Windows.Internal.Shell.TabProxyWindow", "Shell_InputSwitchTopLevelWindow", "SysShadow", "tooltips_class32",
        "DisplaySplit.Host", "DisplaySplit.Editor", "DisplaySplit.Drag", "DisplaySplit.Tray",
    };

    public static string ClassName(nint hwnd)
    {
        char* buf = stackalloc char[256];
        int n = User32.GetClassNameW(hwnd, buf, 256);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    /// <summary>Window text via GetWindowTextW: for windows of other processes this reads the cached caption
    /// without sending WM_GETTEXT to the target thread, so it can't hang.</summary>
    public static string Title(nint hwnd)
    {
        char* buf = stackalloc char[512];
        int n = User32.GetWindowTextW(hwnd, buf, 512);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    public static uint Style(nint hwnd) => (uint)(long)User32.GetWindowLongPtr(hwnd, C.GWL_STYLE);
    public static uint ExStyle(nint hwnd) => (uint)(long)User32.GetWindowLongPtr(hwnd, C.GWL_EXSTYLE);

    public static uint ProcessIdOf(nint hwnd)
    {
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public static uint Cloaked(nint hwnd)
    {
        uint v = 0;
        return Dwmapi.DwmGetWindowAttribute(hwnd, C.DWMWA_CLOAKED, &v, 4) == 0 ? v : 0;
    }

    public static RectI WindowRect(nint hwnd) =>
        User32.GetWindowRect(hwnd, out var r) ? new RectI(r.Left, r.Top, r.Right, r.Bottom) : default;

    /// <summary>Visible bounds (without the invisible resize borders) via DWM.</summary>
    public static RectI VisualRect(nint hwnd)
    {
        RECT r;
        if (Dwmapi.DwmGetWindowAttribute(hwnd, C.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)) == 0)
            return new RectI(r.Left, r.Top, r.Right, r.Bottom);
        return WindowRect(hwnd);
    }

    public static bool IsOwnWindow(nint hwnd) => ProcessIdOf(hwnd) == App.ProcessId;

    /// <summary>
    /// Is this a normal application top-level window we may manage?
    /// <paramref name="allowShellCloaked"/>: treat windows on other virtual desktops as eligible.
    /// </summary>
    public static bool IsManageable(nint hwnd, bool allowShellCloaked = false, bool requireVisible = true)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd)) return false;
        if (User32.GetAncestor(hwnd, C.GA_ROOT) != hwnd) return false;
        if (requireVisible && !User32.IsWindowVisible(hwnd)) return false;
        uint style = Style(hwnd), ex = ExStyle(hwnd);
        if ((style & C.WS_CHILD) != 0) return false;
        if ((ex & C.WS_EX_TOOLWINDOW) != 0 && (ex & C.WS_EX_APPWINDOW) == 0) return false;
        if ((style & (C.WS_THICKFRAME | C.WS_CAPTION)) == 0) return false;
        if ((ex & C.WS_EX_NOACTIVATE) != 0 && (ex & C.WS_EX_APPWINDOW) == 0) return false;
        uint cloak = Cloaked(hwnd);
        if (cloak != 0 && !(allowShellCloaked && cloak == C.DWM_CLOAKED_SHELL)) return false;
        if (IsOwnWindow(hwnd)) return false;
        string cls = ClassName(hwnd);
        if (DeniedClasses.Contains(cls)) return false;
        if (hwnd == User32.GetShellWindow()) return false;
        // Owned windows (dialogs, popups) follow their owner, unless the owner is invisible (common app-frame trick).
        nint owner = User32.GetWindow(hwnd, C.GW_OWNER);
        if (owner != 0 && User32.IsWindowVisible(owner)) return false;
        // Borderless windows covering a whole monitor (games, video players in full screen) are left alone.
        if ((style & C.WS_CAPTION) == 0 && !User32.IsIconic(hwnd))
        {
            var mon = MonitorService.MonitorRectOf(hwnd);
            if (!mon.IsEmpty && WindowRect(hwnd).Contains(mon)) return false;
        }
        return true;
    }

    /// <summary>Maximized according to the window placement (also true for minimized-from-maximized).</summary>
    public static bool IsMaximizedPlacement(nint hwnd)
    {
        var wp = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (!User32.GetWindowPlacement(hwnd, ref wp)) return false;
        const uint WPF_RESTORETOMAXIMIZED = 2;
        return wp.showCmd == C.SW_SHOWMAXIMIZED || (wp.showCmd == C.SW_SHOWMINIMIZED && (wp.flags & WPF_RESTORETOMAXIMIZED) != 0);
    }

    public static bool IsResizable(nint hwnd) => (Style(hwnd) & C.WS_THICKFRAME) != 0;
    public static bool CanMaximize(nint hwnd) => (Style(hwnd) & (C.WS_MAXIMIZEBOX | C.WS_THICKFRAME)) != 0;

    /// <summary>True if the window's process is elevated while we are not (UIPI would block us).</summary>
    public static bool IsBlockedByElevation(nint hwnd)
    {
        if (SelfElevated) return false;
        uint pid = ProcessIdOf(hwnd);
        if (pid == 0) return true;
        nint proc = Kernel32.OpenProcess(C.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == 0) return true; // can't even query → treat as protected
        try
        {
            long created = Kernel32.GetProcessTimes(proc, out var c, out _, out _, out _) ? c.Value : 0;
            lock (ElevationCache)
            {
                if (ElevationCache.TryGetValue(pid, out var e) && e.created == created) return e.elevated;
            }
            bool elevated = IsElevated(proc);
            lock (ElevationCache)
            {
                if (ElevationCache.Count > 512) ElevationCache.Clear();
                ElevationCache[pid] = (created, elevated);
            }
            return elevated;
        }
        finally { Kernel32.CloseHandle(proc); }
    }

    // pid → (process creation time, elevated). Creation time guards against PID reuse.
    static readonly Dictionary<uint, (long created, bool elevated)> ElevationCache = [];

    public static readonly bool SelfElevated = IsElevated(Kernel32.GetCurrentProcess());

    static bool IsElevated(nint process)
    {
        if (!Advapi32.OpenProcessToken(process, C.TOKEN_QUERY, out nint token)) return true;
        try
        {
            uint elevated = 0;
            return Advapi32.GetTokenInformation(token, C.TokenElevation, &elevated, 4, out _) && elevated != 0;
        }
        finally { Kernel32.CloseHandle(token); }
    }

    /// <summary>Process creation time (UTC) or null.</summary>
    public static DateTime? ProcessStartUtc(uint pid)
    {
        nint proc = Kernel32.OpenProcess(C.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == 0) return null;
        try
        {
            if (!Kernel32.GetProcessTimes(proc, out var created, out _, out _, out _)) return null;
            return DateTime.FromFileTimeUtc(created.Value);
        }
        finally { Kernel32.CloseHandle(proc); }
    }

    public static string? ProcessImagePath(uint pid)
    {
        nint proc = Kernel32.OpenProcess(C.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == 0) return null;
        try
        {
            char* buf = stackalloc char[1024];
            uint size = 1024;
            return Kernel32.QueryFullProcessImageNameW(proc, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally { Kernel32.CloseHandle(proc); }
    }

    public static List<nint> TopLevelWindows()
    {
        _enumList = [];
        User32.EnumWindows(&EnumProc, 0);
        var list = _enumList;
        _enumList = null;
        return list;
    }

    [ThreadStatic] static List<nint>? _enumList;

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static int EnumProc(nint hwnd, nint lParam)
    {
        _enumList?.Add(hwnd);
        return 1;
    }
}
