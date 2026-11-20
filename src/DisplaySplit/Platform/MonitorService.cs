using System.Runtime.InteropServices;
using DisplaySplit.Core;
using DisplaySplit.Native;

namespace DisplaySplit.Platform;

public sealed record MonitorInfo(
    nint HMonitor,
    string GdiName,       // \\.\DISPLAY1
    string Key,           // EDID-based stable key, e.g. "edid:4C2D:7158:1"
    string? DevicePath,   // \\?\DISPLAY#SAM7158#...
    string FriendlyName,
    ushort EdidMfg,
    ushort EdidProduct,
    RectI Bounds,
    RectI Work,
    uint Dpi,
    bool Primary)
{
    public double Scale => Dpi / 96.0;
}

/// <summary>Enumerates monitors and resolves stable identities via the DisplayConfig API.</summary>
public static unsafe class MonitorService
{
    static List<MonitorInfo> _monitors = [];

    public static IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public static void Refresh()
    {
        var targets = QueryTargets();
        _collect = [];
        User32.EnumDisplayMonitors(0, 0, &MonitorEnumProc, 0);
        var list = new List<MonitorInfo>();
        foreach (var h in _collect)
        {
            var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
            if (!User32.GetMonitorInfo(h, ref mi)) continue;
            string gdi = new string(mi.szDevice);
            int nul = gdi.IndexOf('\0'); if (nul >= 0) gdi = gdi[..nul];
            Shcore.GetDpiForMonitor(h, 0, out uint dpi, out _);
            if (dpi == 0) dpi = 96;
            targets.TryGetValue(gdi, out var t);
            var bounds = new RectI(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
            string key = t.Mfg != 0 || t.Product != 0
                ? $"edid:{t.Mfg:X4}:{t.Product:X4}:{t.Connector}"
                : $"gdi:{gdi}:{bounds.Width}x{bounds.Height}";
            list.Add(new MonitorInfo(h, gdi, key, t.Path, string.IsNullOrEmpty(t.Name) ? gdi : t.Name, t.Mfg, t.Product, bounds,
                new RectI(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom), dpi,
                (mi.dwFlags & C.MONITORINFOF_PRIMARY) != 0));
        }
        // Two identical monitors on the same connector index can't collide (connector differs) but guard anyway.
        var dupes = list.GroupBy(m => m.Key).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)).ToHashSet();
        if (dupes.Count > 0)
            list = list.Select(m => dupes.Contains(m) ? m with { Key = m.Key + "#" + m.GdiName } : m).ToList();
        _monitors = list.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
        _collect = null;
    }

    [ThreadStatic] static List<nint>? _collect;

    [UnmanagedCallersOnly]
    static int MonitorEnumProc(nint hmon, nint hdc, RECT* rc, nint data)
    {
        _collect?.Add(hmon);
        return 1;
    }

    record struct TargetInfo(ushort Mfg, ushort Product, uint Connector, string? Path, string? Name);

    static Dictionary<string, TargetInfo> QueryTargets()
    {
        var result = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (User32.GetDisplayConfigBufferSizes(C.QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            fixed (DISPLAYCONFIG_PATH_INFO* pp = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* pm = modes)
            {
                if (User32.QueryDisplayConfig(C.QDC_ONLY_ACTIVE_PATHS, ref np, pp, ref nm, pm, 0) != 0) return result;
            }
            for (int i = 0; i < np; i++)
            {
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header.type = C.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                src.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                src.header.adapterId = paths[i].sourceInfo.adapterId;
                src.header.id = paths[i].sourceInfo.id;
                if (User32.DisplayConfigGetDeviceInfo(&src) != 0) continue;
                string gdi = FixedString(src.viewGdiDeviceName, 32);

                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgt.header.type = C.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                tgt.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
                tgt.header.adapterId = paths[i].targetInfo.adapterId;
                tgt.header.id = paths[i].targetInfo.id;
                if (User32.DisplayConfigGetDeviceInfo(&tgt) != 0) continue;
                result.TryAdd(gdi, new TargetInfo(tgt.edidManufactureId, tgt.edidProductCodeId, tgt.connectorInstance,
                    FixedString(tgt.monitorDevicePath, 128), FixedString(tgt.monitorFriendlyDeviceName, 64)));
            }
        }
        catch (Exception ex) { Log.Warn($"QueryDisplayConfig: {ex.Message}"); }
        return result;
    }

    static string FixedString(char* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return new string(p, 0, n);
    }

    public static MonitorInfo? FromPoint(PointI p)
    {
        nint h = User32.MonitorFromPoint(new POINT(p.X, p.Y), C.MONITOR_DEFAULTTONEAREST);
        return ByHandle(h) ?? Nearest(p);
    }

    public static MonitorInfo? FromWindow(nint hwnd)
    {
        nint h = User32.MonitorFromWindow(hwnd, C.MONITOR_DEFAULTTONEAREST);
        return ByHandle(h) ?? (_monitors.Count > 0 ? _monitors[0] : null);
    }

    public static MonitorInfo? FromRect(RectI r)
    {
        var rc = new RECT(r.Left, r.Top, r.Right, r.Bottom);
        nint h = User32.MonitorFromRect(rc, C.MONITOR_DEFAULTTONEAREST);
        return ByHandle(h) ?? Nearest(r.Center);
    }

    public static MonitorInfo? ByKey(string key) => _monitors.FirstOrDefault(m => m.Key == key);

    static MonitorInfo? ByHandle(nint h)
    {
        foreach (var m in _monitors) if (m.HMonitor == h) return m;
        // HMONITOR values can change after display changes; refresh lazily once.
        if (h != 0)
        {
            Refresh();
            foreach (var m in _monitors) if (m.HMonitor == h) return m;
        }
        return null;
    }

    static MonitorInfo? Nearest(PointI p) =>
        _monitors.OrderBy(m => Math.Abs(m.Bounds.Center.X - p.X) + Math.Abs(m.Bounds.Center.Y - p.Y)).FirstOrDefault();

    /// <summary>Monitor bounds of the monitor nearest to the window (no caching, cheap).</summary>
    public static RectI MonitorRectOf(nint hwnd)
    {
        nint h = User32.MonitorFromWindow(hwnd, C.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        return User32.GetMonitorInfo(h, ref mi)
            ? new RectI(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom)
            : default;
    }
}
