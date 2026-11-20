using System.Runtime.InteropServices;

namespace DisplaySplit.Native;

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;
    public POINT(int x, int y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public override readonly string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public char* lpszMenuName;
    public char* lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFOEXW
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    public unsafe fixed char szDevice[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DISPLAY_DEVICEW
{
    public uint cb;
    public fixed char DeviceName[32];
    public fixed char DeviceString[128];
    public uint StateFlags;
    public fixed char DeviceID[128];
    public fixed char DeviceKey[128];
}

[StructLayout(LayoutKind.Sequential)]
public struct WINDOWPLACEMENT
{
    public uint length;
    public uint flags;
    public uint showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}

[StructLayout(LayoutKind.Sequential)]
public struct PAINTSTRUCT
{
    public nint hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    public unsafe fixed byte rgbReserved[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState;
    public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
public struct ICONINFO
{
    public int fIcon;
    public int xHotspot;
    public int yHotspot;
    public nint hbmMask;
    public nint hbmColor;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
public struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

[StructLayout(LayoutKind.Sequential)]
public struct TRACKMOUSEEVENT
{
    public uint cbSize;
    public uint dwFlags;
    public nint hwndTrack;
    public uint dwHoverTime;
}

[StructLayout(LayoutKind.Sequential)]
public struct GUITHREADINFO
{
    public uint cbSize;
    public uint flags;
    public nint hwndActive;
    public nint hwndFocus;
    public nint hwndCapture;
    public nint hwndMenuOwner;
    public nint hwndMoveSize;
    public nint hwndCaret;
    public RECT rcCaret;
}

[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public INPUTUNION u;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx, dy;
    public uint mouseData, dwFlags, time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk, wScan;
    public uint dwFlags, time;
    public nuint dwExtraInfo;
}

public static unsafe partial class User32
{
    const string Dll = "user32.dll";

    [LibraryImport(Dll, SetLastError = true)] public static partial ushort RegisterClassExW(in WNDCLASSEXW wc);
    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport(Dll)] public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint hwnd);
    [LibraryImport(Dll)] public static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(in MSG msg);
    [LibraryImport(Dll)] public static partial nint DispatchMessageW(in MSG msg);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);
    [LibraryImport(Dll)] public static partial void PostQuitMessage(int code);
    [LibraryImport(Dll)] public static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessageW(string name);

    [LibraryImport(Dll)] public static partial nint WindowFromPoint(POINT pt);
    [LibraryImport(Dll)] public static partial nint GetAncestor(nint hwnd, uint flags);
    [LibraryImport(Dll)] public static partial nint GetWindow(nint hwnd, uint cmd);
    [LibraryImport(Dll, EntryPoint = "GetWindowLongPtrW")] public static partial nint GetWindowLongPtr(nint hwnd, int index);
    [LibraryImport(Dll, EntryPoint = "SetWindowLongPtrW")] public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint hwnd, out RECT rect);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(nint hwnd, out RECT rect);
    [LibraryImport(Dll, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindowAsync(nint hwnd, int cmd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(nint hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(nint hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsIconic(nint hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsZoomed(nint hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsHungAppWindow(nint hwnd);
    [LibraryImport(Dll)] public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [LibraryImport(Dll)] public static partial int GetClassNameW(nint hwnd, char* buf, int max);
    [LibraryImport(Dll)] public static partial int GetWindowTextW(nint hwnd, char* buf, int max);
    [LibraryImport(Dll)] public static partial nint GetForegroundWindow();
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport(Dll)] public static partial nint GetShellWindow();
    [LibraryImport(Dll)] public static partial nint GetDesktopWindow();
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowPlacement(nint hwnd, ref WINDOWPLACEMENT wp);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPlacement(nint hwnd, in WINDOWPLACEMENT wp);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> cb, nint lParam);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [LibraryImport(Dll)] public static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowArranged(nint hwnd);

    [LibraryImport(Dll, SetLastError = true)] public static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> fn, nint hMod, uint threadId);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnhookWindowsHookEx(nint hook);
    [LibraryImport(Dll)] public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [LibraryImport(Dll)] public static partial nint SetWinEventHook(uint min, uint max, nint hmod,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> fn, uint pid, uint tid, uint flags);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnhookWinEvent(nint hook);

    [LibraryImport(Dll)] public static partial uint GetDoubleClickTime();
    [LibraryImport(Dll)] public static partial int GetSystemMetrics(int index);
    [LibraryImport(Dll)] public static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport(Dll)] public static partial short GetAsyncKeyState(int vk);
    [LibraryImport(Dll)] public static partial short GetKeyState(int vk);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT pt);
    [LibraryImport(Dll)] public static partial nint SetCapture(nint hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReleaseCapture();
    [LibraryImport(Dll)] public static partial nint GetCapture();
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [LibraryImport(Dll)] public static partial uint SendInput(uint count, INPUT* inputs, int size);

    [LibraryImport(Dll)] public static partial nint MonitorFromPoint(POINT pt, uint flags);
    [LibraryImport(Dll)] public static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport(Dll)] public static partial nint MonitorFromRect(in RECT rc, uint flags);
    [LibraryImport(Dll, EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint hmon, ref MONITORINFOEXW info);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> cb, nint data);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayDevicesW(string? device, uint devNum, ref DISPLAY_DEVICEW dd, uint flags);

    [LibraryImport(Dll)] public static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint fn);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint hwnd, nuint id);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(nint hwnd, RECT* rc, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UpdateWindow(nint hwnd);
    [LibraryImport(Dll)] public static partial nint BeginPaint(nint hwnd, out PAINTSTRUCT ps);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EndPaint(nint hwnd, in PAINTSTRUCT ps);
    [LibraryImport(Dll)] public static partial nint GetDC(nint hwnd);
    [LibraryImport(Dll)] public static partial int ReleaseDC(nint hwnd, nint hdc);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [LibraryImport(Dll)] public static partial int FillRect(nint hdc, in RECT rc, nint brush);
    [LibraryImport(Dll)] public static partial int FrameRect(nint hdc, in RECT rc, nint brush);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial int DrawTextW(nint hdc, string text, int len, ref RECT rc, uint format);
    [LibraryImport(Dll)] public static partial nint LoadCursorW(nint inst, nint id);
    [LibraryImport(Dll)] public static partial nint SetCursor(nint cursor);
    [LibraryImport(Dll)] public static partial nint SetFocus(nint hwnd);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ScreenToClient(nint hwnd, ref POINT pt);

    [LibraryImport(Dll)] public static partial nint CreatePopupMenu();
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [LibraryImport(Dll)] public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(nint menu);

    [LibraryImport(Dll, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(nint hwnd, int id);
    [LibraryImport(Dll)] public static partial nint CreateIconIndirect(in ICONINFO info);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(nint icon);
    [LibraryImport(Dll)] public static partial nint LoadImageW(nint inst, nint name, uint type, int cx, int cy, uint flags);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AllowSetForegroundWindow(uint pid);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SystemParametersInfoW(uint action, uint param, void* pv, uint winIni);
}

public static unsafe partial class Gdi32
{
    const string Dll = "gdi32.dll";
    [LibraryImport(Dll)] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport(Dll)] public static partial nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [LibraryImport(Dll)] public static partial nint CreateDIBSection(nint hdc, in BITMAPINFOHEADER bmi, uint usage, out void* bits, nint section, uint offset);
    [LibraryImport(Dll)] public static partial nint CreateBitmap(int w, int h, uint planes, uint bpp, void* bits);
    [LibraryImport(Dll)] public static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint hdc);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport(Dll)] public static partial nint CreateSolidBrush(uint color);
    [LibraryImport(Dll)] public static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport(Dll)] public static partial nint GetStockObject(int obj);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [LibraryImport(Dll)] public static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport(Dll)] public static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RoundRect(nint hdc, int l, int t, int r, int b, int w, int h);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Rectangle(nint hdc, int l, int t, int r, int b);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool MoveToEx(nint hdc, int x, int y, POINT* prev);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool LineTo(nint hdc, int x, int y);
    [LibraryImport(Dll)] public static partial nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
}

