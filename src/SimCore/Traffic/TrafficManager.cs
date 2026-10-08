using System.Buffers.Binary;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Traffic;

/// <summary>
/// 交通模組(規劃書第 5.2 節):目標船集合、每步確定性更新(與自船同 dt;自船先推進,目標再依自船新狀態更新)、
/// 觸發條件、行為模式、COLREG 覆蓋層、自船對各目標的 CPA/TCPA/BCR、碰撞偵測(船體外形多邊形相交)與事件。
/// 不使用引擎亂數(錨泊微漂為確定性迴盪),故不影響自船的陣風亂數序列。
/// </summary>
public sealed class TrafficManager
{
    /// <summary>臺灣 MID 416 起的預設 MMSI</summary>
    public const int DefaultMmsiBase = 416000001;
    /// <summary>COLREG 判定與觸發條件的評估間隔(步)</summary>
    private readonly int _evalDivider;
    private readonly double _dt;
    private readonly List<TargetShip> _targets = new();
    private LocalTangentPlane _projection;
    private int _nextDefaultMmsi = DefaultMmsiBase;

    public IReadOnlyList<TargetShip> Targets => _targets;
    /// <summary>自船與任一目標外形曾相交(與擱淺旗標相同,保持為真)</summary>
    public bool Collision { get; private set; }
    /// <summary>最小 CPA 門檻(nm;情境 assessment.minCpa_nm)</summary>
    public double MinCpaThresholdNm { get; set; } = 0.5;
    /// <summary>聲號在狀態中維持的秒數</summary>
    public double SoundDurationS { get; set; } = 4.0;

    public event Action<TrafficEvent>? EventRaised;

    public TrafficManager(LocalTangentPlane projection, double dt)
    {
        _projection = projection;
        _dt = dt;
        _evalDivider = Math.Max(1, (int)Math.Round(1.0 / dt));
    }

    /// <summary>依情境建立目標船(tick 0;無觸發條件者立即出現)。</summary>
    public static TrafficManager FromScenario(Scenario.Scenario scenario, LocalTangentPlane projection, double dt)
    {
        var tm = new TrafficManager(projection, dt) { MinCpaThresholdNm = scenario.Assessment.MinCpaNm };
        foreach (var spec in scenario.Targets) tm.AddTarget(spec, tick: 0, raiseEvent: false);
        return tm;
    }

    public TargetShip? Find(string id)
        => _targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>新增目標(情境或指令);無觸發條件者立即出現。</summary>
    public TargetShip AddTarget(ScenarioTarget spec, long tick, bool raiseEvent = true)
    {
        Scenario.Scenario.ValidateTarget(spec);
        if (Find(spec.Id) is not null) throw new InvalidDataException($"目標船 id 重複:{spec.Id}");
        var t = new TargetShip(spec, spec.Mmsi ?? _nextDefaultMmsi++);
        var (x, y) = Resolve(spec.Initial.Position!);
        t.SetInitialState(x, y);
        _targets.Add(t);
        if (spec.Trigger is null || spec.Trigger.Type == TargetTriggerType.None) Activate(t, tick, raiseEvent ? "新增" : "情境開始", raiseEvent);
        if (raiseEvent) Raise(TrafficEventKinds.Control, tick, t.Id, "新增目標");
        return t;
    }

    public bool RemoveTarget(string id, long tick)
    {
        var t = Find(id);
        if (t is null) return false;
        _targets.Remove(t);
        Raise(TrafficEventKinds.Control, tick, id, "刪除目標");
        return true;
    }

    private (double X, double Y) Resolve(ScenarioPosition p)
        => p.X is not null && p.Y is not null ? (p.X.Value, p.Y.Value) : _projection.ToLocal(p.Lat!.Value, p.Lon!.Value);

    private (double X, double Y) Resolve(TargetWaypoint w)
        => w.X is not null && w.Y is not null ? (w.X.Value, w.Y.Value) : _projection.ToLocal(w.Lat!.Value, w.Lon!.Value);

    private void Activate(TargetShip t, long tick, string why, bool raiseEvent = true)
    {
        t.Active = true;
        t.ActivatedTick = tick;
        t.AnchorX = t.X;
        t.AnchorY = t.Y;
        t.AnchorBaseRad = t.Psi;
        if (raiseEvent) Raise(TrafficEventKinds.Activated, tick, t.Id, why);
    }

    private void Raise(string kind, long tick, string? targetId, string? message, EncounterGeometry? g = null)
    {
        EventRaised?.Invoke(new TrafficEvent
        {
            Kind = kind,
            Tick = tick,
            T = tick * _dt,
            TargetId = targetId,
            Message = message,
            RangeNm = g?.RangeNm,
            CpaNm = g?.CpaNm,
            TcpaMin = g?.TcpaMin,
        });
    }

