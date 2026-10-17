using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Assessment;

/// <summary>航線追蹤狀態(進快照;所有數值 double/long,JSON 往返位元一致)。</summary>
public sealed class RouteState
{
    public int LegIndex { get; set; }
    public bool Complete { get; set; }
    /// <summary>目前航跡偏差(m,右正);航線完成後 null</summary>
    public double? Xtd { get; set; }
    public bool OffRoute { get; set; }
    public bool XtdExceeded { get; set; }
    public List<PassedWaypoint> PassedWaypoints { get; set; } = new();
    public long Samples { get; set; }
    public double SumAbsXtd { get; set; }
    public double MaxAbsXtd { get; set; }
    public long MaxXtdTick { get; set; } = -1;
    public int MaxXtdLeg { get; set; }
    public long TimeOutsideS { get; set; }
    public int Excursions { get; set; }
    public double? MinEdgeDistance { get; set; }
    public double SumHeadingDev { get; set; }
    public long HeadingDevSamples { get; set; }
    // ---- 狀態廣播用(每秒更新)
    public double DistanceToWpM { get; set; }
    public double BearingToWpDeg { get; set; }
    public double LegCourseDeg { get; set; }
    /// <summary>目前航段走廊半寬(m;無限制時 null,JSON 不能表示無限大)</summary>
    public double? XtdLimitM { get; set; }
    public double? SpeedLimitKn { get; set; }
}

/// <summary>
/// 航跡保持(情境引擎設計第 4.2 節):航段 i 由 W[i] 到 W[i+1];s = (P−A)·u 沿航跡距離、xtd = (P−A)·nR(右舷法線 (u.y, −u.x),正 = 偏右);
/// 航段切換於 s ≥ L(正橫)或 s ≥ 0 且 |B−P| ≤ arrivalRadius(最後一段只用半徑);不回退;走廊超限事件有 0.8·limit 滯後。
/// </summary>
public sealed class RouteTracker
{
    private readonly ScenarioRoute _route;
    private readonly (double X, double Y)[] _wp;
    private readonly double _defaultArrivalRadius;

    public RouteState State { get; private set; } = new();
    public ScenarioRoute Route => _route;

    public RouteTracker(ScenarioRoute route, LocalTangentPlane projection, double loaM)
    {
        _route = route;
        _wp = route.Waypoints.Select(w => Scenario.Scenario.Resolve(projection, w)).ToArray();
        _defaultArrivalRadius = Math.Max(2.0 * loaM, 150.0);
        State.LegIndex = Math.Clamp(route.StartLeg, 0, Math.Max(0, _wp.Length - 2));
    }

    public int LegCount => Math.Max(0, _wp.Length - 1);

    /// <summary>航段 i 的走廊半寬(到達航點的限制,否則 route 預設,否則無限大)。</summary>
    public double XtdLimit(int leg)
        => (leg + 1 < _route.Waypoints.Count ? _route.Waypoints[leg + 1].XtdLimitM : null) ?? _route.XtdLimitM ?? double.PositiveInfinity;

    public double? SpeedLimit(int leg) => leg + 1 < _route.Waypoints.Count ? _route.Waypoints[leg + 1].SpeedLimitKn : null;

    public string? NextWaypointName => State.Complete || State.LegIndex + 1 >= _route.Waypoints.Count ? null : _route.Waypoints[State.LegIndex + 1].Name;

