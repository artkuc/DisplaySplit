using System.Text.Json.Serialization;

namespace DisplaySplit.Core;

/// <summary>Columns = children side by side (vertical divider lines). Rows = stacked (horizontal lines).</summary>
public enum SplitDir { Columns, Rows }

/// <summary>
/// Guillotine layout tree. A node is either a leaf (zone, <see cref="Id"/> &gt; 0) or a split with ≥2 children
/// whose <see cref="Sizes"/> are fractions summing to 1.
/// </summary>
public sealed class LayoutNode
{
    public int Id { get; set; }
    public SplitDir Dir { get; set; }
    public List<LayoutNode>? Children { get; set; }
    public List<double>? Sizes { get; set; }

    [JsonIgnore] public bool IsLeaf => Children is null || Children.Count == 0;

    public static LayoutNode Leaf(int id) => new() { Id = id };

    public static LayoutNode Split(SplitDir dir, params (double size, LayoutNode node)[] parts)
    {
        double sum = parts.Sum(p => p.size);
        return new LayoutNode
        {
            Dir = dir,
            Children = parts.Select(p => p.node).ToList(),
            Sizes = parts.Select(p => p.size / sum).ToList(),
        };
    }

    public LayoutNode Clone() => IsLeaf
        ? new LayoutNode { Id = Id }
        : new LayoutNode { Dir = Dir, Children = Children!.Select(c => c.Clone()).ToList(), Sizes = Sizes!.ToList() };

    public IEnumerable<LayoutNode> Leaves()
    {
        if (IsLeaf) { yield return this; yield break; }
        foreach (var c in Children!)
            foreach (var l in c.Leaves()) yield return l;
    }

    public int MaxId() => Leaves().Select(l => l.Id).DefaultIfEmpty(0).Max();
}

public sealed class Layout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Custom";
    public LayoutNode Root { get; set; } = LayoutNode.Leaf(1);
    /// <summary>Next zone id to hand out. Ids are never reused within a layout.</summary>
    public int NextId { get; set; } = 2;

    public Layout Clone() => new() { Id = Id, Name = Name, Root = Root.Clone(), NextId = NextId };
}

public readonly record struct Zone(int Id, int Index, RectI Rect);

public readonly record struct Divider(LayoutNode Split, int Index, SplitDir Dir, RectI Line, RectI SplitRect);

/// <summary>Pure operations on layouts. All mutate the given layout in place (callers clone for undo).</summary>
public static class LayoutOps
{
    public const double MinFraction = 0.04;

    // ---------- Resolution ----------

    /// <summary>Resolve zones to pixel rectangles. Zones tile <paramref name="area"/> exactly when gap = 0.</summary>
    public static List<Zone> Resolve(Layout layout, RectI area, int gap = 0)
    {
        var raw = new List<(int id, RectI r)>();
        ResolveNode(layout.Root, area, raw);
        var zones = new List<Zone>(raw.Count);
        int idx = 0;
        foreach (var (id, r) in raw)
        {
            zones.Add(new Zone(id, idx++, ApplyGap(r, area, gap)));
        }
        return zones;
    }

    static RectI ApplyGap(RectI r, RectI area, int gap)
    {
        if (gap <= 0) return r;
        int half = gap / 2, rest = gap - half;
        int l = r.Left == area.Left ? gap : rest;
        int t = r.Top == area.Top ? gap : rest;
        int rr = r.Right == area.Right ? gap : half;
        int b = r.Bottom == area.Bottom ? gap : half;
        var res = new RectI(r.Left + l, r.Top + t, r.Right - rr, r.Bottom - b);
        return res.IsEmpty ? r : res;
    }

    static void ResolveNode(LayoutNode n, RectI rect, List<(int, RectI)> output)
    {
        if (n.IsLeaf) { output.Add((n.Id, rect)); return; }
        var rects = ChildRects(n, rect);
        for (int i = 0; i < n.Children!.Count; i++) ResolveNode(n.Children[i], rects[i], output);
    }

    /// <summary>Child rects using cumulative rounding so children tile the parent exactly.</summary>
    public static RectI[] ChildRects(LayoutNode split, RectI rect)
    {
        int count = split.Children!.Count;
        var res = new RectI[count];
        double cum = 0;
        bool cols = split.Dir == SplitDir.Columns;
        int start = cols ? rect.Left : rect.Top;
        int len = cols ? rect.Width : rect.Height;
        int prev = start;
        for (int i = 0; i < count; i++)
        {
            cum += split.Sizes![i];
            int edge = i == count - 1 ? start + len : start + (int)Math.Round(cum * len);
            res[i] = cols ? new RectI(prev, rect.Top, edge, rect.Bottom) : new RectI(rect.Left, prev, rect.Right, edge);
            prev = edge;
        }
        return res;
    }