    // ------------------------------------------------------------------ 每步更新

    /// <summary>
    /// 推進所有目標一步(自船已推進到 <paramref name="tick"/>):觸發檢查 → 行為 → 運動模型 → 自船會遇指標 → 碰撞。
    /// <paramref name="env"/> 提供均勻流(目標對地運動含流)與風(錨泊艏向)。
    /// </summary>
    public void Step(in ShipKinematics own, in EnvironmentSample env, long tick)
    {
        var evalTick = tick % _evalDivider == 0;
        for (var i = 0; i < _targets.Count; i++)
        {
            var t = _targets[i];
            if (!t.Active)
            {
                if (evalTick && TriggerSatisfied(t, own, tick)) Activate(t, tick, "觸發條件成立");
                continue;
            }
            UpdateTarget(t, own, env, tick, evalTick);
        }
        UpdateOwnShipMetrics(own, tick, evalTick);
    }

    private bool TriggerSatisfied(TargetShip t, in ShipKinematics own, long tick)
    {
        var tr = t.Spec.Trigger;
        if (tr is null) return true;
        switch (tr.Type)
        {
            case TargetTriggerType.None:
                return true;
            case TargetTriggerType.Time:
                return tick * _dt >= (tr.Time ?? 0.0);
            case TargetTriggerType.OwnDistance:
            {
                var (px, py) = Resolve(tr.Point!);
                var d = Math.Sqrt((own.X - px) * (own.X - px) + (own.Y - py) * (own.Y - py)) / Units.MetresPerNauticalMile;
                var ok = true;
                if (tr.LessThan is { } lt) ok &= d < lt;
                if (tr.GreaterThan is { } gt) ok &= d > gt;
                return ok;
            }
            case TargetTriggerType.OwnHeading:
            {
                var h = Units.NormalizeHeadingDeg(Units.RadToDeg(own.HeadingRad));
                if (tr.GreaterThan is { } from && tr.LessThan is { } to)
                    return Units.NormalizeHeadingDeg(h - from) <= Units.NormalizeHeadingDeg(to - from); // 順時針扇區
                if (tr.GreaterThan is { } g) return h > g;
                if (tr.LessThan is { } l) return h < l;
                return true;
            }
            default:
                return true;
        }
    }

    private void UpdateTarget(TargetShip t, in ShipKinematics own, in EnvironmentSample env, long tick, bool evalTick)
    {
        var b = t.Spec.Behaviour;
        var tSince = t.TimeSinceActivation(tick, _dt);

        if (b.Mode == TargetBehaviourMode.Anchored && !t.Manual)
        {
            // 風流合成漂移向量(風致漂移取 3 % 風速);船艏朝其來向
            var driftE = 0.03 * env.WindEastMps + env.CurrentEastMps;
            var driftN = 0.03 * env.WindNorthMps + env.CurrentNorthMps;
            var headTo = driftE * driftE + driftN * driftN > 1e-4 ? Units.NormalizeHeadingRad(Math.Atan2(-driftE, -driftN)) : t.AnchorBaseRad;
            t.IntegrateAnchored(headTo, tSince, _dt);
            TickSound(t, tick);
            return;
        }

        // 1. 基底行為 → HeadingCmd / SpeedCmd
        switch (b.Mode)
        {
            case TargetBehaviourMode.Waypoints:
            case TargetBehaviourMode.Colreg when b.Waypoints is { Count: > 0 }:
                FollowWaypoints(t, tick);
                break;
            case TargetBehaviourMode.Scripted:
                RunScript(t, tSince, tick);
                break;
            case TargetBehaviourMode.Follow:
                FollowLeader(t, own, env);
                break;
            default:
                break; // hold / colreg(無航點):保持指令
        }

        // 2. COLREG 覆蓋層(每秒評估;行動中持續覆寫)
        var heading = t.HeadingCmdRad;
        var speed = t.SpeedCmdMps;
        if (t.ColregEnabled && !t.Manual)
        {
            if (evalTick) EvaluateColreg(t, own, tick);
            if (t.ColregPhase is ColregPhase.GiveWay or ColregPhase.StandOn or ColregPhase.LastResort)
            {
                heading = t.ColregHeadingRad;
                speed = t.ColregSpeedMps;
            }
        }

        // 3. 教官覆寫
        if (t.Manual)
        {
            heading = t.ManualHeadingRad;
            speed = t.ManualSpeedMps;
        }

        t.Integrate(heading, speed, env.CurrentEastMps, env.CurrentNorthMps, _dt);
        TickSound(t, tick);
    }

