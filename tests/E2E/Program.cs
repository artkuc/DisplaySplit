// End-to-end smoke test. Drives the REAL app with synthetic mouse input on the interactive desktop.
// It takes over the mouse for a few seconds and only touches its own test windows.
// Usage: DisplaySplit.E2E.exe            (expects no other DisplaySplit instance running)
using System.Diagnostics;
using System.Runtime.InteropServices;
using DisplaySplit.Core;
using DisplaySplit.Native;

namespace DisplaySplit.E2E;

internal static unsafe class Program
{
    static int _failures;
    static readonly List<string> Report = [];
    /// <summary>Locked workstation / no interactive input: inject what the LL hook would post instead of SendInput.</summary>
    static bool _simulate;

    static int Main(string[] args)
    {
        User32.SetThreadDpiAwarenessContext(-4); // per-monitor v2: measure in physical pixels
        if (User32.FindWindowExW(0, 0, "DisplaySplit.Host", null) != 0)
        {
            Console.WriteLine("A DisplaySplit instance is already running – close it first (tray → Exit).");
            return 2;
        }
        string exe = Path.Combine(AppContext.BaseDirectory, "DisplaySplit.exe");
        string home = Path.Combine(Path.GetTempPath(), "ds-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.json"),
            """{ "startWithWindows": false, "firstRunDone": true, "rememberPositions": true, "doubleClickSnap": true, "shiftDragSnap": true }""");