    public static List<Divider> Dividers(Layout layout, RectI area)
    {
        var list = new List<Divider>();
        CollectDividers(layout.Root, area, list);
        return list;
    }

    static void CollectDividers(LayoutNode n, RectI rect, List<Divider> list)
    {
        if (n.IsLeaf) return;
        var rects = ChildRects(n, rect);
        for (int i = 0; i < rects.Length; i++)
        {
            if (i < rects.Length - 1)
            {
                var line = n.Dir == SplitDir.Columns
                    ? new RectI(rects[i].Right, rect.Top, rects[i].Right, rect.Bottom)
                    : new RectI(rect.Left, rects[i].Bottom, rect.Right, rects[i].Bottom);
                list.Add(new Divider(n, i, n.Dir, line, rect));
            }
            CollectDividers(n.Children![i], rects[i], list);
        }
    }

    public static Zone? ZoneAt(IReadOnlyList<Zone> zones, PointI p)
    {
        foreach (var z in zones) if (z.Rect.Contains(p)) return z;
        // Gaps: pick the nearest zone.
        Zone? best = null; long bestD = long.MaxValue;
        foreach (var z in zones)
        {
            long dx = Math.Max(0, Math.Max(z.Rect.Left - p.X, p.X - (z.Rect.Right - 1)));
            long dy = Math.Max(0, Math.Max(z.Rect.Top - p.Y, p.Y - (z.Rect.Bottom - 1)));
            long d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = z; }
        }
        return best;
    }

    // ---------- Editing ----------

    public static LayoutNode? FindParent(LayoutNode root, LayoutNode target, out int index)
    {
        index = -1;
        if (root.IsLeaf) return null;
        for (int i = 0; i < root.Children!.Count; i++)
        {
            if (ReferenceEquals(root.Children[i], target)) { index = i; return root; }
            var p = FindParent(root.Children[i], target, out index);
            if (p != null) return p;
        }
        return null;
    }

    public static LayoutNode? FindLeaf(LayoutNode root, int id) => root.Leaves().FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// Split a leaf at <paramref name="fraction"/> (0..1 within the leaf, along <paramref name="dir"/>).
    /// The original id stays with the larger part (first part on a tie). Returns the new zone id, or 0 if rejected.
    /// </summary>
    public static int SplitLeaf(Layout layout, int leafId, SplitDir dir, double fraction)
    {
        var leaf = FindLeaf(layout.Root, leafId);
        if (leaf is null) return 0;
        fraction = Math.Clamp(fraction, 0, 1);
        var parent = FindParent(layout.Root, leaf, out int idx);
        // Size of the leaf along the split axis, as a fraction of its parent split (only meaningful when the new
        // split extends the parent in the same direction; an orthogonal split subdivides the whole leaf).
        double leafSize = parent != null && parent.Dir == dir ? parent.Sizes![idx] : 1.0;
        if (fraction * leafSize < MinFraction || (1 - fraction) * leafSize < MinFraction) return 0;

        int newId = layout.NextId++;
        bool keepFirst = fraction >= 0.5;
        var first = LayoutNode.Leaf(keepFirst ? leafId : newId);
        var second = LayoutNode.Leaf(keepFirst ? newId : leafId);

        if (parent != null && parent.Dir == dir)
        {
            double s = parent.Sizes![idx];
            parent.Children![idx] = first;
            parent.Children.Insert(idx + 1, second);
            parent.Sizes[idx] = s * fraction;
            parent.Sizes.Insert(idx + 1, s * (1 - fraction));
        }
        else
        {
            var split = LayoutNode.Split(dir, (fraction, first), (1 - fraction, second));
            if (parent == null) layout.Root = split; else parent.Children![idx] = split;
        }
        return newId;
    }

    /// <summary>Move divider <paramref name="index"/> of <paramref name="split"/> to <paramref name="fractionInSplit"/>
    /// (position measured from the split's start, 0..1). Neighbour sizes are clamped to <paramref name="minFraction"/>.</summary>
    public static void MoveDivider(LayoutNode split, int index, double fractionInSplit, double minFraction = MinFraction)
    {
        var s = split.Sizes!;
        double before = 0;
        for (int i = 0; i < index; i++) before += s[i];
        double pair = s[index] + s[index + 1];
        double lo = before + Math.Min(minFraction, pair / 2), hi = before + pair - Math.Min(minFraction, pair / 2);
        double pos = Math.Clamp(fractionInSplit, lo, hi);
        s[index] = pos - before;
        s[index + 1] = pair - s[index];
    }

    /// <summary>Remove a zone; its space goes to the previous sibling (or next if first).</summary>
    public static bool RemoveZone(Layout layout, int leafId)
    {
        var leaf = FindLeaf(layout.Root, leafId);
        if (leaf is null) return false;
        var parent = FindParent(layout.Root, leaf, out int idx);
        if (parent is null) return false; // the only zone can't be removed
        RemoveChild(parent, idx, idx > 0 ? idx - 1 : 1);
        Normalize(layout);
        return true;
    }

    /// <summary>
    /// Remove a divider (merge the two sides). Two leaves merge into one (keeping the larger id).
    /// Otherwise the smaller side is absorbed by the larger one.
    /// </summary>
    public static bool RemoveDivider(Layout layout, LayoutNode split, int index)
    {
        if (split.IsLeaf || index < 0 || index >= split.Children!.Count - 1) return false;
        var a = split.Children[index];
        var b = split.Children[index + 1];
        double sa = split.Sizes![index], sb = split.Sizes[index + 1];
        if (a.IsLeaf && b.IsLeaf)
        {
            int keep = sa >= sb ? a.Id : b.Id;
            split.Children[index] = LayoutNode.Leaf(keep);
            RemoveChild(split, index + 1, index);
        }
        else if (sa >= sb) RemoveChild(split, index + 1, index);
        else RemoveChild(split, index, index + 1);
        Normalize(layout);
        return true;
    }

    static void RemoveChild(LayoutNode parent, int removeIdx, int receiverIdx)
    {
        parent.Sizes![receiverIdx] += parent.Sizes[removeIdx];
        parent.Children!.RemoveAt(removeIdx);
        parent.Sizes.RemoveAt(removeIdx);
    }

    /// <summary>Collapse single-child splits, flatten nested same-direction splits, renormalise sizes.</summary>
    public static void Normalize(Layout layout)
    {
        layout.Root = NormalizeNode(layout.Root);
        int max = layout.Root.MaxId();
        if (layout.NextId <= max) layout.NextId = max + 1;
    }

    static LayoutNode NormalizeNode(LayoutNode n)
    {
        if (n.IsLeaf) { n.Children = null; n.Sizes = null; return n; }
        var kids = new List<LayoutNode>();
        var sizes = new List<double>();
        for (int i = 0; i < n.Children!.Count; i++)
        {
            var c = NormalizeNode(n.Children[i]);
            double s = n.Sizes![i];
            if (!c.IsLeaf && c.Dir == n.Dir)
            {
                for (int j = 0; j < c.Children!.Count; j++) { kids.Add(c.Children[j]); sizes.Add(c.Sizes![j] * s); }
            }
            else { kids.Add(c); sizes.Add(s); }
        }
        if (kids.Count == 1) return kids[0];
        double sum = sizes.Sum();
        if (sum <= 0) sum = 1;
        n.Children = kids;
        n.Sizes = sizes.Select(x => x / sum).ToList();
        return n;
    }

    /// <summary>Validate and repair a deserialised layout (bad sizes, duplicate ids, empty trees).</summary>
    public static void Repair(Layout layout)
    {
        layout.Root ??= LayoutNode.Leaf(1);
        var seen = new HashSet<int>();
        int next = Math.Max(layout.NextId, layout.Root.MaxId() + 1);
        RepairNode(layout.Root, seen, ref next);
        layout.NextId = next;
        Normalize(layout);
    }

    static void RepairNode(LayoutNode n, HashSet<int> seen, ref int next)
    {
        if (n.IsLeaf)
        {
            if (n.Id <= 0 || !seen.Add(n.Id)) { n.Id = next++; seen.Add(n.Id); }
            return;
        }
        if (n.Sizes is null || n.Sizes.Count != n.Children!.Count || n.Sizes.Any(s => !(s > 0) || double.IsInfinity(s)))
            n.Sizes = Enumerable.Repeat(1.0 / n.Children!.Count, n.Children.Count).ToList();
        foreach (var c in n.Children!) RepairNode(c, seen, ref next);
    }
}
