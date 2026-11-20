using System.Runtime.InteropServices;
using DisplaySplit.Core;
using DisplaySplit.Native;

namespace DisplaySplit.Platform;

/// <summary>
/// WH_MOUSE_LL on a dedicated time-critical thread. Detects title-bar double-clicks using the probe thread's
/// asynchronous caption hit-test and swallows the second click so the app never sees a native double-click.
/// The callback is allocation-free and never calls into other processes.
/// </summary>
public static unsafe class MouseHook
{
    public const uint WM_APP_SNAP = C.WM_APP + 20;        // to host: wParam = hwnd, lParam = packed point
    public const uint WM_APP_SNAP_SHIFT = C.WM_APP + 21;  // same, Shift held
    const uint WM_HOOK_REINSTALL = C.WM_APP + 30;
    const uint WM_HOOK_QUIT = C.WM_APP + 31;

    static nint _hook;
    static uint _threadId;
    static nint _host;
    static DoubleClickDetector _detector = new(500, 4, 4);
    static uint _dblMs = 500;
    static int _halfCx = 2, _halfCy = 2;

    // Hook-thread-only state
    static bool _swallowUp;
    static uint _suppressUntil;
    static PointI _suppressPt;
    static bool _suppressActive;

    /// <summary>Feature switch (Settings.DoubleClickSnap and not paused).</summary>
    public static volatile bool Enabled = true;
    /// <summary>Environment.TickCount64 of the last button press of any kind (user-action signal).</summary>
    public static long LastButtonDownTick;
    public static PointI LastButtonDownPoint;
    public static int ProbeMisses;
    public static int DownCount, Swallowed;
    static nint _lastMoveSizeRoot;
    static long _lastMoveSizeTick;
    static long _firstDownTick;

    /// <summary>UI thread: a move/size loop started for <paramref name="root"/> (evidence of a real caption press).</summary>
    public static void NoteMoveSizeStart(nint root)
    {
        Volatile.Write(ref _lastMoveSizeTick, Environment.TickCount64);
        Volatile.Write(ref _lastMoveSizeRoot, root);
    }
    static long _lastEventTick;

    public static bool IsRunning => _hook != 0;
    public static long LastEventTick => Volatile.Read(ref _lastEventTick);

    public static void Start(nint host)
    {
        _host = host;
        ReadSystemSettings();
        var ready = new ManualResetEventSlim();
        var t = new Thread(() => ThreadMain(ready)) { IsBackground = true, Name = "DisplaySplit.MouseHook" };
        t.Start();
        ready.Wait(2000);
    }

    public static void ReadSystemSettings()
    {
        _dblMs = User32.GetDoubleClickTime();
        int cx = User32.GetSystemMetrics(C.SM_CXDOUBLECLK), cy = User32.GetSystemMetrics(C.SM_CYDOUBLECLK);
        _halfCx = Math.Max(1, cx / 2);
        _halfCy = Math.Max(1, cy / 2);
        _detector.Configure(_dblMs, cx, cy);
    }

    public static void RequestReinstall()
    {
        if (_threadId != 0) User32.PostThreadMessageW(_threadId, WM_HOOK_REINSTALL, 0, 0);
    }

    public static void Stop()
    {
        if (_threadId != 0) User32.PostThreadMessageW(_threadId, WM_HOOK_QUIT, 0, 0);
    }

    static void ThreadMain(ManualResetEventSlim ready)
    {
        _threadId = Kernel32.GetCurrentThreadId();
        Kernel32.SetThreadPriority(Kernel32.GetCurrentThread(), C.THREAD_PRIORITY_TIME_CRITICAL);
        // Force a message queue, warm up the code path (avoid first-call JIT inside the real callback).
        User32.PostThreadMessageW(_threadId, C.WM_NULL, 0, 0);
        WarmUp();
        Install();
        ready.Set();
        while (User32.GetMessageW(out MSG msg, 0, 0, 0) > 0)
        {
            if (msg.hwnd == 0 && msg.message == WM_HOOK_REINSTALL) { Uninstall(); ReadSystemSettings(); Install(); continue; }
            if (msg.hwnd == 0 && msg.message == WM_HOOK_QUIT) break;
            User32.TranslateMessage(msg);
            User32.DispatchMessageW(msg);
        }
        Uninstall();
    }

    static void Install()
    {
        _hook = User32.SetWindowsHookExW(C.WH_MOUSE_LL, &HookProc, Kernel32.GetModuleHandleW(null), 0);
        if (_hook == 0) Log.Error($"SetWindowsHookEx(WH_MOUSE_LL) failed: {Marshal.GetLastPInvokeError()}");
        else Log.Debug("mouse hook installed");
        _lastEventTick = Environment.TickCount64;
    }

