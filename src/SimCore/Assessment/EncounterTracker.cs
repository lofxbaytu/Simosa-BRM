using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Traffic;

namespace SimosaBRM.SimCore.Assessment;

public sealed class EncounterState
{
    public List<EncounterTrack> Records { get; set; } = new();
    public int Collisions { get; set; }
}

/// <summary>
/// 每目標會遇追蹤(第 4.4 節):開始(range ≤ detectionRange、TCPA &gt; 0、CPA &lt; 關注門檻)、首次行動(舵 ≥ 5°、車令、側推、自動舵改向 ≥ 5°)、
/// 航向/速度改變幅度、結束(TCPA &lt; 0 且距離連續 30 s 增加,或目標移除)。跟隨自船的引水船/拖船標為 escort,不列入彙總。
/// </summary>
public sealed class EncounterTracker
{
    private readonly EncounterSpec _spec;
    private readonly double _cpaInterestNm;

    public EncounterState State { get; private set; } = new();

    public EncounterTracker(EncounterSpec? spec, double minCpaNm)
    {
        _spec = spec ?? new EncounterSpec();
        _cpaInterestNm = _spec.CpaInterestNm ?? 2.0 * minCpaNm;
    }

    /// <summary>該目標最新的一筆會遇紀錄(同一目標可有多筆:關閉後再次成立開始條件即新開一筆)。</summary>
    private EncounterTrack? Find(string id) => State.Records.LastOrDefault(r => r.TargetId == id);

    public void OnCollision() => State.Collisions++;

