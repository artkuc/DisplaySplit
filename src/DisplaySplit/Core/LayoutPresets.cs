namespace DisplaySplit.Core;

public sealed record LayoutPreset(string Key, string Name, Func<Func<int>, LayoutNode> Build);

public static class LayoutPresets
{
    static LayoutNode Cols(Func<int> id, params double[] sizes) =>
        LayoutNode.Split(SplitDir.Columns, sizes.Select(s => (s, LayoutNode.Leaf(id()))).ToArray());

    static LayoutNode Rows(Func<int> id, params double[] sizes) =>
        LayoutNode.Split(SplitDir.Rows, sizes.Select(s => (s, LayoutNode.Leaf(id()))).ToArray());

    public static readonly IReadOnlyList<LayoutPreset> All =
    [
        new("full", "Full screen", id => LayoutNode.Leaf(id())),
        new("halves", "Two halves", id => Cols(id, 1, 1)),
        new("thirds", "Three columns", id => Cols(id, 1, 1, 1)),
        new("focus", "Focus center 25/50/25", id => Cols(id, 1, 2, 1)),
        new("main-side", "Main + side 2/3 · 1/3", id => Cols(id, 2, 1)),
        new("quarters", "Four columns", id => Cols(id, 1, 1, 1, 1)),
        new("focus-stacked", "Center + stacked sides", id => LayoutNode.Split(SplitDir.Columns,
            (1, Rows(id, 1, 1)), (2, LayoutNode.Leaf(id())), (1, Rows(id, 1, 1)))),
        new("grid", "2 × 2 grid", id => LayoutNode.Split(SplitDir.Columns, (1, Rows(id, 1, 1)), (1, Rows(id, 1, 1)))),
    ];

    public static LayoutPreset? Find(string key) => All.FirstOrDefault(p => p.Key == key);

    /// <summary>Default for a monitor: ultrawide (aspect ≥ 2.1) gets the 25/50/25 focus layout.</summary>
    public static string DefaultKeyFor(RectI monitor) =>
        monitor.Height > 0 && (double)monitor.Width / monitor.Height >= 2.1 ? "focus" : "full";

    /// <summary>Create a brand new layout from a preset.</summary>
    public static Layout Create(string key)
    {
        var preset = Find(key) ?? All[0];
        var layout = new Layout { Name = preset.Name, NextId = 1 };
        layout.Root = preset.Build(() => layout.NextId++);
        return layout;
    }

    /// <summary>Replace the tree of an existing layout with a preset, handing out fresh ids (never reusing old ones).</summary>
    public static void ApplyTo(Layout layout, string key)
    {
        var preset = Find(key) ?? All[0];
        layout.Root = preset.Build(() => layout.NextId++);
    }
}
