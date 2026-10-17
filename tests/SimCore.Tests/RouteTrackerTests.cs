using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>航跡保持演算法(情境引擎設計第 4.2 節):XTD 正負號、航段切換、不回退、最後一段只用半徑、走廊事件滯後、橫流解析解。</summary>
public class RouteTrackerTests
{
    private static Situation At(double x, double y, double headingDeg, long tick = 50, double sog = 8)
        => new()
        {
            Tick = tick, Time = tick * 0.02, X = x, Y = y, HeadingRad = Units.DegToRad(headingDeg), SogKn = sog, StwKn = sog, RotDegPerMin = 0,
            RudderDeg = 0, RudderOrderDeg = 0, Telegraph = TelegraphOrder.HAH, Rpm = 0, ThrusterOrder = 0, AutopilotEnabled = false,
            UkcM = 10, SquatM = 0, WaterDepthM = 20, MaxDraftM = 5, LoaM = 100, VelE = 0, VelN = 0,
        };

    private static (RouteTracker Tracker, List<(string Kind, object? Data)> Events) Make(string yaml, double loa = 100)
    {
        var route = ContractJson.Deserialize<ScenarioRoute>(YamlJson.ToJson(yaml))!;
        var events = new List<(string, object?)>();
        var t = new RouteTracker(route, new LocalTangentPlane(23.8, 120.05), loa);
        return (t, events);
    }

    private const string Northbound = """
        xtdLimit_m: 50
        waypoints:
          - { name: A, x: 0, y: 0 }
          - { name: B, x: 0, y: 2000, checkpoint: true, speedLimit_kn: 6 }
          - { name: C, x: 2000, y: 2000, report: "report here" }
          - { name: D, x: 2000, y: 4000, arrivalRadius_m: 100 }
        """;

    [Fact]
    public void XtdIsPositiveToStarboardAndAlongTrackIsCorrect()
    {
        var (t, ev) = Make(Northbound);
        void Raise(string k, string? id, object? d) => ev.Add((k, d));
        t.Update(At(30, 500, 0), Raise);
        Assert.Equal(30.0, t.State.Xtd!.Value, 9);        // 航向北、位於東側 = 右舷 → 正
        Assert.Equal(Math.Sqrt(30.0 * 30.0 + 1500.0 * 1500.0), t.State.DistanceToWpM, 9);
        Assert.Equal(0.0, t.State.LegCourseDeg, 9);
        Assert.Equal(50.0, t.State.XtdLimitM);
        Assert.Equal(6.0, t.State.SpeedLimitKn);
        t.Update(At(-20, 600, 0, 100), Raise);
        Assert.Equal(-20.0, t.State.Xtd!.Value, 9);
        Assert.Equal("B", t.NextWaypointName);
        Assert.Empty(ev);
        // 起點後方(s < 0)仍以延長線計 XTD
        t.Update(At(10, -300, 0, 150), Raise);
        Assert.Equal(10.0, t.State.Xtd!.Value, 9);
        Assert.Equal(0, t.State.LegIndex);
    }

    [Fact]
    public void LegAdvancesAtAbeamOrArrivalRadiusAndNeverGoesBack()
    {
        var (t, ev) = Make(Northbound);
        void Raise(string k, string? id, object? d) => ev.Add((k, d));
        t.Update(At(5, 1500, 0, 50), Raise);
        Assert.Equal(0, t.State.LegIndex);     // 距 B 500 m:半徑(預設 max(2·LOA, 150) = 200)之外、未正橫
        t.Update(At(5, 1990, 0, 60), Raise);
        Assert.Equal(1, t.State.LegIndex);     // 距離 10 m → 在到達半徑內 → 通過
        Assert.Contains(ev, e => e.Kind == SimEventKinds.RouteLegChanged);
        Assert.Contains(ev, e => e.Kind == SimEventKinds.CheckpointPassed);
        Assert.Single(t.State.PassedWaypoints);
        Assert.Equal("B", t.State.PassedWaypoints[0].Name);
        Assert.Equal(5.0, t.State.PassedWaypoints[0].XtdM, 9);
        Assert.Equal(Math.Sqrt(5.0 * 5.0 + 10.0 * 10.0), t.State.PassedWaypoints[0].DistanceM, 9);
        ev.Clear();
        // 正橫:航段 1 向東,自船在 C 之東 400 m(半徑外)→ s ≥ L 切換
        t.Update(At(2400, 2010, 90, 100), Raise);
        Assert.Equal(2, t.State.LegIndex);
        Assert.Contains(ev, e => e.Kind == SimEventKinds.ReportPointPassed);
        ev.Clear();
        // 回到前一航段區域也不回退
        t.Update(At(1000, 2000, 270, 150), Raise);
        Assert.Equal(2, t.State.LegIndex);
        // 最後一段:正橫越過 D 但在半徑外 → 不算完成
        t.Update(At(2300, 4500, 0, 200), Raise);
        Assert.False(t.State.Complete);
        Assert.Equal(2, t.State.LegIndex);
        t.Update(At(2050, 4050, 0, 250), Raise);
        Assert.True(t.State.Complete);
        Assert.Contains(ev, e => e.Kind == SimEventKinds.RouteCompleted);
        Assert.Null(t.State.Xtd);
        var status = t.Status();
        Assert.True(status.Complete);
        Assert.Null(status.NextWaypoint);
    }

