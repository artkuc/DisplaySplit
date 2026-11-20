using DisplaySplit.Core;
using DisplaySplit.Native;
using DisplaySplit.Platform;

namespace DisplaySplit;

/// <summary>
/// Window management brain (UI thread only): double-click snapping, zone tracking, slot persistence and
/// restore-on-launch with settle sessions.
/// </summary>
public sealed class Engine : IWinEventSink
{
    sealed class Tracked
    {
        public nint Hwnd;
        public string? Key;          // AppIdentity key (null = unresolved / not resolvable)
        public bool KeyResolved;
        public string? SlotId;
        public SlotKind? Kind;       // what we believe the window is in
        public string? MonitorKey;
        public string? LayoutId;
        public int ZoneId;
        public RectI ZoneRect;
        public RectI? PlacedRect;    // final rect of our last placement (move-only windows never match the zone exactly)
        public RectI? PreSnap;
        public bool PreSnapMaximized;
        public long AppliedTick;     // when we last restored/placed it from a slot
        public bool MaxFromZone;     // maximized while it was in a zone (restore → back to that zone slot)
        public readonly Dictionary<string, int> Explicit = [];
        public readonly Dictionary<string, int> Derived = [];
    }

    sealed class Settle
    {
        public nint Hwnd;
        public long Start;
        public long Deadline;
        public RectI Target;
        public SlotKind Kind;
        public bool Applied;
        public int Reapplies;
        public bool AcceptedMaximize;
        public string Title = "";
        public bool TitleRematched;
        public long ResolveDeadline;
        public nuint Timer;
    }

    readonly Settings _settings;
    readonly LayoutStore _layouts;
    readonly PlacementStore _placements;
    readonly SnapEngine _snap;
    readonly Dictionary<nint, Tracked> _tracked = [];
    readonly HashSet<nint> _seen = [];
    readonly Dictionary<nint, Settle> _settling = [];
    readonly Dictionary<nint, long> _pendingMonitor = []; // windows whose slot's monitor is missing → retry on display change

    nint _msHwnd;
    RectI _msStart;
    bool _msStartZoomed;

    public nint LastForeignForeground { get; private set; }
    public Action<nint, RectI>? FlashZone;              // UI feedback
    public Func<nint, bool>? DragEnd;                   // returns true if Shift-drag handled the drop
    public Action<nint, RectI>? DragBegin;

    public Engine(Settings settings, LayoutStore layouts, PlacementStore placements, SnapEngine snap)
    {
        _settings = settings;
        _layouts = layouts;
        _placements = placements;
        _snap = snap;
    }

    bool Remembering => _settings.RememberPositions && !_settings.Paused;

    Tracked Track(nint hwnd)
    {
        if (!_tracked.TryGetValue(hwnd, out var t)) _tracked[hwnd] = t = new Tracked { Hwnd = hwnd };
        return t;
    }

    string? KeyOf(nint hwnd)
    {
        var t = Track(hwnd);
        if (!t.KeyResolved)
        {
            var r = AppIdentity.Resolve(hwnd);
            if (r is null) return null; // retry later (UWP frame not attached yet)
            t.Key = r.Key;
            t.KeyResolved = true;
        }
        return t.Key;
    }

    bool IsExcluded(string key) => _settings.ExcludedApps.Contains(AppIdentityRules.IdentityPart(key));

    // ------------------------------------------------------------------ startup

    /// <summary>Called once at startup. With --autostart arranges existing windows; always seeds the seen set.</summary>
    public void Startup(bool autostart)
    {
        var all = WindowInfo.TopLevelWindows();
        if (!autostart)
        {
            foreach (var h in all) _seen.Add(h); // manual start: everything that exists now is "old"
            LinkExistingWindows(all);
            return;
        }
        // Logon: windows the arrange pass considers are "seen"; still-hidden windows of apps that are launching right
        // now (tray-first Electron apps…) will be handled by the normal first-show path.
        foreach (var h in all)
            if (WindowInfo.IsManageable(h, allowShellCloaked: true) || (User32.IsIconic(h) && WindowInfo.IsManageable(h, true, false)))
                _seen.Add(h);
        if (_settings.ArrangeOnLogon && Remembering)
        {
            var occupied = new Dictionary<string, HashSet<string>>();
            int applied = 0;
            foreach (var h in all)
            {
                if (!WindowInfo.IsManageable(h, allowShellCloaked: true, requireVisible: true)
                    && !(User32.IsIconic(h) && WindowInfo.IsManageable(h, true, false))) continue;
                var key = KeyOf(h);
                if (key is null || IsExcluded(key) || !_placements.HasSlots(key)) continue;
                if (!occupied.TryGetValue(key, out var occ)) occupied[key] = occ = [];
                var slot = SlotMatcher.Pick(_placements.Get(key), occ, AppIdentityRules.NormalizeTitle(WindowInfo.Title(h)));
                if (slot is null) continue;
                occ.Add(slot.Id);
                if (ApplySlot(h, key, slot, fromArrange: true))
                {
                    applied++;
                    // Apps that are still starting (Chrome, VS Code…) restore their own bounds a moment later:
                    // watch them like newly shown windows.
                    var started = WindowInfo.ProcessStartUtc(WindowInfo.ProcessIdOf(h));
                    if (started is { } st && (DateTime.UtcNow - st).TotalSeconds < RestorePolicy.FreshProcessSeconds)
                        StartAppliedSettle(h, slot.Kind);
                }
            }
            Log.Info($"startup arrange: {applied} window(s)");
        }
    }

