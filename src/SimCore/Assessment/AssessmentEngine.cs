using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using SimosaBRM.SimCore.Traffic;

namespace SimosaBRM.SimCore.Assessment;

/// <summary>評估規則狀態(進快照)。</summary>
public sealed class RuleState
{
    public bool HardFailed { get; set; }
    public List<HardFailRecord> HardFails { get; set; } = new();
    public Dictionary<string, BandState> Bands { get; set; } = new();
    public Dictionary<string, RequiredState> Required { get; set; } = new();
    public bool Ended { get; set; }
}

public sealed class BandState
{
    /// <summary>ok | warn | fail</summary>
    public string State { get; set; } = "ok";
    public double Penalty { get; set; }
    public double? Value { get; set; }
}

public sealed class RequiredState
{
    /// <summary>notApplicable | pending | met | late | missed</summary>
    public string State { get; set; } = "notApplicable";
    public long StartTick { get; set; } = -1;
    public long MetTick { get; set; } = -1;
    public string? By { get; set; }
    public double Penalty { get; set; }
}

/// <summary>評估模組完整快照(進入 <see cref="EngineSnapshot.Assessment"/>;各追蹤器狀態經 JSON 深拷貝)。</summary>
public sealed record AssessmentSnapshot
{
    public RouteState? Route { get; init; }
    public required ZoneState Zones { get; init; }
    public required MetricsState Metrics { get; init; }
    public required EncounterState Encounters { get; init; }
    public required FaultResponseState Faults { get; init; }
    public required RuleState Rules { get; init; }
    /// <summary>操縱量測(情境未使用時 null;舊快照無此欄位時還原為初始狀態)</summary>
    public ManoeuvreState? Manoeuvres { get; init; }
    /// <summary>靠泊/錨泊指標(舊快照無此欄位時還原為初始狀態)</summary>
    public BerthingState? Berthing { get; init; }
}

/// <summary>
/// 評估引擎(第 4.7 節):持有各追蹤器,每秒依序更新(Route/Zone → Metrics → Encounter/FaultResponse),評估硬性失敗、容許帶、
/// 必要事件並產生不可變 <see cref="AssessmentSummary"/>(每秒更新 <see cref="LatestSummary"/>);endExercise/紀錄關閉時定稿。
/// </summary>
public sealed class AssessmentEngine
{
    private readonly SimulationEngine _engine;
    private readonly Scenario.Scenario _scenario;
    private readonly ScenarioAssessment _a;
    private readonly double _dt;
    private readonly double _maxDraft;
    private readonly ParameterSetInfo? _params;
    private readonly Action<string, string?, string?, string?, object?> _raise;

    public RouteTracker? Route { get; private set; }
    public ZoneTracker Zones { get; private set; }
    public MetricsCollector Metrics { get; private set; }
    public EncounterTracker Encounters { get; private set; }
    public FaultResponseTracker Faults { get; private set; }
    /// <summary>操縱量測(迴旋、Z 形、停船、側推、舵機擺動、人員落水;情境引用 manoeuvre 事件/指標或有 MOB 目標時啟用)</summary>
    public ManoeuvreTracker Manoeuvres { get; private set; }
    /// <summary>靠泊與錨泊指標(規劃書第 8.3 節第 4 類;情境有泊位/拖船/錨時輸出 summary.berthing / anchoring)</summary>
    public BerthingTracker Berthing { get; private set; }
    public RuleState Rules { get; private set; } = new();
    public AssessmentSummary? LatestSummary { get; private set; }
    public bool Ended => Rules.Ended;

