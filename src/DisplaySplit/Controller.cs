using DisplaySplit.Core;
using DisplaySplit.Native;
using DisplaySplit.Platform;
using DisplaySplit.UI;

namespace DisplaySplit;

/// <summary>Wires the host window, tray, hotkeys, editor and Shift-drag to the <see cref="Engine"/>.</summary>
public sealed class Controller : IHostHandler, IDisposable
{
    // Hotkey ids
    const int HK_EDITOR = 1, HK_LEFT = 2, HK_RIGHT = 3, HK_LAYOUT_BASE = 10;

    // Tray menu ids
    const int M_EDIT = 1, M_ARRANGE = 2, M_EXCLUDE_FG = 3, M_FORGET_ALL = 4, M_DBL = 5, M_DRAG = 6, M_REMEMBER = 7,
        M_AUTOSTART = 8, M_PAUSE = 9, M_EXIT = 10, M_OPEN_FOLDER = 11, M_HELP = 12, M_PROJECT = 13, M_LAYOUT_BASE = 1000, M_PRESET_BASE = 3000,
        M_INCLUDE_BASE = 5000;

    readonly Settings _settings;
    readonly DebouncedSaver<Settings> _settingsSaver;
    readonly LayoutStore _layouts;
    readonly PlacementStore _placements;
    readonly PlacementWorker _worker;
    readonly SnapEngine _snap;
    readonly Engine _engine;
    readonly TrayIcon _tray;
    readonly DragOverlay _overlay = new();
    EditorSession? _editor;
    readonly List<string> _hotkeyFailures = [];
    nuint _displayTimer, _pruneTimer, _peekTimer;

    // Shift-drag state
    nint _dragHwnd;
    RectI _dragStart;
    PointI _dragCursorStart;
    bool _dragIsMove;
    long _dragNoButtonSince;
    bool _dragArmed, _dragCancelled;
    (MonitorInfo mon, Zone zone)? _dragTarget;
    nuint _dragTimer;

    public Controller(Settings settings, bool autostart)
    {
        _settings = settings;
        _settingsSaver = new DebouncedSaver<Settings>("settings.json", JsonCtx.Default.Settings, () => _settings);
        _layouts = new LayoutStore(() => _settings.GapPx);
        _layouts.Bind(MonitorService.Monitors);
        _placements = new PlacementStore();
        _worker = new PlacementWorker();
        _snap = new SnapEngine(_worker);
        _engine = new Engine(_settings, _layouts, _placements, _snap)
        {
            FlashZone = (_, r) => _overlay.Flash(r),
            DragBegin = OnDragBegin,
            DragEnd = OnDragEnd,
        };
        _tray = new TrayIcon(App.Host.Handle);

        if (_settings.StartWithWindows) Autostart.Set(true);
        RegisterHotkeys();
        CaptionProbe.Start();
        MouseHook.Start(App.Host.Handle);
        ApplyFeatureSwitches();
        WinEventWatcher.Start(_engine);
        _engine.Startup(autostart);
        UpdateTray();
        SchedulePrune();
        if (Environment.GetEnvironmentVariable("DISPLAYSPLIT_DEBUG") == "1") DebugStats();

        if (!_settings.FirstRunDone && !autostart)
            App.SetTimeout(300, () => OpenEditor(coach: true));
        Log.Info($"started (autostart={autostart}, monitors={MonitorService.Monitors.Count}, elevated={WindowInfo.SelfElevated})");
    }

    void SaveSettings() => _settingsSaver.MarkDirty();

    void ApplyFeatureSwitches() => MouseHook.Enabled = _settings.DoubleClickSnap && !_settings.Paused;

    // ------------------------------------------------------------------ hotkeys