    /// <summary>
    /// Manual (re)start: windows that already sit exactly in their remembered zone (or maximized for a Maximized
    /// slot) are linked to that slot without being moved, so later snaps update it instead of creating duplicates.
    /// </summary>
    void LinkExistingWindows(List<nint> all)
    {
        if (!Remembering) return;
        var occupied = new Dictionary<string, HashSet<string>>();
        foreach (var h in all)
        {
            if (!WindowInfo.IsManageable(h, allowShellCloaked: true)) continue;
            var key = KeyOf(h);
            if (key is null || IsExcluded(key) || !_placements.HasSlots(key)) continue;
            if (!occupied.TryGetValue(key, out var occ)) occupied[key] = occ = [];
            bool zoomed = User32.IsZoomed(h);
            var cur = WindowInfo.VisualRect(h);
            foreach (var slot in _placements.Get(key).OrderByDescending(s => SlotMatcher.Score(s, AppIdentityRules.NormalizeTitle(WindowInfo.Title(h)))))
            {
                if (occ.Contains(slot.Id)) continue;
                var res = ResolveSlot(slot);
                if (res is null) continue;
                var (mon, zone) = res.Value;
                bool match = slot.Kind == SlotKind.Maximized
                    ? zoomed && User32.MonitorFromWindow(h, C.MONITOR_DEFAULTTONEAREST) == mon.HMonitor
                    : !zoomed && zone is { } z && cur.NearlyEquals(z.Rect, 4);
                if (!match) continue;
                occ.Add(slot.Id);
                var t = Track(h);
                t.SlotId = slot.Id;
                LoadMaps(t, slot);
                if (zone is { } zz) { SetZoneFromSlot(t, mon, zz, slot); t.PlacedRect = cur; }
                else { t.Kind = SlotKind.Maximized; t.MonitorKey = mon.Key; }
                break;
            }
        }
    }

    /// <summary>Slot ids held by live tracked windows of <paramref name="key"/> (optionally excluding one window).</summary>
    HashSet<string> OccupiedSlots(string key, nint except = 0)
    {
        var set = new HashSet<string>();
        foreach (var t in _tracked.Values)
            if (t.Hwnd != except && t.Key == key && t.SlotId != null && User32.IsWindow(t.Hwnd)) set.Add(t.SlotId);
        return set;
    }

    /// <summary>Tray "Arrange all windows now": apply remembered slots to every open window.</summary>
    public void ArrangeAll()
    {
        if (!Remembering) return;
        var candidates = new List<(nint h, string key)>();
        foreach (var h in WindowInfo.TopLevelWindows())
        {
            if (!WindowInfo.IsManageable(h, allowShellCloaked: true)) continue;
            var key = KeyOf(h);
            if (key is null || IsExcluded(key) || !_placements.HasSlots(key)) continue;
            candidates.Add((h, key));
        }
        // Windows that already own a valid slot keep it; the others pick from what is left.
        var occupied = new Dictionary<string, HashSet<string>>();
        var pending = new List<(nint h, string key)>();
        foreach (var (h, key) in candidates)
        {
            if (!occupied.TryGetValue(key, out var occ)) occupied[key] = occ = [];
            var t = Track(h);
            if (t.SlotId != null && !occ.Contains(t.SlotId) && _placements.Find(key, t.SlotId) is { } own)
            {
                occ.Add(own.Id);
                ApplySlot(h, key, own, fromArrange: true);
            }
            else pending.Add((h, key));
        }
        foreach (var (h, key) in pending)
        {
            var occ = occupied[key];
            var slot = SlotMatcher.Pick(_placements.Get(key), occ, AppIdentityRules.NormalizeTitle(WindowInfo.Title(h)));
            if (slot is null) continue;
            occ.Add(slot.Id);
            ApplySlot(h, key, slot, fromArrange: true);
        }
    }

    // ------------------------------------------------------------------ double-click

    public void OnSnapRequest(nint hwnd, PointI pt, bool shift)
    {
        if (Log.Verbose) Log.Debug($"snap request 0x{hwnd:X} at {pt} shift={shift}");
        if (_settings.Paused || !User32.IsWindow(hwnd)) return;
        EndSettle(hwnd);
        _pendingMonitor.Remove(hwnd);
        var mon = MonitorService.FromPoint(pt);
        if (mon is null) return;
        var t = Track(hwnd);

        if (shift)
        {
            if (User32.IsZoomed(hwnd))
            {
                _snap.Restore(hwnd, () => App.SetTimeout(350, () => OnRestoredFromMaximize(hwnd))); // ShowWindowAsync completes later
            }
            else if (WindowInfo.CanMaximize(hwnd))
            {
                var curRect = WindowInfo.VisualRect(hwnd);
                t.MaxFromZone = t.Kind == SlotKind.Zone && curRect.NearlyEquals(t.ZoneRect, 4);
                if (t.Kind != SlotKind.Zone) { t.PreSnap = curRect; t.PreSnapMaximized = false; }
                _snap.Maximize(hwnd, mon);
                t.Kind = SlotKind.Maximized;
                t.MonitorKey = mon.Key;
                RecordSlot(hwnd, SlotKind.Maximized, mon, null);
                FlashZone?.Invoke(hwnd, mon.Work);
            }
            return;
        }

        var zones = _layouts.Zones(mon);
        var zone = LayoutOps.ZoneAt(zones, pt);
        if (zone is null) return;
        var z = zone.Value;
        bool zoomed = User32.IsZoomed(hwnd);
        var cur = WindowInfo.VisualRect(hwnd);

        bool inThisZone = cur.NearlyEquals(z.Rect, 3) ||
            (t.Kind == SlotKind.Zone && t.ZoneRect == z.Rect && t.PlacedRect is { } pr && pr.NearlyEquals(cur, 2));
        if (!zoomed && inThisZone)
        {
            // N3: already filling that zone → restore previous size (or a sensible fallback) and forget the slot.
            bool wasMax = t.PreSnapMaximized;
            var back = t.PreSnap is { } p && !p.IsEmpty && !p.NearlyEquals(z.Rect, 3) ? p : SnapEngine.FallbackRect(mon.Work);
            t.Kind = null;
            t.PreSnap = null;
            t.PreSnapMaximized = false;
            t.Explicit.Clear(); t.Derived.Clear();
            DeleteSlot(hwnd);
            if (wasMax) _snap.Maximize(hwnd, mon);
            else _snap.Place(hwnd, back, activate: true);
            return;
        }

        if (zoomed) { t.PreSnapMaximized = true; }
        else if (!IsInAnyZone(cur, mon)) { t.PreSnap = cur; t.PreSnapMaximized = false; }
        SnapToZone(hwnd, mon, z, activate: true);
    }