    private void TickSound(TargetShip t, long tick)
    {
        if (t.Sound is not null && tick >= t.SoundUntilTick) { t.Sound = null; t.SoundUntilTick = -1; }
    }

    // ------------------------------------------------------------------ 行為

    private void FollowWaypoints(TargetShip t, long tick)
    {
        var b = t.Spec.Behaviour;
        var wps = b.Waypoints!;
        if (t.WaypointIndex >= wps.Count) return; // 已到終點:保持(或已停船)
        var w = wps[t.WaypointIndex];
        var (wx, wy) = Resolve(w);
        var dx = wx - t.X;
        var dy = wy - t.Y;
        var d = Math.Sqrt(dx * dx + dy * dy);
        var radius = w.Radius ?? Math.Max(100.0, 2.0 * t.Spec.Loa);
        if (d < radius)
        {
            Raise(TrafficEventKinds.Waypoint, tick, t.Id, $"到達航點 {t.WaypointIndex + 1}/{wps.Count}");
            t.WaypointIndex++;
            if (t.WaypointIndex >= wps.Count)
            {
                if (b.Loop) t.WaypointIndex = 0;
                else if (b.StopAtEnd) { t.SpeedCmdMps = 0.0; return; }
                else return;
            }
            w = wps[t.WaypointIndex];
            (wx, wy) = Resolve(w);
            dx = wx - t.X;
            dy = wy - t.Y;
        }
        t.HeadingCmdRad = Units.NormalizeHeadingRad(Math.Atan2(dx, dy));
        if (w.Speed is { } kn) t.SpeedCmdMps = Units.KnToMps(kn);
    }

    private void RunScript(TargetShip t, double tSince, long tick)
    {
        var script = t.Spec.Behaviour.Script;
        if (script is null) return;
        while (t.ScriptIndex < script.Count && tSince >= script[t.ScriptIndex].T)
        {
            var s = script[t.ScriptIndex];
            if (s.Heading is { } h) t.HeadingCmdRad = Units.NormalizeHeadingRad(Units.DegToRad(h));
            if (s.Speed is { } v) t.SpeedCmdMps = Units.KnToMps(v);
            t.RotLimitRadps = s.Rot is { } rot ? Math.Min(t.MaxRotRadps, Units.DegPerMinToRadPerSec(Math.Abs(rot))) : t.MaxRotRadps;
            Raise(TrafficEventKinds.Script, tick, t.Id, $"腳本步 {t.ScriptIndex + 1}:航向 {s.Heading?.ToString("F0") ?? "-"}、航速 {s.Speed?.ToString("F1") ?? "-"} kn");
            t.ScriptIndex++;
        }
    }

    /// <summary>
    /// 跟隨:目標點 P = leader 位置 + 相對方位/距離;所需對地速度 = leader 對地速度 + gain·(P − 位置),
    /// 減去流得到對水速度向量 → 航向/航速指令(速度極小時保持航向、停船)。
    /// </summary>
    private void FollowLeader(TargetShip t, in ShipKinematics own, in EnvironmentSample env)
    {
        var f = t.Spec.Behaviour.Follow ?? DefaultFollow;
        ShipKinematics leader;
        if (string.Equals(f.Leader, "own", StringComparison.OrdinalIgnoreCase)) leader = own;
        else if (Find(f.Leader) is { Active: true } other) leader = other.Kinematics;
        else { t.SpeedCmdMps = 0.0; return; }

        var ang = leader.HeadingRad + Units.DegToRad(f.Bearing);
        var px = leader.X + f.Range * Math.Sin(ang);
        var py = leader.Y + f.Range * Math.Cos(ang);
        var vE = leader.VelE + f.Gain * (px - t.X);
        var vN = leader.VelN + f.Gain * (py - t.Y);
        var maxV = Units.KnToMps(f.MaxSpeed);
        var vg = Math.Sqrt(vE * vE + vN * vN);
        if (vg > maxV) { vE *= maxV / vg; vN *= maxV / vg; }
        var wE = vE - env.CurrentEastMps;
        var wN = vN - env.CurrentNorthMps;
        var ws = Math.Sqrt(wE * wE + wN * wN);
        if (ws < 0.2)
        {
            t.SpeedCmdMps = 0.0;
            t.HeadingCmdRad = leader.HeadingRad;
            return;
        }
        t.HeadingCmdRad = Units.NormalizeHeadingRad(Math.Atan2(wE, wN));
        t.SpeedCmdMps = ws;
    }

    private static readonly TargetFollow DefaultFollow = new();

    // ------------------------------------------------------------------ COLREG 第一版

