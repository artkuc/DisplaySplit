using DisplaySplit.Core;
using DisplaySplit.Native;
using DisplaySplit.Platform;

namespace DisplaySplit.UI;

/// <summary>Editing session across all monitors. Save/Cancel apply to every overlay at once.</summary>
public sealed class EditorSession : IDisposable
{
    internal sealed class MonitorState
    {
        public required MonitorInfo Monitor;
        public List<Layout> Layouts = [];
        public int Index;
        public readonly Stack<(List<Layout> layouts, int index)> Undo = new();
        public Layout Current => Layouts[Index];

        public void PushUndo() => PushSnapshot((Layouts.Select(l => l.Clone()).ToList(), Index));

        public void PushSnapshot((List<Layout> layouts, int index) snapshot)
        {
            Undo.Push(snapshot);
            if (Undo.Count > 100) { var arr = Undo.ToArray(); Undo.Clear(); foreach (var u in arr.Take(100).Reverse()) Undo.Push(u); }
        }
    }

    readonly List<EditorOverlay> _overlays = [];
    readonly Action<EditorSession, bool> _closed;
    public int Gap { get; set; }
    public bool ShowCoach { get; set; }

    public EditorSession(LayoutStore store, int gap, bool coach, Action<EditorSession, bool> closed)
    {
        _closed = closed;
        Gap = gap;
        ShowCoach = coach;
        var primary = MonitorService.Monitors.FirstOrDefault(m => m.Primary) ?? MonitorService.Monitors.FirstOrDefault();
        try
        {
            foreach (var m in MonitorService.Monitors)
            {
                var e = store.For(m);
                var st = new MonitorState
                {
                    Monitor = m,
                    Layouts = e.Layouts.Select(l => l.Clone()).ToList(),
                    Index = Math.Max(0, e.Layouts.FindIndex(l => l.Id == e.ActiveId)),
                };
                _overlays.Add(new EditorOverlay(this, st, coach && m == primary));
            }
        }
        catch
        {
            Dispose(); // never leave half an editor on screen
            throw;
        }
        Focus();
    }

    /// <summary>Activate the overlay under the cursor so keyboard shortcuts work.</summary>
    public void Focus()
    {
        User32.GetCursorPos(out var p);
        var target = _overlays.FirstOrDefault(o => o.State.Monitor.Work.Contains(new PointI(p.X, p.Y))) ?? _overlays.FirstOrDefault();
        target?.Activate();
    }

    internal IEnumerable<MonitorState> States => _overlays.Select(o => o.State);
    internal IReadOnlyList<EditorOverlay> Overlays => _overlays;

    public void Save()
    {
        foreach (var o in _overlays) o.CommitPendingRename();
        Close(true);
    }
    public void Cancel() => Close(false);

    bool _closing;
    void Close(bool save)
    {
        if (_closing) return;
        _closing = true;
        try { _closed(this, save); }
        finally { Dispose(); }
    }

    internal void Redraw()
    {
        foreach (var o in _overlays) o.InvalidateAll();
    }

    internal void DismissCoach()
    {
        if (!ShowCoach) return;
        ShowCoach = false;
        Redraw();
    }

    /// <summary>Toggle the help &amp; shortcuts card on <paramref name="owner"/> (only one overlay shows it).</summary>
    internal void ToggleHelp(EditorOverlay owner)
    {
        if (ShowCoach && owner.ShowsHelp) { DismissCoach(); return; }
        foreach (var o in _overlays) o.ShowsHelp = o == owner;
        ShowCoach = true;
        Redraw();
    }

    /// <summary>Show the help card on the overlay under the cursor (tray "How to use").</summary>
    public void ShowHelp()
    {
        Focus();
        User32.GetCursorPos(out var p);
        var target = _overlays.FirstOrDefault(o => o.State.Monitor.Work.Contains(new PointI(p.X, p.Y))) ?? _overlays.FirstOrDefault();
        if (target is null) return;
        if (!(ShowCoach && target.ShowsHelp)) ToggleHelp(target);
    }

    public void Dispose()
    {
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
    }
}

/// <summary>Full work-area, per-pixel-alpha editor window for one monitor.</summary>
internal sealed class EditorOverlay : NativeWindow
{
    const string Cls = "DisplaySplit.Editor";

    enum HitKind { None, Zone, ZoneRemove, Divider, DividerMerge, Button, Coach }

    readonly record struct Hit(HitKind Kind, int Index = -1, int ZoneId = 0, string? Button = null);

    sealed record Btn(string Id, RectI Rect, string Label, bool Active = false, string? PresetKey = null, string? Tip = null);

    readonly EditorSession _session;
    internal EditorSession.MonitorState State { get; }
    readonly RectI _area;        // screen rect of the overlay (monitor work area)
    readonly float _s;           // DPI scale
    Surface? _surface;
    bool _coach;
    internal bool ShowsHelp { get => _coach; set => _coach = value; }

    // derived geometry (local coordinates)
    List<Zone> _rawZones = [];
    List<Zone> _zones = [];
    List<Divider> _dividers = [];
    List<Btn> _buttons = [];
    RectI _toolbar, _hintBar, _coachRect;

    // interaction
    Hit _hover;
    PointI _mouse;
    bool _mouseInside;
    (bool active, SplitDir dir, int pos, int zoneId, RectI zoneRect) _preview;
    int _dragDivider = -1;
    bool _dragUndoPushed;
    bool _rightPressed;
    PointI _pressPt;
    Hit _pressHit;
    bool _pressed;
    bool _renaming;
    bool _renameAllSelected;
    bool _keepName;
    string _renameText = "";
    bool _snapDisabled;
    readonly List<RectI> _dirty = [];
    bool _fullDirty = true;
    nuint _frameTimer;

    public EditorOverlay(EditorSession session, EditorSession.MonitorState state, bool coach)
    {
        _session = session;
        State = state;
        _coach = coach;
        _area = state.Monitor.Work;
        _s = (float)state.Monitor.Scale;
        EnsureClass(Cls, 0, User32.LoadCursorW(0, C.IDC_ARROW));
        CreateHandle(Cls, "DisplaySplit layout editor", C.WS_POPUP,
            C.WS_EX_LAYERED | C.WS_EX_TOPMOST | C.WS_EX_TOOLWINDOW, _area.Left, _area.Top, _area.Width, _area.Height);
        try { _surface = new Surface(_area.Width, _area.Height); }
        catch { Dispose(); throw; }
        Rebuild();
        Render();
        User32.ShowWindow(Handle, C.SW_SHOWNOACTIVATE);
    }

    public void Activate()
    {
        User32.SetForegroundWindow(Handle);
        User32.SetFocus(Handle);
    }

    int Px(float logical) => (int)Math.Round(logical * _s);

    RectI LocalArea => new(0, 0, _area.Width, _area.Height);

    int MinZonePx(SplitDir dir) =>
        Math.Max(Px(120), (int)((dir == SplitDir.Columns ? _area.Width : _area.Height) * 0.04));