    /// <summary>
    /// A window left the maximized state (Shift+double-click, restore button, Win+Down…). If its restored rect is a
    /// zone (typically the zone it was maximized from) it goes back to a Zone slot; otherwise its slot is forgotten.
    /// </summary>
    void OnRestoredFromMaximize(nint hwnd, int attempt = 0)
    {
        if (!User32.IsWindow(hwnd)) return;
        if (User32.IsZoomed(hwnd))
        {
            if (attempt < 6) App.SetTimeout(300, () => OnRestoredFromMaximize(hwnd, attempt + 1));
            return;
        }
        var t = Track(hwnd);
        t.MaxFromZone = false;
        var cur = WindowInfo.VisualRect(hwnd);
        var mon = MonitorService.FromRect(cur);
        var zone = mon is null ? null : _layouts.Zones(mon).Where(z => z.Rect.NearlyEquals(cur, 4)).Cast<Zone?>().FirstOrDefault();
        if (mon != null && zone is { } z)
        {
            SetZone(t, mon, z);
            t.PlacedRect = cur;
            RecordSlot(hwnd, SlotKind.Zone, mon, z);
        }
        else
        {
            t.Kind = null;
            t.Explicit.Clear(); t.Derived.Clear();
            DeleteSlot(hwnd);
        }
    }

    bool IsInAnyZone(RectI r, MonitorInfo mon) => _layouts.Zones(mon).Any(z => z.Rect.NearlyEquals(r, 4));

    void SetZone(Tracked t, MonitorInfo mon, Zone z)
    {
        string layoutId = _layouts.Active(mon).Id;
        t.Kind = SlotKind.Zone;
        t.MonitorKey = mon.Key;
        t.LayoutId = layoutId;
        t.ZoneId = z.Id;
        t.ZoneRect = z.Rect;
        t.Explicit[layoutId] = z.Id;
        t.Derived.Clear(); // derived entries were computed from the previous position
    }

    void PlaceTracked(Tracked t, RectI target, bool activate)
    {
        t.PlacedRect = null;
        _snap.Place(t.Hwnd, target, activate, (ok, final) => { t.PlacedRect = final; });
    }

    /// <summary>Remember the free-floating rect a window had before a Shift-drag snap (for N3).</summary>
    public void NotePreSnap(nint hwnd, RectI rect, MonitorInfo mon)
    {
        if (User32.IsZoomed(hwnd) || IsInAnyZone(rect, mon)) return;
        var t = Track(hwnd);
        t.PreSnap = rect;
        t.PreSnapMaximized = false;
    }

    /// <summary>Snap to a zone (used by double-click, Shift-drag, hotkeys) and remember it.</summary>
    public void SnapToZone(nint hwnd, MonitorInfo mon, Zone z, bool activate)
    {
        var t = Track(hwnd);
        SetZone(t, mon, z);
        PlaceTracked(t, z.Rect, activate);
        RecordSlot(hwnd, SlotKind.Zone, mon, z);
        FlashZone?.Invoke(hwnd, z.Rect);
    }

    /// <summary>Hotkey: move the foreground window to the neighbouring zone (dir = -1 / +1).</summary>
    public void MoveForegroundToNeighbour(int dir)
    {
        nint hwnd = User32.GetForegroundWindow();
        if (!WindowInfo.IsManageable(hwnd) || WindowInfo.IsBlockedByElevation(hwnd)) return;
        var cur = WindowInfo.VisualRect(hwnd);
        var mon = MonitorService.FromRect(cur);
        if (mon is null) return;
        var zones = _layouts.Zones(mon).OrderBy(z => z.Rect.Left).ThenBy(z => z.Rect.Top).ToList();
        if (zones.Count == 0) return;
        int idx = zones.FindIndex(z => z.Rect.NearlyEquals(cur, 4));
        if (idx < 0 && _tracked.TryGetValue(hwnd, out var tr) && tr.Kind == SlotKind.Zone && tr.PlacedRect is { } pr && pr.NearlyEquals(cur, 2))
            idx = zones.FindIndex(z => z.Rect == tr.ZoneRect);
        if (idx < 0)
        {
            // Not in a zone: pick the zone containing the window centre.
            var c = LayoutOps.ZoneAt(zones, cur.Center);
            idx = c is null ? 0 : zones.FindIndex(z => z.Id == c.Value.Id);
            if (!User32.IsZoomed(hwnd)) { Track(hwnd).PreSnap = cur; Track(hwnd).PreSnapMaximized = false; }
            SnapToZone(hwnd, mon, zones[Math.Max(0, idx)], activate: false);
            return;
        }
        int next = idx + dir;
        if (next < 0 || next >= zones.Count)
        {
            // Wrap to the neighbouring monitor if there is one.
            var mons = MonitorService.Monitors;
            int mi = mons.ToList().FindIndex(m => m.Key == mon.Key) + dir;
            if (mi >= 0 && mi < mons.Count)
            {
                var nz = _layouts.Zones(mons[mi]).OrderBy(z => z.Rect.Left).ToList();
                if (nz.Count > 0) SnapToZone(hwnd, mons[mi], dir > 0 ? nz[0] : nz[^1], activate: false);
            }
            return;
        }
        SnapToZone(hwnd, mon, zones[next], activate: false);
    }

