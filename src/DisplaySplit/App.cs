using System.Collections.Concurrent;
using DisplaySplit.Native;
using DisplaySplit.Platform;
using DisplaySplit.UI;

namespace DisplaySplit;

/// <summary>UI-thread infrastructure: hidden host window, cross-thread Post, timers.</summary>
public static class App
{
    public const string HostClass = "DisplaySplit.Host";
    public const uint WM_APP_RUN = C.WM_APP + 1;
    public const uint WM_APP_OPEN_EDITOR = C.WM_APP + 2;
    public const uint WM_APP_SNAP = C.WM_APP + 3;       // from hook thread: wParam=hwnd, lParam=packed point; shift in flag
    public const uint WM_APP_TRAY = C.WM_APP + 4;

    static readonly ConcurrentQueue<Action> Queue = new();
    static readonly Dictionary<nuint, Action> Timers = [];
    static nuint _nextTimer = 1000;

    public static uint UiThreadId { get; private set; }
    public static HostWindow Host { get; private set; } = null!;
    public static bool IsUiThread => Kernel32.GetCurrentThreadId() == UiThreadId;
    public static uint ProcessId { get; } = Kernel32.GetCurrentProcessId();

    internal static void Init(HostWindow host)
    {
        UiThreadId = Kernel32.GetCurrentThreadId();
        Host = host;
    }

    /// <summary>Run <paramref name="action"/> on the UI thread (thread-safe, never blocks).</summary>
    public static void Post(Action action)
    {
        Queue.Enqueue(action);
        if (Host is { Handle: not 0 }) User32.PostMessageW(Host.Handle, WM_APP_RUN, 0, 0);
    }

    internal static void DrainQueue()
    {
        int budget = 256;
        while (budget-- > 0 && Queue.TryDequeue(out var a))
        {
            try { a(); } catch (Exception ex) { Log.Error("posted action", ex); }
        }
        if (!Queue.IsEmpty) User32.PostMessageW(Host.Handle, WM_APP_RUN, 0, 0);
    }

    /// <summary>One-shot UI timer. Returns an id usable with <see cref="CancelTimeout"/>.</summary>
    public static nuint SetTimeout(int ms, Action action)
    {
        nuint id = _nextTimer++;
        if (_nextTimer > 0x7FFF_0000) _nextTimer = 1000;
        Timers[id] = action;
        User32.SetTimer(Host.Handle, id, (uint)Math.Max(1, ms), 0);
        return id;
    }

    public static void CancelTimeout(ref nuint id)
    {
        if (id == 0) return;
        User32.KillTimer(Host.Handle, id);
        Timers.Remove(id);
        id = 0;
    }

    internal static bool FireTimer(nuint id)
    {
        if (!Timers.Remove(id, out var action)) return false;
        User32.KillTimer(Host.Handle, id);
        try { action(); } catch (Exception ex) { Log.Error("timer", ex); }
        return true;
    }

    public static void RunMessageLoop()
    {
        while (User32.GetMessageW(out MSG msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(msg);
            User32.DispatchMessageW(msg);
        }
    }

    public static void Quit() => User32.PostQuitMessage(0);
}