    /// <param name="raise">(kind, itemId, targetId, message, data) → 引擎發事件(source = assessment)</param>
    /// <param name="targets">操縱量測的試俥目標值(null = 讀 data/ships/&lt;ID&gt;/trial_targets.json;重播時為紀錄標頭的目標值)</param>
    public AssessmentEngine(Scenario.Scenario scenario, ShipParticulars ship, SimulationEngine engine, LocalTangentPlane projection, double dt,
        Action<string, string?, string?, string?, object?> raise, ManoeuvreTargets? targets = null)
    {
        _engine = engine;
        _scenario = scenario;
        _a = scenario.Assessment;
        _dt = dt;
        _raise = raise;
        _params = scenario.ParameterSetInfo;
        _maxDraft = ship.GetLoading(scenario.Ship.Loading).MaxDraft_m;
        var loa = ship.Hull.LengthOverall_m > 0 ? ship.Hull.LengthOverall_m : ship.Hull.LengthBetweenPerpendiculars_m;
        Route = scenario.Route is { } r ? new RouteTracker(r, projection, loa) : null;
        Zones = new ZoneTracker(scenario.Zones, projection);
        // 滿舵角未給時取目前動力學模型的舵角上限(MMG:rudder.maxAngle_deg 70°;暫代模型:normalMaxAngle_deg 35°)
        Metrics = new MetricsCollector(_a.Manoeuvring?.HardOverDeg ?? engine.Actuators.Parameters.RudderMaxDeg, _a.Manoeuvring?.RotLimitDegPerMin, dt, engine.Telegraph);
        Encounters = new EncounterTracker(_a.Encounter, _a.MinCpaNm);
        Faults = new FaultResponseTracker(_a, dt);
        Manoeuvres = new ManoeuvreTracker(engine, ship, scenario.Ship.Loading, dt, ManoeuvreTracker.IsReferenced(scenario), raise, targets);
        Berthing = new BerthingTracker(engine, scenario, loa);
        foreach (var b in _a.Bands) Rules.Bands[b.Id] = new BandState();
        foreach (var q in _a.Required) Rules.Required[q.Id] = new RequiredState();
    }

    private void Raise(string kind, string? targetId, object? data) => _raise(kind, null, targetId, null, data);

    // ------------------------------------------------------------------ 觀察

    public void OnEvent(SimEvent e)
    {
        Manoeuvres.OnEvent(e);
        Berthing.OnEvent(e);
        Faults.OnEvent(e);
        if (e.Kind == TrafficEventKinds.Collision) Encounters.OnCollision();
        if (e.Kind == SimEventKinds.Grounding) Metrics.OnGrounding();
        if (e.Kind == SimEventKinds.Frozen) Metrics.OnFreeze();
        Metrics.OnAlertEvent(e);
        if (Rules.Ended) return;
        // 必要事件:after 起算(發 requiredEventPending,附已解析的 within_s 與期限 deadline_t,供教官站倒數)、expect 達成
        foreach (var rule in _a.Required)
        {
            var st = Rules.Required[rule.Id];
            if (st.State == "notApplicable" && MatchesAfter(rule.After, e))
            {
                st.State = "pending";
                st.StartTick = e.Tick;
                _raise(SimEventKinds.RequiredEventPending, rule.Id, null, null, new
                {
                    after = e.Kind, afterItemId = e.ItemId, afterSeq = e.Seq, start_t = e.T, within_s = rule.WithinS, deadline_t = e.T + rule.WithinS, penalty = rule.Penalty,
                });
                // includePrior:該會遇的首次行動已在開始計時前發生(每筆會遇只發一次 encounterFirstAction)→ 當下以 dt = 0 判 met
                if (PriorFirstAction(rule.Expect) is { } prior) Meet(rule, st, e.Tick, prior.By, prior.T);
                continue;
            }
            if (st.State is "pending" or "missed" && st.MetTick < 0 && MatchesExpect(rule.Expect, e, null) is { } by)
                Meet(rule, st, e.Tick, by);
        }
    }

    public void OnAction(ObservedAction a)
    {
        Manoeuvres.OnAction(a);
        Berthing.OnAction(a, a.Tick * _dt);
        Metrics.OnAction(a);
        Encounters.OnAction(a, a.Tick * _dt, Raise);
        Faults.OnAction(a);
        if (Rules.Ended) return;
        foreach (var rule in _a.Required)
        {
            var st = Rules.Required[rule.Id];
            if (st.State is "pending" or "missed" && st.MetTick < 0 && MatchesExpect(rule.Expect, null, a) is { } by)
                Meet(rule, st, a.Tick, by);
        }
    }