    // ------------------------------------------------------------------ slots

    void RecordSlot(nint hwnd, SlotKind kind, MonitorInfo mon, Zone? zone)
    {
        if (!Remembering) return;
        var key = KeyOf(hwnd);
        if (key is null || IsExcluded(key)) return;
        var t = Track(hwnd);
        string title = AppIdentityRules.NormalizeTitle(WindowInfo.Title(hwnd));
        bool reused = false;
        if (t.SlotId is null)
        {
            // Reuse the app's best free slot instead of accumulating duplicates (e.g. after a DisplaySplit restart).
            var reuse = SlotMatcher.Pick(_placements.Get(key), OccupiedSlots(key, hwnd), title);
            if (reuse != null) { t.SlotId = reuse.Id; reused = true; }
        }
        var existing = t.SlotId != null && !reused ? _placements.Find(key, t.SlotId) : null;
        var slot = new Slot
        {
            Id = t.SlotId ?? Guid.NewGuid().ToString("N")[..10],
            ExplicitZones = existing != null ? new Dictionary<string, int>(existing.ExplicitZones) : [],
            Kind = kind,
            MonitorKey = mon.Key,
            LayoutId = _layouts.Active(mon).Id,
            ZoneId = zone?.Id ?? 0,
            Fraction = zone is { } z ? z.Rect.ToFraction(mon.Work) : new RectF(0, 0, 1, 1),
            Title = title,
            LastUsedUtc = DateTime.UtcNow,
        };
        // If another live window of the same app already owns this slot id, allocate a fresh one.
        if (t.SlotId != null && OccupiedSlots(key, hwnd).Contains(t.SlotId))
        {
            slot.Id = Guid.NewGuid().ToString("N")[..10];
            slot.ExplicitZones = [];
        }
        if (zone is { } zz) slot.ExplicitZones[slot.LayoutId] = zz.Id;
        foreach (var kv in t.Explicit) slot.ExplicitZones.TryAdd(kv.Key, kv.Value);
        // DerivedZones start empty: they are recomputed from the new Fraction when needed.
        t.SlotId = slot.Id;
        _placements.Upsert(key, slot);
    }

    void DeleteSlot(nint hwnd)
    {
        if (!Remembering) return; // "Remember app positions" off (or paused): leave saved data untouched
        if (!_tracked.TryGetValue(hwnd, out var t) || t.SlotId is null || t.Key is null) return;
        _placements.Remove(t.Key, t.SlotId);
        t.SlotId = null;
    }

    /// <summary>Resolve a slot to a monitor + target rect on the current layouts (remapping it if needed).</summary>
    (MonitorInfo mon, Zone? zone)? ResolveSlot(Slot slot)
    {
        var mon = _layouts.MonitorForKey(slot.MonitorKey);
        // Only a slot whose monitor we have never seen (e.g. data from another PC) falls back to the sole monitor.
        // A known-but-disconnected monitor (laptop undocked) keeps its slots for when it comes back.
        if (mon is null && MonitorService.Monitors.Count == 1 && !_layouts.IsKnownKey(slot.MonitorKey)) mon = MonitorService.Monitors[0];
        if (mon is null) return null;
        if (slot.Kind == SlotKind.Maximized) return (mon, null);
        var layout = _layouts.Active(mon);
        var zones = _layouts.Zones(mon);
        slot.ExplicitZones ??= [];
        slot.DerivedZones ??= [];
        if (slot.LayoutId == layout.Id) slot.ExplicitZones.TryAdd(layout.Id, slot.ZoneId);
        if (FindZone(zones, slot.ExplicitZones, layout.Id) is { } ez) return (mon, ez);
        if (FindZone(zones, slot.DerivedZones, layout.Id) is { } dz) return (mon, dz);
        // No valid assignment for this layout: derive by overlap into Derived; explicit data stays intact.
        var best = BestOverlap(zones, slot.Fraction.Resolve(mon.Work));
        if (best is null) return null;
        slot.DerivedZones[layout.Id] = best.Value.Id;
        _placements.MarkDirty();
        return (mon, best);
    }

    /// <summary>Zone of a tracked window in the monitor's active layout (per-layout memory, overlap fallback).</summary>
    Zone? ZoneForTracked(Tracked t, MonitorInfo mon, RectI fallbackRect)
    {
        var layout = _layouts.Active(mon);
        var zones = _layouts.Zones(mon);
        if (FindZone(zones, t.Explicit, layout.Id) is { } ez) return ez;
        if (FindZone(zones, t.Derived, layout.Id) is { } dz) return dz;
        var best = BestOverlap(zones, fallbackRect);
        if (best is { } b) t.Derived[layout.Id] = b.Id;
        return best;
    }