    void RegisterHotkeys()
    {
        nint h = App.Host.Handle;
        _hotkeyFailures.Clear();
        void Reg(int id, uint mods, uint vk, string name)
        {
            if (!User32.RegisterHotKey(h, id, mods | C.MOD_NOREPEAT, vk)) _hotkeyFailures.Add(name);
        }
        Reg(HK_EDITOR, C.MOD_WIN | C.MOD_ALT, 'Z', "Win+Alt+Z");
        Reg(HK_LEFT, C.MOD_WIN | C.MOD_CONTROL | C.MOD_ALT, C.VK_LEFT, "Win+Ctrl+Alt+Left");
        Reg(HK_RIGHT, C.MOD_WIN | C.MOD_CONTROL | C.MOD_ALT, C.VK_RIGHT, "Win+Ctrl+Alt+Right");
        for (int i = 1; i <= 9; i++) Reg(HK_LAYOUT_BASE + i, C.MOD_WIN | C.MOD_CONTROL | C.MOD_ALT, (uint)('0' + i), $"Win+Ctrl+Alt+{i}");
        if (_hotkeyFailures.Count > 0) Log.Warn("hotkeys unavailable: " + string.Join(", ", _hotkeyFailures));
    }

    public void OnHotkey(int id)
    {
        if (id == HK_EDITOR) { OpenOrFocusEditor(); return; }
        if (_settings.Paused) return;
        if (_editor != null) { _editor.Focus(); return; } // layout hotkeys would be overwritten by the editor's Save
        if (id == HK_LEFT) _engine.MoveForegroundToNeighbour(-1);
        else if (id == HK_RIGHT) _engine.MoveForegroundToNeighbour(+1);
        else if (id > HK_LAYOUT_BASE && id <= HK_LAYOUT_BASE + 9)
        {
            User32.GetCursorPos(out var p);
            var mon = MonitorService.FromPoint(new PointI(p.X, p.Y));
            if (mon is null) return;
            var layouts = _layouts.For(mon).Layouts;
            int idx = id - HK_LAYOUT_BASE - 1;
            if (idx < layouts.Count) SwitchLayout(mon, layouts[idx].Id);
        }
    }

    // ------------------------------------------------------------------ layouts

    void SwitchLayout(MonitorInfo mon, string layoutId)
    {
        if (_layouts.For(mon).ActiveId == layoutId) { PeekZones(mon); return; }
        var captured = _engine.CaptureZoneWindows(mon);
        _layouts.SetActive(mon, layoutId);
        _engine.ApplyLayoutChange(mon, captured);
        UpdateTray();
        PeekZones(mon);
    }

    void PeekZones(MonitorInfo mon)
    {
        App.CancelTimeout(ref _peekTimer);
        _overlay.ShowZones(_layouts.Zones(mon), 0);
        _peekTimer = App.SetTimeout(700, () => { _peekTimer = 0; if (_dragHwnd == 0) _overlay.Hide(); });
    }

    void ApplyPresetAsNewLayout(MonitorInfo mon, string presetKey)
    {
        var e = _layouts.For(mon);
        var unit = new RectI(0, 0, 1200, 1200);
        var want = LayoutOps.Resolve(LayoutPresets.Create(presetKey), unit).Select(z => z.Rect).ToList();
        var existing = e.Layouts.FirstOrDefault(l =>
        {
            var have = LayoutOps.Resolve(l, unit).Select(z => z.Rect).ToList();
            return have.Count == want.Count && have.Zip(want).All(p => p.First.NearlyEquals(p.Second, 2));
        });
        if (existing != null) { SwitchLayout(mon, existing.Id); return; }
        var layout = LayoutPresets.Create(presetKey);
        var list = e.Layouts.Select(l => l.Clone()).ToList();
        list.Add(layout);
        var captured = _engine.CaptureZoneWindows(mon);
        _layouts.ReplaceAll(mon, list, layout.Id);
        _engine.ApplyLayoutChange(mon, captured);
        UpdateTray();
        PeekZones(mon);
    }

    // ------------------------------------------------------------------ editor

    void OpenOrFocusEditor()
    {
        if (_editor != null) _editor.Focus();
        else OpenEditor(coach: false);
    }

    void OpenEditor(bool coach)
    {
        if (_editor != null) return;
        _overlay.Hide();
        MonitorService.Refresh();
        _layouts.Bind(MonitorService.Monitors);
        try
        {
            _editor = new EditorSession(_layouts, _settings.GapPx, coach, OnEditorClosed);
        }
        catch (Exception ex)
        {
            Log.Error("editor", ex);
            _editor?.Dispose();
            _editor = null;
        }
    }

