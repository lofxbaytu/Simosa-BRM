using System.Buffers.Binary;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>
/// 時間表執行期(情境引擎設計第 3 節):每秒依 YAML 順序評估 armed 項目的條件(holdFor/repeat/cooldown 狀態機)、
/// 觸發者立刻執行動作或排入 delay_s、執行到期的待執行動作;手動觸發;事件計數(供 event 條件與 since);快照與雜湊。
/// 不使用引擎亂數;動作經引擎 <see cref="SimulationEngine.ExecuteTimelineAction"/> 執行(不寫 input 行)。
/// </summary>
public sealed class TimelineEngine
{
    /// <summary>單一評估步內觸發項目數上限(防止 always 互相觸發的無限迴圈)</summary>
    public const int MaxTriggersPerStep = 16;

    private readonly SimulationEngine _engine;
    private readonly Scenario _scenario;
    private readonly int _sps;
    private readonly List<TimelineItemState> _items = new();
    private readonly List<PendingAction> _pending = new();
    private readonly Dictionary<string, long> _eventCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastEventTick = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public IReadOnlyList<TimelineItemState> Items => _items;
    public IReadOnlyList<PendingAction> PendingActions => _pending;
    public IReadOnlyDictionary<string, long> EventCounts => _eventCounts;
    public int TotalFired => (int)_items.Sum(i => i.FireCount);

    public TimelineEngine(Scenario scenario, SimulationEngine engine, int stepsPerSecond)
    {
        _scenario = scenario;
        _engine = engine;
        _sps = stepsPerSecond;
        for (var i = 0; i < scenario.Timeline.Count; i++)
        {
            var item = scenario.Timeline[i];
            _items.Add(new TimelineItemState { Id = item.Id, Armed = item.Armed });
            _index[item.Id] = i;
        }
    }

    public TimelineItemState? StateOf(string id) => _index.TryGetValue(id, out var i) ? _items[i] : null;

    // ------------------------------------------------------------------ 事件觀察

    /// <summary>每個引擎事件(含交通、評估、教官)都經此記錄計數與最後時刻。</summary>
    public void OnEvent(SimEvent e)
    {
        foreach (var key in Keys(e))
        {
            _eventCounts[key] = _eventCounts.GetValueOrDefault(key) + 1;
            _lastEventTick[key] = e.Tick;
        }
    }

    private static IEnumerable<string> Keys(SimEvent e)
    {
        yield return e.Kind;
        if (e.ItemId is not null) yield return $"{e.Kind}|item:{e.ItemId}";
        if (e.TargetId is not null) yield return $"{e.Kind}|target:{e.TargetId}";
        if (e.ItemId is not null && e.TargetId is not null) yield return $"{e.Kind}|item:{e.ItemId}|target:{e.TargetId}";
        if (e.DataNumber("legIndex") is { } leg) yield return $"{e.Kind}|leg:{(long)leg}";
        if (e.DataString("waypoint") is { } wp) yield return $"{e.Kind}|wp:{wp}";
        if (e.DataString("kind") is { } k) yield return $"{e.Kind}|kind:{k}";
    }

    private static string Key(string kind, string? itemId = null, string? targetId = null, int? legIndex = null, string? waypoint = null)
    {
        if (itemId is not null && targetId is not null) return $"{kind}|item:{itemId}|target:{targetId}";
        if (itemId is not null) return $"{kind}|item:{itemId}";
        if (targetId is not null) return $"{kind}|target:{targetId}";
        if (legIndex is { } l) return $"{kind}|leg:{l}";
        if (waypoint is not null) return $"{kind}|wp:{waypoint}";
        return kind;
    }

    public long CountOf(string kind, string? itemId = null, string? targetId = null) => _eventCounts.GetValueOrDefault(Key(kind, itemId, targetId));

    public long? LastTickOf(SinceSpec s) => _lastEventTick.TryGetValue(Key(s.Event, s.ItemId, s.TargetId, s.LegIndex, s.Waypoint), out var t) ? t : null;