    /// <summary>
    /// 以目標為本船、自船為他船的會遇幾何判定情境(對遇/橫越/追越)與角色,依 CPA/TCPA 門檻觸發:
    /// 對遇右轉;橫越讓路方右轉並減速、直航方保持(第 17 條最後手段);追越船向遠離側轉向;obey = false 只辨識不行動。
    /// 行動後 CPA 仍低於門檻則每 escalateAfter 秒再加轉向(累計 ≤ 90°);通過 CPA(TCPA ≤ 0)後回到基底行為並冷卻 60 s。
    /// </summary>
    private void EvaluateColreg(TargetShip t, in ShipKinematics own, long tick)
    {
        var c = t.ColregSettings;
        var g = Encounter.Compute(t.X, t.Y, t.VelE, t.VelN, t.Psi, own.X, own.Y, own.VelE, own.VelN);
        var rb = Units.WrapRadPi(g.RelBearingRad);                       // 自船相對目標艏向的方位
        var dh = Units.WrapRadPi(own.HeadingRad - t.Psi);                // 航向差
        var ownSog = own.SogMps;
        var tgtSog = t.Kinematics.SogMps;
        var bearingFromOwn = Units.WrapRadPi(Units.NormalizeHeadingRad(Math.Atan2(t.X - own.X, t.Y - own.Y)) - own.HeadingRad);

        string situation;
        bool giveWay;
        double turnSign = 1.0; // 右轉正
        if (Math.Abs(dh) > Units.DegToRad(165.0) && Math.Abs(rb) < Units.DegToRad(15.0))
        {
            situation = "headOn";
            giveWay = true;
        }
        else if (Math.Abs(rb) > Units.DegToRad(112.5))
        {
            // 自船在目標正橫後:自船較快 → 自船追越目標,目標直航;否則無會遇
            situation = ownSog > tgtSog ? "overtaken" : "none";
            giveWay = false;
        }
        else if (Math.Abs(bearingFromOwn) > Units.DegToRad(112.5) && tgtSog > ownSog)
        {
            situation = "overtaking";
            giveWay = true;
            turnSign = rb < 0 ? 1.0 : -1.0; // 向遠離自船的一側轉
        }
        else
        {
            situation = "crossing";
            giveWay = rb > 0; // 自船在目標右舷 → 目標讓路
        }

        var risk = situation != "none" && g.CpaNm < c.CpaThreshold && g.TcpaS > 0 && g.TcpaMin < c.TcpaThreshold;
        var role = situation == "none" ? "none" : situation == "overtaken" ? "standOn" : giveWay ? "giveWay" : "standOn";
        if (t.ColregForce == ColregForce.GiveWay) { role = "giveWay"; giveWay = true; }
        if (t.ColregForce == ColregForce.StandOn) { role = "standOn"; giveWay = false; }
        var label = $"{situation}/{role}";

        switch (t.ColregPhase)
        {
            case ColregPhase.None:
            case ColregPhase.Resumed when t.ColregForce != ColregForce.Auto || tick - t.ColregResumeTick >= 60 * _evalDivider:
                if (t.ColregPhase == ColregPhase.Resumed) t.ColregPhase = ColregPhase.None;
                if (!(risk || t.ColregForce != ColregForce.Auto))
                {
                    SetSituation(t, situation == "none" ? null : label, tick, g);
                    return;
                }
                if (!c.Obey && t.ColregForce == ColregForce.Auto)
                {
                    SetSituation(t, label + "(ignored)", tick, g);
                    return;
                }
                if (giveWay)
                {
                    var turn = situation == "overtaking" ? c.Turn / 2.0 : c.Turn;
                    StartAction(t, tick, turnSign * Units.DegToRad(turn),
                        situation == "crossing" || t.ColregForce == ColregForce.GiveWay ? c.SpeedFactor : 1.0,
                        ColregPhase.GiveWay, label + "/avoiding", g);
                }
                else
                {
                    t.ColregPhase = ColregPhase.StandOn;
                    t.ColregHeadingRad = t.Psi;
                    t.ColregSpeedMps = t.Speed;
                    t.ColregActionTick = tick;
                    SetSituation(t, label + "/holding", tick, g);
                }
                break;

            case ColregPhase.GiveWay:
            case ColregPhase.LastResort:
                if (Passed(g, c))
                {
                    Resume(t, tick, g);
                }
                else if (g.CpaNm < c.CpaThreshold && tick - t.ColregActionTick >= (long)(c.EscalateAfter * _evalDivider)
                         && t.ColregTurnAccumRad < Units.DegToRad(90.0) - 1e-9)
                {
                    StartAction(t, tick, Math.Sign(t.ColregTurnAccumRad == 0 ? 1.0 : t.ColregTurnAccumRad) * Units.DegToRad(c.Turn),
                        c.SpeedFactor, t.ColregPhase, (t.ColregSituation ?? label) + "+", g);
                }
                break;

            case ColregPhase.StandOn:
                if (t.ColregForce == ColregForce.StandOn) break; // 強制直航直到解除
                if (Passed(g, c) || !risk && g.CpaNm >= c.CpaThreshold)
                {
                    Resume(t, tick, g);
                }
                else if (c.LastResort && c.Obey && g.CpaNm < c.CpaThreshold / 2.0 && g.TcpaS > 0 && g.TcpaMin < c.TcpaThreshold / 3.0)
                {
                    StartAction(t, tick, Units.DegToRad(c.Turn), 1.0, ColregPhase.LastResort, label + "/lastResort", g);
                }
                break;

            case ColregPhase.Resumed:
                break;
        }
    }