    public void Update(Situation sit, TrafficManager traffic, Action<string, string?, object?> raise)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in traffic.Targets)
        {
            if (!t.Active || t.EscortsOwnShip) continue; // 跟隨自船的引水船/拖船不列入會遇
            seen.Add(t.Id);
            var g = Encounter.Compute(sit.X, sit.Y, sit.VelE, sit.VelN, sit.HeadingRad, t.X, t.Y, t.VelE, t.VelN);
            var rec = Find(t.Id);
            if (rec is null || rec.Closed)
            {
                if (!(g.RangeNm <= _spec.DetectionRangeNm && g.TcpaS > 0 && g.CpaNm < _cpaInterestNm))
                {
                    // 關閉後再接近但未重新成立開始條件(例如橫向漂近、迴轉後貼近錨泊船):仍把更小的實際通過距離記到該目標最後一筆紀錄,
                    // 使 collisionAvoidance.minCpa_nm、minCpa 容許帶與硬性失敗 cpa 反映真實最小距離
                    if (rec is not null && g.RangeNm <= _spec.DetectionRangeNm && rec.MinRangeNm is { } closedMin && g.RangeNm < closedMin) rec.MinRangeNm = g.RangeNm;
                    continue;
                }
                rec = new EncounterTrack
                {
                    TargetId = t.Id, Name = t.Name, Situation = t.ColregSituation, DetectedAtS = sit.Time,
                    TcpaAtDetectionMin = g.TcpaMin, BcrAtDetectionNm = g.BcrNm, Heading0Rad = sit.HeadingRad, Stw0Kn = sit.StwKn,
                    MinCpaNm = g.CpaNm, MinCpaAtS = sit.Time, MinRangeNm = g.RangeNm, BcrMinNm = g.BcrNm is { } b ? Math.Abs(b) : null,
                    Escort = t.EscortsOwnShip, LastRangeNm = g.RangeNm,
                };
                State.Records.Add(rec);
                raise(SimEventKinds.EncounterStarted, t.Id, new { range_nm = g.RangeNm, cpa_nm = g.CpaNm, tcpa_min = g.TcpaMin, situation = rec.Situation });
                continue;
            }
            if (rec.Closed) continue;
            if (rec.Situation is null && t.ColregSituation is not null) rec.Situation = t.ColregSituation;
            if (g.TcpaS > 0 && (rec.MinCpaNm is not { } mc || g.CpaNm < mc)) { rec.MinCpaNm = g.CpaNm; rec.MinCpaAtS = sit.Time; }
            if (rec.MinRangeNm is not { } mr || g.RangeNm < mr) rec.MinRangeNm = g.RangeNm;
            if (g.BcrNm is { } bcr && (rec.BcrMinNm is not { } bm || Math.Abs(bcr) < bm)) rec.BcrMinNm = Math.Abs(bcr);
            var dh = Math.Abs(Units.RadToDeg(Units.WrapRadPi(sit.HeadingRad - rec.Heading0Rad)));
            if (dh > rec.CourseChangeDeg) rec.CourseChangeDeg = dh;
            var dv = Math.Abs(sit.StwKn - rec.Stw0Kn);
            if (dv > rec.SpeedChangeKn) rec.SpeedChangeKn = dv;
            rec.Substantial = rec.CourseChangeDeg >= _spec.SubstantialCourseChangeDeg || rec.SpeedChangeKn >= _spec.SubstantialSpeedChangeKn;
            if (t.CpaAlarm) rec.CpaAlarmTimeS += 1.0;
            // 結束:TCPA < 0 且距離連續 30 s 增加
            if (g.TcpaS < 0 && rec.LastRangeNm is { } lr && g.RangeNm > lr) rec.OpeningSeconds++;
            else rec.OpeningSeconds = 0;
            rec.LastRangeNm = g.RangeNm;
            if (rec.OpeningSeconds >= 30) Close(rec, sit.Time, "passed", raise);
        }
        foreach (var rec in State.Records)
            if (!rec.Closed && !seen.Contains(rec.TargetId)) Close(rec, sit.Time, "removed", raise);
    }

    private static void Close(EncounterTrack rec, double time, string reason, Action<string, string?, object?> raise)
    {
        rec.ClosedAtS = time;
        raise(SimEventKinds.EncounterClosed, rec.TargetId, new { reason, minCpa_nm = rec.MinCpaNm, minRange_nm = rec.MinRangeNm, courseChange_deg = rec.CourseChangeDeg, speedChange_kn = rec.SpeedChangeKn });
    }

    /// <summary>學員指令:會遇開始後的第一個相關動作。</summary>
    public void OnAction(ObservedAction a, double time, Action<string, string?, object?> raise)
    {
        var relevant = a.Kind switch
        {
            ActionKinds.RudderOrder => a.Magnitude >= 5.0,
            ActionKinds.TelegraphChange or ActionKinds.RpmOrder => true,
            ActionKinds.AutopilotHeading => a.Magnitude >= 5.0,
            _ => false,
        };
        if (!relevant) return;
        foreach (var rec in State.Records)
        {
            if (rec.Closed || rec.FirstActionAtS is not null) continue;
            rec.FirstActionAtS = time;
            rec.TimeToActionS = time - (rec.DetectedAtS ?? time);
            rec.FirstAction = a.Kind switch { ActionKinds.RudderOrder => "rudder", ActionKinds.TelegraphChange => "telegraph", ActionKinds.RpmOrder => "rpm", _ => "autopilot" };
            raise(SimEventKinds.EncounterFirstAction, rec.TargetId, new { action = rec.FirstAction, dt_s = rec.TimeToActionS });
        }
    }

    /// <summary>非 escort 目標的最小實際通過距離(nm)。</summary>
    public double? MinRangeNonEscort()
    {
        double? best = null;
        foreach (var r in State.Records)
            if (!r.Escort && r.MinRangeNm is { } m && (best is null || m < best)) best = m;
        return best;
    }

    public SummaryCollisionAvoidance Summary() => new()
    {
        MinCpaNm = MinRangeNonEscort(),
        Encounters = State.Records.Count(r => !r.Escort),
        Collisions = State.Collisions,
        Targets = State.Records.Select(r => r.ToRecord()).ToArray(),
    };

    public void Restore(EncounterState s) => State = s;
}