    // ------------------------------------------------------------------ geometry

    void Rebuild()
    {
        var layout = State.Current;
        _rawZones = LayoutOps.Resolve(layout, LocalArea, 0);
        _zones = LayoutOps.Resolve(layout, LocalArea, (int)Math.Round(_session.Gap * _s));
        _dividers = LayoutOps.Dividers(layout, LocalArea);
        BuildToolbar();
    }

    void BuildToolbar()
    {
        var list = new List<(string id, int w, string label, bool active, string? preset, string? tip)>();
        int h = Px(40);
        list.Add(("prev", Px(32), "‹", false, null, "Previous layout"));
        string shown = _renaming ? _renameText + "▏" : State.Current.Name + (State.Layouts.Count > 1 ? $"  {State.Index + 1}/{State.Layouts.Count}" : "");
        list.Add(("name", Px(Math.Clamp((_surface?.MeasureText(shown, Px(14), true) ?? Px(120)) / _s + 28, 110, 300)),
            State.Current.Name, _renaming, null, "Click to rename (F2)"));
        list.Add(("next", Px(32), "›", false, null, "Next layout"));
        list.Add(("new", Px(64), "+ New", false, null, "Duplicate as a new layout"));
        if (State.Layouts.Count > 1) list.Add(("delete", Px(64), "Delete", false, null, "Delete this layout"));
        list.Add(("|", Px(14), "", false, null, null));
        foreach (var p in LayoutPresets.All) list.Add(("preset:" + p.Key, Px(46), "", false, p.Key, p.Name));
        list.Add(("|", Px(14), "", false, null, null));
        list.Add(("gaplabel", Px(40), "Gap", false, null, null));
        foreach (int g in new[] { 0, 4, 8, 16 }) list.Add(("gap:" + g, Px(34), g.ToString(), _session.Gap == g, null, $"{g} px between zones"));
        list.Add(("|", Px(14), "", false, null, null));
        list.Add(("help", Px(32), "?", false, null, "Help & shortcuts (F1)"));
        list.Add(("undo", Px(58), "Undo", false, null, "Ctrl+Z"));
        list.Add(("cancel", Px(70), "Cancel", false, null, "Esc"));
        list.Add(("save", Px(70), "Save", true, null, "Enter"));

        int gap = Px(6), pad = Px(10);
        int Total() => list.Sum(i => i.w) + gap * (list.Count - 1) + pad * 2;
        // Narrow monitors: drop preset thumbnails (from the end), then the gap selector, until everything fits.
        int avail = _area.Width - Px(16);
        while (Total() > avail && list.FindLastIndex(i => i.id.StartsWith("preset:", StringComparison.Ordinal)) is int pi && pi >= 0) list.RemoveAt(pi);
        if (Total() > avail) list.RemoveAll(i => i.id.StartsWith("gap", StringComparison.Ordinal));
        while (Total() > avail && list.FindIndex(i => i.id == "|") is int si && si >= 0) list.RemoveAt(si);
        int total = Total();
        int x = Math.Max(Px(8), (_area.Width - total) / 2), y = Px(16);
        _toolbar = RectI.FromXYWH(x, y, Math.Min(total, _area.Width - Px(16)), h + pad * 2);
        _buttons = [];
        int cx = x + pad;
        foreach (var (id, w, label, active, preset, tip) in list)
        {
            _buttons.Add(new Btn(id, RectI.FromXYWH(cx, y + pad, w, h), label, active, preset, tip));
            cx += w + gap;
        }
        int hintH = Px(36);
        int hintW = Math.Min(Px(940), _area.Width - Px(32));
        _hintBar = RectI.FromXYWH(_area.Width / 2 - hintW / 2, _area.Height - hintH - Px(16), hintW, hintH);
        int cw = Math.Min(Px(1060), _area.Width - Px(32)), ch = Math.Min(Px(500), _area.Height - Px(160));
        _coachRect = RectI.FromXYWH(_area.Width / 2 - cw / 2, _area.Height / 2 - ch / 2, cw, ch);
    }

    // ------------------------------------------------------------------ hit testing

    Hit HitTest(PointI p)
    {
        if (_toolbar.Contains(p))
        {
            foreach (var b in _buttons) if (b.Rect.Contains(p) && b.Id != "|" && b.Id != "gaplabel") return new Hit(HitKind.Button, Button: b.Id);
            return new Hit(HitKind.None);
        }
        if (_coach && _session.ShowCoach && _coachRect.Contains(p)) return new Hit(HitKind.Coach);
        if (_dragDivider < 0)
        {
            // Merge handle at divider midpoint (only for the hovered divider, like a hover affordance).
            if (_hover.Kind is HitKind.Divider or HitKind.DividerMerge && _hover.Index >= 0 && _hover.Index < _dividers.Count)
            {
                var c = MergeHandleCenter(_dividers[_hover.Index]);
                if (Dist2(p, c) <= Px(13) * Px(13)) return new Hit(HitKind.DividerMerge, _hover.Index);
            }
            int band = Px(9);
            for (int i = 0; i < _dividers.Count; i++)
            {
                var d = _dividers[i];
                var r = d.Dir == SplitDir.Columns
                    ? new RectI(d.Line.Left - band, d.Line.Top, d.Line.Left + band, d.Line.Bottom)
                    : new RectI(d.Line.Left, d.Line.Top - band, d.Line.Right, d.Line.Top + band);
                if (r.Contains(p)) return new Hit(HitKind.Divider, i);
            }
        }
        foreach (var z in _rawZones)
        {
            if (!z.Rect.Contains(p)) continue;
            if (_rawZones.Count > 1)
            {
                var c = RemoveHandleCenter(ZoneDrawRect(z.Id));
                if (Dist2(p, c) <= Px(15) * Px(15)) return new Hit(HitKind.ZoneRemove, ZoneId: z.Id);
            }
            return new Hit(HitKind.Zone, ZoneId: z.Id);
        }
        return new Hit(HitKind.None);
    }

    static long Dist2(PointI a, PointI b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);

    PointI MergeHandleCenter(Divider d) => d.Dir == SplitDir.Columns
        ? new PointI(d.Line.Left, (d.Line.Top + d.Line.Bottom) / 2)
        : new PointI((d.Line.Left + d.Line.Right) / 2, d.Line.Top);

    PointI RemoveHandleCenter(RectI zoneRect)
    {
        var c = new PointI(zoneRect.Right - Px(26), zoneRect.Top + Px(26));
        // Keep the × clear of the toolbar (it would be hidden under it for e.g. halves/thirds on wide screens).
        if (_toolbar.Inflate(Px(16), Px(16)).Contains(c)) c = new PointI(c.X, _toolbar.Bottom + Px(40));
        return c;
    }

    RectI ZoneDrawRect(int id)
    {
        foreach (var z in _zones) if (z.Id == id) return InsetForDraw(z.Rect);
        return default;
    }