    private static bool Passed(in EncounterGeometry g, TargetColreg c)
        => g.TcpaS <= 0.0 || g.RangeNm > 3.0 * c.CpaThreshold && g.CpaNm >= c.CpaThreshold;

    private void StartAction(TargetShip t, long tick, double turnRad, double speedFactor, ColregPhase phase, string label, in EncounterGeometry g)
    {
        var baseHeading = t.ColregPhase is ColregPhase.GiveWay or ColregPhase.LastResort ? t.ColregHeadingRad : t.Psi;
        var baseSpeed = t.ColregPhase is ColregPhase.GiveWay or ColregPhase.LastResort ? t.ColregSpeedMps : t.Speed;
        t.ColregHeadingRad = Units.NormalizeHeadingRad(baseHeading + turnRad);
        t.ColregSpeedMps = Math.Max(baseSpeed * speedFactor, 0.3 * t.Speed);
        t.ColregTurnAccumRad += turnRad;
        t.ColregPhase = phase;
        t.ColregActionTick = tick;
        SetSituation(t, label, tick, g, force: true);
    }

    private void Resume(TargetShip t, long tick, in EncounterGeometry g)
    {
        t.ColregPhase = ColregPhase.Resumed;
        t.ColregResumeTick = tick;
        t.ColregTurnAccumRad = 0.0;
        if (t.ColregForce == ColregForce.GiveWay) t.ColregForce = ColregForce.Auto;
        SetSituation(t, "resumed", tick, g, force: true);
    }

    private void SetSituation(TargetShip t, string? label, long tick, in EncounterGeometry g, bool force = false)
    {
        if (!force && string.Equals(t.ColregSituation, label, StringComparison.Ordinal)) return;
        t.ColregSituation = label;
        if (label is not null) Raise(TrafficEventKinds.Colreg, tick, t.Id, label, g);
    }

    // ------------------------------------------------------------------ 自船指標與碰撞

    private void UpdateOwnShipMetrics(in ShipKinematics own, long tick, bool evalTick)
    {
        (double E, double N)[]? ownPoly = null;
        foreach (var t in _targets)
        {
            if (!t.Active) continue;
            var g = Encounter.Compute(own.X, own.Y, own.VelE, own.VelN, own.HeadingRad, t.X, t.Y, t.VelE, t.VelN);
            if (g.RangeNm < t.MinRangeNm) t.MinRangeNm = g.RangeNm;
            if (g.TcpaS > 0 && g.CpaNm < t.MinCpaNm) t.MinCpaNm = g.CpaNm;

            if (evalTick)
            {
                var alarm = g.TcpaS > 0 && g.CpaNm < MinCpaThresholdNm;
                if (alarm && !t.CpaAlarm)
                {
                    t.CpaAlarm = true;
                    Raise(TrafficEventKinds.CpaAlarm, tick, t.Id, $"CPA {g.CpaNm:F2} nm 低於門檻 {MinCpaThresholdNm:F2} nm", g);
                }
                else if (!alarm && t.CpaAlarm)
                {
                    t.CpaAlarm = false;
                    Raise(TrafficEventKinds.CpaClear, tick, t.Id, "CPA 警報解除", g);
                }
            }

            // 碰撞:先以外接圓粗篩,再做多邊形相交
            var reach = 0.5 * (own.Loa + t.Spec.Loa) + 1.0;
            if (g.RangeM <= reach)
            {
                ownPoly ??= Encounter.ShipOutline(own.X, own.Y, own.HeadingRad, own.Loa, own.Beam);
                var tp = Encounter.ShipOutline(t.X, t.Y, t.Psi, t.Spec.Loa, t.Spec.Beam);
                if (Encounter.PolygonsIntersect(ownPoly, tp) && !Collision)
                {
                    Collision = true;
                    Raise(TrafficEventKinds.Collision, tick, t.Id, $"自船與目標 {t.Id} 碰撞", g);
                }
            }
        }
    }

    // ------------------------------------------------------------------ 指令

