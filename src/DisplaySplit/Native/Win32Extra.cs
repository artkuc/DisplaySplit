using System.Runtime.InteropServices;

namespace DisplaySplit.Native;

[StructLayout(LayoutKind.Sequential)]
public struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BLENDFUNCTION
{
    public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct UPDATELAYEREDWINDOWINFO
{
    public uint cbSize;
    public nint hdcDst;
    public POINT* pptDst;
    public SIZE* psize;
    public nint hdcSrc;
    public POINT* pptSrc;
    public uint crKey;
    public BLENDFUNCTION* pblend;
    public uint dwFlags;
    public RECT* prcDirty;
}

[StructLayout(LayoutKind.Sequential)]
public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

[StructLayout(LayoutKind.Sequential)]
public struct FILETIME { public uint Low, High; public readonly long Value => ((long)High << 32) | Low; }

[StructLayout(LayoutKind.Sequential)]
public struct LUID { public uint LowPart; public int HighPart; }

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
    public uint scaling; public uint refreshNum, refreshDen; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public uint size; public LUID adapterId; public uint id; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public fixed char viewGdiDeviceName[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;
    public fixed char monitorFriendlyDeviceName[64];
    public fixed char monitorDevicePath[128];
}

[StructLayout(LayoutKind.Sequential)]
public struct PROPERTYKEY { public Guid fmtid; public uint pid; }

[StructLayout(LayoutKind.Sequential, Size = 24)]
public struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public nint p; public nint p2; }

