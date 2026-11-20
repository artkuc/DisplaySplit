using DisplaySplit.Native;

namespace DisplaySplit.UI;

public interface IHostHandler
{
    void OnDisplayChanged();
    void OnWorkAreaChanged();
    void OnTaskbarCreated();
    void OnEndSession();
    void OnResumeOrUnlock();
    void OnShellWindowDestroyed(nint hwnd);
    void OnHotkey(int id);
    void OnTrayMessage(uint evt, int x, int y);
    void OnOpenEditorRequest();
    void OnSnapRequest(nint hwnd, int x, int y, bool shift);
}

/// <summary>
/// Hidden top-level window (never shown). Unlike an HWND_MESSAGE window it receives broadcasts:
/// WM_DISPLAYCHANGE, WM_SETTINGCHANGE, TaskbarCreated, WM_ENDSESSION, WM_POWERBROADCAST, session changes, shell hook.
/// </summary>
public sealed class HostWindow : NativeWindow
{
    const uint SPI_SETWORKAREA = 0x002F;
    readonly uint _taskbarCreated;
    readonly uint _shellHookMsg;
    public IHostHandler? Handler { get; set; }

    public HostWindow()
    {
        EnsureClass(App.HostClass);
        _taskbarCreated = User32.RegisterWindowMessageW("TaskbarCreated");
        _shellHookMsg = User32.RegisterWindowMessageW("SHELLHOOK");
        CreateHandle(App.HostClass, "DisplaySplit", C.WS_POPUP, C.WS_EX_TOOLWINDOW, 0, 0, 0, 0);
        User32.RegisterShellHookWindow(Handle);
        // When elevated, let medium-integrity Explorer's TaskbarCreated broadcast through (tray icon re-add).
        User32.ChangeWindowMessageFilterEx(Handle, _taskbarCreated, 1 /* MSGFLT_ALLOW */, 0);
        Wtsapi32.WTSRegisterSessionNotification(Handle, C.NOTIFY_FOR_THIS_SESSION);
    }

    protected override nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case App.WM_APP_RUN:
                App.DrainQueue();
                return 0;
            case C.WM_TIMER:
                if (App.FireTimer((nuint)wParam)) return 0;
                break;
            case C.WM_DISPLAYCHANGE:
                Handler?.OnDisplayChanged();
                return 0;
            case C.WM_SETTINGCHANGE:
                if ((uint)wParam == SPI_SETWORKAREA) Handler?.OnWorkAreaChanged();
                // Double-click time / rectangle changed → the hook re-reads them on reinstall.
                else if ((uint)wParam is 0x0020 /*SPI_SETDOUBLECLICKTIME*/ or 0x001D /*SPI_SETDOUBLECLKWIDTH*/
                         or 0x001E /*SPI_SETDOUBLECLKHEIGHT*/ or 0x0021 /*SPI_SETMOUSEBUTTONSWAP*/)
                    Platform.MouseHook.RequestReinstall();
                return 0;
            case C.WM_QUERYENDSESSION:
                return 1;
            case C.WM_ENDSESSION:
                if (wParam != 0) Handler?.OnEndSession();
                return 0;
            case C.WM_POWERBROADCAST:
                if ((uint)wParam is C.PBT_APMRESUMEAUTOMATIC or C.PBT_APMRESUMESUSPEND) Handler?.OnResumeOrUnlock();
                return 1;
            case C.WM_WTSSESSION_CHANGE:
                if ((uint)wParam is C.WTS_SESSION_UNLOCK or C.WTS_SESSION_LOGON) Handler?.OnResumeOrUnlock();
                return 0;
            case C.WM_HOTKEY:
                Handler?.OnHotkey((int)wParam);
                return 0;
            case App.WM_APP_TRAY:
                // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = anchor x/y
                Handler?.OnTrayMessage((uint)C.LoWord(lParam), C.LoWord(wParam), C.HiWord(wParam));
                return 0;
            case Platform.MouseHook.WM_APP_SNAP:
            case Platform.MouseHook.WM_APP_SNAP_SHIFT:
                Handler?.OnSnapRequest(wParam, C.LoWord(lParam), C.HiWord(lParam), msg == Platform.MouseHook.WM_APP_SNAP_SHIFT);
                return 0;
            case App.WM_APP_OPEN_EDITOR:
                Handler?.OnOpenEditorRequest();
                return 0;
            case C.WM_CLOSE:
                App.Quit(); // e.g. installer / restart manager asking us to exit: clean shutdown (state is flushed)
                return 0;
        }
        if (msg == _taskbarCreated && msg != 0) { Handler?.OnTaskbarCreated(); return 0; }
        if (msg == _shellHookMsg && msg != 0)
        {
            if ((int)wParam == C.HSHELL_WINDOWDESTROYED) Handler?.OnShellWindowDestroyed(lParam);
            return 0;
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public override void Dispose()
    {
        if (Handle != 0)
        {
            User32.DeregisterShellHookWindow(Handle);
            Wtsapi32.WTSUnRegisterSessionNotification(Handle);
        }
        base.Dispose();
    }
}