    static void Uninstall()
    {
        if (_hook != 0) { User32.UnhookWindowsHookEx(_hook); _hook = 0; }
        _swallowUp = false;
        _suppressActive = false;
    }

    static void WarmUp()
    {
        bool saved = Enabled;
        Enabled = false;
        var d = new MSLLHOOKSTRUCT { pt = new POINT(-30000, -30000), time = 1 };
        HookCore(C.WM_MOUSEMOVE, &d);
        HookCore(C.WM_RBUTTONDOWN, &d);
        HookCore(C.WM_LBUTTONUP, &d);
        _ = CaptionProbe.Latest;
        Enabled = saved;
        _detector.Reset();
    }

    [UnmanagedCallersOnly]
    static nint HookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            uint msg = (uint)wParam;
            if (msg != C.WM_MOUSEMOVE && HookCore(msg, (MSLLHOOKSTRUCT*)lParam)) return 1;
        }
        return User32.CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>Returns true to swallow the event.</summary>
    static bool HookCore(uint msg, MSLLHOOKSTRUCT* d)
    {
        switch (msg)
        {
            case C.WM_MOUSEMOVE:
                return false;
            case C.WM_LBUTTONDOWN:
            {
                Interlocked.Increment(ref DownCount);
                // A press always starts a new press/release pair: drop a stale "swallow the release" flag
                // (its release may have been eaten by another hook or lost on a desktop switch).
                _swallowUp = false;
                long now = Environment.TickCount64;
                Volatile.Write(ref _lastEventTick, now);
                Volatile.Write(ref LastButtonDownTick, now);
                var pt = new PointI(d->pt.X, d->pt.Y);
                LastButtonDownPoint = pt;
                if (!Enabled) { _detector.Reset(); return false; }

                // A fast third click after a swallowed double-click would form a native double-click with click #1.
                if (_suppressActive)
                {
                    if (unchecked(d->time - _suppressUntil) > int.MaxValue /* time < until */ &&
                        Math.Abs(pt.X - _suppressPt.X) <= _halfCx && Math.Abs(pt.Y - _suppressPt.Y) <= _halfCy)
                    {
                        _swallowUp = true;
                        return true;
                    }
                    _suppressActive = false;
                }

                var r = _detector.OnLeftDown(d->time, pt);
                if (r.Kind == ClickKind.First)
                {
                    _firstDownTick = now;
                    CaptionProbe.Request(r.Seq, pt, d->time);
                    return false;
                }
                var probe = CaptionProbe.Latest;
                if (probe is null || probe.Seq != r.Seq) { Interlocked.Increment(ref ProbeMisses); return false; }
                if (!probe.Ok) return false;
                bool shift = User32.GetAsyncKeyState(C.VK_SHIFT) < 0;
                if (!probe.Sizable) return false; // fixed-size dialogs keep native behaviour (nothing to snap/maximize)
                if (probe.NeedsEvidence &&
                    !(Volatile.Read(ref _lastMoveSizeRoot) == probe.Root && Volatile.Read(ref _lastMoveSizeTick) >= _firstDownTick))
                    return false; // caption found only via the root fallback and press #1 started no move loop

                _swallowUp = true;
                Interlocked.Increment(ref Swallowed);
                _suppressActive = true;
                _suppressUntil = unchecked(probe.FirstTime + _dblMs);
                _suppressPt = probe.Point;
                User32.PostMessageW(_host, shift ? WM_APP_SNAP_SHIFT : WM_APP_SNAP, probe.Root, C.MakeLParam(pt.X, pt.Y));
                return true;
            }
            case C.WM_LBUTTONUP:
                Volatile.Write(ref _lastEventTick, Environment.TickCount64);
                if (_swallowUp) { _swallowUp = false; return true; }
                return false;
            case C.WM_RBUTTONDOWN:
            case C.WM_MBUTTONDOWN:
            case C.WM_XBUTTONDOWN:
                Volatile.Write(ref _lastEventTick, Environment.TickCount64);
                LastButtonDownPoint = new PointI(d->pt.X, d->pt.Y);
                Volatile.Write(ref LastButtonDownTick, Environment.TickCount64);
                _detector.Reset();
                _suppressActive = false;
                return false;
            default:
                return false;
        }
    }
}

public sealed record ProbeResult(int Seq, nint Root, bool Ok, bool Sizable, bool NeedsEvidence, PointI Point, uint FirstTime);

/// <summary>Caption hit-testing on its own thread so neither the hook nor the UI thread ever waits on other apps.</summary>
public static unsafe class CaptionProbe
{
    const uint WM_PROBE = C.WM_APP + 40;
    static uint _threadId;
    static ProbeResult? _latest;
    static int _pendingSeq;
    static PointI _pendingPt;
    static uint _pendingTime;