    /// <summary>
    /// expect 中標 <c>includePrior: true</c> 的 <c>{event: {kind: encounterFirstAction, targetId}}</c>:該目標最新一筆會遇仍進行中且已有首次行動時,
    /// 回傳首次行動時刻(s)與達成方式。已結束的會遇(首次行動屬於前一次會遇)不計;未指定 targetId 時不適用(ScenarioValidator 警告)。
    /// </summary>
    private (double T, string By)? PriorFirstAction(ExpectSpec spec)
    {
        foreach (var leaf in spec.Leaves())
        {
            if (leaf.Event is not { IncludePrior: true, TargetId: { } tid } ev || ev.Kind != SimEventKinds.EncounterFirstAction) continue;
            var rec = Encounters.State.Records.LastOrDefault(r => string.Equals(r.TargetId, tid, StringComparison.OrdinalIgnoreCase));
            if (rec is { Closed: false, FirstActionAtS: { } t }) return (t, $"event:{ev.Kind}");
        }
        return null;
    }

    private void Meet(RequiredEvent rule, RequiredState st, long tick, string by, double? priorT = null)
    {
        st.MetTick = tick;
        st.By = by;
        var dt = (tick - st.StartTick) * _dt;
        // 逾時判定以 dt > within_s 為準(與 FaultResponseTracker 的 withinFirstAction = dt ≤ within_s 一致),不只看整秒評估是否已判 missed:
        // after 事件落在非整秒 tick(教官 injectFault)時,期限與下一整秒之間送達的動作仍為 late,並先補發 requiredEventMissed 使事件序列與整秒路徑相同
        if (st.State == "pending" && dt > rule.WithinS) MarkMissed(rule, st);
        if (st.State == "missed")
        {
            st.State = "late";
            var overdue = dt - rule.WithinS;
            st.Penalty = rule.LatePenaltyPerS is { } per ? Math.Min(rule.Penalty, per * overdue) : rule.Penalty;
        }
        else
        {
            st.State = "met";
            st.Penalty = 0;
        }
        if (priorT is { } pt)
            _raise(SimEventKinds.RequiredEventMet, rule.Id, null, null, new { dt_s = dt, within_s = rule.WithinS, by, late = st.State == "late", penalty = st.Penalty, prior_t = pt });
        else
            _raise(SimEventKinds.RequiredEventMet, rule.Id, null, null, new { dt_s = dt, within_s = rule.WithinS, by, late = st.State == "late", penalty = st.Penalty });
    }

    private void MarkMissed(RequiredEvent rule, RequiredState st)
    {
        st.State = "missed";
        st.Penalty = rule.Penalty;
        _raise(SimEventKinds.RequiredEventMissed, rule.Id, null, null, new { within_s = rule.WithinS, penalty = rule.Penalty });
    }

    private static bool MatchesAfter(AfterSpec after, SimEvent e)
    {
        if (e.Kind != after.Event) return false;
        if (after.ItemId is not null && e.ItemId != after.ItemId) return false;
        if (after.TargetId is not null && !string.Equals(e.TargetId, after.TargetId, StringComparison.OrdinalIgnoreCase)) return false;
        if (after.Waypoint is not null && e.DataString("waypoint") != after.Waypoint) return false;
        if (after.LegIndex is { } leg && e.DataNumber("legIndex") is var l && (l is null || (int)l.Value != leg)) return false;
        if (after.ZoneId is not null && e.DataString("zoneId") != after.ZoneId) return false;
        return true;
    }