        _simulate = args.Contains("--simulate") || IsLocked();
        if (_simulate) Console.WriteLine("SIMULATED INPUT MODE (desktop locked or --simulate): hook→UI messages are injected; caption hit-testing is checked directly.");
        User32.GetCursorPos(out var savedCursor);
        Process? app = null;
        try
        {
            app = Start(exe, home, autostart: false);
            var mon = Monitor();
            var zones = LayoutOps.Resolve(LayoutPresets.Create(LayoutPresets.DefaultKeyFor(mon.bounds)), mon.work);
            Console.WriteLine($"monitor {mon.bounds} work {mon.work}; zones: {string.Join(" ", zones.Select(z => z.Rect))}");
            var mid = zones[zones.Count / 2];
            var left = zones[0];

            // 1. Double-click on caption → fills the zone under the cursor
            var w1 = TargetWindow.Create(mid.Rect.Left + 100, mid.Rect.Top + 150, 900, 600, dpiUnaware: false);
            Thread.Sleep(400);
            var orig = Visual(w1.Hwnd);
            Check("caption hit-test finds caption (DPI-aware window)", Platform.CaptionProbe.IsCaptionFrom(w1.Hwnd, CaptionPoint(w1.Hwnd), out var r1, out _) && r1 == w1.Hwnd, () => "not a caption");
            Check("client area is not a caption", !Platform.CaptionProbe.IsCaptionFrom(w1.Hwnd, Center(w1.Hwnd), out _, out _), () => "client reported as caption");
            DoubleClickCaption(w1.Hwnd);
            Check("double-click snaps to zone", WaitFor(() => Visual(w1.Hwnd).NearlyEquals(mid.Rect, 2)), () => $"{Visual(w1.Hwnd)} vs {mid.Rect}");

            // 2. Double-click again → restores previous size (N3)
            Thread.Sleep(700);
            DoubleClickCaption(w1.Hwnd);
            Check("second double-click restores", WaitFor(() => Visual(w1.Hwnd).NearlyEquals(orig, 3)), () => $"{Visual(w1.Hwnd)} vs {orig}");

            // 3. Shift+double-click → maximize; again → restore
            Thread.Sleep(700);
            DoubleClickCaption(w1.Hwnd, shift: true);
            Check("shift+double-click maximizes", WaitFor(() => User32.IsZoomed(w1.Hwnd)), () => Visual(w1.Hwnd).ToString());
            Thread.Sleep(700);
            DoubleClickCaption(w1.Hwnd, shift: true);
            Check("shift+double-click again restores", WaitFor(() => !User32.IsZoomed(w1.Hwnd)), () => Visual(w1.Hwnd).ToString());

            // 4. Shift + single click on caption → nothing happens
            Thread.Sleep(700);
            if (!_simulate)
            {
                var before = Visual(w1.Hwnd);
                ClickCaption(w1.Hwnd, shift: true);
                Thread.Sleep(800);
                Check("shift+single click leaves window alone", Visual(w1.Hwnd).NearlyEquals(before, 1), () => $"{Visual(w1.Hwnd)} vs {before}");
            }

            // 5. DPI-unaware window (hit-test coordinates must be mapped correctly across DPI contexts)
            var w2 = TargetWindow.Create(left.Rect.Left + 50, left.Rect.Top + 100, 700, 500, dpiUnaware: true);
            Thread.Sleep(400);
            Check("caption hit-test works for DPI-unaware window", Platform.CaptionProbe.IsCaptionFrom(w2.Hwnd, CaptionPoint(w2.Hwnd), out var r2, out _) && r2 == w2.Hwnd, () => "not a caption");
            DoubleClickCaption(w2.Hwnd);
            Check("DPI-unaware window snaps", WaitFor(() => Visual(w2.Hwnd).NearlyEquals(left.Rect, 3)), () => $"{Visual(w2.Hwnd)} vs {left.Rect}");
            w2.Close();

            // 6. Snap w1 into the left zone, close it, open a new window of the same app → restored there (R6)
            Thread.Sleep(500);
            User32.SetWindowPos(w1.Hwnd, 0, left.Rect.Left + 80, left.Rect.Top + 120, 800, 500, C.SWP_NOZORDER);
            Thread.Sleep(400);
            DoubleClickCaption(w1.Hwnd);
            Check("snap into left zone", WaitFor(() => Visual(w1.Hwnd).NearlyEquals(left.Rect, 2)), () => $"{Visual(w1.Hwnd)} vs {left.Rect}");
            Thread.Sleep(1500); // debounced save
            w1.Close();
            Thread.Sleep(500);
            var w3 = TargetWindow.Create(mid.Rect.Left + 300, mid.Rect.Top + 300, 900, 600, dpiUnaware: false);
            Check("relaunched window restored into its zone", WaitFor(() => Visual(w3.Hwnd).NearlyEquals(left.Rect, 2), 4000),
                () => $"{Visual(w3.Hwnd)} vs {left.Rect}");
            w3.Close();
            Thread.Sleep(500);

            // 7. App that moves itself shortly after showing → settle logic puts it back
            var w4 = TargetWindow.Create(mid.Rect.Left + 300, mid.Rect.Top + 300, 900, 600, dpiUnaware: false, selfMoveAfterMs: 700);
            Thread.Sleep(2500);
            Check("self-moving app settles back into zone", WaitFor(() => Visual(w4.Hwnd).NearlyEquals(left.Rect, 2), 3000),
                () => $"{Visual(w4.Hwnd)} vs {left.Rect}");

            // 8. Logon path: window exists before DisplaySplit starts with --autostart → arranged
            Stop(app); app = null;
            User32.SetWindowPos(w4.Hwnd, 0, mid.Rect.Left + 200, mid.Rect.Top + 200, 900, 600, C.SWP_NOZORDER);
            Thread.Sleep(300);
            app = Start(exe, home, autostart: true);
            Check("--autostart arranges existing windows", WaitFor(() => Visual(w4.Hwnd).NearlyEquals(left.Rect, 2), 4000),
                () => $"{Visual(w4.Hwnd)} vs {left.Rect}");
            w4.Close();

            // 9. Resource usage snapshot
            app.Refresh();
            Console.WriteLine($"app working set: {app.WorkingSet64 / 1024 / 1024} MB, private: {app.PrivateMemorySize64 / 1024 / 1024} MB, threads: {app.Threads.Count}");
            var cpu0 = app.TotalProcessorTime;
            Thread.Sleep(3000);
            app.Refresh();
            Console.WriteLine($"app CPU over 3 s idle: {(app.TotalProcessorTime - cpu0).TotalMilliseconds:F0} ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine("E2E crashed: " + ex);
            _failures++;
        }
        finally
        {
            User32.SetCursorPos(savedCursor.X, savedCursor.Y);
            if (app != null) Stop(app);
            TargetWindow.CloseAll();
            string log = Path.Combine(home, "log.txt");
            if (File.Exists(log)) Console.WriteLine("---- app log ----\n" + File.ReadAllText(log));
            try { Directory.Delete(home, true); } catch { }
        }
        Console.WriteLine("---- results ----");
        foreach (var r in Report) Console.WriteLine(r);
        Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        return _failures;
    }

