using System.Runtime.InteropServices;
using System.Text.Json;
using DisplaySplit.Core;
using DisplaySplit.Native;

namespace DisplaySplit.Tests;

public class DoubleClickTests
{
    [Fact]
    public void Detects_second_click_within_time_and_rect()
    {
        var d = new DoubleClickDetector(500, 4, 4);
        var a = d.OnLeftDown(1000, new PointI(10, 10));
        Assert.Equal(ClickKind.First, a.Kind);
        var b = d.OnLeftDown(1300, new PointI(11, 9));
        Assert.Equal(ClickKind.Second, b.Kind);
        Assert.Equal(a.Seq, b.Seq);
    }

    [Fact]
    public void Too_slow_or_too_far_starts_new_sequence()
    {
        var d = new DoubleClickDetector(500, 4, 4);
        var a = d.OnLeftDown(1000, new PointI(10, 10));
        var b = d.OnLeftDown(1600, new PointI(10, 10));
        Assert.Equal(ClickKind.First, b.Kind);
        Assert.NotEqual(a.Seq, b.Seq);
        var c = d.OnLeftDown(1700, new PointI(20, 10));
        Assert.Equal(ClickKind.First, c.Kind);
    }

    [Fact]
    public void Third_click_starts_new_sequence_and_reset_breaks()
    {
        var d = new DoubleClickDetector(500, 4, 4);
        d.OnLeftDown(1000, new PointI(0, 0));
        Assert.Equal(ClickKind.Second, d.OnLeftDown(1100, new PointI(0, 0)).Kind);
        Assert.Equal(ClickKind.First, d.OnLeftDown(1200, new PointI(0, 0)).Kind);
        d.Reset();
        Assert.Equal(ClickKind.First, d.OnLeftDown(1250, new PointI(0, 0)).Kind);
    }

    [Fact]
    public void Handles_tick_wraparound()
    {
        var d = new DoubleClickDetector(500, 4, 4);
        d.OnLeftDown(uint.MaxValue - 100, new PointI(0, 0));
        Assert.Equal(ClickKind.Second, d.OnLeftDown(200, new PointI(0, 0)).Kind);
    }
}

public class IdentityTests
{
    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Discord\app-1.0.9175\Discord.exe", @"c:\users\a\appdata\local\discord\discord.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe",
        @"c:\program files\windowsapps\microsoft.windowsterminal__8wekyb3d8bbwe\windowsterminal.exe")]
    [InlineData(@"C:\Program Files\JetBrains\Rider\bin\rider64.exe", @"c:\program files\jetbrains\rider\bin\rider64.exe")]
    [InlineData(@"C:\tools\app\12.0.6099.130\app.exe", @"c:\tools\app\app.exe")]
    public void Exe_paths_are_version_independent(string input, string expected) =>
        Assert.Equal(expected, AppIdentityRules.NormalizeExePath(input));

    [Theory]
    [InlineData("● main.cs - MyProj - Visual Studio Code", "main.cs - myproj")]
    [InlineData("(3) Inbox - Outlook", "inbox")]
    [InlineData("Notepad", "notepad")]
    [InlineData("Downloads — File Explorer", "downloads")]
    [InlineData("", "")]
    public void Titles_are_normalised(string input, string expected) =>
        Assert.Equal(expected, AppIdentityRules.NormalizeTitle(input));

    [Fact]
    public void Wpf_window_class_guid_is_normalised()
    {
        string a = AppIdentityRules.MakeKey("exe:c:\\x\\devenv.exe", "HwndWrapper[DefaultDomain;;df3844d0-1a2b-4c5d-8e9f-0123456789ab]");
        string b = AppIdentityRules.MakeKey("exe:c:\\x\\devenv.exe", "HwndWrapper[DefaultDomain;;11111111-2222-3333-4444-555555555555]");
        Assert.Equal(a, b);
        Assert.Equal(a, AppIdentityRules.NormalizeKey("exe:c:\\x\\devenv.exe|HwndWrapper[DefaultDomain;;ABCDEF01-2222-3333-4444-555555555555]"));
        Assert.EndsWith("|Chrome_WidgetWin_1", AppIdentityRules.MakeKey("aumid:chrome", "Chrome_WidgetWin_1"));
    }

    [Fact]
    public void Key_roundtrip() =>
        Assert.Equal("exe:c:\\a.exe", AppIdentityRules.IdentityPart(AppIdentityRules.MakeKey("exe:c:\\a.exe", "Chrome_WidgetWin_1")));
}

public class RestoreTests
{
    static RestoreCandidate Fresh() => new(false, false, true, true, 2, 0, false, false, true, 1200, 800);

    [Fact]
    public void Fresh_window_is_applied() => Assert.Equal(RestoreDecision.Apply, RestorePolicy.Decide(Fresh()));

    [Fact]
    public void Old_process_with_other_windows_is_skipped() =>
        Assert.Equal(RestoreDecision.SkipNotFresh, RestorePolicy.Decide(Fresh() with { ProcessAgeSeconds = 600, OtherLiveWindowsWithSameKey = 1 }));

    [Fact]
    public void Old_process_first_window_is_applied() =>
        Assert.Equal(RestoreDecision.Apply, RestorePolicy.Decide(Fresh() with { ProcessAgeSeconds = 600 }));