    /// <summary>每秒更新;事件經 <paramref name="raise"/>(kind, data)。</summary>
    public void Update(Situation sit, Action<string, string?, object?> raise)
    {
        var st = State;
        if (st.Complete || LegCount == 0) { st.Xtd = null; return; }
        var leg = st.LegIndex;
        var (ax, ay) = _wp[leg];
        var (bx, by) = _wp[leg + 1];
        var dx = bx - ax;
        var dy = by - ay;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var ux = dx / len;
        var uy = dy / len;
        var px = sit.X - ax;
        var py = sit.Y - ay;
        var s = px * ux + py * uy;
        var xtd = px * uy - py * ux; // 右舷法線 (uy, −ux)
        var distToB = Math.Sqrt((bx - sit.X) * (bx - sit.X) + (by - sit.Y) * (by - sit.Y));
        var wpB = _route.Waypoints[leg + 1];
        var radius = wpB.ArrivalRadiusM ?? _defaultArrivalRadius;
        var limit = XtdLimit(leg);
        var legCourse = Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(ux, uy)));

        st.Xtd = xtd;
        st.DistanceToWpM = distToB;
        st.BearingToWpDeg = Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(bx - sit.X, by - sit.Y)));
        st.LegCourseDeg = legCourse;
        st.XtdLimitM = double.IsFinite(limit) ? limit : null;
        st.SpeedLimitKn = SpeedLimit(leg);
        st.OffRoute = Math.Abs(xtd) > 5.0 * Units.MetresPerNauticalMile;

        // 累計
        var abs = Math.Abs(xtd);
        st.Samples++;
        st.SumAbsXtd += abs;
        if (abs > st.MaxAbsXtd || st.MaxXtdTick < 0) { st.MaxAbsXtd = abs; st.MaxXtdTick = sit.Tick; st.MaxXtdLeg = leg; }
        if (double.IsFinite(limit))
        {
            var edge = limit - abs;
            if (st.MinEdgeDistance is not { } me || edge < me) st.MinEdgeDistance = edge;
            if (abs > limit)
            {
                st.TimeOutsideS++;
                if (!st.XtdExceeded)
                {
                    st.XtdExceeded = true;
                    st.Excursions++;
                    raise(SimEventKinds.XtdExceeded, null, new { xtd_m = xtd, limit_m = limit, legIndex = leg });
                }
            }
            else if (st.XtdExceeded && abs <= 0.8 * limit)
            {
                st.XtdExceeded = false;
                raise(SimEventKinds.XtdRestored, null, new { xtd_m = xtd, limit_m = limit, legIndex = leg });
            }
            if (abs <= limit)
            {
                st.SumHeadingDev += Math.Abs(Units.WrapDeg180(sit.HeadingDeg - legCourse));
                st.HeadingDevSamples++;
            }
        }
        else
        {
            st.SumHeadingDev += Math.Abs(Units.WrapDeg180(sit.HeadingDeg - legCourse));
            st.HeadingDevSamples++;
        }

        // 航段切換(最後一段只用半徑;s < 0 即自船尚未到達本航段起點時不計入通過:航段短於半徑或從 A 之前接近時不會提早「通過」B)
        var last = leg == LegCount - 1;
        var passed = (s >= 0 && distToB <= radius) || (!last && s >= len);
        if (!passed) return;
        st.PassedWaypoints.Add(new PassedWaypoint { Name = wpB.Name, PassedAtS = sit.Time, XtdM = xtd, DistanceM = distToB });
        if (wpB.Checkpoint) raise(SimEventKinds.CheckpointPassed, null, new { waypoint = wpB.Name, xtd_m = xtd, distance_m = distToB });
        if (wpB.Report is not null) raise(SimEventKinds.ReportPointPassed, null, new { waypoint = wpB.Name, report = wpB.Report });
        if (last)
        {
            st.Complete = true;
            st.Xtd = null;
            st.XtdExceeded = false;
            raise(SimEventKinds.RouteCompleted, null, new { waypoint = wpB.Name });
            return;
        }
        st.LegIndex = leg + 1;
        raise(SimEventKinds.RouteLegChanged, null, new { legIndex = leg + 1, from = leg, to = leg + 1, waypoint = wpB.Name });
    }

    public SummaryTrack Summary(double dt, double? minDistanceToNoGo)
    {
        var st = State;
        return new SummaryTrack
        {
            MeanAbsXtdM = st.Samples > 0 ? st.SumAbsXtd / st.Samples : 0.0,
            MaxXtdM = st.MaxAbsXtd,
            MaxXtdAtS = st.MaxXtdTick >= 0 ? st.MaxXtdTick * dt : null,
            MaxXtdLeg = st.MaxXtdTick >= 0 ? st.MaxXtdLeg : null,
            TimeOutsideCorridorS = st.TimeOutsideS,
            CorridorExcursions = st.Excursions,
            FractionOutsideCorridor = st.Samples > 0 ? (double)st.TimeOutsideS / st.Samples : 0.0,
            MinDistanceToCorridorEdgeM = st.MinEdgeDistance,
            MinDistanceToNoGoM = minDistanceToNoGo,
            MeanHeadingDeviationDeg = st.HeadingDevSamples > 0 ? st.SumHeadingDev / st.HeadingDevSamples : 0.0,
            LegIndex = st.LegIndex,
            Complete = st.Complete,
            OffRoute = st.OffRoute,
            Waypoints = st.PassedWaypoints.ToArray(),
        };
    }

    public RouteStatus Status() => new()
    {
        LegIndex = State.LegIndex,
        NextWaypoint = NextWaypointName,
        XtdM = State.Xtd,
        XtdLimitM = State.XtdLimitM,
        DistanceToWaypointNm = State.Complete ? null : State.DistanceToWpM / Units.MetresPerNauticalMile,
        BearingToWaypointDeg = State.Complete ? null : State.BearingToWpDeg,
        LegCourseDeg = State.Complete ? null : State.LegCourseDeg,
        SpeedLimitKn = State.SpeedLimitKn,
        Complete = State.Complete,
    };

    public void Restore(RouteState s) => State = s;
}
