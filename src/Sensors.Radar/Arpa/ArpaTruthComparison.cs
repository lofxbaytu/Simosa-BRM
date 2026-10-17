using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Sensors.Radar.Arpa;

/// <summary>ARPA 追蹤目標與核心真值目標的比對(教官/驗收用;學員端不輸出)。</summary>
public sealed record TruthMatch(
    int TrackId, string TruthId, string State, double AgeS, bool Stable,
    double PositionErrorM, double ArpaCpaNm, double TruthCpaNm, double CpaErrorNm,
    double ArpaTcpaMin, double TruthTcpaMin, double CourseErrorDeg, double SpeedErrorKn);

public static class ArpaTruthComparison
{
    /// <summary>
    /// 每個有估計值的追蹤目標配對最近的真值目標(距離 &lt; maxDistanceM),比較位置、CPA/TCPA(真值取 state.targets[] 的 cpa_nm/tcpa_min,
    /// 與 SimCore <c>Encounter</c> 同一定義)、航向與速度。
    /// </summary>
    public static IReadOnlyList<TruthMatch> Compare(RadarUnit unit, RadarScene scene, double maxDistanceM = 500.0)
    {
        var list = new List<TruthMatch>();
        if (scene.TruthTargets is not { Count: > 0 } truths) return list;
        foreach (var tr in unit.Arpa.Tracks)
        {
            if (!tr.HasEstimate || tr.State == TrackState.Lost) continue;
            var s = unit.Arpa.Solve(tr, scene.T, null);
            TargetState? best = null;
            var bestD = double.PositiveInfinity;
            foreach (var t in truths)
            {
                var d = Math.Sqrt((t.Pos.X - s.X) * (t.Pos.X - s.X) + (t.Pos.Y - s.Y) * (t.Pos.Y - s.Y));
                if (d < bestD) { bestD = d; best = t; }
            }
            if (best is null || bestD > maxDistanceM) continue;
            list.Add(new TruthMatch(tr.Id, best.Id, ContractJson.EnumName(tr.State), tr.AgeS(scene.T), tr.AgeS(scene.T) >= unit.Config.Arpa.StableTimeS,
                bestD, s.CpaNm, best.CpaNm, Math.Abs(s.CpaNm - best.CpaNm), s.TcpaMin, best.TcpaMin,
                Math.Abs(Units.WrapDeg180(s.CourseDeg - best.Cog)), Math.Abs(s.SpeedKn - best.Sog)));
        }
        return list;
    }
}