public static unsafe partial class Dwmapi
{
    [LibraryImport("dwmapi.dll")] public static partial int DwmGetWindowAttribute(nint hwnd, uint attr, void* value, uint size);
    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint hwnd, uint attr, void* value, uint size);
}

public static unsafe partial class Shell32
{
    [LibraryImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Shell_NotifyIconW(uint msg, in NOTIFYICONDATAW data);
}

public static unsafe partial class Shcore
{
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(nint hmon, int type, out uint dpiX, out uint dpiY);
}

public static unsafe partial class Kernel32
{
    const string Dll = "kernel32.dll";
    [LibraryImport(Dll)] public static partial uint GetCurrentThreadId();
    [LibraryImport(Dll)] public static partial uint GetCurrentProcessId();
    [LibraryImport(Dll, SetLastError = true)] public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseHandle(nint h);
    [LibraryImport(Dll, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buf, ref uint size);
    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateMutexW(nint attrs, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial nint GetModuleHandleW(string? name);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial nint LoadLibraryW(string name);
    [LibraryImport(Dll)] public static partial nint GetProcAddress(nint module, nint ordinalOrName);
    [LibraryImport(Dll)] public static partial ulong GetTickCount64();
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetPriorityClass(nint process, uint cls);
    [LibraryImport(Dll)] public static partial nint GetCurrentProcess();
    [LibraryImport(Dll)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetProcessWorkingSetSize(nint process, nint min, nint max);
}

public static unsafe partial class Advapi32
{
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out nint token);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint token, int cls, void* info, uint len, out uint retLen);
}