    RectI InsetForDraw(RectI r)
    {
        int inset = _session.Gap == 0 ? Px(3) : 0; // with no gap keep a visible seam between zones
        return r.Inflate(-inset, -inset);
    }

    // ------------------------------------------------------------------ snapping

    int SnapPosition(int pos, int start, int len, bool horizontalAxis, IEnumerable<int> extra)
    {
        if (_snapDisabled) return pos;
        int full = horizontalAxis ? _area.Width : _area.Height;
        int threshold = Px(28);
        int best = pos, bestD = int.MaxValue;
        void Try(int c)
        {
            int d = Math.Abs(c - pos);
            if (d < bestD && d <= threshold) { bestD = d; best = c; }
        }
        for (int i = 1; i < 12; i++) Try((int)Math.Round(full * i / 12.0));
        Try(start + len / 2);
        Try(start + len / 3);
        Try(start + len * 2 / 3);
        foreach (int e in extra) Try(e);
        return best;
    }

    IEnumerable<int> OtherDividerPositions(SplitDir dir, int except = -1)
    {
        for (int i = 0; i < _dividers.Count; i++)
        {
            if (i == except || _dividers[i].Dir != dir) continue;
            yield return dir == SplitDir.Columns ? _dividers[i].Line.Left : _dividers[i].Line.Top;
        }
    }

    void UpdatePreview()
    {
        var old = _preview;
        _preview = default;
        if (_hover.Kind == HitKind.Zone && _dragDivider < 0 && !_renaming && _mouseInside && !(_coach && _session.ShowCoach))
        {
            var z = _rawZones.FirstOrDefault(z => z.Id == _hover.ZoneId);
            if (!z.Rect.IsEmpty)
            {
                bool shift = _testShift ?? (User32.GetKeyState(C.VK_SHIFT) & 0x8000) != 0;
                var dir = shift ? SplitDir.Rows : SplitDir.Columns;
                bool cols = dir == SplitDir.Columns;
                int start = cols ? z.Rect.Left : z.Rect.Top, len = cols ? z.Rect.Width : z.Rect.Height;
                int pos = SnapPosition(cols ? _mouse.X : _mouse.Y, start, len, cols, OtherDividerPositions(dir));
                int min = MinZonePx(dir);
                if (pos - start >= min && start + len - pos >= min)
                    _preview = (true, dir, pos, z.Id, z.Rect);
            }
        }
        if (old != _preview)
        {
            if (old.active) Dirty(PreviewDirty(old.dir, old.pos, old.zoneRect));
            if (_preview.active) Dirty(PreviewDirty(_preview.dir, _preview.pos, _preview.zoneRect));
        }
    }

    RectI PreviewDirty(SplitDir dir, int pos, RectI zone)
    {
        int w = Px(150);
        var band = dir == SplitDir.Columns
            ? new RectI(pos - w, zone.Top, pos + w, zone.Bottom)
            : new RectI(zone.Left, pos - Px(60), zone.Right, pos + Px(60));
        // Labels are centred in the zone and replaced by the preview sizes; repaint the label area too.
        var c = zone.Center;
        var label = RectI.FromXYWH(c.X - Px(220), c.Y - Px(80), Px(440), Px(170));
        return Union(band, label);
    }