    /// <summary>expect 是否由事件或指令觀察達成;回傳達成方式名稱。</summary>
    private static string? MatchesExpect(ExpectSpec spec, SimEvent? e, ObservedAction? a)
    {
        if (spec.Any is not null)
        {
            foreach (var s in spec.Any)
                if (MatchesExpect(s, e, a) is { } by) return by;
            return null;
        }
        if (spec.Name is { } name)
        {
            if (a is not null)
            {
                if (name == a.Kind) return name;
                if (name == ActionKinds.SteeringModeChange && a.Kind == ActionKinds.AutopilotOff) return name;
                return null;
            }
            if (e is not null && name == "acknowledge" && e.Kind == SimEventKinds.Acknowledge) return $"acknowledge:{e.DataString("kind")}";
            if (e is not null && name == ActionKinds.SteeringModeChange && e.Kind == SimEventKinds.Acknowledge && e.DataString("kind") == "steeringMode") return "acknowledge:steeringMode";
            return null;
        }
        if (e is null) return null;
        if (spec.Acknowledge is { } ack)
        {
            if (e.Kind != SimEventKinds.Acknowledge) return null;
            var kind = e.DataString("kind");
            return ack.Kind is null || ack.Kind == kind ? $"acknowledge:{kind}" : null;
        }
        if (spec.Event is { } ev)
        {
            if (e.Kind != ev.Kind) return null;
            if (ev.ItemId is not null && e.ItemId != ev.ItemId) return null;
            if (ev.TargetId is not null && !string.Equals(e.TargetId, ev.TargetId, StringComparison.OrdinalIgnoreCase)) return null;
            return $"event:{ev.Kind}";
        }
        return null;
    }

    // ------------------------------------------------------------------ 每秒評估

    /// <summary>目前有效的 UKC 門檻(區域優先)。</summary>
    public (double LimitM, string Source)? UkcLimit()
    {
        if (Zones.UkcLimit(_maxDraft) is { } z)
        {
            var idx = _scenario.Zones.FindIndex(x => x.Id == z.Id);
            var zone = _scenario.Zones[idx];
            var key = _params?.RefAt(zone.UkcMin?.FractionOfDraft is not null ? $"zones[{idx}].ukcMin.fractionOfDraft" : $"zones[{idx}].ukcMin.metres")?.Key;
            return (z.LimitM, key ?? $"zones.{z.Id}");
        }
        if (_a.Ukc?.Min is { } g)
        {
            var key = _params?.RefAt(g.FractionOfDraft is not null ? "assessment.ukc.min.fractionOfDraft" : "assessment.ukc.min.metres")?.Key;
            return (g.ToMetres(_maxDraft), key ?? "assessment.ukc.min");
        }
        return null;
    }

    /// <summary>目前有效的速限(航段與區域取較嚴者)。</summary>
    public (double Limit, string Source)? SpeedLimit()
    {
        (double, string)? best = null;
        if (Route is { } r && !r.State.Complete && r.SpeedLimit(r.State.LegIndex) is { } legLimit)
            best = (legLimit, $"leg:{r.NextWaypointName}");
        if (Zones.SpeedLimit() is { } z && (best is null || z.Limit < best.Value.Item1)) best = (z.Limit, $"zone:{z.Id}");
        return best;
    }

    /// <summary>評估步(Route/Zone → Metrics → Encounter/FaultResponse);時間表在此之後、規則評估在 <see cref="EvaluateRules"/>。</summary>
    public void Update(Situation sit)
    {
        Route?.Update(sit, Raise);
        Zones.Update(sit, Raise);
        Metrics.Sample(sit, SpeedLimit(), UkcLimit(), Raise);
        Encounters.Update(sit, _engine.Traffic, Raise);
        Manoeuvres.Update(sit);
        Berthing.Update(sit);
    }

