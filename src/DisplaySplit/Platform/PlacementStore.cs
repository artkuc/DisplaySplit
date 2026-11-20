using DisplaySplit.Core;

namespace DisplaySplit.Platform;

/// <summary>Remembered per-app window slots (R6), persisted to placements.json.</summary>
public sealed class PlacementStore
{
    readonly PlacementsFile _file;
    readonly DebouncedSaver<PlacementsFile> _saver;

    public PlacementStore()
    {
        _file = Storage.Load("placements.json", JsonCtx.Default.PlacementsFile, () => new PlacementsFile(), out _);
        _file.Apps ??= [];
        // Merge keys that only differ by a volatile class part (e.g. WPF GUIDs from older builds).
        foreach (var k in _file.Apps.Keys.ToList())
        {
            string nk = AppIdentityRules.NormalizeKey(k);
            if (nk == k) continue;
            var list = _file.Apps[k];
            _file.Apps.Remove(k);
            if (list is null) continue;
            if (!_file.Apps.TryGetValue(nk, out var target)) _file.Apps[nk] = target = [];
            foreach (var s in list.Where(s => s != null).OrderBy(s => s.LastUsedUtc)) SlotMatcher.Upsert(target, s);
        }
        foreach (var k in _file.Apps.Keys.ToList())
        {
            var list = _file.Apps[k];
            list?.RemoveAll(s => s is null || string.IsNullOrEmpty(s.Id));
            if (list is null || list.Count == 0) { _file.Apps.Remove(k); continue; }
            foreach (var s in list)
            {
                s.ExplicitZones ??= [];
                s.DerivedZones ??= [];
                s.MonitorKey ??= "";
                s.LayoutId ??= "";
                s.Title ??= "";
            }
        }
        _saver = new DebouncedSaver<PlacementsFile>("placements.json", JsonCtx.Default.PlacementsFile, () => _file);
    }

    public void Flush() => _saver.Flush();

    public IReadOnlyList<Slot> Get(string key) => _file.Apps.TryGetValue(key, out var l) ? l : [];

    public bool HasSlots(string key) => _file.Apps.TryGetValue(key, out var l) && l.Count > 0;

    public IEnumerable<(string Key, Slot Slot)> All() =>
        _file.Apps.SelectMany(kv => kv.Value.Select(s => (kv.Key, s)));

    public void Upsert(string key, Slot slot)
    {
        if (!_file.Apps.TryGetValue(key, out var list)) _file.Apps[key] = list = [];
        SlotMatcher.Upsert(list, slot);
        _saver.MarkDirty();
    }

    public void Remove(string key, string slotId)
    {
        if (!_file.Apps.TryGetValue(key, out var list)) return;
        if (list.RemoveAll(s => s.Id == slotId) > 0)
        {
            if (list.Count == 0) _file.Apps.Remove(key);
            _saver.MarkDirty();
        }
    }

    public Slot? Find(string key, string slotId) =>
        _file.Apps.TryGetValue(key, out var list) ? list.FirstOrDefault(s => s.Id == slotId) : null;

    /// <summary>Remove every slot of an app identity (all window classes).</summary>
    public void ForgetIdentity(string identity)
    {
        int n = 0;
        foreach (var k in _file.Apps.Keys.ToList())
            if (AppIdentityRules.IdentityPart(k) == identity) { _file.Apps.Remove(k); n++; }
        if (n > 0) _saver.MarkDirty();
    }

    public void ForgetAll()
    {
        _file.Apps.Clear();
        _saver.MarkDirty();
    }

    public void MarkDirty() => _saver.MarkDirty();
}
