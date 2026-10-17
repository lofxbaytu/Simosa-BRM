using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>區域追蹤(情境引擎設計第 4.3 節):多邊形內外/邊上、經緯度與 ENU 混用、進出事件、ukcPolicy 門檻與水深覆寫、速限秒數。</summary>
public class ZoneTrackerTests
{
    [Fact]
    public void PolygonContainsHandlesConvexConcaveAndEdges()
    {
        var square = new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0) };
        Assert.True(Polygon2D.Contains(square, 5, 5));
        Assert.False(Polygon2D.Contains(square, 15, 5));
        Assert.True(Polygon2D.Contains(square, 10, 5));   // 邊上
        Assert.True(Polygon2D.Contains(square, 0, 0));    // 頂點
        var concave = new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (5.0, 2.0), (0.0, 10.0) };
        Assert.True(Polygon2D.Contains(concave, 2, 2));
        Assert.False(Polygon2D.Contains(concave, 5, 8));  // 凹口
        Assert.Equal(5.0, Polygon2D.DistanceToBoundary(square, 15, 5), 9);
        Assert.False(Polygon2D.SelfIntersects(square));
        Assert.True(Polygon2D.SelfIntersects(new[] { (0.0, 0.0), (10.0, 10.0), (10.0, 0.0), (0.0, 10.0) }));
    }

    [Fact]
    public void EnterExitEventsAndMixedCoordinates()
    {
        var proj = new LocalTangentPlane(23.8, 120.05);
        var (lat, lon) = proj.ToGeodetic(1000, 1000);
        var zones = ContractJson.Deserialize<List<ScenarioZone>>(YamlJson.ToJson($$"""
            - id: z
              type: noGo
              polygon: [ { x: 0, y: 0 }, { x: 1000, y: 0 }, { lat: {{lat}}, lon: {{lon}} }, { x: 0, y: 1000 } ]
            """))!;
        var zt = new ZoneTracker(zones, proj);
        var events = new List<string>();
        void Raise(string k, string? id, object? d) => events.Add(k);
        Situation S(double x, double y) => new()
        {
            Tick = 50, Time = 1, X = x, Y = y, HeadingRad = 0, SogKn = 5, StwKn = 5, RotDegPerMin = 0, RudderDeg = 0, RudderOrderDeg = 0,
            Telegraph = TelegraphOrder.HAH, Rpm = 0, ThrusterOrder = 0, AutopilotEnabled = false, UkcM = 10, SquatM = 0, WaterDepthM = 20, MaxDraftM = 5, LoaM = 100, VelE = 0, VelN = 0,
        };
        zt.Update(S(-100, 500), Raise);
        Assert.False(zt.IsInside("z"));
        Assert.Equal(100.0, zt.State.MinDistanceToNoGo!.Value, 6);
        zt.Update(S(500, 500), Raise);
        Assert.True(zt.IsInside("z"));
        Assert.Equal(new[] { SimEventKinds.ZoneEntered }, events);
        Assert.Equal(0.0, zt.State.MinDistanceToNoGo!.Value);
        zt.Update(S(1500, 500), Raise);
        Assert.Equal(new[] { SimEventKinds.ZoneEntered, SimEventKinds.ZoneExited }, events);
    }

    [Fact]
    public void UkcPolicyZoneOverridesDepthAndLimitAndSpeedLimitZoneCountsSeconds()
    {
        var e = TestData.MinimalEngine("""
            zones:
              - id: shallow
                type: ukcPolicy
                waterDepth_m: 7
                ukcMin: { fractionOfDraft: 0.2 }
                polygon: [ { x: -500, y: 200 }, { x: 500, y: 200 }, { x: 500, y: 2000 }, { x: -500, y: 2000 } ]
              - id: slow
                type: speedLimit
                speedLimit_kn: 3
                polygon: [ { x: -500, y: 200 }, { x: 500, y: 200 }, { x: 500, y: 2000 }, { x: -500, y: 2000 } ]
            assessment:
              ukc: { min: { metres: 0.5 } }
            """, speedKn: 6.0, waterDepth: 30);
        var events = TestData.Capture(e);
        Assert.IsType<Environment.ZoneDepth>(e.Environment.Depth);
        Assert.Equal(30.0, e.Environment.Depth.DepthAt(0, 0));
        Assert.Equal(7.0, e.Environment.Depth.DepthAt(0, 1000));
        TestData.RunTo(e, 200); // 6 kn ≈ 3.1 m/s → 200 s 約 600 m,已在區內
        Assert.True(e.Assessment!.Zones.IsInside("shallow"));
        Assert.Equal(7.0, e.BuildState().WaterDepth);
        var maxDraft = TestData.Fsb1().GetLoading(LoadingCondition.Ballast).MaxDraft_m;
        var limit = e.Assessment.UkcLimit()!.Value;
        Assert.Equal(0.2 * maxDraft, limit.LimitM, 9);
        Assert.Equal("zones.shallow", limit.Source);
        Assert.Contains(events, x => x.Kind == SimEventKinds.ZoneEntered && x.DataString("zoneId") == "shallow");
        Assert.Contains(events, x => x.Kind == SimEventKinds.SpeedLimitExceeded && x.DataString("source") == "zone");
        var m = e.Assessment.Metrics.State;
        Assert.True(m.TimeOverSpeedLimitS > 50, $"{m.TimeOverSpeedLimitS}");
        Assert.Equal("zone:slow", m.SpeedLimitSource);
        Assert.True(m.MaxOverSpeedKn > 2);
        Assert.Equal(e.Assessment.LatestSummary!.Ukc.LimitSource, "zones.shallow");
        Assert.True(e.Assessment.LatestSummary.Ukc.MinM < 7 - maxDraft + 0.01);
    }
}
