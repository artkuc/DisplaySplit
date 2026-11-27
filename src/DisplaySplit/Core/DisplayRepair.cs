namespace DisplaySplit.Core;

/// <summary>
/// Experimental "repair zone windows after a display change". When a monitor sleeps, some setups (often DisplayPort)
/// report it as unplugged; Windows then moves and shrinks normal (non-maximized) windows to fit a stand-in display and
/// does not put them back when the monitor returns. Zone windows are normal windows, so they are hit.
/// </summary>
public static class DisplayRepair
{
    /// <summary>
    /// A zone window has drifted when something other than the user moved or resized it: it neither has the rect of
    /// our last placement nor the zone's rect. User moves clear <paramref name="placed"/> (or set it to the new rect),
    /// so without a known placement nothing is considered drifted.
    /// </summary>
    public static bool IsDrifted(RectI? placed, RectI current, RectI zone) =>
        placed is { } p && !current.IsEmpty && !p.NearlyEquals(current, 2) && !current.NearlyEquals(zone, 4);
}
