using System.Collections.Concurrent;
using DisplaySplit.Core;
using DisplaySplit.Native;

namespace DisplaySplit.Platform;

public readonly record struct Insets(int Left, int Top, int Right, int Bottom)
{
    public RectI Expand(RectI r) => new(r.Left - Left, r.Top - Top, r.Right + Right, r.Bottom + Bottom);
    public static Insets Between(RectI window, RectI visual) =>
        new(Math.Max(0, visual.Left - window.Left), Math.Max(0, visual.Top - window.Top),
            Math.Max(0, window.Right - visual.Right), Math.Max(0, window.Bottom - visual.Bottom));
}

/// <summary>
/// Moves windows to visual target rects. Every foreign-window call runs on the <see cref="PlacementWorker"/>;
/// results are verified on the UI thread by short-lived polling (only while a placement is in flight).
/// </summary>
public sealed unsafe class SnapEngine(PlacementWorker worker)
{
    readonly ConcurrentDictionary<nint, Insets> _restoredInsets = new();
    readonly Dictionary<nint, (RectI rect, long until)> _expected = [];
    readonly Dictionary<nint, Verification> _verifying = [];

    sealed class Verification
    {
        public nint Hwnd;
        public RectI Target;     // desired visual rect
        public RectI Zone;       // zone (for centring when min-size prevents fitting)
        public RectI Before;
        public RectI Last;
        public long Deadline;
        public bool Retried, Corrected, Activate, DpiFixed, ViaPlacement;
        public uint Dpi;
        public nuint Timer;
        public Action<bool, RectI>? Done;
    }

    /// <summary>Is <paramref name="hwnd"/> currently being moved by us (events about it should be ignored)?</summary>
    public bool IsOwnMove(nint hwnd)
    {
        if (_verifying.ContainsKey(hwnd)) return true;
        if (_expected.TryGetValue(hwnd, out var e))
        {
            if (Environment.TickCount64 < e.until) return true;
            _expected.Remove(hwnd);
        }
        return false;
    }

    /// <summary>Place window so its visible frame equals <paramref name="target"/>. Callback on UI thread.</summary>
    public void Place(nint hwnd, RectI target, bool activate, Action<bool, RectI>? done = null, bool keepMinimized = false)
    {
        if (_verifying.Remove(hwnd, out var old)) App.CancelTimeout(ref old.Timer);
        var before = WindowInfo.VisualRect(hwnd);
        var zone = target;
        if (!WindowInfo.IsResizable(hwnd) && !User32.IsZoomed(hwnd) && !User32.IsIconic(hwnd) && !before.IsEmpty)
            target = zone.CenterSize(before.Width, before.Height); // fixed-size: keep its size, centre it in the zone
        var v = new Verification
        {
            Hwnd = hwnd, Target = target, Zone = zone, Before = before, Last = before, Activate = activate, Done = done,
            Deadline = Environment.TickCount64 + 1500, Dpi = User32.GetDpiForWindow(hwnd),
            ViaPlacement = User32.IsZoomed(hwnd) || User32.IsIconic(hwnd) || IsArranged(hwnd),
        };
        _verifying[hwnd] = v;
        _expected[hwnd] = (target, Environment.TickCount64 + 2500);
        var mon = MonitorService.FromRect(target);
        RectI work = mon?.Work ?? target, bounds = mon?.Bounds ?? target;
        worker.Enqueue(hwnd, () => PlaceCore(hwnd, target, activate, keepMinimized, work, bounds),
            () =>
            {
                if (!keepMinimized) { ScheduleVerify(v, 60); return; }
                if (_verifying.TryGetValue(hwnd, out var cur) && cur == v) Finish(v, true, reportTarget: true); // a newer Place may own it now
            });
    }