    static Process Start(string exe, string home, bool autostart)
    {
        var psi = new ProcessStartInfo(exe, autostart ? "--autostart" : "") { UseShellExecute = false };
        psi.Environment["DISPLAYSPLIT_HOME"] = home;
        psi.Environment["DISPLAYSPLIT_DEBUG"] = "1";
        var p = Process.Start(psi)!;
        if (!WaitFor(() => User32.FindWindowExW(0, 0, "DisplaySplit.Host", null) != 0, 8000)) throw new Exception("app did not start");
        Thread.Sleep(800);
        return p;
    }

    static void Stop(Process p)
    {
        nint host = User32.FindWindowExW(0, 0, "DisplaySplit.Host", null);
        if (host != 0) User32.PostMessageW(host, C.WM_CLOSE, 0, 0);
        if (!p.WaitForExit(5000)) { p.Kill(); Console.WriteLine("app had to be killed"); _failures++; }
    }

    static (RectI bounds, RectI work) Monitor()
    {
        User32.GetCursorPos(out var p);
        nint h = User32.MonitorFromPoint(new POINT(p.X, p.Y), C.MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        User32.GetMonitorInfo(h, ref mi);
        return (new RectI(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
            new RectI(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom));
    }

    static RectI Visual(nint hwnd)
    {
        RECT r;
        Dwmapi.DwmGetWindowAttribute(hwnd, C.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT));
        return new RectI(r.Left, r.Top, r.Right, r.Bottom);
    }

    static bool WaitFor(Func<bool> cond, int ms = 2500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    static void Check(string name, bool ok, Func<string> detail)
    {
        string line = (ok ? "PASS  " : "FAIL  ") + name + (ok ? "" : "  — " + detail());
        Report.Add(line);
        Console.WriteLine(line);
        if (!ok) _failures++;
    }

    static PointI CaptionPoint(nint hwnd)
    {
        var v = Visual(hwnd);
        uint dpi = User32.GetDpiForWindow(hwnd);
        // Use the system DPI of the monitor for the caption offset (the window may be DPI-unaware).
        nint mon = User32.MonitorFromWindow(hwnd, C.MONITOR_DEFAULTTONEAREST);
        Shcore.GetDpiForMonitor(mon, 0, out uint mdpi, out _);
        return new PointI(v.Left + v.Width / 3, v.Top + (int)(14 * mdpi / 96.0));
    }

    static bool IsLocked()
    {
        nint fg = User32.GetForegroundWindow();
        if (fg == 0) return true;
        User32.GetWindowThreadProcessId(fg, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName.Equals("LockApp", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static PointI Center(nint hwnd) => Visual(hwnd).Center;

    static void DoubleClickCaption(nint hwnd, bool shift = false)
    {
        var p = CaptionPoint(hwnd);
        if (_simulate)
        {
            nint host = User32.FindWindowExW(0, 0, "DisplaySplit.Host", null);
            User32.PostMessageW(host, shift ? Platform.MouseHook.WM_APP_SNAP_SHIFT : Platform.MouseHook.WM_APP_SNAP, hwnd, C.MakeLParam(p.X, p.Y));
            return;
        }
        MoveTo(p);
        Thread.Sleep(60);
        if (shift) Key(C.VK_SHIFT, down: true);
        Click(); Thread.Sleep(90); Click();
        Thread.Sleep(60);
        if (shift) Key(C.VK_SHIFT, down: false);
    }

    static void ClickCaption(nint hwnd, bool shift)
    {
        MoveTo(CaptionPoint(hwnd));
        Thread.Sleep(60);
        if (shift) Key(C.VK_SHIFT, down: true);
        Click();
        Thread.Sleep(60);
        if (shift) Key(C.VK_SHIFT, down: false);
    }

    const uint MOUSEEVENTF_MOVE = 1, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;

    static void MoveTo(PointI p)
    {
        int vx = User32.GetSystemMetrics(76), vy = User32.GetSystemMetrics(77), vw = User32.GetSystemMetrics(78), vh = User32.GetSystemMetrics(79);
        var input = new INPUT { type = 0 };
        input.u.mi.dx = (int)Math.Round((p.X - vx) * 65535.0 / (vw - 1));
        input.u.mi.dy = (int)Math.Round((p.Y - vy) * 65535.0 / (vh - 1));
        input.u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        User32.SendInput(1, &input, sizeof(INPUT));
    }

    static void Click()
    {
        var inputs = stackalloc INPUT[2];
        inputs[0] = new INPUT { type = 0 }; inputs[0].u.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
        inputs[1] = new INPUT { type = 0 }; inputs[1].u.mi.dwFlags = MOUSEEVENTF_LEFTUP;
        User32.SendInput(1, &inputs[0], sizeof(INPUT));
        Thread.Sleep(30);
        User32.SendInput(1, &inputs[1], sizeof(INPUT));
    }

    static void Key(int vk, bool down)
    {
        var input = new INPUT { type = 1 };
        input.u.ki.wVk = (ushort)vk;
        input.u.ki.dwFlags = down ? 0u : 2u;
        User32.SendInput(1, &input, sizeof(INPUT));
    }
}

/// <summary>A plain overlapped Win32 window on its own thread (optionally DPI-unaware, optionally moving itself).</summary>
internal sealed unsafe class TargetWindow
{
    static readonly List<TargetWindow> All = [];
    static bool _registered;
    static readonly Dictionary<nint, (int x, int y, int w, int h)> SelfMove = [];

    public nint Hwnd { get; private set; }
    uint _thread;

    public static TargetWindow Create(int x, int y, int w, int h, bool dpiUnaware, int selfMoveAfterMs = 0)
    {
        var t = new TargetWindow();
        var ready = new ManualResetEventSlim();
        var th = new Thread(() =>
        {
            t._thread = Kernel32.GetCurrentThreadId();
            if (dpiUnaware) User32.SetThreadDpiAwarenessContext(-1);
            else User32.SetThreadDpiAwarenessContext(-4);
            lock (All)
            {
                if (!_registered)
                {
                    fixed (char* name = "DS.E2E.Target")
                    {
                        var wc = new WNDCLASSEXW
                        {
                            cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WndProc, hInstance = Kernel32.GetModuleHandleW(null),
                            hCursor = User32.LoadCursorW(0, C.IDC_ARROW), hbrBackground = (nint)6, lpszClassName = name,
                        };
                        User32.RegisterClassExW(wc);
                    }
                    _registered = true;
                }
            }
            if (dpiUnaware)
            {
                // Coordinates are logical for an unaware window; convert from physical using the monitor scale.
                nint mon = User32.MonitorFromPoint(new POINT(x, y), C.MONITOR_DEFAULTTONEAREST);
                Shcore.GetDpiForMonitor(mon, 0, out uint dpi, out _);
                double s = dpi / 96.0;
                x = (int)(x / s); y = (int)(y / s); w = (int)(w / s); h = (int)(h / s);
            }
            t.Hwnd = User32.CreateWindowExW(0, "DS.E2E.Target", "DisplaySplit E2E target", 0x00CF0000 /*WS_OVERLAPPEDWINDOW*/ | C.WS_VISIBLE,
                x, y, w, h, 0, 0, Kernel32.GetModuleHandleW(null), 0);
            if (selfMoveAfterMs > 0)
            {
                lock (SelfMove) SelfMove[t.Hwnd] = (x, y, w, h);
                User32.SetTimer(t.Hwnd, 7, (uint)selfMoveAfterMs, 0);
            }
            ready.Set();
            while (User32.GetMessageW(out MSG m, 0, 0, 0) > 0) { User32.TranslateMessage(m); User32.DispatchMessageW(m); }
        }) { IsBackground = true };
        th.Start();
        ready.Wait();
        lock (All) All.Add(t);
        return t;
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint wp, nint lp)
    {
        if (msg == C.WM_TIMER && wp == 7)
        {
            User32.KillTimer(hwnd, 7);
            (int x, int y, int w, int h) r;
            lock (SelfMove) r = SelfMove[hwnd];
            User32.SetWindowPos(hwnd, 0, r.x + 40, r.y + 40, r.w, r.h, C.SWP_NOZORDER | C.SWP_NOACTIVATE); // "restore my own bounds"
            return 0;
        }
        if (msg == C.WM_DESTROY) { User32.PostQuitMessage(0); return 0; }
        return User32.DefWindowProcW(hwnd, msg, wp, lp);
    }

    public void Close()
    {
        if (Hwnd != 0 && User32.IsWindow(Hwnd)) User32.PostMessageW(Hwnd, C.WM_CLOSE, 0, 0);
        Thread.Sleep(100);
    }

    public static void CloseAll()
    {
        lock (All) foreach (var t in All) t.Close();
    }
}