    static Zone? FindZone(IReadOnlyList<Zone> zones, Dictionary<string, int> map, string layoutId)
    {
        if (!map.TryGetValue(layoutId, out int id)) return null;
        foreach (var z in zones) if (z.Id == id) return z;
        map.Remove(layoutId); // that zone no longer exists
        return null;
    }

    static Zone? BestOverlap(IReadOnlyList<Zone> zones, RectI want)
    {
        Zone? best = null;
        long bestArea = -1;
        foreach (var z in zones)
        {
            long a = z.Rect.Intersect(want).Area;
            if (a > bestArea) { bestArea = a; best = z; }
        }
        if (bestArea <= 0) best = LayoutOps.ZoneAt(zones, want.Center);
        return best;
    }

    bool ApplySlot(nint hwnd, string key, Slot slot, bool fromArrange)
    {
        var res = ResolveSlot(slot);
        if (res is null) return false;
        var (mon, zone) = res.Value;
        var t = Track(hwnd);
        t.SlotId = slot.Id;
        t.MonitorKey = mon.Key;
        t.AppliedTick = Environment.TickCount64;
        LoadMaps(t, slot);
        bool iconic = User32.IsIconic(hwnd);
        if (slot.Kind == SlotKind.Maximized)
        {
            t.Kind = SlotKind.Maximized;
            nint curMon = User32.MonitorFromWindow(hwnd, C.MONITOR_DEFAULTTONEAREST);
            if (User32.IsZoomed(hwnd) && curMon == mon.HMonitor) return true; // already right
            if (iconic) { _snap.SetMinimizedMaximized(hwnd, mon); return true; }
            _snap.Maximize(hwnd, mon);
            return true;
        }
        var z = zone!.Value;
        SetZoneFromSlot(t, mon, z, slot);
        if (iconic) { _snap.SetMinimizedTarget(hwnd, z.Rect); return true; }
        if (!User32.IsZoomed(hwnd) && WindowInfo.VisualRect(hwnd).NearlyEquals(z.Rect, 2)) { t.PlacedRect = z.Rect; return true; }
        PlaceTracked(t, z.Rect, activate: false);
        return true;
    }

    /// <summary>The tracked window's per-layout maps become exactly the slot's (never a merge of two slots).</summary>
    static void LoadMaps(Tracked t, Slot slot)
    {
        t.Explicit.Clear();
        t.Derived.Clear();
        foreach (var kv in slot.ExplicitZones ?? []) t.Explicit[kv.Key] = kv.Value;
        foreach (var kv in slot.DerivedZones ?? []) t.Derived[kv.Key] = kv.Value;
    }

    void SetZoneFromSlot(Tracked t, MonitorInfo mon, Zone z, Slot slot)
    {
        string lid = _layouts.Active(mon).Id;
        bool isExplicit = slot.ExplicitZones.TryGetValue(lid, out int ezid) && ezid == z.Id;
        t.Kind = SlotKind.Zone; t.MonitorKey = mon.Key; t.LayoutId = lid; t.ZoneId = z.Id; t.ZoneRect = z.Rect;
        if (isExplicit) t.Explicit[lid] = z.Id; else t.Derived[lid] = z.Id;
    }

    // ------------------------------------------------------------------ new windows (R6)

    public void OnWindowShown(nint hwnd)
    {
        if (_seen.Contains(hwnd)) return;
        if (!WindowInfo.IsManageable(hwnd)) return; // may become eligible later (e.g. uncloak)
        _seen.Add(hwnd);
        if (!Remembering) return;
        var s = new Settle
        {
            Hwnd = hwnd,
            Start = Environment.TickCount64,
            Deadline = Environment.TickCount64 + 10_000,
            ResolveDeadline = Environment.TickCount64 + 5_000,
        };
        _settling[hwnd] = s;
        s.Timer = App.SetTimeout(80, () => { s.Timer = 0; SettleTick(s); });
    }