    /// <summary>
    /// targetControl:args.id 指定目標(add 除外)。支援 add{目標定義}、remove、heading/speed(教官覆寫)、release、
    /// position、waypoints、aisOn、aisError、behaviour、colreg(giveWay/standOn/auto/off)、lights、sound、activate。
    /// 無效的 id 或格式只忽略(指令已進入紀錄,重播結果一致)。
    /// </summary>
    public void ApplyControl(SimCommand cmd, long tick)
    {
        if (cmd.Args is not { ValueKind: JsonValueKind.Object } args) return;

        if (args.TryGetProperty("add", out var add) && add.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var added = JsonSerializer.Deserialize<ScenarioTarget>(add.GetRawText(), ContractJson.Options);
                if (added is not null && Find(added.Id) is null) AddTarget(added, tick);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { /* 格式錯誤:忽略 */ }
        }

        var id = cmd.Arg("id") is { ValueKind: JsonValueKind.String } idEl ? idEl.GetString() : null;
        if (id is null) return;
        var t = Find(id);
        if (t is null) return;

        if (cmd.ArgAsBool("remove") == true)
        {
            RemoveTarget(id, tick);
            return;
        }
        if (cmd.ArgAsBool("activate") == true && !t.Active) Activate(t, tick, "教官啟動");

        var spec = t.Spec;
        var specChanged = false;

        if (cmd.Arg("position") is { ValueKind: JsonValueKind.Object } posEl)
        {
            var p = JsonSerializer.Deserialize<ScenarioPosition>(posEl.GetRawText(), ContractJson.Options);
            if (p is not null && ((p.Lat is not null && p.Lon is not null) || (p.X is not null && p.Y is not null)))
            {
                var (x, y) = Resolve(p);
                t.X = x; t.Y = y; t.AnchorX = x; t.AnchorY = y;
                Raise(TrafficEventKinds.Control, tick, id, "位置覆寫");
            }
        }

        var heading = cmd.ArgAsDouble("heading");
        var speed = cmd.ArgAsDouble("speed");
        if (heading is not null || speed is not null)
        {
            if (!t.Manual) { t.ManualHeadingRad = t.Psi; t.ManualSpeedMps = t.Speed; }
            t.Manual = true;
            if (heading is { } h) t.ManualHeadingRad = Units.NormalizeHeadingRad(Units.DegToRad(h));
            if (speed is { } v) t.ManualSpeedMps = Math.Max(0.0, Units.KnToMps(v));
            Raise(TrafficEventKinds.Control, tick, id, $"教官覆寫:航向 {heading?.ToString("F0") ?? "-"}、航速 {speed?.ToString("F1") ?? "-"} kn");
        }
        if (cmd.ArgAsBool("release") == true && t.Manual)
        {
            t.Manual = false;
            t.HeadingCmdRad = t.Psi;
            t.SpeedCmdMps = t.Speed;
            Raise(TrafficEventKinds.Control, tick, id, "解除覆寫");
        }

        if (cmd.Arg("waypoints") is { ValueKind: JsonValueKind.Array } wpEl)
        {
            var wps = JsonSerializer.Deserialize<List<TargetWaypoint>>(wpEl.GetRawText(), ContractJson.Options);
            if (wps is { Count: > 0 })
            {
                spec.Behaviour.Waypoints = wps;
                if (spec.Behaviour.Mode is TargetBehaviourMode.Hold or TargetBehaviourMode.Scripted or TargetBehaviourMode.Follow or TargetBehaviourMode.Anchored)
                    spec.Behaviour.Mode = TargetBehaviourMode.Waypoints;
                t.WaypointIndex = 0;
                specChanged = true;
                Raise(TrafficEventKinds.Control, tick, id, $"航點更新({wps.Count} 點)");
            }
        }

        if (cmd.Arg("behaviour") is { ValueKind: JsonValueKind.Object } bEl)
        {
            var b = JsonSerializer.Deserialize<TargetBehaviour>(bEl.GetRawText(), ContractJson.Options);
            if (b is not null)
            {
                spec.Behaviour = b;
                t.WaypointIndex = 0;
                t.ScriptIndex = 0;
                t.ColregPhase = ColregPhase.None;
                t.ColregTurnAccumRad = 0.0;
                t.ColregSituation = null;
                t.AnchorX = t.X; t.AnchorY = t.Y; t.AnchorBaseRad = t.Psi;
                t.ActivatedTick = t.Active ? tick : t.ActivatedTick; // 腳本時間自切換起算
                t.HeadingCmdRad = t.Psi;
                t.SpeedCmdMps = t.Speed;
                specChanged = true;
                Raise(TrafficEventKinds.Control, tick, id, $"行為切換:{b.Mode}");
            }
        }

        if (cmd.ArgAsBool("aisOn") is { } aisOn)
        {
            spec.AisOn = aisOn;
            Raise(TrafficEventKinds.Control, tick, id, aisOn ? "AIS 開" : "AIS 關");
        }
        if (cmd.Arg("aisError") is { } aeEl)
        {
            if (aeEl.ValueKind == JsonValueKind.Null) spec.AisError = null;
            else if (aeEl.ValueKind == JsonValueKind.Object)
                spec.AisError = JsonSerializer.Deserialize<TargetAisError>(aeEl.GetRawText(), ContractJson.Options);
            Raise(TrafficEventKinds.Control, tick, id, spec.AisError is null ? "AIS 錯誤清除" : "AIS 錯誤注入");
        }

        if (cmd.Arg("colreg") is { ValueKind: JsonValueKind.String } cEl)
        {
            switch (cEl.GetString()?.ToLowerInvariant())
            {
                case "giveway":
                    t.ColregForce = ColregForce.GiveWay;
                    t.ColregPhase = ColregPhase.None;
                    EnsureColreg(spec).Enabled = true;
                    break;
                case "standon":
                    t.ColregForce = ColregForce.StandOn;
                    t.ColregPhase = ColregPhase.None;
                    EnsureColreg(spec).Enabled = true;
                    break;
                case "auto":
                    t.ColregForce = ColregForce.Auto;
                    t.ColregPhase = ColregPhase.None;
                    EnsureColreg(spec).Enabled = true;
                    break;
                case "off":
                    t.ColregForce = ColregForce.Auto;
                    t.ColregPhase = ColregPhase.None;
                    t.ColregSituation = null;
                    EnsureColreg(spec).Enabled = false;
                    if (spec.Behaviour.Mode == TargetBehaviourMode.Colreg)
                        spec.Behaviour.Mode = spec.Behaviour.Waypoints is { Count: > 0 } ? TargetBehaviourMode.Waypoints : TargetBehaviourMode.Hold;
                    break;
            }
            t.HeadingCmdRad = t.Psi;
            t.SpeedCmdMps = t.Speed;
            Raise(TrafficEventKinds.Control, tick, id, $"COLREG 強制:{cEl.GetString()}");
        }

        if (cmd.Arg("lights") is { ValueKind: JsonValueKind.String } lEl)
        {
            spec.Lights = lEl.GetString();
            Raise(TrafficEventKinds.Control, tick, id, $"燈號:{spec.Lights}");
        }
        if (cmd.Arg("sound") is { ValueKind: JsonValueKind.String } sEl && sEl.GetString() is { Length: > 0 } sig)
        {
            t.Sound = sig;
            t.SoundUntilTick = tick + (long)Math.Round(SoundDurationS / _dt);
            Raise(TrafficEventKinds.Sound, tick, id, sig);
        }

        if (specChanged) t.ReplaceSpec(spec);
    }