    // ------------------------------------------------------------------ 評估

    /// <summary>每秒評估(引擎在交通模組之後、評估規則之前呼叫)。</summary>
    public void Evaluate(long tick, Situation sit)
    {
        if (_engine.Ended) return;
        var triggered = 0;
        for (var i = 0; i < _scenario.Timeline.Count; i++)
        {
            if (_engine.Ended) return; // 本步內某項目的 endExercise 已結束練習:之後的項目與待執行動作不再觸發
            var item = _scenario.Timeline[i];
            var st = _items[i];
            if (!st.Armed || item.IsManual) continue;
            var ok = Evaluate(item.When!, sit);
            if (!ok)
            {
                if (st.SatisfiedSinceTick >= 0) _items[i] = st with { SatisfiedSinceTick = -1 };
                continue;
            }
            if (st.SatisfiedSinceTick < 0) { st = st with { SatisfiedSinceTick = tick }; _items[i] = st; }
            if (item.HoldForS > 0 && tick - st.SatisfiedSinceTick < (long)Math.Round(item.HoldForS * _sps)) continue;
            if (!RepeatAllows(item, st, tick)) continue;
            if (triggered >= MaxTriggersPerStep)
            {
                _engine.RaiseTimelineEvent(SimEventKinds.Message, item.Id, $"單一評估步觸發超過 {MaxTriggersPerStep} 項,停止評估(檢查 repeat/cooldown)",
                    SimEvent.DataOf(new { to = "instructor", text = $"單一評估步觸發超過 {MaxTriggersPerStep} 項", severity = "warning" }), false);
                break;
            }
            triggered++;
            Fire(i, tick, manual: false, forced: false);
        }
        if (!_engine.Ended) ExecutePending(tick);
    }

    private bool RepeatAllows(TimelineItem item, TimelineItemState st, long tick)
    {
        switch (item.Repeat.Mode)
        {
            case RepeatMode.Once:
                return st.FireCount == 0;
            case RepeatMode.Always:
                return st.LastFiredTick < 0 || tick - st.LastFiredTick >= (long)Math.Round(item.CooldownS * _sps);
            default:
                if (item.Repeat.Max is { } max && st.FireCount >= max) return false;
                var gap = Math.Max(item.Repeat.EveryS, item.CooldownS);
                return st.LastFiredTick < 0 || tick - st.LastFiredTick >= (long)Math.Round(gap * _sps);
        }
    }

    /// <summary>教官手動觸發:manual 項目(或 force)可觸發;once 已觸發者需 force;回傳是否成功(事件由引擎記錄)。</summary>
    public (bool Ok, string Reason) TriggerManual(string id, bool force, long tick)
    {
        if (!_index.TryGetValue(id, out var i)) return (false, $"時間表項目不存在:{id}");
        var item = _scenario.Timeline[i];
        var st = _items[i];
        if (!item.IsManual && !force) return (false, $"項目 {id} 非手動項目,需 force");
        if (item.Repeat.Mode == RepeatMode.Once && st.FireCount > 0 && !force) return (false, $"項目 {id} 已觸發過(once),需 force");
        if (item.Repeat.Mode == RepeatMode.Every && item.Repeat.Max is { } max && st.FireCount >= max && !force) return (false, $"項目 {id} 已達 max {max}");
        Fire(i, tick, manual: true, forced: force);
        ExecutePending(tick);
        return (true, "ok");
    }

    private void Fire(int index, long tick, bool manual, bool forced)
    {
        var item = _scenario.Timeline[index];
        var st = _items[index];
        st = st with { FireCount = st.FireCount + 1, LastFiredTick = tick };
        _items[index] = st;
        var visible = item.Visibility == TimelineVisibility.All;
        _engine.RaiseTimelineEvent(SimEventKinds.TimelineTriggered, item.Id, item.Name ?? item.Id,
            SimEvent.DataOf(new { fireCount = st.FireCount, manual, forced }), visible);
        if (item.Phase is { } phase) _engine.SetPhase(phase, SimEventSources.Timeline, item.Id);
        var delayTicks = (long)Math.Round(item.DelayS * _sps);
        if (delayTicks > 0)
        {
            for (var a = 0; a < item.Actions.Count; a++)
                _pending.Add(new PendingAction(item.Id, a, tick + delayTicks, st.FireCount));
            return;
        }
        for (var a = 0; a < item.Actions.Count; a++)
            _engine.ExecuteTimelineAction(item, item.Actions[a], a);
    }