    void SettleTick(Settle s)
    {
        if (!_settling.TryGetValue(s.Hwnd, out var live) || live != s) return;
        nint hwnd = s.Hwnd;
        long now = Environment.TickCount64;
        if (!User32.IsWindow(hwnd) || now > s.Deadline) { EndSettle(hwnd); return; }
        if (UserActedOn(hwnd, s.Start)) { EndSettle(hwnd); return; }

        if (!s.Applied)
        {
            var key = KeyOf(hwnd);
            if (key is null)
            {
                if (now > s.ResolveDeadline) { EndSettle(hwnd); return; }
                Reschedule(s, 250);
                return;
            }
            if (!_placements.HasSlots(key) || IsExcluded(key)) { EndSettle(hwnd); return; }
            var rect = WindowInfo.WindowRect(hwnd);
            uint pid = WindowInfo.ProcessIdOf(hwnd);
            var started = WindowInfo.ProcessStartUtc(pid);
            double age = started is null ? 0 : (DateTime.UtcNow - started.Value).TotalSeconds;
            var others = OtherLiveWindows(hwnd, key);
            var decision = RestorePolicy.Decide(new RestoreCandidate(
                AlreadyHandled: false, Excluded: false, RememberEnabled: Remembering, HasSlots: true,
                ProcessAgeSeconds: age, OtherLiveWindowsWithSameKey: others.Count,
                MouseButtonDown: User32.GetAsyncKeyState(C.VK_LBUTTON) < 0 || User32.GetAsyncKeyState(C.VK_RBUTTON) < 0,
                MoveSizeLoopActive: _msHwnd != 0,
                Resizable: WindowInfo.IsResizable(hwnd) || User32.IsZoomed(hwnd),
                Width: User32.IsZoomed(hwnd) || User32.IsIconic(hwnd) ? RestorePolicy.MinWidth : rect.Width,
                Height: User32.IsZoomed(hwnd) || User32.IsIconic(hwnd) ? RestorePolicy.MinHeight : rect.Height));
            if (decision == RestoreDecision.SkipUserBusy) { Reschedule(s, 250); return; }
            if (decision != RestoreDecision.Apply) { Log.Debug($"restore skip 0x{hwnd:X}: {decision}"); EndSettle(hwnd); return; }

            var occupied = others.Select(o => _tracked.TryGetValue(o, out var ot) ? ot.SlotId : null)
                .Where(id => id != null).Select(id => id!).ToHashSet();
            s.Title = AppIdentityRules.NormalizeTitle(WindowInfo.Title(hwnd));
            var slot = SlotMatcher.Pick(_placements.Get(key), occupied, s.Title);
            if (slot is null) { EndSettle(hwnd); return; }
            if (!ApplySlot(hwnd, key, slot, fromArrange: false))
            {
                _pendingMonitor[hwnd] = Environment.TickCount64; // its monitor isn't connected (yet)
                EndSettle(hwnd);
                return;
            }
            s.Applied = true;
            s.Kind = slot.Kind;
            s.Target = Track(hwnd).ZoneRect;
            Log.Debug($"restored 0x{hwnd:X} {key} → {slot.Kind} {s.Target}");
            Reschedule(s, 300);
            return;
        }

        // Applied: watch for the app moving itself (Chrome/VS Code restore their own bounds after showing).
        var t = Track(hwnd);
        if (_snap.IsOwnMove(hwnd)) { Reschedule(s, 200); return; }

        // Title became available/changed → maybe a strictly better slot exists (multi-window apps).
        if (!s.TitleRematched && t.Key != null)
        {
            string title = AppIdentityRules.NormalizeTitle(WindowInfo.Title(hwnd));
            if (title != s.Title)
            {
                s.Title = title;
                var currentSlot = t.SlotId != null ? _placements.Find(t.Key, t.SlotId) : null;
                var others = OtherLiveWindows(hwnd, t.Key);
                var occupied = others.Select(o => _tracked.TryGetValue(o, out var ot) ? ot.SlotId : null)
                    .Where(id => id != null).Select(id => id!).ToHashSet();
                var better = SlotMatcher.Pick(_placements.Get(t.Key), occupied, title);
                if (better != null && currentSlot != null && better.Id != currentSlot.Id &&
                    SlotMatcher.Score(better, title) > SlotMatcher.Score(currentSlot, title))
                {
                    s.TitleRematched = true;
                    ApplySlot(hwnd, t.Key, better, fromArrange: false);
                    s.Kind = better.Kind;
                    s.Target = t.ZoneRect;
                    Reschedule(s, 300);
                    return;
                }
            }
        }

        if (User32.IsIconic(hwnd)) { Reschedule(s, 300); return; }
        if (s.Kind == SlotKind.Zone)
        {
            bool zoomed = User32.IsZoomed(hwnd);
            var cur = WindowInfo.VisualRect(hwnd);
            bool placed = t.PlacedRect is { } pr && pr.NearlyEquals(cur, 2); // e.g. centred because of a min size
            if (!zoomed && (cur.NearlyEquals(s.Target, 3) || placed)) { Reschedule(s, 300); return; }
            if (zoomed && s.Reapplies >= 1) { s.AcceptedMaximize = true; EndSettle(hwnd); return; }
            if (s.Reapplies >= 3) { EndSettle(hwnd); return; }
            s.Reapplies++;
            if (Log.Verbose) Log.Debug($"settle re-apply #{s.Reapplies} 0x{hwnd:X}");
            PlaceTracked(t, s.Target, activate: false);
            Reschedule(s, 300);
            return;
        }
        // Maximized slot: the app may un-maximize itself while restoring its own state.
        if (!User32.IsZoomed(hwnd) && s.Reapplies < 3 && t.MonitorKey != null && _layouts.MonitorForKey(t.MonitorKey) is { } mm)
        {
            s.Reapplies++;
            _snap.Maximize(hwnd, mm);
            t.AppliedTick = Environment.TickCount64;
        }
        Reschedule(s, 400);
    }

    void StartAppliedSettle(nint hwnd, SlotKind kind)
    {
        EndSettle(hwnd);
        var s = new Settle
        {
            Hwnd = hwnd, Start = Environment.TickCount64, Deadline = Environment.TickCount64 + 10_000,
            ResolveDeadline = Environment.TickCount64, Applied = true, Kind = kind, Target = Track(hwnd).ZoneRect,
            Title = AppIdentityRules.NormalizeTitle(WindowInfo.Title(hwnd)),
        };
        _settling[hwnd] = s;
        Reschedule(s, 400);
    }

    void Reschedule(Settle s, int ms) => s.Timer = App.SetTimeout(ms, () => { s.Timer = 0; SettleTick(s); });

    void EndSettle(nint hwnd)
    {
        if (_settling.Remove(hwnd, out var s)) App.CancelTimeout(ref s.Timer);
    }