    void OnEditorClosed(EditorSession session, bool save)
    {
        _editor = null;
        bool firstRun = !_settings.FirstRunDone;
        if (save)
        {
            var captured = MonitorService.Monitors.ToDictionary(m => m.Key, m => _engine.CaptureZoneWindows(m));
            if (_settings.GapPx != session.Gap) { _settings.GapPx = session.Gap; SaveSettings(); }
            foreach (var st in session.States)
            {
                foreach (var l in st.Layouts) LayoutOps.Repair(l);
                _layouts.ReplaceAll(st.Monitor, st.Layouts, st.Current.Id);
            }
            foreach (var m in MonitorService.Monitors)
                _engine.ApplyLayoutChange(m, captured.GetValueOrDefault(m.Key) ?? []);
            _layouts.Flush();
        }
        if (firstRun)
        {
            _settings.FirstRunDone = true;
            SaveSettings();
            _tray.Balloon("DisplaySplit is running",
                "Double-click a title bar to snap into a zone (Shift = full screen). Find me in the tray (^) — drag the icon to the taskbar to pin it.");
        }
        UpdateTray();
    }

    public void OnOpenEditorRequest() => OpenOrFocusEditor();

    // ------------------------------------------------------------------ snapping (hook → UI)

    public void OnSnapRequest(nint hwnd, int x, int y, bool shift)
    {
        if (_dragHwnd == hwnd) _dragCancelled = true;
        if (_editor != null) return;
        _engine.OnSnapRequest(hwnd, new PointI(x, y), shift);
    }

    // ------------------------------------------------------------------ Shift-drag (N1)

    void OnDragBegin(nint hwnd, RectI start)
    {
        StopDrag();
        if (!_settings.ShiftDragSnap || _settings.Paused || _editor != null) return;
        if (!WindowInfo.IsManageable(hwnd) || WindowInfo.IsBlockedByElevation(hwnd)) return;
        _dragHwnd = hwnd;
        _dragStart = start;
        User32.GetCursorPos(out var cp);
        _dragCursorStart = new PointI(cp.X, cp.Y);
        // Move or resize? Decided once: resize handles live in the invisible borders outside the visible frame and
        // in a thin band along its top edge. (Maximized/snapped windows are restored inside the move loop, so their
        // size legitimately changes during a move — comparing sizes would never arm.)
        var mon = MonitorService.FromPoint(_dragCursorStart);
        int band = (int)Math.Round(5 * (mon?.Scale ?? 1));
        _dragIsMove = User32.IsZoomed(hwnd) || (start.Inflate(-band, 0) is var inner &&
            new RectI(inner.Left, start.Top + band, inner.Right, start.Bottom - band).Contains(_dragCursorStart));
        _dragNoButtonSince = 0;
        _dragArmed = false;
        _dragCancelled = false;
        _dragTarget = null;
        _dragTimer = App.SetTimeout(16, DragTick);
    }

    void DragTick()
    {
        _dragTimer = 0;
        if (_dragHwnd == 0) return;
        if (!User32.IsWindow(_dragHwnd)) { StopDrag(); return; }
        // Safety net: if MOVESIZEEND was missed, stop once no button has been down for a while.
        bool buttonDown = User32.GetAsyncKeyState(C.VK_LBUTTON) < 0 || User32.GetAsyncKeyState(C.VK_RBUTTON) < 0;
        if (!buttonDown)
        {
            if (_dragNoButtonSince == 0) _dragNoButtonSince = Environment.TickCount64;
            else if (Environment.TickCount64 - _dragNoButtonSince > 600) { StopDrag(); return; }
        }
        else _dragNoButtonSince = 0;
        bool shift = User32.GetAsyncKeyState(C.VK_SHIFT) < 0;
        User32.GetCursorPos(out var p);
        var pt = new PointI(p.X, p.Y);
        int dx = User32.GetSystemMetrics(C.SM_CXDRAG), dy = User32.GetSystemMetrics(C.SM_CYDRAG);
        bool moved = Math.Abs(pt.X - _dragCursorStart.X) >= dx || Math.Abs(pt.Y - _dragCursorStart.Y) >= dy;
        _dragArmed = shift && moved && _dragIsMove && !_dragCancelled;
        if (_dragArmed)
        {
            var mon = MonitorService.FromPoint(pt);
            if (mon != null)
            {
                var zones = _layouts.Zones(mon);
                var z = LayoutOps.ZoneAt(zones, pt);
                _dragTarget = z is { } zz ? (mon, zz) : null;
                _overlay.ShowZones(zones, z?.Id ?? 0);
            }
        }
        else
        {
            _dragTarget = null;
            _overlay.Hide();
        }
        _dragTimer = App.SetTimeout(16, DragTick);
    }

