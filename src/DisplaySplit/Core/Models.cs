using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisplaySplit.Core;

public sealed class Settings
{
    public int Version { get; set; } = 1;
    public bool DoubleClickSnap { get; set; } = true;
    public bool ShiftDragSnap { get; set; } = true;
    public bool RememberPositions { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ArrangeOnLogon { get; set; } = true;
    public bool Paused { get; set; }
    public int GapPx { get; set; }
    public bool FirstRunDone { get; set; }
    /// <summary>Experimental: after the monitor set changes (e.g. a monitor woke up), put zone windows that Windows
    /// moved or shrank back into their zones.</summary>
    public bool RepairAfterDisplayChange { get; set; }
    /// <summary>App keys (see <see cref="AppIdentity.Key"/>) that are never remembered/restored.</summary>
    public List<string> ExcludedApps { get; set; } = [];
}

public sealed class MonitorLayouts
{
    /// <summary>Best stable key we have (EDID based when available).</summary>
    public string MonitorKey { get; set; } = "";
    public string? DevicePath { get; set; }
    /// <summary>Keys this monitor was known by before (e.g. after a port/driver change); slots may still use them.</summary>
    public List<string> PreviousKeys { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public List<Layout> Layouts { get; set; } = [];
    public string ActiveId { get; set; } = "";

    [JsonIgnore] public Layout Active => Layouts.FirstOrDefault(l => l.Id == ActiveId) ?? Layouts[0];
}

public sealed class LayoutsFile
{
    public int Version { get; set; } = 1;
    public List<MonitorLayouts> Monitors { get; set; } = [];
}

public enum SlotKind { Zone, Maximized }

/// <summary>One remembered placement of one window of an app.</summary>
public sealed class Slot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public SlotKind Kind { get; set; }
    public string MonitorKey { get; set; } = "";
    public string LayoutId { get; set; } = "";
    public int ZoneId { get; set; }
    /// <summary>Zone rect as fraction of the monitor work area – fallback when the zone id no longer exists.</summary>
    public double FL { get; set; }
    public double FT { get; set; }
    public double FR { get; set; } = 1;
    public double FB { get; set; } = 1;
    /// <summary>Normalised window title when the slot was saved (used to match multi-window apps).</summary>
    public string Title { get; set; } = "";
    public DateTime LastUsedUtc { get; set; }
    /// <summary>Zones the user explicitly chose, per layout of this monitor.</summary>
    public Dictionary<string, int> ExplicitZones { get; set; } = [];
    /// <summary>Zones derived by overlap for layouts the user never placed this window in (cleared on explicit moves).</summary>
    public Dictionary<string, int> DerivedZones { get; set; } = [];

    [JsonIgnore] public RectF Fraction { get => new(FL, FT, FR, FB); set { FL = value.Left; FT = value.Top; FR = value.Right; FB = value.Bottom; } }
}

public sealed class PlacementsFile
{
    public int Version { get; set; } = 1;
    /// <summary>Key = <see cref="AppIdentity.Key"/>.</summary>
    public Dictionary<string, List<Slot>> Apps { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(LayoutsFile))]
[JsonSerializable(typeof(PlacementsFile))]
internal sealed partial class JsonCtx : JsonSerializerContext;