public static unsafe partial class User32
{
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateLayeredWindowIndirect(nint hwnd, in UPDATELAYEREDWINDOWINFO info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PhysicalToLogicalPointForPerMonitorDPI(nint hwnd, ref POINT pt);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterShellHookWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeregisterShellHookWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetLastInputInfo(ref LASTINPUTINFO lii);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowExW(nint parent, nint childAfter, string? cls, string? title);
    [LibraryImport("user32.dll")] public static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [LibraryImport("user32.dll")]
    public static partial int QueryDisplayConfig(uint flags, ref uint numPaths, DISPLAYCONFIG_PATH_INFO* paths, ref uint numModes, DISPLAYCONFIG_MODE_INFO* modes, nint topology);
    [LibraryImport("user32.dll")] public static partial int DisplayConfigGetDeviceInfo(void* packet);
    [LibraryImport("user32.dll")] public static partial nint GetParent(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetCursorPos(int x, int y);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxW(nint hwnd, string text, string caption, uint type);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint msg, uint action, nint filterStatus);
    [LibraryImport("user32.dll")] public static partial nint SetThreadDpiAwarenessContext(nint ctx);
}

public static unsafe partial class Kernel32
{
    [LibraryImport("kernel32.dll")] public static partial int GetApplicationUserModelId(nint process, ref uint length, char* id);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
    [LibraryImport("kernel32.dll")] public static partial void GetSystemTimeAsFileTime(out FILETIME ft);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetThreadPriority(nint thread, int priority);
    [LibraryImport("kernel32.dll")] public static partial nint GetCurrentThread();
}

public static unsafe partial class Shell32
{
    [LibraryImport("shell32.dll")] public static partial int SHGetPropertyStoreForWindow(nint hwnd, in Guid riid, out nint store);
}

public static unsafe partial class Ole32
{
    [LibraryImport("ole32.dll")] public static partial int PropVariantClear(ref PROPVARIANT pv);
    [LibraryImport("ole32.dll")] public static partial int CoInitializeEx(nint reserved, uint coinit);
}

public static unsafe partial class Msimg32
{
    [LibraryImport("msimg32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AlphaBlend(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, BLENDFUNCTION bf);
}

public static unsafe partial class Wtsapi32
{
    [LibraryImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [LibraryImport("wtsapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool WTSUnRegisterSessionNotification(nint hwnd);
}

public static class C
{
    // Window messages
    public const uint WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_MOVE = 0x0003, WM_SIZE = 0x0005, WM_ACTIVATE = 0x0006,
        WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008, WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011,
        WM_QUIT = 0x0012, WM_ERASEBKGND = 0x0014, WM_SETTINGCHANGE = 0x001A, WM_ACTIVATEAPP = 0x001C,
        WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021, WM_GETMINMAXINFO = 0x0024, WM_ENDSESSION = 0x0016,
        WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102,
        WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_COMMAND = 0x0111, WM_SYSCOMMAND = 0x0112, WM_TIMER = 0x0113,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208,
        WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_MOUSEHWHEEL = 0x020E,
        WM_CAPTURECHANGED = 0x0215, WM_POWERBROADCAST = 0x0218, WM_MOUSELEAVE = 0x02A3,
        WM_WTSSESSION_CHANGE = 0x02B1, WM_DPICHANGED = 0x02E0, WM_DISPLAYCHANGE = 0x007E, WM_HOTKEY = 0x0312,
        WM_USER = 0x0400, WM_APP = 0x8000, WM_CONTEXTMENU = 0x007B, WM_NULL = 0;

    public const uint WS_OVERLAPPED = 0, WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000,
        WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000,
        WS_MAXIMIZEBOX = 0x00010000, WS_DISABLED = 0x08000000;
    public const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_USERDATA = -21;
    public const uint GA_PARENT = 1, GA_ROOT = 2, GA_ROOTOWNER = 3;
    public const uint GW_OWNER = 4, GW_CHILD = 5;

    public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3, SW_MAXIMIZE = 3,
        SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_MINIMIZE = 6, SW_SHOWMINNOACTIVE = 7, SW_SHOWNA = 8, SW_RESTORE = 9;

    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOREDRAW = 0x8, SWP_NOACTIVATE = 0x10,
        SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200,
        SWP_NOSENDCHANGING = 0x400, SWP_ASYNCWINDOWPOS = 0x4000;
    public static readonly nint HWND_TOPMOST = -1, HWND_NOTOPMOST = -2, HWND_TOP = 0;

    public const int WH_MOUSE_LL = 14;
    public const uint LLMHF_INJECTED = 1;

    public const int HTTRANSPARENT = -1, HTNOWHERE = 0, HTCLIENT = 1, HTCAPTION = 2;
    public const uint SMTO_NORMAL = 0, SMTO_BLOCK = 1, SMTO_ABORTIFHUNG = 2, SMTO_ERRORONEXIT = 0x20;

    public const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37, SM_CXDRAG = 68, SM_CYDRAG = 69, SM_SWAPBUTTON = 23;
    public const int VK_LBUTTON = 1, VK_RBUTTON = 2, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B,
        VK_RETURN = 0x0D, VK_DELETE = 0x2E, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_BACK = 0x08, VK_TAB = 0x09, VK_F2 = 0x71;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B,
        EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017,
        EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003,
        EVENT_OBJECT_LOCATIONCHANGE = 0x800B, EVENT_OBJECT_NAMECHANGE = 0x800C,
        EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const int OBJID_WINDOW = 0, CHILDID_SELF = 0;

    public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;

    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14, DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
        DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    public const uint DWM_CLOAKED_APP = 1, DWM_CLOAKED_SHELL = 2, DWM_CLOAKED_INHERITED = 4;

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    public const uint NIIF_INFO = 1, NIIF_NOSOUND = 0x10;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, NIN_BALLOONUSERCLICK = 0x405;

    public const uint MF_STRING = 0, MF_GRAYED = 1, MF_DISABLED = 2, MF_CHECKED = 8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800,
        MF_DEFAULT = 0x1000, MFT_RADIOCHECK = 0x200;
    public const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80, TPM_BOTTOMALIGN = 0x20, TPM_RIGHTALIGN = 8;

    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    public const int IDC_ARROW = 32512, IDC_CROSS = 32515, IDC_SIZEWE = 32644, IDC_SIZENS = 32645, IDC_HAND = 32649,
        IDC_SIZEALL = 32646, IDC_NO = 32648;

    public const uint ULW_ALPHA = 2;
    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    public const uint LWA_ALPHA = 2;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;

    public const int MA_NOACTIVATE = 3;
    public const uint TME_LEAVE = 2;
    public const uint PBT_APMRESUMEAUTOMATIC = 0x12, PBT_APMRESUMESUSPEND = 7;
    public const uint WTS_SESSION_UNLOCK = 0x8, WTS_SESSION_LOGON = 0x5, NOTIFY_FOR_THIS_SESSION = 0;
    public const int HSHELL_WINDOWCREATED = 1, HSHELL_WINDOWDESTROYED = 2;
    public const uint ASFW_ANY = unchecked((uint)-1);
    public const int THREAD_PRIORITY_TIME_CRITICAL = 15, THREAD_PRIORITY_HIGHEST = 2, THREAD_PRIORITY_ABOVE_NORMAL = 1;

    public const uint WPF_ASYNCWINDOWPLACEMENT = 4;
    public const int CS_DBLCLKS = 8, CS_HREDRAW = 2, CS_VREDRAW = 1;

    public const uint QDC_ONLY_ACTIVE_PATHS = 2;
    public const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    public const uint SPI_GETWORKAREA = 0x30;

    // GDI
    public const int TRANSPARENT = 1;
    public const uint SRCCOPY = 0x00CC0020;
    public const uint DIB_RGB_COLORS = 0;

    public static int LoWord(nint v) => (short)((long)v & 0xFFFF);
    public static int HiWord(nint v) => (short)(((long)v >> 16) & 0xFFFF);
    public static nint MakeLParam(int lo, int hi) => (nint)(((hi & 0xFFFF) << 16) | (lo & 0xFFFF));

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}

