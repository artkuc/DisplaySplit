using System.Collections.Concurrent;
using DisplaySplit.Native;

namespace DisplaySplit.Platform;

/// <summary>
/// Executes potentially blocking cross-process window calls (SetWindowPlacement, SetWindowPos, ShowWindow…) off the UI
/// thread, one at a time, FIFO. A watchdog replaces a worker that is stuck on a hung window and blacklists that window.
/// </summary>
public sealed class PlacementWorker : IDisposable
{
    sealed class Job(nint hwnd, Action work, Action? done)
    {
        public readonly nint Hwnd = hwnd;
        public readonly Action Work = work;
        public readonly Action? Done = done;
    }

    readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>());
    readonly ConcurrentDictionary<nint, long> _blacklist = new();
    readonly Timer _watchdog;
    readonly object _lock = new();
    int _generation;
    Job? _current;
    long _currentStart;
    volatile bool _disposed;

    const int StuckMs = 1500;
    const int MaxAbandoned = 4;
    int _abandoned;
    const int BlacklistMs = 30_000;

    public PlacementWorker()
    {
        int gen;
        lock (_lock) gen = ++_generation;
        StartThread(gen);
        _watchdog = new Timer(_ => CheckStuck(), null, 500, 500);
    }

    void StartThread(int gen)
    {
        var t = new Thread(() => Run(gen)) { IsBackground = true, Name = $"DisplaySplit.Placement#{gen}" };
        t.Start();
    }

    void Run(int gen)
    {
        try
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                lock (_lock) { _current = job; _currentStart = Environment.TickCount64; }
                if (!IsBlacklisted(job.Hwnd))
                {
                    try { job.Work(); }
                    catch (Exception ex) { Log.Error("placement job", ex); }
                }
                bool stillOwner;
                lock (_lock)
                {
                    stillOwner = gen == _generation;
                    if (stillOwner) _current = null;
                }
                if (job.Done != null) App.Post(job.Done);
                if (!stillOwner)
                {
                    Interlocked.Decrement(ref _abandoned); // a replacement worker took over while we were stuck
                    return;
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    void CheckStuck()
    {
        if (_disposed) return;
        Job? stuck = null;
        int gen = 0;
        bool replace = false;
        lock (_lock)
        {
            if (_current != null && Environment.TickCount64 - _currentStart > StuckMs)
            {
                stuck = _current;
                // Decide in the same critical section: replace only while few threads are abandoned; otherwise the
                // stuck thread stays the owner (the queue waits) — never a state where nobody consumes the queue.
                if (Volatile.Read(ref _abandoned) < MaxAbandoned)
                {
                    replace = true;
                    Interlocked.Increment(ref _abandoned);
                    _current = null;
                    gen = ++_generation; // the stuck thread sees a newer generation and exits once its call returns
                }
                else _currentStart = Environment.TickCount64; // re-check later, don't spam
            }
        }
        if (stuck == null) return;
        _blacklist[stuck.Hwnd] = Environment.TickCount64 + BlacklistMs;
        if (!replace) { Log.Warn("placement worker stuck; too many abandoned threads, queue waits"); return; }
        Log.Warn($"placement worker stuck on 0x{stuck.Hwnd:X}; replacing worker, window blacklisted");
        StartThread(gen);
    }

    public bool IsBlacklisted(nint hwnd)
    {
        if (hwnd == 0 || !_blacklist.TryGetValue(hwnd, out long until)) return false;
        if (Environment.TickCount64 < until) return true;
        _blacklist.TryRemove(hwnd, out _);
        return false;
    }

    /// <summary>Queue work against <paramref name="hwnd"/>; <paramref name="done"/> runs on the UI thread afterwards.</summary>
    public void Enqueue(nint hwnd, Action work, Action? done = null)
    {
        if (_disposed) return;
        if (IsBlacklisted(hwnd)) { if (done != null) App.Post(done); return; }
        _queue.Add(new Job(hwnd, work, done));
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdog.Dispose();
        _queue.CompleteAdding();
    }
}