    bool UserActedOn(nint hwnd, long since)
    {
        long down = Volatile.Read(ref MouseHook.LastButtonDownTick);
        if (down <= since) return false;
        var p = MouseHook.LastButtonDownPoint;
        nint at = User32.WindowFromPoint(new POINT(p.X, p.Y));
        return at != 0 && User32.GetAncestor(at, C.GA_ROOT) == hwnd;
    }

    List<nint> OtherLiveWindows(nint hwnd, string key)
    {
        var list = new List<nint>();
        foreach (var h in WindowInfo.TopLevelWindows())
        {
            if (h == hwnd || !User32.IsWindowVisible(h)) continue;
            if (!WindowInfo.IsManageable(h, allowShellCloaked: true)) continue;
            if (KeyOf(h) == key) list.Add(h);
        }
        return list;
    }

    // ------------------------------------------------------------------ WinEvents

    public void OnMoveSizeStart(nint hwnd)
    {
        MouseHook.NoteMoveSizeStart(hwnd);
        EndSettle(hwnd);
        _pendingMonitor.Remove(hwnd);
        _snap.Cancel(hwnd); // never correct a window the user is dragging, and judge the drag result normally
        _msHwnd = hwnd;
        _msStart = WindowInfo.VisualRect(hwnd);
        _msStartZoomed = User32.IsZoomed(hwnd);
        // Hook liveness: a move/size loop always starts with a button press the LL hook must have seen.
        if (MouseHook.IsRunning && Environment.TickCount64 - MouseHook.LastEventTick > 2000 &&
            User32.GetAsyncKeyState(C.VK_LBUTTON) < 0)
        {
            Log.Warn("mouse hook appears dead; reinstalling");
            MouseHook.RequestReinstall();
        }
        DragBegin?.Invoke(hwnd, _msStart);
    }

    public void OnMoveSizeEnd(nint hwnd)
    {
        bool wasOurs = _msHwnd == hwnd;
        _msHwnd = 0;
        if (DragEnd?.Invoke(hwnd) == true) return; // Shift-drag snapped it
        if (!wasOurs || _settings.Paused || _snap.IsOwnMove(hwnd)) return;
        var now = WindowInfo.VisualRect(hwnd);
        bool zoomed = User32.IsZoomed(hwnd);
        if (now.NearlyEquals(_msStart, 1) && zoomed == _msStartZoomed) return; // plain click on caption
        if (!WindowInfo.IsManageable(hwnd)) return;
        var mon = MonitorService.FromRect(now);
        if (mon is null) return;
        var t = Track(hwnd);
        if (zoomed)
        {
            t.MaxFromZone = t.Kind == SlotKind.Zone;
            t.Kind = SlotKind.Maximized;
            t.MonitorKey = mon.Key;
            RecordSlot(hwnd, SlotKind.Maximized, mon, null);
            return;
        }
        var match = _layouts.Zones(mon).Where(z => z.Rect.NearlyEquals(now, 4)).Cast<Zone?>().FirstOrDefault();
        if (match is { } z)
        {
            SetZone(t, mon, z);
            t.PlacedRect = now;
            RecordSlot(hwnd, SlotKind.Zone, mon, z);
        }
        else
        {
            if (t.Kind == SlotKind.Zone) t.PreSnap = null; // user chose a free size; nothing to restore to
            t.Kind = null;
            t.Explicit.Clear(); t.Derived.Clear();
            DeleteSlot(hwnd);
        }
    }

    public void OnMinimizeStart(nint hwnd) => EndSettle(hwnd);

    public void OnForeground(nint hwnd)
    {
        if (hwnd != 0 && !WindowInfo.IsOwnWindow(hwnd) && WindowInfo.IsManageable(hwnd, false)) LastForeignForeground = hwnd;
        if (_settling.ContainsKey(hwnd) || _snap.IsOwnMove(hwnd)) return;
        if (!Remembering) return;
        if (_tracked.TryGetValue(hwnd, out var t) && t.Kind == SlotKind.Maximized &&
            Environment.TickCount64 - t.AppliedTick > 15_000 &&
            !User32.IsZoomed(hwnd) && !User32.IsIconic(hwnd) &&
            // Borderless full screen (F11, video) is not "un-maximized" and isn't manageable: leave the slot alone.
            WindowInfo.IsManageable(hwnd) && !WindowInfo.IsMaximizedPlacement(hwnd))
        {
            OnRestoredFromMaximize(hwnd); // the user un-maximized it (button, Win+Down, …)
        }
    }

    public void OnWindowDestroyed(nint hwnd)
    {
        EndSettle(hwnd);
        _tracked.Remove(hwnd);
        _seen.Remove(hwnd);
        _pendingMonitor.Remove(hwnd);
        _snap.Forget(hwnd);
    }

    /// <summary>Drop dead handles (shell hook may miss some, e.g. windows that never had a taskbar button).</summary>
    public void Prune()
    {
        foreach (var h in _tracked.Keys.Where(h => !User32.IsWindow(h)).ToList()) OnWindowDestroyed(h);
        _seen.RemoveWhere(h => !User32.IsWindow(h));
    }

    // ------------------------------------------------------------------ layout changes (N4)