    [Fact]
    public void Busy_small_disabled_handled()
    {
        Assert.Equal(RestoreDecision.SkipUserBusy, RestorePolicy.Decide(Fresh() with { MouseButtonDown = true }));
        Assert.Equal(RestoreDecision.SkipTooSmall, RestorePolicy.Decide(Fresh() with { Width = 300 }));
        Assert.Equal(RestoreDecision.SkipDisabled, RestorePolicy.Decide(Fresh() with { Excluded = true }));
        Assert.Equal(RestoreDecision.SkipHandled, RestorePolicy.Decide(Fresh() with { AlreadyHandled = true }));
        Assert.Equal(RestoreDecision.SkipNotResizable, RestorePolicy.Decide(Fresh() with { Resizable = false }));
    }

    [Fact]
    public void Slot_matcher_prefers_title_then_recency_and_skips_occupied()
    {
        var now = DateTime.UtcNow;
        var slots = new List<Slot>
        {
            new() { Id = "a", Title = "proj-a", LastUsedUtc = now.AddMinutes(-5) },
            new() { Id = "b", Title = "proj-b", LastUsedUtc = now.AddMinutes(-1) },
            new() { Id = "c", Title = "", LastUsedUtc = now },
        };
        Assert.Equal("a", SlotMatcher.Pick(slots, new HashSet<string>(), "proj-a")!.Id);
        Assert.Equal("c", SlotMatcher.Pick(slots, new HashSet<string>(), "unknown")!.Id);
        Assert.Equal("b", SlotMatcher.Pick(slots, new HashSet<string> { "a", "c" }, "proj-a")!.Id);
        Assert.Null(SlotMatcher.Pick(slots, new HashSet<string> { "a", "b", "c" }, "x"));
    }

    [Fact]
    public void Upsert_is_bounded_lru()
    {
        var list = new List<Slot>();
        for (int i = 0; i < SlotMatcher.MaxSlotsPerApp + 3; i++)
            SlotMatcher.Upsert(list, new Slot { Id = "s" + i, LastUsedUtc = DateTime.UtcNow.AddSeconds(i) });
        Assert.Equal(SlotMatcher.MaxSlotsPerApp, list.Count);
        Assert.DoesNotContain(list, s => s.Id == "s0");
        SlotMatcher.Upsert(list, new Slot { Id = "s5", Title = "updated" });
        Assert.Equal("updated", list.Single(s => s.Id == "s5").Title);
    }
}

public class PersistenceTests
{
    [Fact]
    public void Json_roundtrip()
    {
        var f = new LayoutsFile();
        var l = LayoutPresets.Create("focus-stacked");
        f.Monitors.Add(new MonitorLayouts { MonitorKey = "edid:1:2:0", Layouts = [l], ActiveId = l.Id });
        string json = JsonSerializer.Serialize(f, JsonCtx.Default.LayoutsFile);
        var back = JsonSerializer.Deserialize(json, JsonCtx.Default.LayoutsFile)!;
        Assert.Equal(LayoutOps.Resolve(l, new RectI(0, 0, 5120, 2088)), LayoutOps.Resolve(back.Monitors[0].Active, new RectI(0, 0, 5120, 2088)));

        var p = new PlacementsFile();
        p.Apps["k"] = [new Slot { Id = "x", Fraction = new RectF(0.25, 0, 0.75, 1), ExplicitZones = { ["L"] = 2 } }];
        var pb = JsonSerializer.Deserialize(JsonSerializer.Serialize(p, JsonCtx.Default.PlacementsFile), JsonCtx.Default.PlacementsFile)!;
        Assert.Equal(0.25, pb.Apps["k"][0].Fraction.Left);
        Assert.Equal(2, pb.Apps["k"][0].ExplicitZones["L"]);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_bak_and_is_quarantined()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ds-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // Storage.Home is process-wide, so exercise the same algorithm via Save/Load in the real home with a unique name.
            string name = "test-" + Guid.NewGuid().ToString("N")[..8] + ".json";
            Platform.Storage.Save(name, new Settings { GapPx = 8 }, JsonCtx.Default.Settings);
            Platform.Storage.Save(name, new Settings { GapPx = 16 }, JsonCtx.Default.Settings); // .bak now has 8
            File.WriteAllText(Platform.Storage.PathOf(name), "{ not json");
            var s = Platform.Storage.Load(name, JsonCtx.Default.Settings, () => new Settings(), out bool fromDisk);
            Assert.True(fromDisk);
            Assert.Equal(8, s.GapPx);
            Assert.NotEmpty(Directory.GetFiles(Platform.Storage.Home, name + ".corrupt-*"));
            foreach (var f in Directory.GetFiles(Platform.Storage.Home, name + "*")) File.Delete(f);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class InteropTests
{
    [Fact]
    public unsafe void Struct_sizes_match_win64()
    {
        Assert.Equal(48, sizeof(MSG));
        Assert.Equal(80, sizeof(WNDCLASSEXW));
        Assert.Equal(32, sizeof(MSLLHOOKSTRUCT));
        Assert.Equal(104, sizeof(MONITORINFOEXW));
        Assert.Equal(44, sizeof(WINDOWPLACEMENT));
        Assert.Equal(976, sizeof(NOTIFYICONDATAW));
        Assert.Equal(72, sizeof(DISPLAYCONFIG_PATH_INFO));
        Assert.Equal(64, sizeof(DISPLAYCONFIG_MODE_INFO));
        Assert.Equal(420, sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
        Assert.Equal(84, sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
        Assert.Equal(80, sizeof(UPDATELAYEREDWINDOWINFO));
        Assert.Equal(40, sizeof(INPUT));
        Assert.Equal(72, sizeof(PAINTSTRUCT));
        Assert.Equal(840, sizeof(DISPLAY_DEVICEW));
        Assert.Equal(24, Marshal.SizeOf<PROPVARIANT>());
    }
}