    bool OnDragEnd(nint hwnd)
    {
        if (hwnd != _dragHwnd) return false;
        // Evaluate the final state once more (Shift may have been pressed in the last frame).
        if (_dragTimer != 0) { App.CancelTimeout(ref _dragTimer); DragTick(); App.CancelTimeout(ref _dragTimer); }
        if (hwnd != _dragHwnd) return false;
        bool armed = _dragArmed && !_dragCancelled;
        var target = _dragTarget;
        var start = _dragStart;
        _dragCursorStartAtEnd = _dragCursorStart;
        StopDrag();
        if (!armed || target is null) return false;
        User32.GetCursorPos(out var endCursor);
        if (Math.Abs(endCursor.X - _dragCursorStartAtEnd.X) < 2 && Math.Abs(endCursor.Y - _dragCursorStartAtEnd.Y) < 2) return false; // no real move
        var (mon, zone) = target.Value;
        App.SetTimeout(1, () =>
        {
            if (!User32.IsWindow(hwnd)) return;
            _engine.NotePreSnap(hwnd, start, mon);
            _engine.SnapToZone(hwnd, mon, zone, activate: false);
        });
        return true;
    }

    PointI _dragCursorStartAtEnd;

    void StopDrag()
    {
        App.CancelTimeout(ref _dragTimer);
        if (_dragHwnd != 0) _overlay.Hide();
        _dragHwnd = 0;
        _dragArmed = false;
        _dragTarget = null;
    }

    // ------------------------------------------------------------------ tray

    void UpdateTray()
    {
        var mon = MonitorService.Monitors.FirstOrDefault(m => m.Primary) ?? MonitorService.Monitors.FirstOrDefault();
        string layout = mon != null ? _layouts.Active(mon).Name : "";
        string tip = "DisplaySplit" + (layout.Length > 0 ? " — " + layout : "") + (_settings.Paused ? " (paused)" : "");
        if (_hotkeyFailures.Count > 0) tip += "\nHotkey in use: " + string.Join(", ", _hotkeyFailures);
        _tray.SetState(tip.Length > 127 ? tip[..127] : tip, _settings.Paused);
    }

    public void OnTrayMessage(uint evt, int x, int y)
    {
        // NOTIFYICON_VERSION_4: one left click = NIN_SELECT, right click / menu key = WM_CONTEXTMENU.
        // (The raw WM_LBUTTONUP/WM_RBUTTONUP that also arrive are ignored so nothing fires twice.)
        switch (evt)
        {
            case C.NIN_SELECT:
            case C.NIN_KEYSELECT:
                OpenOrFocusEditor();
                break;
            case C.WM_CONTEXTMENU:
                ShowTrayMenu(x, y);
                break;
        }
    }