    public static ProbeResult? Latest => Volatile.Read(ref _latest);

    public static void Start()
    {
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _threadId = Kernel32.GetCurrentThreadId();
            Kernel32.SetThreadPriority(Kernel32.GetCurrentThread(), C.THREAD_PRIORITY_ABOVE_NORMAL);
            User32.PostThreadMessageW(_threadId, C.WM_NULL, 0, 0);
            ready.Set();
            while (User32.GetMessageW(out MSG msg, 0, 0, 0) > 0)
            {
                if (msg.hwnd == 0 && msg.message == WM_PROBE) RunProbe((int)msg.wParam);
            }
        }) { IsBackground = true, Name = "DisplaySplit.Probe" };
        t.Start();
        ready.Wait(2000);
    }

    /// <summary>Called from the hook thread (non-blocking).</summary>
    public static void Request(int seq, PointI pt, uint time)
    {
        Volatile.Write(ref _latest, null);
        _pendingPt = pt;
        _pendingTime = time;
        Volatile.Write(ref _pendingSeq, seq);
        if (_threadId != 0) User32.PostThreadMessageW(_threadId, WM_PROBE, seq, 0);
    }

    static void RunProbe(int seq)
    {
        if (Volatile.Read(ref _pendingSeq) != seq) return; // superseded
        var pt = _pendingPt;
        uint time = _pendingTime;
        nint root = 0;
        bool ok = false, sizable = false, evidence = false;
        try
        {
            ok = IsCaption(pt, out root, out evidence) && Eligible(root);
            sizable = ok && (WindowInfo.IsResizable(root) || WindowInfo.CanMaximize(root));
        }
        catch (Exception ex) { Log.Warn($"probe: {ex.Message}"); }
        if (Log.Verbose) Log.Debug($"probe seq={seq} pt={pt} root=0x{root:X} ok={ok} sizable={sizable} fallback={evidence}");
        if (Volatile.Read(ref _pendingSeq) == seq)
            Volatile.Write(ref _latest, new ProbeResult(seq, root, ok, sizable, evidence, pt, time));
    }

    public static bool IsCaption(PointI pt, out nint root, out bool viaRootFallback)
    {
        root = 0;
        viaRootFallback = false;
        nint h = User32.WindowFromPoint(new POINT(pt.X, pt.Y));
        return h != 0 && IsCaptionFrom(h, pt, out root, out viaRootFallback);
    }

    /// <summary>Hit-test chain starting at window <paramref name="h"/> (the deepest window under the point).</summary>
    public static bool IsCaptionFrom(nint h, PointI pt, out nint root, out bool viaRootFallback)
    {
        viaRootFallback = false;
        root = User32.GetAncestor(h, C.GA_ROOT);
        if (root == 0) return false;
        nint cur = h;
        for (int depth = 0; cur != 0 && depth < 16; depth++)
        {
            int ht = HitTest(cur, pt);
            if (Log.Verbose) Log.Debug($"  hittest 0x{cur:X} ({WindowInfo.ClassName(cur)}) = {ht}");
            if (ht == C.HTCAPTION) return true;
            if (ht == int.MinValue) return false; // hung / timed out
            if (cur == root) return false;
            if (ht != C.HTTRANSPARENT)
            {
                // XAML-island / custom-frame title bars (Win11 Explorer…): the child says HTCLIENT but the root's
                // custom frame reports HTCAPTION. Only HTCAPTION counts.
                viaRootFallback = true;
                return HitTest(root, pt) == C.HTCAPTION;
            }
            cur = User32.GetAncestor(cur, C.GA_PARENT);
        }
        return false;
    }

    /// <summary>
    /// WM_NCHITTEST with the PHYSICAL point: verified on Win11 that the system already maps the coordinates for
    /// DPI-unaware/system-aware targets when sent from a per-monitor-aware thread (pre-converting breaks it).
    /// </summary>
    static int HitTest(nint hwnd, PointI pt)
    {
        nint ok = User32.SendMessageTimeoutW(hwnd, C.WM_NCHITTEST, 0, C.MakeLParam(pt.X, pt.Y),
            C.SMTO_ABORTIFHUNG | C.SMTO_ERRORONEXIT, 60, out nint res);
        return ok == 0 ? int.MinValue : (int)res;
    }

    static bool Eligible(nint root)
    {
        bool m = WindowInfo.IsManageable(root), e = m && WindowInfo.IsBlockedByElevation(root);
        if (Log.Verbose) Log.Debug($"  eligible 0x{root:X}: manageable={m} elevatedBlock={e}");
        return m && !e;
    }
}
