namespace SimosaBRM.SimCore.Scenario;

/// <summary>時間表執行狀態快照(進入 <see cref="Engine.EngineSnapshot.Timeline"/>;情境引擎設計第 3.6 節)。</summary>
public sealed record TimelineSnapshot
{
    public required string Phase { get; init; }
    /// <summary>事件序號(下一個事件的 seq)</summary>
    public required long Seq { get; init; }
    /// <summary>其中評估事件(source = assessment)的數量;雜湊只計 Seq − AssessmentEvents,使重新評分不改變雜湊</summary>
    public long AssessmentEvents { get; init; }
    public required IReadOnlyList<TimelineItemState> Items { get; init; }
    public required IReadOnlyList<PendingAction> PendingActions { get; init; }
    public required IReadOnlyList<EnvironmentRamp> Ramps { get; init; }
    public required Dictionary<string, long> EventCounts { get; init; }
    public required Dictionary<string, long> LastEventTick { get; init; }
    /// <summary>情境階段歷程(phase、起始 tick)</summary>
    public IReadOnlyList<PhaseSpan>? PhaseHistory { get; init; }
    public bool Ended { get; init; }
}

public sealed record TimelineItemState
{
    public required string Id { get; init; }
    public bool Armed { get; init; } = true;
    public long FireCount { get; init; }
    public long LastFiredTick { get; init; } = -1;
    /// <summary>holdFor 用:條件自何時連續成立(−1 = 未成立)</summary>
    public long SatisfiedSinceTick { get; init; } = -1;
}

/// <summary>delay_s 排入的待執行動作。</summary>
public sealed record PendingAction(string ItemId, int ActionIndex, long ExecuteTick, long FireCount);

public sealed record PhaseSpan(string Phase, long FromTick);