    void ShowTrayMenu(int x, int y)
    {
        var items = new List<MenuItem>
        {
            new(0, $"DisplaySplit {App.Version}", Enabled: false),
            MenuItem.Sep(),
            new(M_EDIT, "Edit layout…\tWin+Alt+Z", Default: true),
        };
        var monitors = MonitorService.Monitors;
        foreach (var (mon, mi) in monitors.Select((m, i) => (m, i)))
        {
            var e = _layouts.For(mon);
            var sub = new List<MenuItem>();
            for (int i = 0; i < e.Layouts.Count; i++)
            {
                string hk = i < 9 ? $"\tWin+Ctrl+Alt+{i + 1}" : "";
                sub.Add(new MenuItem(M_LAYOUT_BASE + mi * 100 + i, e.Layouts[i].Name + hk, Checked: e.Layouts[i].Id == e.ActiveId, Radio: true));
            }
            sub.Add(MenuItem.Sep());
            var presets = LayoutPresets.All.Select((p, pi) => new MenuItem(M_PRESET_BASE + mi * 100 + pi, p.Name)).ToList();
            sub.Add(new MenuItem(0, "New from preset", Children: presets));
            string title = monitors.Count > 1 ? $"Layout — {mon.FriendlyName} ({mon.Bounds.Width}×{mon.Bounds.Height})" : "Layout";
            items.Add(new MenuItem(0, title, Children: sub));
        }
        items.Add(new MenuItem(M_ARRANGE, "Arrange all windows now", Enabled: _settings.RememberPositions));
        string? fgIdentity = _engine.IdentityOf(_engine.LastForeignForeground);
        string fgName = fgIdentity is null ? "" : FriendlyIdentity(fgIdentity);
        items.Add(new MenuItem(M_EXCLUDE_FG, fgIdentity is null ? "Don't remember current app" : $"Don't remember \"{fgName}\"",
            Enabled: fgIdentity != null && !_settings.ExcludedApps.Contains(fgIdentity)));
        if (_settings.ExcludedApps.Count > 0)
        {
            var ex = _settings.ExcludedApps.Select((id, i) => new MenuItem(M_INCLUDE_BASE + i, "Remember again: " + FriendlyIdentity(id))).ToList();
            items.Add(new MenuItem(0, $"Excluded apps ({_settings.ExcludedApps.Count})", Children: ex));
        }
        items.Add(new MenuItem(M_FORGET_ALL, "Forget all remembered positions…"));
        items.Add(MenuItem.Sep());
        items.Add(new MenuItem(M_DBL, "Double-click title bar to snap", Checked: _settings.DoubleClickSnap));
        items.Add(new MenuItem(M_DRAG, "Hold Shift while dragging to snap", Checked: _settings.ShiftDragSnap));
        items.Add(new MenuItem(M_REMEMBER, "Remember app positions", Checked: _settings.RememberPositions));
        items.Add(new MenuItem(M_AUTOSTART, "Start with Windows", Checked: Autostart.IsEnabled()));
        items.Add(MenuItem.Sep());
        items.Add(new MenuItem(M_HELP, "How to use && shortcuts…"));
        items.Add(new MenuItem(M_PROJECT, "Project page (GitHub)…"));
        items.Add(new MenuItem(M_OPEN_FOLDER, "Open settings folder"));
        items.Add(new MenuItem(M_PAUSE, _settings.Paused ? "Resume" : "Pause"));
        items.Add(new MenuItem(M_EXIT, "Exit"));

        if (_editor != null)
        {
            // While editing, layout commands from the tray would be overwritten by the editor's Save.
            items = items.Select(i => i.Children != null && i.Text.StartsWith("Layout", StringComparison.Ordinal) ? i with { Enabled = false } : i).ToList();
        }
        int cmd = _tray.ShowMenu(items, x, y);
        if (cmd != 0) OnMenu(cmd, fgIdentity);
        _editor?.Focus(); // the menu took the foreground; give keyboard focus back to an open editor
    }

    static string FriendlyIdentity(string identity)
    {
        if (identity.StartsWith("exe:", StringComparison.Ordinal)) return Path.GetFileName(identity[4..]);
        if (identity.StartsWith("aumid:", StringComparison.Ordinal))
        {
            string a = identity[6..];
            int bang = a.IndexOf('!');
            if (bang > 0) a = a[..bang];
            int us = a.IndexOf('_');
            if (us > 0) a = a[..us];
            return a.Length > 40 ? a[..40] + "…" : a;
        }
        return identity;
    }

