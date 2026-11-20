namespace DisplaySplit.Core;

/// <summary>Facts about a window that just became visible, gathered by the platform layer.</summary>
public readonly record struct RestoreCandidate(
    bool AlreadyHandled,
    bool Excluded,
    bool RememberEnabled,
    bool HasSlots,
    double ProcessAgeSeconds,
    int OtherLiveWindowsWithSameKey,
    bool MouseButtonDown,
    bool MoveSizeLoopActive,
    bool Resizable,
    int Width,
    int Height);

public enum RestoreDecision
{
    Apply,
    SkipHandled,
    SkipDisabled,
    SkipNoSlots,
    SkipNotFresh,
    SkipUserBusy,
    SkipNotResizable,
    SkipTooSmall,
}

public static class RestorePolicy
{
    public const double FreshProcessSeconds = 60;
    public const int MinWidth = 400, MinHeight = 300;

    public static RestoreDecision Decide(in RestoreCandidate c)
    {
        if (c.AlreadyHandled) return RestoreDecision.SkipHandled;
        if (!c.RememberEnabled || c.Excluded) return RestoreDecision.SkipDisabled;
        if (!c.HasSlots) return RestoreDecision.SkipNoSlots;
        if (c.ProcessAgeSeconds > FreshProcessSeconds && c.OtherLiveWindowsWithSameKey > 0) return RestoreDecision.SkipNotFresh;
        if (c.MouseButtonDown || c.MoveSizeLoopActive) return RestoreDecision.SkipUserBusy;
        if (!c.Resizable) return RestoreDecision.SkipNotResizable;
        if (c.Width < MinWidth || c.Height < MinHeight) return RestoreDecision.SkipTooSmall;
        return RestoreDecision.Apply;
    }
}

public static class SlotMatcher
{
    /// <summary>
    /// Pick the best slot for a window: slots occupied by other live windows of the same app are skipped;
    /// best title score wins, ties broken by most recent use. Returns null when no free slot exists.
    /// </summary>
    public static Slot? Pick(IReadOnlyList<Slot> slots, IReadOnlySet<string> occupiedIds, string normalizedTitle)
    {
        Slot? best = null; int bestScore = -1;
        foreach (var s in slots)
        {
            if (occupiedIds.Contains(s.Id)) continue;
            int score = AppIdentityRules.TitleScore(normalizedTitle, s.Title);
            if (score > bestScore || (score == bestScore && s.LastUsedUtc > best!.LastUsedUtc))
            {
                best = s; bestScore = score;
            }
        }
        return best;
    }

    public static int Score(Slot slot, string normalizedTitle) => AppIdentityRules.TitleScore(normalizedTitle, slot.Title);

    public const int MaxSlotsPerApp = 8;

    /// <summary>Insert or replace (by id) a slot, keeping the list bounded with LRU eviction.</summary>
    public static void Upsert(List<Slot> slots, Slot slot)
    {
        int idx = slots.FindIndex(s => s.Id == slot.Id);
        if (idx >= 0) { slots[idx] = slot; return; }
        if (slots.Count >= MaxSlotsPerApp)
        {
            int oldest = 0;
            for (int i = 1; i < slots.Count; i++) if (slots[i].LastUsedUtc < slots[oldest].LastUsedUtc) oldest = i;
            slots.RemoveAt(oldest);
        }
        slots.Add(slot);
    }
}