    private void ExecutePending(long tick)
    {
        if (_pending.Count == 0) return;
        var due = _pending.Where(p => p.ExecuteTick <= tick).ToList();
        if (due.Count == 0) return;
        _pending.RemoveAll(p => p.ExecuteTick <= tick);
        foreach (var p in due)
        {
            if (!_index.TryGetValue(p.ItemId, out var i)) continue;
            var item = _scenario.Timeline[i];
            if (p.ActionIndex < item.Actions.Count) _engine.ExecuteTimelineAction(item, item.Actions[p.ActionIndex], p.ActionIndex);
        }
    }

    /// <summary>arm/disarm(動作);回傳是否改變。</summary>
    public bool SetArmed(string id, bool armed)
    {
        if (!_index.TryGetValue(id, out var i)) return false;
        var st = _items[i];
        if (st.Armed == armed) return false;
        _items[i] = st with { Armed = armed, SatisfiedSinceTick = -1 };
        return true;
    }

    // ------------------------------------------------------------------ 條件

    /// <summary>以目前狀態評估條件(遞迴);所有 leaf 以評估時刻的狀態判定。</summary>
    public bool Evaluate(TriggerCondition c, Situation sit)
    {
        if (c.All is not null) return c.All.Count > 0 && c.All.All(k => Evaluate(k, sit));
        if (c.Any is not null) return c.Any.Any(k => Evaluate(k, sit));
        if (c.Not is not null) return !Evaluate(c.Not, sit);
        var a = _engine.Assessment;
        switch (c.Type)
        {
            case "time":
            {
                var origin = 0L;
                if (c.Since is { } s)
                {
                    if (LastTickOf(s) is not { } t0) return false;
                    origin = t0;
                }
                return sit.Tick - origin >= (long)Math.Round((c.AtLeast ?? 0) * _sps);
            }
            case "ownDistance":
            {
                if (ResolveDistancePoint(c) is not var (px, py)) return false;
                var d = Math.Sqrt((sit.X - px) * (sit.X - px) + (sit.Y - py) * (sit.Y - py)) / Units.MetresPerNauticalMile;
                return (c.LessThan is not { } lt || d < lt) && (c.GreaterThan is not { } gt || d > gt);
            }
            case "ownHeading":
            {
                var h = sit.HeadingDeg;
                if (c.GreaterThan is { } from && c.LessThan is { } to) return Units.NormalizeHeadingDeg(h - from) <= Units.NormalizeHeadingDeg(to - from);
                if (c.GreaterThan is { } g) return h > g;
                if (c.LessThan is { } l) return h < l;
                return true;
            }
            case "ownSpeed":
            {
                var v = string.Equals(c.Metric, "stw", StringComparison.OrdinalIgnoreCase) ? sit.StwKn : sit.SogKn;
                return (c.LessThan is not { } lt || v < lt) && (c.GreaterThan is not { } gt || v > gt);
            }
            case "ukc":
            {
                var ok = true;
                if (c.LessThan is { } lt) ok &= sit.UkcM < lt;
                if (c.GreaterThan is { } gt) ok &= sit.UkcM > gt;
                if (c.LessThanFractionOfDraft is { } fr) ok &= sit.UkcM < fr * sit.MaxDraftM;
                return ok;
            }
            case "xtd":
            {
                if (a?.Route is not { } rt || rt.State.Xtd is not { } xtd) return false;
                var side = (c.Side ?? "any").ToLowerInvariant();
                if (side == "port" && xtd >= 0) return false;
                if (side == "starboard" && xtd <= 0) return false;
                return Math.Abs(xtd) > (c.GreaterThan ?? 0);
            }
            case "routeLeg":
            {
                if (a?.Route is not { } rt) return false;
                if (c.PassedWaypoint is { } pw) return rt.State.PassedWaypoints.Any(w => w.Name == pw);
                if (c.EqualsNumber is { } eq) return rt.State.LegIndex == (int)eq;
                return rt.State.LegIndex >= (int)(c.AtLeast ?? 0);
            }
            case "zone":
            {
                if (a?.Zones is not { } zt || c.Id is null) return false;
                return zt.IsInside(c.Id) == (c.Inside ?? true);
            }
            case "phase":
                return string.Equals(_engine.Phase, c.EqualsString, StringComparison.Ordinal);
            case "event":
            {
                if (c.Kind is null) return false;
                var n = CountOf(c.Kind, c.ItemId, c.TargetId);
                return n >= (c.Count?.AtLeast ?? 1);
            }
            case "fault":
            {
                var active = c.Name is not null && _engine.Faults.Contains(c.Name);
                return active == (c.Active ?? true);
            }
            case "telegraph":
            {
                var tg = sit.Telegraph.ToString();
                if (c.In is { } inList) return inList.Any(t => string.Equals(t, tg, StringComparison.OrdinalIgnoreCase));
                if (c.NotIn is { } notIn) return !notIn.Any(t => string.Equals(t, tg, StringComparison.OrdinalIgnoreCase));
                return true;
            }
            case "autopilot":
                return sit.AutopilotEnabled == (c.Enabled ?? true);
            default:
                return false; // manual 與未知型別永遠為假
        }
    }