    /// <summary>硬性失敗、容許帶、必要事件逾時;更新 LatestSummary。定稿後(endExercise)不再評估也不重建 summary(Finalize 的物件即唯一的 final summary)。</summary>
    public void EvaluateRules(Situation sit)
    {
        if (Rules.Ended) return;
        foreach (var rule in _a.HardFail)
        {
            if (Rules.HardFails.Any(h => h.Id == rule.Id)) continue;
            var (hit, value, limit) = CheckHardFail(rule, sit);
            if (!hit) continue;
            Rules.HardFailed = true;
            var idx = _a.HardFail.IndexOf(rule);
            var key = _params?.Resolved.FirstOrDefault(p => p.Path.StartsWith($"assessment.hardFail[{idx}].", StringComparison.Ordinal))?.Key;
            Rules.HardFails.Add(new HardFailRecord { Id = rule.Id, Rule = rule.Rule, TS = sit.Time, Value = value, Limit = limit, ParamKey = key });
            _raise(SimEventKinds.HardFail, rule.Id, null, $"硬性失敗:{rule.Rule}", new { rule = rule.Rule, value, limit, paramKey = key });
        }
        foreach (var band in _a.Bands)
        {
            var st = Rules.Bands[band.Id];
            var value = MetricValue(band.Metric, sit);
            st.Value = value;
            if (value is not { } v) continue;
            var failV = band.FailValue;
            var warnV = band.Warn;
            var isFail = failV is { } f && (band.Below ? v < f : v > f);
            var isWarn = !isFail && warnV is { } w && (band.Below ? v < w : v > w);
            if (isFail && st.State != "fail")
            {
                st.State = "fail";
                st.Penalty = band.FailPenalty ?? band.Penalty;
                _raise(SimEventKinds.BandFail, band.Id, null, $"{band.Metric} = {v:G6}", new { metric = band.Metric, value = v, fail = failV, penalty = st.Penalty });
            }
            else if (isWarn && st.State == "ok")
            {
                st.State = "warn";
                st.Penalty = band.Penalty;
                _raise(SimEventKinds.BandWarn, band.Id, null, $"{band.Metric} = {v:G6}", new { metric = band.Metric, value = v, warn = warnV, penalty = st.Penalty });
            }
        }
        foreach (var rule in _a.Required)
        {
            var st = Rules.Required[rule.Id];
            if (st.State != "pending") continue;
            if ((sit.Tick - st.StartTick) * _dt > rule.WithinS) MarkMissed(rule, st);
        }
        LatestSummary = BuildSummary(sit.Tick, final: false);
    }

    private (bool Hit, double? Value, double? Limit) CheckHardFail(HardFailRule rule, Situation sit)
    {
        switch (rule.Rule)
        {
            case "grounding": return (_engine.Aground, null, null);
            case "collision": return (_engine.Collision, null, null);
            case "cpa":
            {
                var limit = rule.LessThanNumber ?? 0;
                double? worst = null;
                foreach (var r in Encounters.State.Records)
                {
                    if (rule.ExcludeEscorts && r.Escort) continue;
                    if (r.MinRangeNm is { } m && (worst is null || m < worst)) worst = m;
                }
                return (worst is { } w && w < limit, worst, limit);
            }
            case "ukc":
            {
                var limit = rule.LessThanUkc?.ToMetres(_maxDraft) ?? 0;
                return (sit.UkcM < limit, sit.UkcM, limit);
            }
            case "zoneEntered":
                return (rule.ZoneId is not null && Zones.IsInside(rule.ZoneId), null, null);
            case "event":
                return (rule.Kind is not null && _engine.Timeline?.CountOf(rule.Kind) > 0, null, null);
            default:
                return (false, null, null);
        }
    }