    /// <summary>Capture which tracked windows currently fill their zone on a monitor (before a layout change).</summary>
    public List<(nint hwnd, RectI oldRect)> CaptureZoneWindows(MonitorInfo mon)
    {
        var list = new List<(nint, RectI)>();
        foreach (var t in _tracked.Values)
        {
            if (t.Kind != SlotKind.Zone || t.MonitorKey != mon.Key || !User32.IsWindow(t.Hwnd)) continue;
            if (User32.IsZoomed(t.Hwnd)) continue;
            var cur = WindowInfo.VisualRect(t.Hwnd);
            bool inZone = User32.IsIconic(t.Hwnd) || cur.NearlyEquals(t.ZoneRect, 4) || (t.PlacedRect is { } pr && pr.NearlyEquals(cur, 2));
            if (inZone) list.Add((t.Hwnd, t.ZoneRect));
        }
        return list;
    }

    /// <summary>
    /// After the monitor's layout changed (edited or switched): refit zone windows using their per-layout assignment
    /// (overlap fallback) and resolve stored slots for the new active layout without overwriting explicit data.
    /// </summary>
    public void ApplyLayoutChange(MonitorInfo mon, List<(nint hwnd, RectI oldRect)> captured)
    {
        foreach (var (hwnd, oldRect) in captured)
        {
            var t = Track(hwnd);
            var nz = ZoneForTracked(t, mon, oldRect);
            if (nz is not { } z) continue;
            t.Kind = SlotKind.Zone;
            t.LayoutId = _layouts.Active(mon).Id;
            t.ZoneId = z.Id;
            t.ZoneRect = z.Rect;
            if (User32.IsIconic(hwnd)) _snap.SetMinimizedTarget(hwnd, z.Rect);
            else if (!z.Rect.NearlyEquals(oldRect, 1)) PlaceTracked(t, z.Rect, activate: false);
            if (t.Key != null && t.SlotId != null && _placements.Find(t.Key, t.SlotId) is { } slot)
            {
                bool expl = t.Explicit.TryGetValue(t.LayoutId, out int eid) && eid == z.Id;
                if (expl) slot.ExplicitZones[t.LayoutId] = z.Id; else slot.DerivedZones[t.LayoutId] = z.Id;
            }
        }
        foreach (var (_, slot) in _placements.All().ToList())
        {
            if (slot.Kind != SlotKind.Zone) continue;
            var m = _layouts.MonitorForKey(slot.MonitorKey);
            if (m is null || m.Key != mon.Key) continue;
            ResolveSlot(slot);
        }
        _placements.MarkDirty();
    }

    public void OnDisplayChanged()
    {
        // Monitors may have been re-keyed by LayoutStore.Bind: follow them.
        foreach (var t in _tracked.Values)
            if (t.MonitorKey != null && MonitorService.ByKey(t.MonitorKey) is null && _layouts.MonitorForKey(t.MonitorKey) is { } nm)
                t.MonitorKey = nm.Key;

        // Windows whose slot's monitor was missing when they appeared: try again (within 2 minutes).
        foreach (var (h, since) in _pendingMonitor.ToList())
        {
            _pendingMonitor.Remove(h);
            if (!User32.IsWindow(h) || Environment.TickCount64 - since > 120_000 || !Remembering) continue;
            var key = KeyOf(h);
            if (key is null || IsExcluded(key)) continue;
            var slot = SlotMatcher.Pick(_placements.Get(key), OccupiedSlots(key, h), AppIdentityRules.NormalizeTitle(WindowInfo.Title(h)));
            if (slot != null && !ApplySlot(h, key, slot, fromArrange: false)) _pendingMonitor[h] = since;
        }

        // Zone rects moved (resolution / taskbar change): refit windows that were in zones.
        foreach (var mon in MonitorService.Monitors)
        {
            foreach (var t in _tracked.Values.Where(t => t.Kind == SlotKind.Zone && t.MonitorKey == mon.Key && User32.IsWindow(t.Hwnd)).ToList())
            {
                if (User32.IsZoomed(t.Hwnd)) continue;
                var z = ZoneForTracked(t, mon, t.ZoneRect);
                if (z is not { } zz || zz.Rect.NearlyEquals(t.ZoneRect, 1)) continue;
                var old = t.ZoneRect;
                t.ZoneRect = zz.Rect;
                t.ZoneId = zz.Id;
                if (User32.IsIconic(t.Hwnd)) _snap.SetMinimizedTarget(t.Hwnd, zz.Rect);
                else
                {
                    var cur = WindowInfo.VisualRect(t.Hwnd);
                    if (cur.NearlyEquals(old, 4) || (t.PlacedRect is { } pr && pr.NearlyEquals(cur, 2))) PlaceTracked(t, zz.Rect, false);
                }
            }
        }
    }

    // ------------------------------------------------------------------ exclusions

    public string? IdentityOf(nint hwnd)
    {
        var key = hwnd != 0 && User32.IsWindow(hwnd) ? KeyOf(hwnd) : null;
        return key is null ? null : AppIdentityRules.IdentityPart(key);
    }

    public void Exclude(string identity)
    {
        if (!_settings.ExcludedApps.Contains(identity)) _settings.ExcludedApps.Add(identity);
        _placements.ForgetIdentity(identity);
        foreach (var t in _tracked.Values.Where(t => t.Key != null && AppIdentityRules.IdentityPart(t.Key) == identity))
            ResetTracking(t);
    }

    public void ForgetAll()
    {
        _placements.ForgetAll();
        foreach (var t in _tracked.Values) ResetTracking(t);
    }

    static void ResetTracking(Tracked t)
    {
        t.SlotId = null;
        t.Kind = null;
        t.Explicit.Clear();
        t.Derived.Clear();
        t.PlacedRect = null;
    }
}