    static RectI Union(RectI a, RectI b) => a.IsEmpty ? b : b.IsEmpty ? a :
        new RectI(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    // ------------------------------------------------------------------ actions

    void Mutate(Func<Layout, bool> change)
    {
        var snapshot = (State.Layouts.Select(l => l.Clone()).ToList(), State.Index);
        if (!change(State.Current)) return; // nothing changed → no undo entry
        State.PushSnapshot(snapshot);
        if (!_keepName) MarkCustom();
        _keepName = false;
        LayoutOps.Normalize(State.Current);
        AfterLayoutChange();
    }

    void AfterLayoutChange()
    {
        Rebuild();
        _hover = HitTest(_mouse);
        UpdatePreview();
        _fullDirty = true;
        ScheduleRender();
    }

    void OnButton(string id)
    {
        switch (id)
        {
            case "prev": SwitchLayout(-1); break;
            case "next": SwitchLayout(+1); break;
            case "new":
                State.PushUndo();
                var copy = State.Current.Clone();
                copy.Id = Guid.NewGuid().ToString("N")[..8];
                copy.Name = $"Layout {State.Layouts.Count + 1}";
                State.Layouts.Insert(State.Index + 1, copy);
                State.Index++;
                AfterLayoutChange();
                StartRename();
                break;
            case "delete":
                if (State.Layouts.Count <= 1) break;
                State.PushUndo();
                State.Layouts.RemoveAt(State.Index);
                State.Index = Math.Min(State.Index, State.Layouts.Count - 1);
                AfterLayoutChange();
                break;
            case "name": StartRename(); break;
            case "undo": DoUndo(); break;
            case "help": _session.ToggleHelp(this); break;
            case "cancel": _session.Cancel(); break;
            case "save": CommitRename(); _session.Save(); break;
            default:
                if (id.StartsWith("preset:", StringComparison.Ordinal))
                {
                    string key = id[7..];
                    _keepName = true; // the layout now IS the preset: it keeps/gets the preset's name
                    Mutate(l =>
                    {
                        bool presetNamed = l.Name == "Custom" || LayoutPresets.All.Any(p => p.Name == l.Name);
                        LayoutPresets.ApplyTo(l, key);
                        if (presetNamed) l.Name = LayoutPresets.Find(key)!.Name;
                        return true;
                    });
                    _keepName = false;
                }
                else if (id.StartsWith("gap:", StringComparison.Ordinal))
                {
                    _session.Gap = int.Parse(id[4..]);
                    _session.Redraw();
                }
                break;
        }
    }

    /// <summary>A layout that no longer matches its preset must not keep the preset's name.</summary>
    bool MarkCustom()
    {
        if (!LayoutPresets.All.Any(p => p.Name == State.Current.Name)) return false;
        State.Current.Name = "Custom";
        return true;
    }

    internal void CommitPendingRename() => CommitRename();

    /// <summary>Re-layout the toolbar and repaint both its old and new extent (it re-centres when it changes width).</summary>
    void RebuildToolbarDirty()
    {
        var old = _toolbar;
        BuildToolbar();
        Dirty(Union(old, _toolbar).Inflate(Px(4), Px(50)));
        ScheduleRender();
    }

    void SwitchLayout(int dir)
    {
        if (State.Layouts.Count < 2) return;
        CommitRename();
        State.Index = (State.Index + dir + State.Layouts.Count) % State.Layouts.Count;
        AfterLayoutChange();
    }

    void DoUndo()
    {
        if (State.Undo.Count == 0) return;
        var (layouts, index) = State.Undo.Pop();
        State.Layouts = layouts;
        State.Index = Math.Clamp(index, 0, layouts.Count - 1);
        AfterLayoutChange();
    }

    void StartRename()
    {
        if (_renaming) return;
        _renaming = true;
        _renameText = State.Current.Name;
        _renameAllSelected = true; // like a text box: the whole name is selected, typing replaces it
        AfterLayoutChange();
    }

    void CommitRename()
    {
        if (!_renaming) return;
        _renaming = false;
        string name = _renameText.Trim();
        if (name.Length > 0 && name != State.Current.Name)
        {
            State.PushUndo();
            if (name.Length > 40) name = name[..(char.IsHighSurrogate(name[39]) ? 39 : 40)];
            State.Current.Name = name;
        }
        AfterLayoutChange();
    }

    void SplitAtPreview()
    {
        if (!_preview.active) return;
        var p = _preview;
        bool cols = p.dir == SplitDir.Columns;
        double frac = cols ? (double)(p.pos - p.zoneRect.Left) / p.zoneRect.Width : (double)(p.pos - p.zoneRect.Top) / p.zoneRect.Height;
        Mutate(l => LayoutOps.SplitLeaf(l, p.zoneId, p.dir, frac) != 0);
    }

    void MergeDivider(int index)
    {
        if (index < 0 || index >= _dividers.Count) return;
        var d = _dividers[index];
        // Divider records reference nodes of the current layout instance, so mutate in place after pushing undo.
        State.PushUndo();
        LayoutOps.RemoveDivider(State.Current, d.Split, d.Index);
        MarkCustom();
        AfterLayoutChange();
    }

    void RemoveZone(int zoneId) => Mutate(l => LayoutOps.RemoveZone(l, zoneId));

    void DragDividerTo(PointI p)
    {
        var d = _dividers[_dragDivider];
        bool cols = d.Dir == SplitDir.Columns;
        int start = cols ? d.SplitRect.Left : d.SplitRect.Top, len = cols ? d.SplitRect.Width : d.SplitRect.Height;
        int pos = SnapPosition(cols ? p.X : p.Y, start, len, cols, OtherDividerPositions(d.Dir, _dragDivider));
        double minFrac = Math.Min(0.45, (double)MinZonePx(d.Dir) / Math.Max(1, len));
        int curPos = cols ? d.Line.Left : d.Line.Top;
        if (Math.Abs(pos - curPos) < 1) return;
        if (!_dragUndoPushed)
        {
            // Snapshot before the first change; the divider record still points into the current tree.
            State.PushUndo();
            _dragUndoPushed = true;
            if (MarkCustom()) RebuildToolbarDirty();
        }
        var before = _zones;
        var oldLine = d.Line;
        LayoutOps.MoveDivider(d.Split, d.Index, (double)(pos - start) / len, minFrac);
        Rebuild();
        // Dirty only what changed: the band swept by the moved edges plus old/new labels and handles.
        int pad = Px(14) + (int)Math.Round(_session.Gap * _s);
        for (int i = 0; i < _zones.Count && i < before.Count; i++)
        {
            RectI o = before[i].Rect, n = _zones[i].Rect;
            if (o == n) continue;
            if (o.Left != n.Left) Dirty(new RectI(Math.Min(o.Left, n.Left) - pad, Math.Min(o.Top, n.Top) - pad, Math.Max(o.Left, n.Left) + pad, Math.Max(o.Bottom, n.Bottom) + pad));
            if (o.Right != n.Right) Dirty(new RectI(Math.Min(o.Right, n.Right) - pad, Math.Min(o.Top, n.Top) - pad, Math.Max(o.Right, n.Right) + pad, Math.Max(o.Bottom, n.Bottom) + pad));
            if (o.Top != n.Top) Dirty(new RectI(Math.Min(o.Left, n.Left) - pad, Math.Min(o.Top, n.Top) - pad, Math.Max(o.Right, n.Right) + pad, Math.Max(o.Top, n.Top) + pad));
            if (o.Bottom != n.Bottom) Dirty(new RectI(Math.Min(o.Left, n.Left) - pad, Math.Min(o.Bottom, n.Bottom) - pad, Math.Max(o.Right, n.Right) + pad, Math.Max(o.Bottom, n.Bottom) + pad));
            Dirty(LabelRect(InsetForDraw(o)));
            Dirty(LabelRect(InsetForDraw(n)));
            Dirty(HandleRect(InsetForDraw(o)));
            Dirty(HandleRect(InsetForDraw(n)));
        }
        Dirty(oldLine.Inflate(pad, pad));
        if (_dragDivider < _dividers.Count) Dirty(_dividers[_dragDivider].Line.Inflate(pad, pad));
        _dragDivider = Math.Min(_dragDivider, _dividers.Count - 1);
        ScheduleRender();
    }

    RectI LabelRect(RectI zone)
    {
        var c = zone.Center;
        return RectI.FromXYWH(c.X - Px(130), c.Y - Px(80), Px(260), Px(150)).Intersect(zone.Inflate(Px(2), Px(2)));
    }

    RectI HandleRect(RectI zone)
    {
        var c = RemoveHandleCenter(zone);
        return RectI.FromXYWH(c.X - Px(18), c.Y - Px(18), Px(36), Px(36));
    }

    // ------------------------------------------------------------------ window procedure

    protected override nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case C.WM_MOUSEACTIVATE:
                // First click on an inactive overlay only activates it (never splits by accident).
            {
                nint fg = User32.GetForegroundWindow();
                bool ours = fg == hwnd || (fg != 0 && WindowInfo.IsOwnWindow(fg) && WindowInfo.ClassName(fg) == Cls);
                return ours ? 1 /*MA_ACTIVATE*/ : 2 /*MA_ACTIVATEANDEAT*/;
            }
            case C.WM_SETCURSOR:
                if (C.LoWord(lParam) == C.HTCLIENT)
                {
                    int cur = _hover.Kind switch
                    {
                        HitKind.Divider => _dividers.Count > _hover.Index && _hover.Index >= 0 && _dividers[_hover.Index].Dir == SplitDir.Columns ? C.IDC_SIZEWE : C.IDC_SIZENS,
                        HitKind.Button or HitKind.ZoneRemove or HitKind.DividerMerge => C.IDC_HAND,
                        HitKind.Zone => C.IDC_CROSS,
                        _ => C.IDC_ARROW,
                    };
                    if (_dragDivider >= 0) cur = _dividers[_dragDivider].Dir == SplitDir.Columns ? C.IDC_SIZEWE : C.IDC_SIZENS;
                    User32.SetCursor(User32.LoadCursorW(0, cur));
                    return 1;
                }
                break;
            case C.WM_MOUSEMOVE:
            {
                var p = new PointI(C.LoWord(lParam), C.HiWord(lParam));
                if (!_mouseInside)
                {
                    _mouseInside = true;
                    var tme = new TRACKMOUSEEVENT { cbSize = 24, dwFlags = C.TME_LEAVE, hwndTrack = hwnd };
                    User32.TrackMouseEvent(ref tme);
                }
                _mouse = p;
                _snapDisabled = (User32.GetKeyState(C.VK_CONTROL) & 0x8000) != 0;
                if (_dragDivider >= 0) { DragDividerTo(p); return 0; }
                if (_pressed && _pressHit.Kind == HitKind.DividerMerge && _pressHit.Index >= 0 && _pressHit.Index < _dividers.Count &&
                    (Math.Abs(p.X - _pressPt.X) >= Px(4) || Math.Abs(p.Y - _pressPt.Y) >= Px(4)))
                {
                    // Started on the × but moved: it's a resize drag, not a merge click.
                    _pressed = false;
                    _dragUndoPushed = false;
                    _dragDivider = _pressHit.Index;
                    User32.SetCapture(hwnd);
                    _preview = default;
                    DragDividerTo(p);
                    return 0;
                }
                SetHover(HitTest(p));
                UpdatePreview();
                ScheduleRender();
                return 0;
            }
            case C.WM_MOUSELEAVE:
                _mouseInside = false;
                SetHover(new Hit(HitKind.None));
                UpdatePreview();
                ScheduleRender();
                return 0;
            case C.WM_LBUTTONDOWN:
            {
                var p = new PointI(C.LoWord(lParam), C.HiWord(lParam));
                _mouse = p;
                var h = HitTest(p);
                if (_coach && _session.ShowCoach)
                {
                    _session.DismissCoach();
                    // A click anywhere closes the card; toolbar buttons (except "?" itself) still work.
                    if (h.Kind != HitKind.Button || h.Button == "help") return 0;
                }
                if (_renaming && !(h.Kind == HitKind.Button && h.Button == "name"))
                {
                    CommitRename();
                    if (h.Kind != HitKind.Button) return 0; // the click only finishes the rename; it never splits
                }
                _pressed = true;
                _pressPt = p;
                _pressHit = h;
                if (h.Kind == HitKind.Divider)
                {
                    _dragUndoPushed = false; // pushed on the first real move, so a plain click adds no undo entry
                    _dragDivider = h.Index;
                    User32.SetCapture(hwnd);
                    _preview = default;
                    _fullDirty = true;
                    ScheduleRender();
                }
                return 0;
            }
            case C.WM_LBUTTONUP:
            {
                var p = new PointI(C.LoWord(lParam), C.HiWord(lParam));
                bool wasPressed = _pressed;
                _pressed = false;
                if (_dragDivider >= 0)
                {
                    _dragDivider = -1;
                    User32.ReleaseCapture();
                    LayoutOps.Normalize(State.Current);
                    AfterLayoutChange();
                    return 0;
                }
                if (!wasPressed) return 0;
                bool click = Math.Abs(p.X - _pressPt.X) < Px(4) && Math.Abs(p.Y - _pressPt.Y) < Px(4);
                if (!click) return 0;
                if (_pressHit.Kind == HitKind.Button) { OnButton(_pressHit.Button!); return 0; }
                var h = HitTest(p);
                if (h.Kind != _pressHit.Kind || h.Button != _pressHit.Button || h.ZoneId != _pressHit.ZoneId) return 0;
                switch (h.Kind)
                {
                    case HitKind.Button: OnButton(h.Button!); break;
                    case HitKind.ZoneRemove: RemoveZone(h.ZoneId); break;
                    case HitKind.DividerMerge: MergeDivider(h.Index); break;
                    case HitKind.Zone: SplitAtPreview(); break;
                }
                return 0;
            }
            case C.WM_RBUTTONDOWN:
                _rightPressed = _dragDivider < 0;
                return 0;
            case C.WM_RBUTTONUP:
            {
                if (!_rightPressed) return 0; // the press only activated the overlay (eaten) or a drag was running
                _rightPressed = false;
                if (_renaming) { CommitRename(); return 0; }
                if (_coach && _session.ShowCoach) { _session.DismissCoach(); return 0; }
                var p = new PointI(C.LoWord(lParam), C.HiWord(lParam));
                var h = HitTest(p);
                if (h.Kind is HitKind.Divider or HitKind.DividerMerge) MergeDivider(h.Index);
                else if (h.Kind is HitKind.Zone or HitKind.ZoneRemove && _rawZones.Count > 1) RemoveZone(h.ZoneId);
                return 0;
            }
            case C.WM_CAPTURECHANGED:
                if (_dragDivider >= 0 && lParam != hwnd)
                {
                    _dragDivider = -1;
                    AfterLayoutChange();
                }
                return 0;
            case C.WM_KEYDOWN:
            case C.WM_KEYUP:
            {
                int vk = (int)wParam;
                if (msg == C.WM_KEYUP)
                {
                    if (vk is C.VK_SHIFT or C.VK_CONTROL) { _snapDisabled = (User32.GetKeyState(C.VK_CONTROL) & 0x8000) != 0; UpdatePreview(); ScheduleRender(); }
                    return 0;
                }
                OnKey(vk);
                return 0;
            }
            case C.WM_CHAR:
                if (_renaming)
                {
                    char ch = (char)wParam;
                    if (ch == '\x01') { _renameAllSelected = true; }                       // Ctrl+A
                    else if (ch == '\x7F' || (ch == '\b' && _renameAllSelected)) { _renameText = ""; _renameAllSelected = false; } // Ctrl+Backspace
                    else if (ch == '\b') { if (_renameText.Length > 0) _renameText = _renameText[..^1]; }
                    else if (ch >= ' ')
                    {
                        if (_renameAllSelected) { _renameText = ""; _renameAllSelected = false; }
                        if (_renameText.Length < 40) _renameText += ch;
                    }
                    RebuildToolbarDirty();
                }
                return 0;
            case C.WM_SYSKEYDOWN:
                if ((int)wParam == 0x73 /*F4*/) { _session.Cancel(); return 0; }
                break;
            case C.WM_ACTIVATE:
                if (C.LoWord(wParam) == 0)
                {
                    _pressed = false;
                    _rightPressed = false;
                    if (_dragDivider >= 0) { _dragDivider = -1; User32.ReleaseCapture(); AfterLayoutChange(); }
                }
                return 0;
            case C.WM_DPICHANGED:
            case C.WM_DISPLAYCHANGE:
                return 0; // the controller re-checks monitor geometry (debounced) and closes the editor only if it changed
            case C.WM_ERASEBKGND:
                return 1;
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    void OnKey(int vk)
    {
        bool ctrl = (User32.GetKeyState(C.VK_CONTROL) & 0x8000) != 0;
        if (_dragDivider >= 0)
        {
            if (vk is C.VK_CONTROL) { _snapDisabled = true; }
            return; // no structural edits (undo/delete/save) in the middle of a drag
        }
        if (_renaming)
        {
            if (vk == C.VK_RETURN) CommitRename();
            else if (vk == C.VK_ESCAPE) { _renaming = false; AfterLayoutChange(); }
            return;
        }
        if (_coach && _session.ShowCoach && vk is not (C.VK_SHIFT or C.VK_CONTROL))
        {
            _session.DismissCoach(); // any key just closes the help card
            return;
        }
        switch (vk)
        {
            case 0x70: _session.ToggleHelp(this); break;          // F1
            case C.VK_ESCAPE: _session.Cancel(); break;
            case C.VK_RETURN: _session.Save(); break;
            case 0x53 when ctrl: _session.Save(); break;          // Ctrl+S
            case 0x5A when ctrl: DoUndo(); break;                 // Ctrl+Z
            case C.VK_DELETE:
            case C.VK_BACK:
                if (_hover.Kind is HitKind.Divider or HitKind.DividerMerge) MergeDivider(_hover.Index);
                else if (_hover.Kind is HitKind.Zone or HitKind.ZoneRemove && _rawZones.Count > 1) RemoveZone(_hover.ZoneId);
                break;
            case C.VK_F2: StartRename(); break;
            case C.VK_LEFT when ctrl: SwitchLayout(-1); break;
            case C.VK_RIGHT when ctrl: SwitchLayout(+1); break;
            case C.VK_SHIFT:
            case C.VK_CONTROL:
                _snapDisabled = ctrl;
                UpdatePreview();
                ScheduleRender();
                break;
        }
    }

    void SetHover(Hit h)
    {
        if (h == _hover) return;
        DirtyFor(_hover);
        _hover = h;
        DirtyFor(_hover);
    }

    void DirtyFor(Hit h)
    {
        switch (h.Kind)
        {
            case HitKind.Zone:
            case HitKind.ZoneRemove:
                Dirty(ZoneDrawRect(h.ZoneId).Inflate(Px(2), Px(2)));
                break;
            case HitKind.Divider:
            case HitKind.DividerMerge:
                if (h.Index >= 0 && h.Index < _dividers.Count)
                {
                    var d = _dividers[h.Index];
                    var c = MergeHandleCenter(d);
                    Dirty(d.Line.Inflate(Px(16), Px(16)));
                    Dirty(RectI.FromXYWH(c.X - Px(20), c.Y - Px(20), Px(40), Px(40)));
                }
                break;
            case HitKind.Button:
                Dirty(_toolbar.Inflate(Px(4), Px(80)));
                break;
        }
    }

    // ------------------------------------------------------------------ rendering

    public void InvalidateAll()
    {
        Rebuild();
        _fullDirty = true;
        ScheduleRender();
    }

    void Dirty(RectI r)
    {
        if (!r.IsEmpty) _dirty.Add(r.Intersect(LocalArea));
    }

    void ScheduleRender()
    {
        if (_frameTimer != 0) return;
        _frameTimer = App.SetTimeout(8, () => { _frameTimer = 0; Render(); });
    }

    void Render()
    {
        if (_surface is null || Handle == 0) return;
        List<RectI> rects;
        if (_fullDirty) { rects = [LocalArea]; _fullDirty = false; _dirty.Clear(); }
        else
        {
            rects = Merge(_dirty.Where(r => !r.IsEmpty).ToList());
            _dirty.Clear();
            if (rects.Count == 0) return;
        }
        RectI bounds = default;
        foreach (var r in rects)
        {
            DrawScene(r);
            bounds = Union(bounds, r);
        }
        _surface.ResetClip();
        Gdip.GdipFlush(_surface.G, 1);
        _surface.Present(Handle, _area.Left, _area.Top, bounds == LocalArea ? null : new RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
    }

    static List<RectI> Merge(List<RectI> rects)
    {
        // Merge overlapping rects to avoid drawing the same pixels twice.
        bool merged = true;
        while (merged && rects.Count > 1)
        {
            merged = false;
            for (int i = 0; i < rects.Count && !merged; i++)
                for (int j = i + 1; j < rects.Count && !merged; j++)
                    if (!rects[i].Intersect(rects[j]).IsEmpty)
                    {
                        rects[i] = Union(rects[i], rects[j]);
                        rects.RemoveAt(j);
                        merged = true;
                    }
        }
        return rects;
    }

    void DrawScene(RectI clip)
    {
        var s = _surface!;
        s.Clip(clip.Left, clip.Top, clip.Width, clip.Height);
        // Backdrop (alpha ≥ 10 everywhere so the overlay is never click-through).
        s.FillRaw(clip.Left, clip.Top, clip.Width, clip.Height, Theme.Backdrop);

        int radius = Px(10);
        foreach (var z in _zones)
        {
            var r = InsetForDraw(z.Rect);
            if (r.Intersect(clip).IsEmpty) continue;
            bool hovered = (_hover.Kind is HitKind.Zone or HitKind.ZoneRemove) && _hover.ZoneId == z.Id && _dragDivider < 0;
            DrawZoneBody(s, r, radius, hovered ? Theme.ZoneFillHover : Theme.ZoneFill, clip);
            s.StrokeRound(r.Left + 0.5f, r.Top + 0.5f, r.Width - 1, r.Height - 1, radius, hovered ? Theme.Accent : Theme.ZoneBorder, Math.Max(1, Px(1.5f)));
            DrawZoneLabel(s, z, r);
            if (hovered && _rawZones.Count > 1)
            {
                {
                    var c = RemoveHandleCenter(r);
                    bool hot = _hover.Kind == HitKind.ZoneRemove;
                    s.FillCircle(c.X, c.Y, Px(14), hot ? Theme.Danger : 0xCC2A303B);
                    float a = Px(5);
                    s.Line(c.X - a, c.Y - a, c.X + a, c.Y + a, Theme.Text, Px(2));
                    s.Line(c.X - a, c.Y + a, c.X + a, c.Y - a, Theme.Text, Px(2));
                }
            }
        }

        // Divider highlight + merge handle
        int activeDiv = _dragDivider >= 0 ? _dragDivider : _hover.Kind is HitKind.Divider or HitKind.DividerMerge ? _hover.Index : -1;
        if (activeDiv >= 0 && activeDiv < _dividers.Count)
        {
            var d = _dividers[activeDiv];
            float w = Px(4);
            if (d.Dir == SplitDir.Columns) s.FillRound(d.Line.Left - w / 2, d.Line.Top + Px(8), w, d.Line.Height - Px(16), w / 2, Theme.Accent);
            else s.FillRound(d.Line.Left + Px(8), d.Line.Top - w / 2, d.Line.Width - Px(16), w, w / 2, Theme.Accent);
            if (_dragDivider < 0)
            {
                var c = MergeHandleCenter(d);
                bool hot = _hover.Kind == HitKind.DividerMerge;
                s.FillCircle(c.X, c.Y, Px(12), hot ? Theme.Danger : Theme.Accent);
                float a = Px(4.5f);
                s.Line(c.X - a, c.Y - a, c.X + a, c.Y + a, Theme.Text, Px(2));
                s.Line(c.X - a, c.Y + a, c.X + a, c.Y - a, Theme.Text, Px(2));
            }
        }

        if (_preview.active) DrawPreview(s);
        if (!_toolbar.Intersect(clip).IsEmpty) DrawToolbar(s);
        if (!_hintBar.Intersect(clip).IsEmpty) DrawHint(s);
        if (_coach && _session.ShowCoach && !_coachRect.Intersect(clip).IsEmpty) DrawCoach(s);
    }

    static void DrawZoneBody(Surface s, RectI r, int radius, uint fill, RectI clip)
    {
        // Composite of zone fill over the backdrop, written raw (fast) for the "cross" of the rounded rect,
        // GDI+ for the four corner arcs.
        uint composite = Over(fill, Theme.Backdrop);
        int rr = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var a = new RectI(r.Left + rr, r.Top, r.Right - rr, r.Bottom).Intersect(clip);
        var b = new RectI(r.Left, r.Top + rr, r.Right, r.Bottom - rr).Intersect(clip);
        if (!a.IsEmpty) s.FillRawPremul(a.Left, a.Top, a.Width, a.Height, composite);
        if (!b.IsEmpty) s.FillRawPremul(b.Left, b.Top, b.Width, b.Height, composite);
        float d = rr * 2;
        Gdip.GdipSetSolidFillColor(s.Brush, fill);
        Gdip.GdipFillPie(s.G, s.Brush, r.Left, r.Top, d, d, 180, 90);
        Gdip.GdipFillPie(s.G, s.Brush, r.Right - d, r.Top, d, d, 270, 90);
        Gdip.GdipFillPie(s.G, s.Brush, r.Right - d, r.Bottom - d, d, d, 0, 90);
        Gdip.GdipFillPie(s.G, s.Brush, r.Left, r.Bottom - d, d, d, 90, 90);
    }

    /// <summary>Premultiplied result of straight-alpha src over straight-alpha dst.</summary>
    static uint Over(uint src, uint dst)
    {
        double sa = (src >> 24) / 255.0, da = (dst >> 24) / 255.0;
        double oa = sa + da * (1 - sa);
        byte Ch(int shift) => (byte)Math.Round(((src >> shift) & 0xFF) * sa + ((dst >> shift) & 0xFF) * da * (1 - sa));
        return ((uint)Math.Round(oa * 255) << 24) | ((uint)Ch(16) << 16) | ((uint)Ch(8) << 8) | Ch(0);
    }

    void DrawZoneLabel(Surface s, Zone z, RectI r)
    {
        bool previewHere = _preview.active && _preview.zoneId == z.Id;
        var c = r.Center;
        if (previewHere) return; // the preview draws its own size read-outs
        float big = Math.Min(Px(56), Math.Max(Px(18), Math.Min(r.Width, r.Height) / 4f));
        s.Text((z.Index + 1).ToString(), r.Left, c.Y - big * 1.1f, r.Width, big * 1.3f, big, Theme.Text, bold: true);
        var raw = _rawZones.FirstOrDefault(x => x.Id == z.Id).Rect;
        string size = $"{z.Rect.Width} × {z.Rect.Height}";
        string pct = $"{Math.Round(100.0 * raw.Width / _area.Width)}% wide";
        if (r.Width > Px(110) && r.Height > Px(110))
        {
            s.Text(size, r.Left, c.Y + big * 0.25f, r.Width, Px(24), Px(15), Theme.TextDim);
            s.Text(pct, r.Left, c.Y + big * 0.25f + Px(22), r.Width, Px(22), Px(13), Theme.TextFaint);
        }
    }

    void DrawPreview(Surface s)
    {
        var p = _preview;
        var z = p.zoneRect;
        if (p.dir == SplitDir.Columns)
        {
            s.Line(p.pos, z.Top + Px(10), p.pos, z.Bottom - Px(10), Theme.Accent, Px(2), Gdip.DashStyleDash);
            int lw = p.pos - z.Left, rw = z.Right - p.pos;
            var c = z.Center;
            s.FillRound(p.pos - Px(132), c.Y - Px(20), Px(124), Px(40), Px(8), Theme.Panel);
            s.Text($"{lw} px", p.pos - Px(132), c.Y - Px(20), Px(124), Px(40), Px(16), Theme.Text, bold: true);
            s.FillRound(p.pos + Px(8), c.Y - Px(20), Px(124), Px(40), Px(8), Theme.Panel);
            s.Text($"{rw} px", p.pos + Px(8), c.Y - Px(20), Px(124), Px(40), Px(16), Theme.Text, bold: true);
        }
        else
        {
            s.Line(z.Left + Px(10), p.pos, z.Right - Px(10), p.pos, Theme.Accent, Px(2), Gdip.DashStyleDash);
            int th = p.pos - z.Top, bh = z.Bottom - p.pos;
            var c = z.Center;
            s.FillRound(c.X - Px(62), p.pos - Px(48), Px(124), Px(40), Px(8), Theme.Panel);
            s.Text($"{th} px", c.X - Px(62), p.pos - Px(48), Px(124), Px(40), Px(16), Theme.Text, bold: true);
            s.FillRound(c.X - Px(62), p.pos + Px(8), Px(124), Px(40), Px(8), Theme.Panel);
            s.Text($"{bh} px", c.X - Px(62), p.pos + Px(8), Px(124), Px(40), Px(16), Theme.Text, bold: true);
        }
    }

    void DrawToolbar(Surface s)
    {
        var t = _toolbar;
        s.FillRound(t.Left, t.Top, t.Width, t.Height, Px(14), Theme.Panel);
        s.StrokeRound(t.Left + 0.5f, t.Top + 0.5f, t.Width - 1, t.Height - 1, Px(14), Theme.PanelBorder, 1);
        Btn? tipFor = null;
        foreach (var b in _buttons)
        {
            var r = b.Rect;
            if (b.Id == "|")
            {
                s.Line(r.Left + r.Width / 2f, r.Top + Px(8), r.Left + r.Width / 2f, r.Bottom - Px(8), Theme.PanelBorder, 1);
                continue;
            }
            if (b.Id == "gaplabel") { s.Text(b.Label, r.Left, r.Top, r.Width, r.Height, Px(13), Theme.TextDim); continue; }
            bool hot = _hover.Kind == HitKind.Button && _hover.Button == b.Id;
            if (hot) tipFor = b;
            bool isActivePreset = false;
            uint bg = b.Active ? Theme.ButtonActive : hot ? Theme.ButtonHover : Theme.Button;
            if (b.PresetKey != null) bg = hot ? Theme.ButtonHover : Theme.Button;
            if (b.Id == "name") bg = _renaming ? Theme.ButtonHover : hot ? Theme.ButtonHover : 0x00000000;
            if (bg != 0) s.FillRound(r.Left, r.Top, r.Width, r.Height, Px(8), bg);
            if (b.PresetKey != null)
            {
                DrawPresetThumb(s, b.PresetKey, r.Inflate(-Px(6), -Px(8)), hot || isActivePreset);
                continue;
            }
            if (b.Id == "name" && _renaming && _renameAllSelected && _renameText.Length > 0)
            {
                float tw = s.MeasureText(_renameText, Px(14), bold: true);
                s.FillRound(r.Left + r.Width / 2f - tw / 2 - Px(4), r.Top + Px(8), tw + Px(8), r.Height - Px(16), Px(4), Theme.AccentSoft);
            }
            string label = b.Id == "name" && _renaming ? _renameText + (_renameAllSelected ? "" : "▏") : b.Label;
            if (b.Id == "name" && State.Layouts.Count > 1 && !_renaming) label += $"  {State.Index + 1}/{State.Layouts.Count}";
            uint fg = b.Id == "delete" && hot ? Theme.Danger : Theme.Text;
            s.Text(label, r.Left + Px(4), r.Top, r.Width - Px(8), r.Height, b.Id is "prev" or "next" ? Px(22) : Px(14), fg,
                bold: b.Id is "save" or "name");
        }
        if (tipFor?.Tip is { } tip)
        {
            float w = s.MeasureText(tip, Px(13)) + Px(20);
            var r = tipFor.Rect;
            float x = Math.Clamp(r.Left + r.Width / 2f - w / 2, t.Left, t.Right - w);
            s.FillRound(x, t.Bottom + Px(6), w, Px(28), Px(6), Theme.Panel);
            s.Text(tip, x, t.Bottom + Px(6), w, Px(28), Px(13), Theme.TextDim);
        }
    }

    void DrawPresetThumb(Surface s, string key, RectI r, bool hot)
    {
        var tmp = LayoutPresets.Create(key);
        foreach (var z in LayoutOps.Resolve(tmp, r, Math.Max(1, Px(2))))
            s.FillRect(z.Rect.Left, z.Rect.Top, z.Rect.Width, z.Rect.Height, hot ? Theme.Accent : 0xFF6B7385);
    }

    void DrawHint(Surface s)
    {
        var r = _hintBar;
        s.FillRound(r.Left, r.Top, r.Width, r.Height, Px(10), Theme.Panel);
        string text = _renaming
            ? "Type a name · Enter to confirm · Esc to cancel"
            : "Click to split  ·  Shift = horizontal  ·  Drag a line to resize  ·  × or right-click to remove  ·  Ctrl+Z undo  ·  Enter save  ·  Esc cancel  ·  F1 help & shortcuts";
        s.Text(text, r.Left + Px(12), r.Top, r.Width - Px(24), r.Height, Px(13), Theme.TextDim);
    }

    static readonly (string keys, string what)[] HelpWindows =
    [
        ("Double-click title bar", "Snap window into the zone under the cursor"),
        ("Double-click again", "Back to its previous size"),
        ("Shift + double-click", "Maximize to full screen / restore"),
        ("Shift + drag window", "Drop it into the highlighted zone"),
        ("Win+Ctrl+Alt + ← / →", "Move window to the next zone"),
        ("Win+Ctrl+Alt + 1…9", "Switch to layout 1…9"),
        ("Win+Alt+Z", "Open this layout editor"),
    ];

    static readonly (string keys, string what)[] HelpEditor =
    [
        ("Click", "Split a zone (Shift = horizontal)"),
        ("Drag a line", "Resize zones (Ctrl = no snapping)"),
        ("× / right-click", "Remove a zone or a line"),
        ("Ctrl+Z", "Undo"),
        ("F2", "Rename layout"),
        ("Ctrl + ← / →", "Previous / next layout"),
        ("Enter / Esc", "Save / cancel"),
    ];

    void DrawCoach(Surface s)
    {
        var r = _coachRect;
        s.FillRound(r.Left, r.Top, r.Width, r.Height, Px(16), 0xFF1B1F27); // opaque: nothing shows through
        s.StrokeRound(r.Left + 0.5f, r.Top + 0.5f, r.Width - 1, r.Height - 1, Px(16), Theme.Accent, Px(1.5f));

        float pad = Px(32), y = r.Top + Px(26);
        s.Text("DisplaySplit", r.Left + pad, y, r.Width - 2 * pad, Px(34), Px(22), Theme.Text, bold: true, hAlign: Gdip.StringAlignmentNear);
        y += Px(36);
        s.Text("Splits your screen into zones. Windows snap into a zone and reopen there next time you start them.",
            r.Left + pad, y, r.Width - 2 * pad, Px(24), Px(14), Theme.TextDim, hAlign: Gdip.StringAlignmentNear);
        y += Px(44);

        float colGap = Px(28), colW = (r.Width - 2 * pad - colGap) / 2;
        DrawHelpColumn(s, "WINDOWS — ANYWHERE", HelpWindows, r.Left + pad, y, colW);
        DrawHelpColumn(s, "IN THIS EDITOR", HelpEditor, r.Left + pad + colW + colGap, y, colW);

        s.Text("Tray icon: switch layouts, re-arrange windows, pause, start with Windows.",
            r.Left + pad, r.Bottom - Px(78), r.Width - 2 * pad, Px(22), Px(13), Theme.TextFaint, hAlign: Gdip.StringAlignmentNear);
        s.Text("Click anywhere or press any key to close  ·  F1 or ? to open again",
            r.Left, r.Bottom - Px(46), r.Width, Px(26), Px(13), Theme.Accent);
    }

    void DrawHelpColumn(Surface s, string title, (string keys, string what)[] rows, float x, float y, float w)
    {
        s.Text(title, x, y, w, Px(20), Px(11.5f), Theme.Accent, bold: true, hAlign: Gdip.StringAlignmentNear);
        y += Px(30);
        // Key column as wide as the widest key chip in this column (descriptions then fit on one line).
        float keyW = Math.Min(rows.Max(rw => s.MeasureText(rw.keys, Px(12.5f), bold: true)) + Px(18), w * 0.5f), rowH = Px(38);
        foreach (var (keys, what) in rows)
        {
            // Key "chip"
            float tw = Math.Min(keyW, s.MeasureText(keys, Px(12.5f), bold: true) + Px(18));
            s.FillRound(x, y + Px(4), tw, rowH - Px(10), Px(6), 0xFF2A303B);
            s.StrokeRound(x + 0.5f, y + Px(4) + 0.5f, tw - 1, rowH - Px(10) - 1, Px(6), Theme.PanelBorder, 1);
            s.Text(keys, x, y + Px(4), tw, rowH - Px(10), Px(12.5f), Theme.Text, bold: true);
            s.Text(what, x + keyW + Px(12), y, w - keyW - Px(12), rowH, Px(13.5f), Theme.TextDim, hAlign: Gdip.StringAlignmentNear);
            y += rowH;
        }
    }

    // ---- test/diagnostic hooks (used by EditorSnapshot)
    bool? _testShift;
    internal void TestSetShift(bool shift) => _testShift = shift;
    internal void TestRenderNow() { App.CancelTimeout(ref _frameTimer); Render(); }
    internal RectI TestToolbar => _toolbar;
    internal Surface? TestSurface => _surface;

    protected override void OnDestroyed()
    {
        App.CancelTimeout(ref _frameTimer);
        _surface?.Dispose();
        _surface = null;
    }
}

