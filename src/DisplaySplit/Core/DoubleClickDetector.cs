namespace DisplaySplit.Core;

public enum ClickKind { First, Second }

public readonly record struct ClickResult(ClickKind Kind, int Seq);

/// <summary>
/// Pure double-click state machine fed with raw low-level mouse button-down events.
/// Mirrors the Win32 rules: second press within GetDoubleClickTime() ms and within the
/// SM_CXDOUBLECLK × SM_CYDOUBLECLK rectangle centred on the first press.
/// </summary>
public sealed class DoubleClickDetector
{
    uint _maxMs;
    int _halfCx, _halfCy;

    bool _hasFirst;
    uint _firstTime;
    PointI _firstPt;
    int _seq;

    public DoubleClickDetector(uint doubleClickMs, int cxDoubleClk, int cyDoubleClk) => Configure(doubleClickMs, cxDoubleClk, cyDoubleClk);

    public void Configure(uint doubleClickMs, int cxDoubleClk, int cyDoubleClk)
    {
        _maxMs = doubleClickMs;
        _halfCx = Math.Max(1, cxDoubleClk / 2);
        _halfCy = Math.Max(1, cyDoubleClk / 2);
    }

    public int CurrentSeq => _seq;

    /// <summary>Left button pressed at <paramref name="time"/> (ms, wraps) and <paramref name="pt"/>.</summary>
    public ClickResult OnLeftDown(uint time, PointI pt)
    {
        if (_hasFirst && unchecked(time - _firstTime) <= _maxMs &&
            Math.Abs(pt.X - _firstPt.X) <= _halfCx && Math.Abs(pt.Y - _firstPt.Y) <= _halfCy)
        {
            _hasFirst = false; // a third press starts a new sequence
            return new ClickResult(ClickKind.Second, _seq);
        }
        _hasFirst = true;
        _firstTime = time;
        _firstPt = pt;
        _seq = unchecked(_seq + 1);
        return new ClickResult(ClickKind.First, _seq);
    }

    /// <summary>Any other button (right/middle/x) breaks the sequence.</summary>
    public void Reset() => _hasFirst = false;
}
