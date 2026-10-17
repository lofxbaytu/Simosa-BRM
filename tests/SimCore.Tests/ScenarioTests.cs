using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class ScenarioTests
{
    [Fact]
    public void LoadsBaselineScenario()
    {
        var sc = TestData.Baseline();
        Assert.Equal("E01_baseline", sc.Id);
        Assert.Equal("FSB1", sc.Ship.Id);
        Assert.Equal(LoadingCondition.Ballast, sc.Ship.Loading);
        Assert.Equal(20260101, sc.Seed);
        Assert.NotNull(sc.Origin);
        Assert.Equal(23.80, sc.Origin!.Lat, 9);
        Assert.Equal(120.05, sc.Origin.Lon, 9);
        Assert.Equal(0.0, sc.Initial.Heading);
        Assert.Equal(7.8, sc.Initial.Speed);
        Assert.Equal("HAH", sc.Initial.Telegraph);
        Assert.Equal(15.0, sc.Environment.Wind.TrueSpeed);
        Assert.Equal(45.0, sc.Environment.Wind.TrueDir);
        Assert.Equal(0.1, sc.Environment.Wind.Gustiness);
        Assert.Equal(200.0, sc.Environment.Current.Set);
        Assert.Equal(0.5, sc.Environment.Current.Drift);
        Assert.Equal(30.0, sc.Environment.WaterDepth);
        Assert.Equal("2026-01-01T02:00:00Z", sc.StartTimeUtc);
    }

    [Fact]
    public void OriginDefaultsToInitialPosition()
    {
        var sc = ScenarioLoader.FromYaml("""
            id: T
            ship: { id: FSB2, loading: full }
            initial: { position: { lat: 24.1, lon: 120.4 }, heading: 90, speed: 5 }
            environment: { waterDepth: 20 }
            """).Validate();
        Assert.Equal(24.1, sc.Origin!.Lat);
        Assert.Equal(120.4, sc.Origin.Lon);
        Assert.Equal(LoadingCondition.Full, sc.Ship.Loading);
        Assert.Null(sc.Initial.Telegraph);
    }

    [Fact]
    public void LocalPositionRequiresOrigin()
    {
        var yaml = """
            id: T
            ship: { id: FSB1 }
            initial: { position: { x: 100, y: 200 }, heading: 0 }
            environment: { waterDepth: 20 }
            """;
        Assert.Throws<InvalidDataException>(() => ScenarioLoader.FromYaml(yaml).Validate());
    }

    [Fact]
    public void UnknownKeysAreIgnoredAndJsonRoundTrips()
    {
        var sc = ScenarioLoader.FromYaml("""
            id: T
            futureKey: 123
            ship: { id: FSB1, loading: ballast }
            initial: { position: { lat: 23.8, lon: 120.0 }, heading: 10, speed: 3, telegraph: SAH }
            environment: { wind: { trueSpeed: 5, trueDir: 90 }, waterDepth: 15 }
            """).Validate();
        var json = sc.ToJson();
        var back = Scenario.Scenario.FromJson(json);
        Assert.Equal(sc.Ship.Loading, back.Ship.Loading);
        Assert.Equal(sc.Initial.Telegraph, back.Initial.Telegraph);
        Assert.Equal(sc.Environment.Wind.TrueDir, back.Environment.Wind.TrueDir);
        Assert.Equal(sc.Origin!.Lat, back.Origin!.Lat);
    }

    [Fact]
    public void ScenarioSchemaIsValidJsonSchema()
    {
        var path = Path.Combine(TestData.Root, "src", "Contracts", "scenario.schema.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal("Scenario", root.GetProperty("title").GetString());
        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("id", required);
        Assert.Contains("ship", required);
        Assert.Contains("initial", required);
        Assert.Contains("environment", required);
        // 範例應能被 C# 情境模型讀取
        var example = root.GetProperty("examples")[0].GetRawText();
        var sc = Scenario.Scenario.FromJson(example);
        Assert.Equal("E01_baseline", sc.Id);
        Assert.Equal("HAH", sc.Initial.Telegraph);
    }

    [Fact]
    public void LoadsE07WithTimelineRouteZonesAssessmentBriefing()
    {
        var sc = TestData.E07();
        Assert.Equal("E07_steering_failure", sc.Id);
        Assert.Equal(LoadingCondition.Full, sc.Ship.Loading);
        Assert.Equal(6.0, sc.Environment.VisibilityNm);
        Assert.Equal(7, sc.Timeline.Count);
        var fail = sc.Timeline.Single(t => t.Id == "steeringFail");
        Assert.Equal("degraded", fail.Phase);
        Assert.NotNull(fail.When!.All);
        Assert.Equal("routeLeg", fail.When.All![0].Type);
        Assert.Equal("routeLegChanged", fail.When.All[1].Since!.Event);
        Assert.Equal(1, fail.When.All[1].Since!.LegIndex);
        Assert.Equal(new[] { "injectFault", "voiceScript", "message" }, fail.Actions.Select(a => a.Kind));
        Assert.Equal("steeringGear", fail.Actions[0].AsString());
        Assert.Equal("helmsman", fail.Actions[1].AsVoiceScript()!.Role);
        Assert.True(sc.Timeline.Single(t => t.Id == "emergencySteering").IsManual);
        Assert.Equal(RepeatMode.Every, sc.Timeline.Single(t => t.Id == "speedCoach").Repeat.Mode);
        Assert.Equal(3, sc.Timeline.Single(t => t.Id == "speedCoach").Repeat.Max);
        Assert.Equal(4, sc.Route!.Waypoints.Count);
        Assert.Equal(50.0, sc.Route.XtdLimitM);
        Assert.True(sc.Route.Waypoints[1].Checkpoint);
        Assert.Equal(8.0, sc.Route.Waypoints[1].SpeedLimitKn);
        Assert.Equal(120.0, sc.Route.Waypoints[3].ArrivalRadiusM);
        Assert.Equal(3, sc.Zones.Count);
        Assert.Equal(ZoneType.UkcPolicy, sc.Zones[0].Type);
        Assert.Equal(12.5, sc.Zones[0].WaterDepthM);
        Assert.Equal(0.10, sc.Zones[0].UkcMin!.FractionOfDraft);
        Assert.Equal(ZoneType.NoGo, sc.Zones[1].Type);
        Assert.Equal(5.0, sc.Zones[2].SpeedLimitKn);
        Assert.Equal(0.05, sc.Assessment.MinCpaNm); // navigation.cpa.channel_nm(D27)
        Assert.Equal(1.0, sc.Assessment.Ukc!.Min!.Metres);
        Assert.Equal(4, sc.Assessment.HardFail.Count);
        Assert.Equal(5, sc.Assessment.Bands.Count);
        Assert.Equal(2.0, sc.Assessment.Bands[0].Fail!.Multiplier);
        Assert.Equal(4, sc.Assessment.Required.Count);
        Assert.Equal(5, sc.Assessment.Required[0].Expect.Any!.Count);
        Assert.Equal("steeringMode", sc.Assessment.Required[0].Expect.Any![4].Acknowledge!.Kind);
        Assert.Equal(60.0, sc.Assessment.Required[0].WithinS);
        Assert.Equal("timelineTriggered", sc.Assessment.Required[2].Expect.Event!.Kind);
        Assert.Equal(70.0, sc.Assessment.Scoring!.PassMark);
        Assert.Equal(2, sc.Briefing!.Attachments.Count);
        Assert.Equal(3, sc.Briefing.Roles.Count);
        Assert.Equal(3, sc.Briefing.MinParticipants);
        Assert.NotNull(sc.ParameterSetInfo);
        // JSON 往返(紀錄標頭)保留全部欄位
        var back = Scenario.Scenario.FromJson(sc.ToJson());
        Assert.Equal(sc.ToJson(), back.ToJson());
        Assert.Equal(2.0, back.Assessment.Bands[0].Fail!.Multiplier);
        // YAML 往返
        var again = ScenarioLoader.FromYaml(ScenarioLoader.ToYaml(sc)).Validate();
        Assert.Equal(sc.ToJson(), again.ToJson());
    }

    [Fact]
    public void BriefingAcceptsStringOrObjectAndSchemaExampleHasTimeline()
    {
        var sc = ScenarioLoader.FromYaml("""
            id: T
            ship: { id: FSB1 }
            initial: { position: { lat: 23.8, lon: 120.0 }, heading: 0 }
            environment: { waterDepth: 20 }
            briefing: 只有文字
            """).Validate();
        Assert.Equal("只有文字", sc.Briefing!.Text);
        Assert.Contains("\"briefing\":\"只有文字\"", sc.ToJson());
        var path = Path.Combine(TestData.Root, "src", "Contracts", "scenario.schema.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var ex = Scenario.Scenario.FromJson(doc.RootElement.GetProperty("examples")[0].GetRawText());
        Assert.Equal(3, ex.Timeline.Count);
        Assert.Equal(2, ex.Route!.Waypoints.Count);
        Assert.Equal(2, ex.Assessment.HardFail.Count);
        Assert.Equal(10.0, ex.Environment.VisibilityNm);
        foreach (var def in new[] { "triggerCondition", "timelineAction", "timelineItem", "route", "routeWaypoint", "zone", "paramRef", "numberOrParam", "briefing" })
            Assert.True(doc.RootElement.GetProperty("$defs").TryGetProperty(def, out _), def);
        var engine = new SimosaBRM.SimCore.Engine.SimulationEngine(TestData.Fsb1(), ex);
        engine.Run(50);
    }


    /// <summary>E07 出港船 OUT1 的航跡位於自船航線左舷側 ≥ 100 m(左舷對左舷通過),不與自船航線相交;minCpa 門檻用航道參數。</summary>
    [Fact]
    public void E07OutboundTargetKeepsClearOfOwnRoute()
    {
        var sc = TestData.E07();
        var proj = new Geo.LocalTangentPlane(sc.Origin!.Lat, sc.Origin.Lon);
        (double X, double Y) Pt(double? lat, double? lon, double? x, double? y) => x is { } px && y is { } py ? (px, py) : proj.ToLocal(lat!.Value, lon!.Value);
        var route = sc.Route!.Waypoints.Select(w => Pt(w.Lat, w.Lon, w.X, w.Y)).ToArray();
        var out1 = sc.Targets.Single(t => t.Id == "OUT1");
        var ip = out1.Initial.Position!;
        var path = new[] { Pt(ip.Lat, ip.Lon, ip.X, ip.Y) }.Concat(out1.Behaviour.Waypoints!.Select(w => Pt(w.Lat, w.Lon, w.X, w.Y))).ToArray();
        static double SegDist((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
        {
            var dx = b.X - a.X; var dy = b.Y - a.Y; var l2 = dx * dx + dy * dy;
            var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
            return Math.Sqrt((p.X - a.X - t * dx) * (p.X - a.X - t * dx) + (p.Y - a.Y - t * dy) * (p.Y - a.Y - t * dy));
        }
        double RouteDist((double X, double Y) p) => Enumerable.Range(0, route.Length - 1).Min(i => SegDist(p, route[i], route[i + 1]));
        var min = double.MaxValue;
        for (var i = 0; i + 1 < path.Length; i++)
            for (var k = 0; k <= 100; k++)
                min = Math.Min(min, RouteDist((path[i].X + (path[i + 1].X - path[i].X) * k / 100.0, path[i].Y + (path[i + 1].Y - path[i].Y) * k / 100.0)));
        Assert.True(min >= 100, $"OUT1 航跡與自船航線最小距離 {min:F0} m");
        // 左舷側:對最近航段的右舷法線取負值
        foreach (var p in path)
        {
            var i = Enumerable.Range(0, route.Length - 1).MinBy(i => SegDist(p, route[i], route[i + 1]));
            var (a, b) = (route[i], route[i + 1]);
            var l = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            var side = (p.X - a.X) * (b.Y - a.Y) / l - (p.Y - a.Y) * (b.X - a.X) / l;
            Assert.True(side < -100, $"OUT1 點 ({p.X:F0},{p.Y:F0}) 應在自船航線左舷 ≥ 100 m,實際 {side:F0}");
        }
        Assert.Equal(0.05, sc.Assessment.MinCpaNm);
        var band = sc.Assessment.Bands.Single(b => b.Id == "minCpa");
        Assert.Equal(0.05, band.Warn);
        Assert.Equal(0.025, band.FailValue);
        Assert.Contains(sc.ParameterSetInfo!.Resolved, r => r.Key == "navigation.cpa.channel_nm" && r.Path == "assessment.minCpa_nm");
    }
}