    void PlaceCore(nint hwnd, RectI target, bool activate, bool keepMinimized, RectI work, RectI bounds)
    {
        if (!User32.IsWindow(hwnd)) return;
        bool zoomed = User32.IsZoomed(hwnd), iconic = User32.IsIconic(hwnd);
        bool arranged = false;
        try { arranged = User32.IsWindowArranged(hwnd); } catch (EntryPointNotFoundException) { }

        if (zoomed || iconic || arranged)
        {
            var ins = _restoredInsets.TryGetValue(hwnd, out var c) ? c : DefaultInsets(hwnd);
            var wp = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            if (!User32.GetWindowPlacement(hwnd, ref wp)) return;
            var r = ins.Expand(target);
            // rcNormalPosition is in workspace coordinates (relative to the work area) for normal app windows.
            int ox = work.Left - bounds.Left, oy = work.Top - bounds.Top;
            if ((WindowInfo.ExStyle(hwnd) & C.WS_EX_TOOLWINDOW) != 0) { ox = 0; oy = 0; }
            wp.rcNormalPosition = new RECT(r.Left - ox, r.Top - oy, r.Right - ox, r.Bottom - oy);
            wp.showCmd = (uint)(iconic && keepMinimized ? C.SW_SHOWMINNOACTIVE : activate ? C.SW_RESTORE : C.SW_SHOWNOACTIVATE);
            wp.flags = C.WPF_ASYNCWINDOWPLACEMENT;
            User32.SetWindowPlacement(hwnd, wp);
        }
        else
        {
            var ins = MeasureInsets(hwnd);
            var r = ins.Expand(target);
            uint flags = C.SWP_NOZORDER | C.SWP_NOOWNERZORDER | C.SWP_NOACTIVATE | C.SWP_ASYNCWINDOWPOS;
            bool ok = User32.SetWindowPos(hwnd, 0, r.Left, r.Top, r.Width, r.Height, flags);
            if (Log.Verbose) Log.Debug($"SetWindowPos 0x{hwnd:X} {r} ok={ok} err={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
            if (activate) User32.SetForegroundWindow(hwnd);
        }
    }

    static bool IsArranged(nint hwnd)
    {
        try { return User32.IsWindowArranged(hwnd); } catch (EntryPointNotFoundException) { return false; }
    }

    Insets MeasureInsets(nint hwnd)
    {
        var ins = Insets.Between(WindowInfo.WindowRect(hwnd), WindowInfo.VisualRect(hwnd));
        if (!User32.IsZoomed(hwnd) && !User32.IsIconic(hwnd)) _restoredInsets[hwnd] = ins;
        return ins;
    }

    static Insets DefaultInsets(nint hwnd)
    {
        uint style = WindowInfo.Style(hwnd);
        if ((style & C.WS_THICKFRAME) == 0 || (style & C.WS_CAPTION) != C.WS_CAPTION) return default;
        uint dpi = User32.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        const int SM_CXSIZEFRAME = 32, SM_CXPADDEDBORDER = 92;
        int f = User32.GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + User32.GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
        return new Insets(f, 0, f, f);
    }

    void ScheduleVerify(Verification v, int ms)
    {
        if (!_verifying.TryGetValue(v.Hwnd, out var cur) || cur != v) return;
        v.Timer = App.SetTimeout(ms, () => { v.Timer = 0; Verify(v); });
    }

    void Verify(Verification v)
    {
        if (!_verifying.TryGetValue(v.Hwnd, out var cur) || cur != v) return;
        if (!User32.IsWindow(v.Hwnd)) { Finish(v, false); return; }
        var now = WindowInfo.VisualRect(v.Hwnd);
        if (now.NearlyEquals(v.Target, 2)) { Finish(v, true); return; }
        if (User32.IsZoomed(v.Hwnd) || User32.IsIconic(v.Hwnd))
        {
            if (Environment.TickCount64 > v.Deadline) { Finish(v, false); return; }
            ScheduleVerify(v, 100);
            return;
        }

        bool changed = !now.NearlyEquals(v.Before, 1);
        bool stable = now.NearlyEquals(v.Last, 0);
        v.Last = now;
        if (changed && stable)
        {
            uint dpi = User32.GetDpiForWindow(v.Hwnd);
            if ((dpi != 0 && dpi != v.Dpi && !v.DpiFixed) || v.ViaPlacement)
            {
                // Also after a SetWindowPlacement (restore from maximized/snapped): the insets used were a guess.
                v.ViaPlacement = false;
                // Moved to a monitor with another DPI: the app rescaled itself (WM_DPICHANGED). Re-place to the
                // ORIGINAL target once with re-measured insets — this is not a min-size constraint.
                v.DpiFixed = true;
                v.Dpi = dpi;
                v.Before = now;
                v.Deadline = Environment.TickCount64 + 1500;
                var t0 = v.Target;
                worker.Enqueue(v.Hwnd, () =>
                {
                    var r = MeasureInsets(v.Hwnd).Expand(t0);
                    User32.SetWindowPos(v.Hwnd, 0, r.Left, r.Top, r.Width, r.Height,
                        C.SWP_NOZORDER | C.SWP_NOOWNERZORDER | C.SWP_NOACTIVATE | C.SWP_ASYNCWINDOWPOS);
                }, () => ScheduleVerify(v, 80));
                return;
            }
            if (v.Corrected) { Finish(v, Fits(now, v.Zone)); return; }
            v.Corrected = true;
            if (now.Width > v.Target.Width + 2 || now.Height > v.Target.Height + 2)
            {
                // Min-size constraint: keep the size the app insists on, centre inside the zone.
                int w = Math.Max(now.Width, v.Target.Width), h = Math.Max(now.Height, v.Target.Height);
                int x = v.Zone.Left + (v.Zone.Width - w) / 2;
                int y = h > v.Zone.Height ? v.Zone.Top : v.Zone.Top + (v.Zone.Height - h) / 2;
                var mon = MonitorService.FromRect(v.Zone);
                if (mon != null)
                {
                    x = Math.Clamp(x, mon.Work.Left, Math.Max(mon.Work.Left, mon.Work.Right - w));
                    y = Math.Clamp(y, mon.Work.Top, Math.Max(mon.Work.Top, mon.Work.Bottom - h));
                }
                v.Target = RectI.FromXYWH(x, y, w, h);
            }
            // Re-place (insets are re-measured: DPI may have changed after a cross-monitor move).
            v.Before = now;
            v.Deadline = Environment.TickCount64 + 1500;
            var target = v.Target;
            worker.Enqueue(v.Hwnd, () =>
            {
                var r = MeasureInsets(v.Hwnd).Expand(target);
                User32.SetWindowPos(v.Hwnd, 0, r.Left, r.Top, r.Width, r.Height,
                    C.SWP_NOZORDER | C.SWP_NOOWNERZORDER | C.SWP_NOACTIVATE | C.SWP_ASYNCWINDOWPOS);
            }, () => ScheduleVerify(v, 80));
            return;
        }
        if (Environment.TickCount64 > v.Deadline)
        {
            if (v.Retried) { Finish(v, false); return; }
            v.Retried = true;
            v.Deadline = Environment.TickCount64 + 1500;
            var target = v.Target;
            var mon = MonitorService.FromRect(target);
            RectI work = mon?.Work ?? target, bounds = mon?.Bounds ?? target;
            worker.Enqueue(v.Hwnd, () => PlaceCore(v.Hwnd, target, false, false, work, bounds), () => ScheduleVerify(v, 100));
            return;
        }
        ScheduleVerify(v, 100);
    }

    static bool Fits(RectI now, RectI zone) => zone.Inflate(2, 2).Contains(now) || now.NearlyEquals(zone, 2);

    void Finish(Verification v, bool ok, bool reportTarget = false)
    {
        if (Log.Verbose) Log.Debug($"place 0x{v.Hwnd:X} target {v.Target} ok={ok} now {WindowInfo.VisualRect(v.Hwnd)}");
        _verifying.Remove(v.Hwnd);
        App.CancelTimeout(ref v.Timer);
        _expected[v.Hwnd] = (v.Target, Environment.TickCount64 + 600);
        var final = reportTarget || !User32.IsWindow(v.Hwnd) ? v.Target : WindowInfo.VisualRect(v.Hwnd);
        try { v.Done?.Invoke(ok, final); } catch (Exception ex) { Log.Error("snap done", ex); }
    }

    /// <summary>Maximize on the given monitor (moving the normal rect there first if needed).</summary>
    public void Maximize(nint hwnd, MonitorInfo monitor, Action? done = null)
    {
        _expected[hwnd] = (monitor.Work, Environment.TickCount64 + 2000);
        worker.Enqueue(hwnd, () =>
        {
            if (!User32.IsWindow(hwnd)) return;
            nint current = User32.MonitorFromWindow(hwnd, C.MONITOR_DEFAULTTONEAREST);
            if (current == monitor.HMonitor)
            {
                if (!User32.IsZoomed(hwnd)) User32.ShowWindowAsync(hwnd, C.SW_MAXIMIZE);
                return;
            }
            var wp = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            if (!User32.GetWindowPlacement(hwnd, ref wp)) return;
            var nr = FallbackRect(monitor.Work);
            int ox = monitor.Work.Left - monitor.Bounds.Left, oy = monitor.Work.Top - monitor.Bounds.Top;
            wp.rcNormalPosition = new RECT(nr.Left - ox, nr.Top - oy, nr.Right - ox, nr.Bottom - oy);
            wp.showCmd = C.SW_SHOWMAXIMIZED;
            wp.flags = C.WPF_ASYNCWINDOWPLACEMENT;
            User32.SetWindowPlacement(hwnd, wp);
        }, done);
    }

    public void Restore(nint hwnd, Action? done = null)
    {
        _expected[hwnd] = (default, Environment.TickCount64 + 1500);
        worker.Enqueue(hwnd, () => User32.ShowWindowAsync(hwnd, C.SW_RESTORE), done);
    }

    /// <summary>A minimized window whose slot is Maximized: normal rect on the target monitor, restore-to-maximized.</summary>
    public void SetMinimizedMaximized(nint hwnd, MonitorInfo monitor)
    {
        _expected[hwnd] = (monitor.Work, Environment.TickCount64 + 1500);
        worker.Enqueue(hwnd, () =>
        {
            if (!User32.IsWindow(hwnd) || !User32.IsIconic(hwnd)) return;
            var wp = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            if (!User32.GetWindowPlacement(hwnd, ref wp)) return;
            var nr = FallbackRect(monitor.Work);
            int ox = monitor.Work.Left - monitor.Bounds.Left, oy = monitor.Work.Top - monitor.Bounds.Top;
            wp.rcNormalPosition = new RECT(nr.Left - ox, nr.Top - oy, nr.Right - ox, nr.Bottom - oy);
            wp.showCmd = C.SW_SHOWMINNOACTIVE;
            wp.flags = C.WPF_ASYNCWINDOWPLACEMENT | WPF_RESTORETOMAXIMIZED;
            User32.SetWindowPlacement(hwnd, wp);
        });
    }

    const uint WPF_RESTORETOMAXIMIZED = 2;

    /// <summary>Set the restore rect of a minimized window without showing it.</summary>
    public void SetMinimizedTarget(nint hwnd, RectI target) => Place(hwnd, target, false, null, keepMinimized: true);

    /// <summary>The "unknown pre-snap rect" fallback: centred 60 % × 70 % of the work area.</summary>
    public static RectI FallbackRect(RectI work)
    {
        int w = work.Width * 60 / 100, h = work.Height * 70 / 100;
        return work.CenterSize(w, h);
    }

    /// <summary>The user grabbed the window (move/size loop): stop any verification/correction for it.</summary>
    public void Cancel(nint hwnd)
    {
        _expected.Remove(hwnd);
        if (_verifying.Remove(hwnd, out var v))
        {
            App.CancelTimeout(ref v.Timer);
            // Report where the window is now, so callers still learn its placed rect (N3 / settle / refit).
            var now = User32.IsWindow(hwnd) ? WindowInfo.VisualRect(hwnd) : v.Target;
            try { v.Done?.Invoke(Fits(now, v.Zone), now); } catch (Exception ex) { Log.Error("snap cancel", ex); }
        }
    }

    public void Forget(nint hwnd)
    {
        _restoredInsets.TryRemove(hwnd, out _);
        _expected.Remove(hwnd);
        if (_verifying.Remove(hwnd, out var v)) App.CancelTimeout(ref v.Timer);
    }
}