    /// <summary>容許帶指標的目前值(名稱見 <see cref="MetricNames"/>)。</summary>
    public double? MetricValue(string name, Situation sit)
    {
        var m = Metrics.State;
        var r = Route?.State;
        switch (name)
        {
            case "track.meanAbsXtd_m": return r is { Samples: > 0 } ? r.SumAbsXtd / r.Samples : null;
            case "track.maxXtd_m": return r?.MaxAbsXtd;
            case "track.timeOutsideCorridor_s": return r?.TimeOutsideS;
            case "track.corridorExcursions": return r?.Excursions;
            case "track.fractionOutsideCorridor": return r is { Samples: > 0 } ? (double)r.TimeOutsideS / r.Samples : null;
            case "track.minDistanceToCorridorEdge_m": return r?.MinEdgeDistance;
            case "track.minDistanceToNoGo_m": return Zones.State.MinDistanceToNoGo;
            case "track.meanHeadingDeviation_deg": return r is { HeadingDevSamples: > 0 } ? r.SumHeadingDev / r.HeadingDevSamples : null;
            case "ukc.min_m": return m.MinUkcM;
            case "ukc.timeBelowLimit_s": return m.TimeBelowUkcLimitS;
            case "ukc.excursions": return m.UkcExcursions;
            case "ukc.groundings": return m.Groundings;
            case "collisionAvoidance.minCpa_nm": return Encounters.MinRangeNonEscort();
            case "collisionAvoidance.encounters": return Encounters.State.Records.Count(x => !x.Escort);
            case "collisionAvoidance.collisions": return Encounters.State.Collisions;
            case "manoeuvring.hardOverCount": return m.HardOverCount;
            case "manoeuvring.hardOverTime_s": return m.HardOverTimeS;
            case "manoeuvring.rudderOrders": return m.RudderOrders;
            case "manoeuvring.maxRot_degPerMin": return m.MaxRotDegPerMin;
            case "manoeuvring.timeRotOverLimit_s": return m.TimeRotOverLimitS;
            case "manoeuvring.rotExcursions": return m.RotExcursions;
            case "manoeuvring.telegraphChanges": return m.TelegraphChanges;
            case "manoeuvring.rpmOrders": return m.RpmOrders;
            case "manoeuvring.thrusterOrders": return m.ThrusterOrders;
            case "manoeuvring.thrusterTime_s": return m.ThrusterTimeS;
            case "manoeuvring.timeOverSpeedLimit_s": return m.TimeOverSpeedLimitS;
            case "manoeuvring.maxOverSpeed_kn": return m.MaxOverSpeedKn;
            case "manoeuvring.autopilotTime_s": return m.AutopilotTimeS;
            case "manoeuvring.autopilotSwitches": return m.AutopilotSwitches;
            case "alerts.meanAckTime_s": return Metrics.AlertSummary().MeanAckTimeS;
            case "alerts.maxAckTime_s": return Metrics.AlertSummary().MaxAckTimeS;
            case "alerts.unacknowledged": return Metrics.AlertSummary().Unacknowledged;
            case "alerts.escalations": return m.AlertEscalations;
            case "bnwas.stage2Count": return m.BnwasStage2;
            case "bnwas.stage3Count": return m.BnwasStage3;
            case "general.duration_s": return sit.Time;
            case "general.distance_nm": return m.DistanceM / Units.MetresPerNauticalMile;
            case "general.avgSog_kn": return m.Samples > 0 ? m.SumSogKn / m.Samples : null;
            case "general.maxSog_kn": return m.MaxSogKn;
            default:
                if (name.StartsWith("berthing.", StringComparison.Ordinal) || name.StartsWith("anchoring.", StringComparison.Ordinal)) return Berthing.Metric(name, sit?.Time ?? _engine.Time);
                return name.StartsWith("manoeuvres.", StringComparison.Ordinal) ? Manoeuvres.Metric(name) : null;
        }
    }

    // ------------------------------------------------------------------ 定稿與摘要

    /// <summary>定稿(endExercise / 紀錄關閉):之後規則不再變,summary.final = true。</summary>
    public AssessmentSummary Finalize(long tick)
    {
        Rules.Ended = true;
        LatestSummary = BuildSummary(tick, final: true);
        return LatestSummary;
    }