    private static TargetColreg EnsureColreg(ScenarioTarget spec) => spec.Behaviour.Colreg ??= new TargetColreg();

    // ------------------------------------------------------------------ 廣播狀態

    /// <summary>已出現目標的廣播狀態(含自船對其的會遇指標與 AIS 報告);無目標時 null。</summary>
    public IReadOnlyList<TargetState>? BuildStates(in ShipKinematics own)
    {
        if (_targets.Count == 0) return null;
        var list = new List<TargetState>(_targets.Count);
        foreach (var t in _targets)
        {
            if (!t.Active) continue;
            var g = Encounter.Compute(own.X, own.Y, own.VelE, own.VelN, own.HeadingRad, t.X, t.Y, t.VelE, t.VelN);
            var (lat, lon) = _projection.ToGeodetic(t.X, t.Y);
            var (sogMps, cogRad) = EnvironmentMath.CogSog(t.VelE, t.VelN, t.Psi);
            var heading = Units.NormalizeHeadingDeg(Units.RadToDeg(t.Psi));
            var cog = Units.NormalizeHeadingDeg(Units.RadToDeg(cogRad));
            var sog = Units.MpsToKn(sogMps);
            var rot = Units.RadPerSecToDegPerMin(t.R);
            list.Add(new TargetState
            {
                Id = t.Id,
                Name = t.Name,
                Mmsi = t.Mmsi,
                ShipType = t.Spec.ShipType,
                Loa = t.Spec.Loa,
                Beam = t.Spec.Beam,
                Draft = t.Spec.Draft,
                Pos = new GeoPosition { Lat = lat, Lon = lon, X = t.X, Y = t.Y },
                Heading = heading,
                Cog = cog,
                Sog = sog,
                Stw = Units.MpsToKn(t.Speed),
                Rot = rot,
                RangeNm = g.RangeNm,
                BearingDeg = Units.NormalizeHeadingDeg(Units.RadToDeg(g.BearingRad)),
                RelBearingDeg = Units.NormalizeHeadingDeg(Units.RadToDeg(g.RelBearingRad)),
                CpaNm = g.CpaNm,
                TcpaMin = g.TcpaMin,
                BcrNm = g.BcrNm,
                BctMin = g.BctMin,
                AisOn = t.Spec.AisOn,
                Lights = t.LightsClass,
                Sound = t.Sound,
                Behaviour = t.Manual ? "manual" : ContractJson.EnumName(t.Spec.Behaviour.Mode),
                Colreg = t.ColregEnabled ? t.ColregSituation : null,
                Ais = BuildAis(t, lat, lon, heading, cog, sog, rot),
            });
        }
        return list;
    }

