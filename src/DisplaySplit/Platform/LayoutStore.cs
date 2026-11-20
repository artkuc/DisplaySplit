using DisplaySplit.Core;

namespace DisplaySplit.Platform;

/// <summary>Per-monitor named layouts, bound to the currently connected monitors with tiered identity matching.</summary>
public sealed class LayoutStore
{
    readonly LayoutsFile _file;
    readonly DebouncedSaver<LayoutsFile> _saver;
    readonly Dictionary<string, MonitorLayouts> _bound = []; // current MonitorInfo.Key → entry
    readonly Func<int> _gapLogical;

    public event Action? Changed;

    public LayoutStore(Func<int> gapLogical)
    {
        _gapLogical = gapLogical;
        _file = Storage.Load("layouts.json", JsonCtx.Default.LayoutsFile, () => new LayoutsFile(), out _);
        _file.Monitors ??= [];
        _file.Monitors.RemoveAll(m => m is null);
        foreach (var m in _file.Monitors)
        {
            m.MonitorKey ??= "";
            m.Layouts ??= [];
            m.Layouts.RemoveAll(l => l is null);
            foreach (var l in m.Layouts) { l.Id ??= Guid.NewGuid().ToString("N")[..8]; l.Name ??= "Layout"; }
            foreach (var l in m.Layouts) LayoutOps.Repair(l);
            m.PreviousKeys ??= [];
        }
        _file.Monitors.RemoveAll(m => m.Layouts.Count == 0);
        _saver = new DebouncedSaver<LayoutsFile>("layouts.json", JsonCtx.Default.LayoutsFile, () => _file);
    }

    public void Flush() => _saver.Flush();
    public void MarkDirty() { _saver.MarkDirty(); Changed?.Invoke(); }

    /// <summary>Match connected monitors to stored entries (exact key → device path → same EDID model → sole pair).</summary>
    public void Bind(IReadOnlyList<MonitorInfo> monitors)
    {
        _bound.Clear();
        var free = new List<MonitorLayouts>(_file.Monitors);
        var unmatched = new List<MonitorInfo>();
        bool changed = false;

        foreach (var m in monitors)
        {
            var e = free.FirstOrDefault(x => x.MonitorKey == m.Key) ?? free.FirstOrDefault(x => x.PreviousKeys.Contains(m.Key));
            if (e != null) { Take(e, m); } else unmatched.Add(m);
        }
        foreach (var m in unmatched.ToList())
        {
            var e = m.DevicePath != null ? free.FirstOrDefault(x => string.Equals(x.DevicePath, m.DevicePath, StringComparison.OrdinalIgnoreCase)) : null;
            e ??= ModelPrefix(m.Key) is { } pre ? free.FirstOrDefault(x => ModelPrefix(x.MonitorKey) == pre) : null;
            if (e != null) { Take(e, m); unmatched.Remove(m); }
        }
        if (unmatched.Count == 1 && free.Count == 1 && monitors.Count == 1) { Take(free[0], unmatched[0]); unmatched.Clear(); }
        foreach (var m in unmatched)
        {
            var layout = LayoutPresets.Create(LayoutPresets.DefaultKeyFor(m.Bounds));
            var e = new MonitorLayouts
            {
                MonitorKey = m.Key, DevicePath = m.DevicePath, Width = m.Bounds.Width, Height = m.Bounds.Height,
                Layouts = [layout], ActiveId = layout.Id,
            };
            _file.Monitors.Add(e);
            _bound[m.Key] = e;
            changed = true;
        }
        if (changed) _saver.MarkDirty();

        void Take(MonitorLayouts e, MonitorInfo m)
        {
            free.Remove(e);
            if (e.MonitorKey != m.Key)
            {
                if (!e.PreviousKeys.Contains(e.MonitorKey)) e.PreviousKeys.Add(e.MonitorKey);
                e.PreviousKeys.Remove(m.Key);
                if (e.PreviousKeys.Count > 8) e.PreviousKeys.RemoveAt(0);
                e.MonitorKey = m.Key;
                changed = true;
            }
            if (e.DevicePath != m.DevicePath || e.Width != m.Bounds.Width || e.Height != m.Bounds.Height)
            {
                e.DevicePath = m.DevicePath; e.Width = m.Bounds.Width; e.Height = m.Bounds.Height; changed = true;
            }
            if (e.Layouts.All(l => l.Id != e.ActiveId)) { e.ActiveId = e.Layouts[0].Id; changed = true; }
            _bound[m.Key] = e;
        }
    }

    static string? ModelPrefix(string key)
    {
        if (!key.StartsWith("edid:", StringComparison.Ordinal)) return null;
        int last = key.LastIndexOf(':');
        return last > 5 ? key[..last] : null;
    }

    public MonitorLayouts For(MonitorInfo m)
    {
        if (!_bound.TryGetValue(m.Key, out var e)) { Bind(MonitorService.Monitors); e = _bound.GetValueOrDefault(m.Key); }
        if (e is null)
        {
            var layout = LayoutPresets.Create(LayoutPresets.DefaultKeyFor(m.Bounds));
            e = new MonitorLayouts { MonitorKey = m.Key, Layouts = [layout], ActiveId = layout.Id };
            _file.Monitors.Add(e);
            _bound[m.Key] = e;
            _saver.MarkDirty();
        }
        return e;
    }

    /// <summary>Is this a monitor we have a stored entry for (connected or not)?</summary>
    public bool IsKnownKey(string key) =>
        _file.Monitors.Any(m => m.MonitorKey == key || m.PreviousKeys.Contains(key));

    /// <summary>Connected monitor for a stored key (current or previous key).</summary>
    public MonitorInfo? MonitorForKey(string key)
    {
        foreach (var m in MonitorService.Monitors)
        {
            if (m.Key == key) return m;
            if (_bound.TryGetValue(m.Key, out var e) && e.PreviousKeys.Contains(key)) return m;
        }
        return null;
    }

    public Layout Active(MonitorInfo m) => For(m).Active;

    public int GapPx(MonitorInfo m) => (int)Math.Round(_gapLogical() * m.Scale);

    public List<Zone> Zones(MonitorInfo m) => LayoutOps.Resolve(Active(m), m.Work, GapPx(m));

    public List<Zone> Zones(MonitorInfo m, Layout layout) => LayoutOps.Resolve(layout, m.Work, GapPx(m));

    public void SetActive(MonitorInfo m, string layoutId)
    {
        var e = For(m);
        if (e.Layouts.Any(l => l.Id == layoutId) && e.ActiveId != layoutId) { e.ActiveId = layoutId; MarkDirty(); }
    }

    /// <summary>Replace the monitor's layouts with the edited set (from the editor) and the chosen active one.</summary>
    public void ReplaceAll(MonitorInfo m, List<Layout> layouts, string activeId)
    {
        var e = For(m);
        e.Layouts = layouts.Select(l => l.Clone()).ToList();
        e.ActiveId = e.Layouts.Any(l => l.Id == activeId) ? activeId : e.Layouts[0].Id;
        MarkDirty();
    }
}
