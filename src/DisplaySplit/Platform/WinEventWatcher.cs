using System.Runtime.InteropServices;
using DisplaySplit.Native;

namespace DisplaySplit.Platform;

public interface IWinEventSink
{
    void OnWindowShown(nint hwnd);          // OBJECT_SHOW / OBJECT_UNCLOAKED (top-level candidates)
    void OnMoveSizeStart(nint hwnd);
    void OnMoveSizeEnd(nint hwnd);
    void OnMinimizeStart(nint hwnd);
    void OnForeground(nint hwnd);
}

/// <summary>
/// Out-of-context WinEvent hooks, registered one event each (no ranges). Callbacks arrive on the UI thread's message
/// loop and filter to window-level events before doing anything else.
/// </summary>
public static unsafe class WinEventWatcher
{
    static readonly List<nint> Hooks = [];
    static IWinEventSink? _sink;

    public static void Start(IWinEventSink sink)
    {
        _sink = sink;
        foreach (uint e in new[]
                 {
                     C.EVENT_OBJECT_SHOW, C.EVENT_OBJECT_UNCLOAKED, C.EVENT_SYSTEM_MOVESIZESTART, C.EVENT_SYSTEM_MOVESIZEEND,
                     C.EVENT_SYSTEM_MINIMIZESTART, C.EVENT_SYSTEM_FOREGROUND,
                 })
        {
            nint h = User32.SetWinEventHook(e, e, 0, &Callback, 0, 0, C.WINEVENT_OUTOFCONTEXT | C.WINEVENT_SKIPOWNPROCESS);
            if (h == 0) Log.Warn($"SetWinEventHook 0x{e:X} failed");
            else Hooks.Add(h);
        }
    }

    public static void Stop()
    {
        foreach (var h in Hooks) User32.UnhookWinEvent(h);
        Hooks.Clear();
        _sink = null;
    }

    [UnmanagedCallersOnly]
    static void Callback(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == 0 || idObject != C.OBJID_WINDOW || idChild != C.CHILDID_SELF) return;
        var sink = _sink;
        if (sink is null) return;
        try
        {
            switch (evt)
            {
                case C.EVENT_OBJECT_SHOW:
                case C.EVENT_OBJECT_UNCLOAKED:
                    // Cheap pre-filter: only top-level windows.
                    if (User32.GetAncestor(hwnd, C.GA_ROOT) == hwnd) sink.OnWindowShown(hwnd);
                    break;
                case C.EVENT_SYSTEM_MOVESIZESTART: sink.OnMoveSizeStart(hwnd); break;
                case C.EVENT_SYSTEM_MOVESIZEEND: sink.OnMoveSizeEnd(hwnd); break;
                case C.EVENT_SYSTEM_MINIMIZESTART: sink.OnMinimizeStart(hwnd); break;
                case C.EVENT_SYSTEM_FOREGROUND: sink.OnForeground(hwnd); break;
            }
        }
        catch (Exception ex) { Log.Error($"winevent 0x{evt:X}", ex); }
    }
}