    public AssessmentSummary BuildSummary(long tick, bool final)
    {
        var time = tick * _dt;
        var scoring = _a.Scoring ?? new ScoringSpec();
        var bands = new List<BandResult>();
        double penalties = 0;
        for (var i = 0; i < _a.Bands.Count; i++)
        {
            var b = _a.Bands[i];
            var st = Rules.Bands[b.Id];
            penalties += st.Penalty;
            var pr = _params?.RefAt($"assessment.bands[{i}].warn") ?? _params?.RefAt($"assessment.bands[{i}].fail");
            bands.Add(new BandResult
            {
                Id = b.Id, Metric = b.Metric, Value = st.Value, Warn = b.Warn, Fail = b.FailValue, State = st.State, Penalty = st.Penalty,
                ParamKey = pr?.Key, Source = pr?.Source, Revision = pr?.Revision, Clause = pr?.Clause,
            });
        }
        var required = new List<RequiredResult>();
        for (var i = 0; i < _a.Required.Count; i++)
        {
            var q = _a.Required[i];
            var st = Rules.Required[q.Id];
            penalties += st.Penalty;
            var pr = _params?.RefAt($"assessment.required[{i}].within_s");
            required.Add(new RequiredResult
            {
                Id = q.Id, State = st.State, DtS = st.MetTick >= 0 ? (st.MetTick - st.StartTick) * _dt : null, WithinS = q.WithinS, By = st.By, Penalty = st.Penalty,
                ParamKey = pr?.Key, Source = pr?.Source, Revision = pr?.Revision, Clause = pr?.Clause,
            });
        }
        var score = Math.Max(0.0, scoring.Start - penalties);
        var parameters = (_params?.Resolved ?? Array.Empty<ParameterRef>())
            .GroupBy(p => p.Key).Select(g => g.First())
            .Select(p => new SummaryParameter { Key = p.Key, Value = p.Value, Unit = p.Unit, Source = p.Source, Revision = p.Revision, Clause = p.Clause })
            .ToArray();
        var counts = _engine.Timeline?.EventCounts ?? new Dictionary<string, long>();
        var byKind = counts.Where(kv => !kv.Key.Contains('|')).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
        return new AssessmentSummary
        {
            Final = final,
            ScenarioId = _scenario.Id,
            ShipId = _scenario.Ship.Id,
            Loading = _scenario.Ship.Loading,
            ParameterSet = _params is null ? null : new SummaryParameterSet { Id = _params.Id, Version = _params.Version, Sha256 = _params.Sha256 },
            Phases = _engine.PhaseSpans(time),
            General = Metrics.General(time),
            CollisionAvoidance = Encounters.Summary(),
            Track = Route?.Summary(_dt, Zones.State.MinDistanceToNoGo),
            Ukc = Metrics.Ukc(),
            Manoeuvring = Metrics.Manoeuvring(tick),
            FaultResponses = Faults.State.Records.ToArray(),
            Alerts = Metrics.AlertSummary(),
            Manoeuvres = Manoeuvres.Enabled ? Clone(Manoeuvres.Summary().ToList()) : null,
            Berthing = Berthing.BerthingSummary() is { } bs ? Clone(bs) : null,
            Anchoring = Berthing.AnchoringSummary(time) is { } an ? Clone(an) : null,
            Events = new SummaryEvents { Total = byKind.Values.Sum(), ByKind = byKind },
            Assessment = new SummaryAssessment
            {
                Score = score, PassMark = scoring.PassMark, Passed = !Rules.HardFailed && score >= scoring.PassMark, HardFailed = Rules.HardFailed,
                HardFails = Rules.HardFails.ToArray(), Bands = bands, Required = required, Parameters = parameters,
            },
        };
    }

    // ------------------------------------------------------------------ 快照

    private static T Clone<T>(T value) => ContractJson.Deserialize<T>(ContractJson.Serialize(value))!;

    public AssessmentSnapshot CreateSnapshot() => new()
    {
        Route = Route is null ? null : Clone(Route.State),
        Zones = Clone(Zones.State),
        Metrics = Clone(Metrics.State),
        Encounters = Clone(Encounters.State),
        Faults = Clone(Faults.State),
        Rules = Clone(Rules),
        Manoeuvres = Manoeuvres.Enabled ? Clone(Manoeuvres.State) : null,
        Berthing = Berthing.BerthingEnabled || Berthing.AnchoringEnabled ? Clone(Berthing.State) : null,
    };

    public void Restore(AssessmentSnapshot s)
    {
        if (Route is not null && s.Route is not null) Route.Restore(Clone(s.Route));
        Zones.Restore(Clone(s.Zones));
        Metrics.Restore(Clone(s.Metrics));
        Encounters.Restore(Clone(s.Encounters));
        Faults.Restore(Clone(s.Faults));
        Rules = Clone(s.Rules);
        if (Manoeuvres.Enabled) Manoeuvres.Restore(s.Manoeuvres is { } ms ? Clone(ms) : new ManoeuvreState());
        Berthing.Restore(s.Berthing is { } bst ? Clone(bst) : new BerthingState());
        foreach (var b in _a.Bands) Rules.Bands.TryAdd(b.Id, new BandState());
        foreach (var q in _a.Required) Rules.Required.TryAdd(q.Id, new RequiredState());
    }
}