    private (double X, double Y)? ResolveDistancePoint(TriggerCondition c)
    {
        var proj = _engine.Projection;
        if (c.Point is not null) return Scenario.Resolve(proj, c.Point);
        if (c.Waypoint is { } wn)
        {
            var wp = _scenario.Route?.Waypoints.FirstOrDefault(w => w.Name == wn);
            return wp is null ? null : Scenario.Resolve(proj, wp);
        }
        if (c.Target is { } tid)
        {
            var t = _engine.Traffic.Find(tid);
            return t is null || !t.Active ? null : (t.X, t.Y);
        }
        return null;
    }

    // ------------------------------------------------------------------ 快照 / 雜湊

    public TimelineSnapshot CreateSnapshot(string phase, long seq, long assessmentEvents, IReadOnlyList<EnvironmentRamp> ramps, IReadOnlyList<PhaseSpan> phaseHistory, bool ended) => new()
    {
        Phase = phase,
        Seq = seq,
        AssessmentEvents = assessmentEvents,
        Items = _items.ToArray(),
        PendingActions = _pending.ToArray(),
        Ramps = ramps.ToArray(),
        EventCounts = new Dictionary<string, long>(_eventCounts),
        LastEventTick = new Dictionary<string, long>(_lastEventTick),
        PhaseHistory = phaseHistory.ToArray(),
        Ended = ended,
    };

    public void Restore(TimelineSnapshot s)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var saved = s.Items.FirstOrDefault(x => x.Id == _items[i].Id);
            _items[i] = saved ?? new TimelineItemState { Id = _items[i].Id, Armed = _scenario.Timeline[i].Armed };
        }
        _pending.Clear();
        _pending.AddRange(s.PendingActions);
        _eventCounts.Clear();
        foreach (var (k, v) in s.EventCounts) _eventCounts[k] = v;
        _lastEventTick.Clear();
        foreach (var (k, v) in s.LastEventTick) _lastEventTick[k] = v;
    }

    /// <summary>每個項目進入雜湊的 int64 數。</summary>
    public const int HashLongsPerItem = 3;

    public int WriteHash(Span<byte> buf)
    {
        var o = 0;
        foreach (var st in _items)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], st.FireCount); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], st.LastFiredTick); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], st.SatisfiedSinceTick); o += 8;
        }
        return o;
    }
}
