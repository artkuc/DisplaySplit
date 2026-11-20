using DisplaySplit.Core;

namespace DisplaySplit.Tests;

public class LayoutTests
{
    static readonly RectI Work = new(0, 0, 5120, 2088);

    static void AssertTiles(List<Zone> zones, RectI area)
    {
        long sum = zones.Sum(z => z.Rect.Area);
        Assert.Equal(area.Area, sum);
        for (int i = 0; i < zones.Count; i++)
        {
            Assert.True(area.Contains(zones[i].Rect));
            for (int j = i + 1; j < zones.Count; j++) Assert.True(zones[i].Rect.Intersect(zones[j].Rect).IsEmpty);
        }
    }

    [Fact]
    public void Presets_tile_exactly_and_have_unique_ids()
    {
        foreach (var p in LayoutPresets.All)
        {
            var l = LayoutPresets.Create(p.Key);
            var zones = LayoutOps.Resolve(l, Work);
            AssertTiles(zones, Work);
            Assert.Equal(zones.Count, zones.Select(z => z.Id).Distinct().Count());
            Assert.True(l.NextId > zones.Max(z => z.Id));
        }
    }

    [Fact]
    public void Odd_sizes_tile_without_one_pixel_gaps()
    {
        var l = LayoutPresets.Create("thirds");
        var area = new RectI(-1707, 13, 3413 - 1707, 1392 + 13);
        AssertTiles(LayoutOps.Resolve(l, area), area);
    }

    [Fact]
    public void Focus_default_for_ultrawide()
    {
        Assert.Equal("focus", LayoutPresets.DefaultKeyFor(new RectI(0, 0, 5120, 2160)));
        Assert.Equal("full", LayoutPresets.DefaultKeyFor(new RectI(0, 0, 2560, 1440)));
        var zones = LayoutOps.Resolve(LayoutPresets.Create("focus"), Work);
        Assert.Equal([1280, 2560, 1280], zones.Select(z => z.Rect.Width));
    }

    [Fact]
    public void Split_keeps_id_with_larger_part_and_inserts_sibling_in_same_direction()
    {
        var l = LayoutPresets.Create("halves"); // ids 1,2
        int newId = LayoutOps.SplitLeaf(l, 1, SplitDir.Columns, 0.25);
        Assert.Equal(3, newId);
        var zones = LayoutOps.Resolve(l, Work);
        Assert.Equal(3, zones.Count);
        Assert.Equal(newId, zones[0].Id);  // smaller left quarter gets the new id
        Assert.Equal(1, zones[1].Id);      // larger part keeps the original id
        Assert.Equal(640, zones[0].Rect.Width);
        Assert.DoesNotContain(l.Root.Children!, c => !c.IsLeaf); // flat: no nested columns
    }

    [Fact]
    public void Split_orthogonal_nests_and_rejects_tiny_parts()
    {
        var l = LayoutPresets.Create("full");
        Assert.Equal(0, LayoutOps.SplitLeaf(l, 1, SplitDir.Rows, 0.01));
        int id = LayoutOps.SplitLeaf(l, 1, SplitDir.Rows, 0.5);
        Assert.True(id > 0);
        Assert.Equal(SplitDir.Rows, l.Root.Dir);
    }

    [Fact]
    public void Horizontal_split_of_narrow_column_uses_its_own_axis()
    {
        // Regression: a 25 % column split horizontally at 12 % of its height used to be rejected
        // because the min-size check multiplied by the column's WIDTH fraction.
        var l = LayoutPresets.Create("focus");
        int id = LayoutOps.SplitLeaf(l, 1, SplitDir.Rows, 0.12);
        Assert.True(id > 0);
        var zones = LayoutOps.Resolve(l, Work);
        Assert.Equal(4, zones.Count);
        AssertTiles(zones, Work);
    }