    void OnMenu(int cmd, string? fgIdentity)
    {
        var monitors = MonitorService.Monitors;
        if (cmd >= M_INCLUDE_BASE)
        {
            int i = cmd - M_INCLUDE_BASE;
            if (i < _settings.ExcludedApps.Count) { _settings.ExcludedApps.RemoveAt(i); SaveSettings(); }
            return;
        }
        if (cmd >= M_PRESET_BASE)
        {
            int mi = (cmd - M_PRESET_BASE) / 100, pi = (cmd - M_PRESET_BASE) % 100;
            if (mi < monitors.Count && pi < LayoutPresets.All.Count) ApplyPresetAsNewLayout(monitors[mi], LayoutPresets.All[pi].Key);
            return;
        }
        if (cmd >= M_LAYOUT_BASE)
        {
            int mi = (cmd - M_LAYOUT_BASE) / 100, li = (cmd - M_LAYOUT_BASE) % 100;
            if (mi < monitors.Count)
            {
                var layouts = _layouts.For(monitors[mi]).Layouts;
                if (li < layouts.Count) SwitchLayout(monitors[mi], layouts[li].Id);
            }
            return;
        }
        switch (cmd)
        {
            case M_EDIT: OpenOrFocusEditor(); break;
            case M_PROJECT:
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(App.ProjectUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Warn(ex.Message); }
                break;
            case M_HELP:
                if (_editor != null) _editor.ShowHelp();
                else OpenEditor(coach: true);
                break;
            case M_ARRANGE: _engine.ArrangeAll(); break;
            case M_EXCLUDE_FG:
                if (fgIdentity != null) { _engine.Exclude(fgIdentity); SaveSettings(); }
                break;
            case M_FORGET_ALL:
                if (User32.MessageBoxW(App.Host.Handle, "Forget every remembered window position?\n\nWindows stay where they are now; apps will no longer be restored into zones until you snap them again.",
                        "DisplaySplit", 0x1 /*OKCANCEL*/ | 0x30 /*WARNING*/ | 0x40000 /*TOPMOST*/) == 1)
                    _engine.ForgetAll();
                break;
            case M_DBL: _settings.DoubleClickSnap = !_settings.DoubleClickSnap; ApplyFeatureSwitches(); SaveSettings(); break;
            case M_DRAG: _settings.ShiftDragSnap = !_settings.ShiftDragSnap; SaveSettings(); break;
            case M_REMEMBER: _settings.RememberPositions = !_settings.RememberPositions; SaveSettings(); break;
            case M_AUTOSTART:
                bool on = !Autostart.IsEnabled();
                Autostart.Set(on);
                _settings.StartWithWindows = on;
                SaveSettings();
                break;
            case M_OPEN_FOLDER:
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Storage.Home}\"") { UseShellExecute = false }); }
                catch (Exception ex) { Log.Warn(ex.Message); }
                break;
            case M_PAUSE:
                _settings.Paused = !_settings.Paused;
                ApplyFeatureSwitches();
                SaveSettings();
                UpdateTray();
                break;
            case M_EXIT: App.Quit(); break;
        }
    }

    // ------------------------------------------------------------------ system notifications

    public void OnDisplayChanged() => ScheduleDisplayRefresh(1500);
    public void OnWorkAreaChanged() => ScheduleDisplayRefresh(300);

    void ScheduleDisplayRefresh(int ms)
    {
        App.CancelTimeout(ref _displayTimer);
        _displayTimer = App.SetTimeout(ms, () =>
        {
            _displayTimer = 0;
            var before = MonitorService.Monitors.Select(m => (m.Key, m.Bounds, m.Work, m.Dpi)).ToList();
            MonitorService.Refresh();
            var after = MonitorService.Monitors.Select(m => (m.Key, m.Bounds, m.Work, m.Dpi)).ToList();
            if (_editor != null && !before.SequenceEqual(after)) _editor.Cancel(); // geometry changed under the editor
            _layouts.Bind(MonitorService.Monitors);
            _engine.OnDisplayChanged();
            MouseHook.RequestReinstall();
            UpdateTray();
        });
    }

    public void OnTaskbarCreated()
    {
        _tray.Add();
        UpdateTray();
    }

    public void OnEndSession() => Flush();

    public void OnResumeOrUnlock()
    {
        MouseHook.RequestReinstall();
        ScheduleDisplayRefresh(1000);
    }

    public void OnShellWindowDestroyed(nint hwnd) => _engine.OnWindowDestroyed(hwnd);

    void DebugStats()
    {
        Log.Debug($"stats downs={MouseHook.DownCount} swallowed={MouseHook.Swallowed} misses={MouseHook.ProbeMisses} hook={MouseHook.IsRunning}");
        App.SetTimeout(2000, DebugStats);
    }

    void SchedulePrune() => _pruneTimer = App.SetTimeout(60_000, () => { _pruneTimer = 0; _engine.Prune(); SchedulePrune(); });

    public void Flush()
    {
        _settingsSaver.Flush();
        _layouts.Flush();
        _placements.Flush();
    }

    public void Dispose()
    {
        _editor?.Dispose();
        _editor = null;
        StopDrag();
        WinEventWatcher.Stop();
        MouseHook.Stop();
        Flush();
        _overlay.Dispose();
        _tray.Dispose();
        _worker.Dispose();
    }
}