    [Fact]
    public void CorridorExcursionEventsHaveHysteresis()
    {
        var (t, ev) = Make(Northbound);
        void Raise(string k, string? id, object? d) => ev.Add((k, d));
        long tick = 0;
        foreach (var x in new[] { 10.0, 55, 60, 45, 38, 30, 55 })
            t.Update(At(x, 500, 0, tick += 50), Raise);
        Assert.Equal(3, ev.Count(e => e.Kind is SimEventKinds.XtdExceeded or SimEventKinds.XtdRestored));
        Assert.Equal(SimEventKinds.XtdExceeded, ev[0].Kind);
        Assert.Equal(SimEventKinds.XtdRestored, ev[1].Kind);      // 45 > 40 仍在滯後內,38 ≤ 0.8·50 才恢復
        Assert.Equal(SimEventKinds.XtdExceeded, ev[2].Kind);
        Assert.Equal(2, t.State.Excursions);
        Assert.Equal(3, t.State.TimeOutsideS);
        var s = t.Summary(0.02, null);
        Assert.Equal(60.0, s.MaxXtdM, 9);
        Assert.Equal(-10.0, s.MinDistanceToCorridorEdgeM!.Value, 9);
        Assert.Equal(7, t.State.Samples);
        Assert.Equal((10 + 55 + 60 + 45 + 38 + 30 + 55) / 7.0, s.MeanAbsXtdM, 9);
    }

    [Fact]
    public void CrossCurrentGivesLinearXtdInEngine()
    {
        // 北向航線、流向正東 1 kn、自船停俥(0 kn)→ XTD(t) ≈ 0.5144 m/s × t(右偏為正)
        var e = TestData.MinimalEngine("""
            route:
              xtdLimit_m: 500
              waypoints: [ { name: A, x: 0, y: -1000 }, { name: B, x: 0, y: 50000 } ]
            """, speedKn: 0.0);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
        e.Enqueue(SimCommand.SetEnvironment(currentSetDeg: 90, currentDriftKn: 1.0));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 120);
        var xtd = e.Assessment!.Route!.State.Xtd!.Value;
        Assert.InRange(xtd, 0.5144 * 120 - 2.0, 0.5144 * 120 + 2.0);
        var st = e.BuildState().Route!;
        Assert.Equal(xtd, st.XtdM);
        Assert.Equal("B", st.NextWaypoint);
        Assert.Equal(0, st.LegIndex);
        Assert.InRange(st.BearingToWaypointDeg!.Value, 359.0, 360.0);
    }


    /// <summary>到達半徑判定需自船已過航段起點(s ≥ 0):航段短於半徑、或從 A 之前接近時不會提早「通過」B。</summary>
    [Fact]
    public void ArrivalRadiusRequiresAlongTrackPositionAfterLegStart()
    {
        var (t, ev) = Make("""
            waypoints:
              - { name: A, x: 0, y: 100 }
              - { name: B, x: 0, y: 200 }
              - { name: C, x: 0, y: 300 }
              - { name: D, x: 0, y: 5000 }
            """);
        void Raise(string k, string? id, object? d) => ev.Add((k, d));
        t.Update(At(0, 4, 0), Raise);          // 距 B 196 m < 預設半徑 200 m,但 s = −96(尚未到 A)
        Assert.Equal(0, t.State.LegIndex);
        Assert.Empty(ev);
        Assert.Empty(t.State.PassedWaypoints);
        t.Update(At(0, 101, 0, 100), Raise);   // 過 A 後距 B 99 m → 通過 B
        Assert.Equal(1, t.State.LegIndex);
        Assert.Single(t.State.PassedWaypoints, w => w.Name == "B");
        t.Update(At(0, 201, 0, 150), Raise);   // 航段 B→C 長 100 m ≤ 半徑:進入後才通過 C
        Assert.Equal(2, t.State.LegIndex);
        // 最後一段:自船在 C 之前(s < 0)不通過;到達 D 附近才完成
        t.Update(At(0, 250, 0, 250), Raise);
        Assert.False(t.State.Complete);
        Assert.Equal(2, t.State.LegIndex);
        t.Update(At(0, 4900, 0, 300), Raise);
        Assert.True(t.State.Complete);
    }
}