    [Fact]
    public void Move_divider_clamps_and_preserves_pair_sum()
    {
        var l = LayoutPresets.Create("thirds");
        LayoutOps.MoveDivider(l.Root, 0, 0.0001);
        Assert.InRange(l.Root.Sizes![0], LayoutOps.MinFraction - 1e-9, 0.2);
        Assert.Equal(1.0, l.Root.Sizes.Sum(), 6);
        LayoutOps.MoveDivider(l.Root, 0, 0.5);
        Assert.Equal(0.5, l.Root.Sizes[0], 6);
        Assert.Equal(1.0 / 6, l.Root.Sizes[1], 6);
    }

    [Fact]
    public void Remove_zone_always_possible_and_collapses()
    {
        var l = LayoutPresets.Create("focus-stacked"); // cols: [rows 1,2] [3] [rows 4,5]
        Assert.True(LayoutOps.RemoveZone(l, 4));
        Assert.True(LayoutOps.RemoveZone(l, 5)); // right column disappears entirely
        var zones = LayoutOps.Resolve(l, Work);
        Assert.Equal(3, zones.Count);
        AssertTiles(zones, Work);
        Assert.False(LayoutOps.RemoveZone(LayoutPresets.Create("full"), 1)); // the last zone stays
    }

    [Fact]
    public void Remove_divider_merges_leaves_and_absorbs_smaller_subtree()
    {
        var l = LayoutPresets.Create("halves");
        Assert.True(LayoutOps.RemoveDivider(l, l.Root, 0));
        Assert.True(l.Root.IsLeaf);

        var l2 = LayoutPresets.Create("focus-stacked");
        var divs = LayoutOps.Dividers(l2, Work);
        var top = divs.First(d => d.Split == l2.Root && d.Index == 1); // between centre (50%) and right stack (25%)
        Assert.True(LayoutOps.RemoveDivider(l2, top.Split, top.Index));
        var zones = LayoutOps.Resolve(l2, Work);
        Assert.Equal(3, zones.Count);
        Assert.Contains(zones, z => z.Id == 3 && z.Rect.Width == 3840);
    }

    [Fact]
    public void Gap_applies_outer_and_inner()
    {
        var zones = LayoutOps.Resolve(LayoutPresets.Create("halves"), Work, 8);
        Assert.Equal(8, zones[0].Rect.Left);
        Assert.Equal(2560 - 4, zones[0].Rect.Right);
        Assert.Equal(2560 + 4, zones[1].Rect.Left);
        Assert.Equal(5120 - 8, zones[1].Rect.Right);
    }

    [Fact]
    public void Preset_apply_never_reuses_ids()
    {
        var l = LayoutPresets.Create("thirds"); // 1,2,3
        LayoutPresets.ApplyTo(l, "halves");
        var ids = l.Root.Leaves().Select(x => x.Id).ToList();
        Assert.Equal([4, 5], ids);
    }

    [Fact]
    public void Repair_fixes_broken_trees()
    {
        var l = new Layout
        {
            NextId = 1,
            Root = new LayoutNode
            {
                Dir = SplitDir.Columns,
                Children = [LayoutNode.Leaf(1), LayoutNode.Leaf(1), new LayoutNode { Dir = SplitDir.Rows, Children = [LayoutNode.Leaf(0)], Sizes = [1] }],
                Sizes = [double.NaN, -1, 2],
            },
        };
        LayoutOps.Repair(l);
        var zones = LayoutOps.Resolve(l, Work);
        Assert.Equal(3, zones.Select(z => z.Id).Distinct().Count());
        AssertTiles(zones, Work);
    }

    [Fact]
    public void ZoneAt_falls_back_to_nearest_in_gaps()
    {
        var zones = LayoutOps.Resolve(LayoutPresets.Create("halves"), Work, 16);
        Assert.Equal(zones[0].Id, LayoutOps.ZoneAt(zones, new PointI(2558, 1000))!.Value.Id);
        Assert.Equal(zones[1].Id, LayoutOps.ZoneAt(zones, new PointI(2563, 1000))!.Value.Id);
    }
}