    /// <summary>AIS 報告:套用錯誤注入(位置偏移、航向/COG/SOG 誤差、靜態資料錯誤);AIS 關或不發送時 null。</summary>
    private AisReport? BuildAis(TargetShip t, double lat, double lon, double heading, double cog, double sog, double rot)
    {
        var spec = t.Spec;
        if (!spec.AisOn) return null;
        var e = spec.AisError;
        if (e?.Silent == true) return null;

        var (rLat, rLon) = (lat, lon);
        if (e is { PositionOffset: > 0 })
        {
            var b = Units.DegToRad(e.PositionOffsetBearing);
            (rLat, rLon) = _projection.ToGeodetic(t.X + e.PositionOffset * Math.Sin(b), t.Y + e.PositionOffset * Math.Cos(b));
        }
        var name = t.Name;
        var typeCode = TargetShip.AisShipTypeCode(spec.ShipType);
        var mmsi = t.Mmsi;
        var loa = spec.Loa;
        var beam = spec.Beam;
        if (e is not null)
        {
            if (e.StaticError)
            {
                name = e.Name ?? name + " II";
                typeCode = TargetShip.AisShipTypeCode(e.ShipType ?? "pleasure");
                mmsi = e.Mmsi ?? mmsi;
                loa *= 0.5;
                beam *= 0.5;
            }
            else
            {
                if (e.Name is not null) name = e.Name;
                if (e.ShipType is not null) typeCode = TargetShip.AisShipTypeCode(e.ShipType);
                if (e.Mmsi is not null) mmsi = e.Mmsi.Value;
            }
        }
        int? imo = int.TryParse(spec.Imo, out var imoNum) ? imoNum : null;
        return new AisReport
        {
            Mmsi = mmsi,
            Name = name,
            CallSign = spec.CallSign,
            Imo = imo,
            ShipType = typeCode,
            NavStatus = t.AisNavStatus,
            Lat = rLat,
            Lon = rLon,
            Cog = Units.NormalizeHeadingDeg(cog + (e?.CogError ?? 0.0)),
            Sog = Math.Max(0.0, sog + (e?.SogError ?? 0.0)),
            Heading = Units.NormalizeHeadingDeg(heading + (e?.HeadingError ?? 0.0)),
            Rot = rot,
            DimToBow = loa / 2.0,
            DimToStern = loa / 2.0,
            DimToPort = beam / 2.0,
            DimToStarboard = beam / 2.0,
            Draught = spec.Draft,
            Destination = spec.Destination,
        };
    }

    // ------------------------------------------------------------------ 雜湊 / 快照

    /// <summary>每艘目標進入狀態鏈雜湊的 double 數(x、y、ψ、對水速度、迴轉率)。</summary>
    public const int HashDoublesPerTarget = 5;

    /// <summary>把所有目標(含未出現者)的狀態寫入雜湊緩衝區;回傳寫入位元組數。</summary>
    public int WriteHash(Span<byte> buf)
    {
        var o = 0;
        foreach (var t in _targets)
        {
            foreach (var d in new[] { t.X, t.Y, t.Psi, t.Speed, t.R })
            {
                BinaryPrimitives.WriteInt64LittleEndian(buf[o..], BitConverter.DoubleToInt64Bits(d));
                o += 8;
            }
        }
        return o;
    }

    public TrafficSnapshot CreateSnapshot() => new()
    {
        Targets = _targets.Select(TargetSnapshot.From).ToArray(),
        Collision = Collision,
        NextDefaultMmsi = _nextDefaultMmsi,
    };

    public void Restore(TrafficSnapshot s, LocalTangentPlane projection)
    {
        _projection = projection;
        _targets.Clear();
        foreach (var ts in s.Targets) _targets.Add(ts.ToTarget());
        Collision = s.Collision;
        _nextDefaultMmsi = s.NextDefaultMmsi;
    }
}
