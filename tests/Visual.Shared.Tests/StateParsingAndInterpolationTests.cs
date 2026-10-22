using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.Visual.Shared.Smoke;
using SimosaBRM.Visual.Shared.State;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

public class StateFrameParserTests
{
    [Fact]
    public void ClassifiesStateEventAssessmentAndGarbage()
    {
        var state = ContractJson.Serialize(SmokeSequence.Frame(0));
        Assert.Equal(FrameKind.State, StateFrameParser.Classify(state));
        Assert.Equal(FrameKind.Event, StateFrameParser.Classify("{\"type\":\"event\",\"event\":{\"kind\":\"collision\"}}"));
        Assert.Equal(FrameKind.Assessment, StateFrameParser.Classify("{\"type\":\"assessment\",\"final\":false}"));
        Assert.Equal(FrameKind.Unknown, StateFrameParser.Classify("not json"));
        Assert.Equal(FrameKind.Unknown, StateFrameParser.Classify("[1,2]"));
        Assert.Equal(FrameKind.Unknown, StateFrameParser.Classify("{\"foo\":1}"));
        Assert.Equal("collision", StateFrameParser.TryGetEventKind("{\"type\":\"event\",\"event\":{\"kind\":\"collision\"}}"));
    }

    [Fact]
    public void ParsesFullStateWithTargetsTugsAnchorsAndRadarEnvironment()
    {
        var json = ContractJson.Serialize(SmokeSequence.Frame(10, withRadarEnvironment: true));
        var s = StateFrameParser.TryParseState(json);
        Assert.NotNull(s);
        Assert.Equal("FSB1", s!.ShipId);
        Assert.Equal(4, s.Targets!.Count);
        Assert.Equal("T1", s.Targets[0].Id);
        Assert.Single(s.Tugs!);
        Assert.Equal(2, s.Anchors!.Count);
        Assert.NotNull(s.RadarEnvironment);
        Assert.Single(s.RadarEnvironment!.RainAreas);
        Assert.Equal(6.0, s.VisibilityNm);
    }

    [Fact]
    public void ParsesRecordedCoreFixtureFrames()
    {
        // 網頁夾具是核心實跑的紀錄(含 truth/sensors/alerts 等);每個 state 行都應能解析
        var fixture = Path.Combine(TestPaths.Root, "src", "Instruments.Web", "tests", "fixtures", "record-E05-core.jsonl");
        if (!File.Exists(fixture)) return;
        var parsed = 0;
        foreach (var line in File.ReadLines(fixture))
        {
            if (!line.Contains("\"state\"", StringComparison.Ordinal)) continue;
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("state", out var st)) continue;
            var s = StateFrameParser.TryParseState(st.GetRawText());
            Assert.NotNull(s);
            parsed++;
        }
        Assert.True(parsed > 0);
    }

    [Fact]
    public void BadStateReturnsNullInsteadOfThrowing()
    {
        Assert.Null(StateFrameParser.TryParseState("{\"t\":1,\"pos\":\"oops\"}"));
    }
}

public class OriginSolverTests
{
    [Theory]
    [InlineData(22.53, 120.24, 4321.0, -1234.5)]
    [InlineData(24.30, 120.47, -15000.0, 9000.0)]
    [InlineData(25.15, 121.74, 0.0, 0.0)]
    public void RecoversOriginFromPositionWithEnu(double lat0, double lon0, double x, double y)
    {
        var plane = new LocalTangentPlane(lat0, lon0);
        var (lat, lon) = plane.ToGeodetic(x, y);
        var solved = OriginSolver.FromPosition(new GeoPosition { Lat = lat, Lon = lon, X = x, Y = y });
        Assert.InRange(solved.OriginLat, lat0 - 1e-8, lat0 + 1e-8);
        Assert.InRange(solved.OriginLon, lon0 - 1e-8, lon0 + 1e-8);
        var (bx, by) = solved.ToLocal(lat, lon);
        Assert.InRange(bx, x - 0.01, x + 0.01);
        Assert.InRange(by, y - 0.01, y + 0.01);
    }
}

public class StateInterpolatorTests
{
    private static OwnShipState Frame(double t, double x, double y, double heading, double sog = 8, double cog = -1, double rot = 0, TargetState[]? targets = null)
        => SmokeSequence.Frame(0) with
        {
            T = t,
            Tick = (long)(t * 50),
            Pos = new GeoPosition { Lat = 22.5, Lon = 120.2, X = x, Y = y },
            Heading = heading,
            Cog = cog < 0 ? heading : cog,
            Sog = sog,
            Rot = rot,
            Targets = targets,
            RadarEnvironment = null,
        };

    [Fact]
    public void InterpolatesBetweenFramesWithBuffer()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        it.Push(Frame(0.00, 0, 0, 10), 100.00);
        it.Push(Frame(0.04, 4, 0, 20), 100.04);
        it.Push(Frame(0.08, 8, 0, 30), 100.08);
        var s = it.Sample(100.06)!;
        Assert.InRange(it.Rate, 0.99, 1.01);
        Assert.InRange(s.SimTime, 0.0599, 0.0601);
        Assert.InRange(s.Own.East, 5.99, 6.01);
        Assert.InRange(s.Own.HeadingDeg, 24.9, 25.1);
        Assert.False(s.Extrapolating);
        Assert.False(s.Stalled);
    }

    [Fact]
    public void HeadingInterpolationTakesShortestArcAcrossNorth()
    {
        var mid = StateInterpolator.LerpHeading(350, 10, 0.5);
        Assert.True(mid < 0.01 || mid > 359.99);
        Assert.InRange(StateInterpolator.LerpHeading(10, 350, 0.25), 4.99, 5.01);
    }

    [Fact]
    public void ExtrapolatesBeyondLatestFrameThenStalls()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0, MaxExtrapolationSeconds = 0.25 };
        // 10 kn 向東 = 5.144 m/s
        it.Push(Frame(0.00, 0, 0, 90, sog: 10), 100.00);
        it.Push(Frame(0.04, 0.2058, 0, 90, sog: 10), 100.04);
        var a = it.Sample(100.14)!; // 0.1 s 超前
        Assert.True(a.Extrapolating);
        Assert.False(a.Stalled);
        Assert.InRange(a.Own.East, 0.2058 + 0.5144 - 0.01, 0.2058 + 0.5144 + 0.01);
        var b = it.Sample(101.0)!; // 遠超上限:停在 0.25 s 的外推位置
        Assert.True(b.Stalled);
        Assert.InRange(b.Own.East, 0.2058 + 5.144 * 0.25 - 0.01, 0.2058 + 5.144 * 0.25 + 0.01);
    }

    [Fact]
    public void RotIsAppliedDuringExtrapolation()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0, MaxExtrapolationSeconds = 1.0 };
        it.Push(Frame(0.00, 0, 0, 0, sog: 0, rot: 60), 100.00); // 60°/min = 1°/s
        it.Push(Frame(0.04, 0, 0, 0.04, sog: 0, rot: 60), 100.04);
        var s = it.Sample(100.54)!;
        Assert.InRange(s.Own.HeadingDeg, 0.53, 0.55);
    }

    [Fact]
    public void FrozenStreamHasZeroRateAndHoldsPosition()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        for (var i = 0; i < 5; i++) it.Push(Frame(1.0, 100, 50, 45), 100.0 + i * 0.04);
        Assert.Equal(0.0, it.Rate);
        var s = it.Sample(100.5)!;
        Assert.Equal(100, s.Own.East);
        Assert.False(s.Stalled);
        Assert.False(s.Extrapolating);
    }

    [Fact]
    public void TimescaleTenIsTrackedByRate()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        for (var i = 0; i < 6; i++) it.Push(Frame(i * 0.4, i * 4, 0, 0), 100.0 + i * 0.04);
        Assert.InRange(it.Rate, 9.9, 10.1);
        var s = it.Sample(100.0 + 5 * 0.04 - 0.02)!; // 落在最後兩幀之間
        Assert.False(s.Extrapolating);
        Assert.InRange(s.Own.East, 17.9, 18.1);
    }

    [Fact]
    public void TimeRegressionResetsHistoryAndClearsRadarEnvironment()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        it.Push(SmokeSequence.Frame(0, withRadarEnvironment: true), 100.00);
        it.Push(SmokeSequence.Frame(1), 100.04);
        Assert.NotNull(it.Sample(100.04)!.RadarEnvironment);
        it.Push(SmokeSequence.Frame(0), 100.08); // 倒帶
        Assert.Equal(1, it.Resets);
        Assert.Equal(1, it.Count);
        Assert.Null(it.Sample(100.08)!.RadarEnvironment);
        it.Push(SmokeSequence.Frame(1, withRadarEnvironment: true), 100.12);
        Assert.NotNull(it.Sample(100.12)!.RadarEnvironment);
    }

    [Fact]
    public void LargeForwardJumpResetsHistory()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0, JumpThresholdSeconds = 5 };
        it.Push(Frame(0, 0, 0, 0), 100.0);
        it.Push(Frame(0.04, 1, 0, 0), 100.04);
        it.Push(Frame(60, 500, 0, 0), 100.08); // 快照還原
        Assert.Equal(1, it.Resets);
        Assert.Equal(500, it.Sample(100.08)!.Own.East);
    }

    [Fact]
    public void TargetsArePairedByIdAndUnpairedOnesUseLatest()
    {
        var t1a = SmokeSequence.Frame(0).Targets![0] with { Id = "A", Pos = new GeoPosition { Lat = 0, Lon = 0, X = 0, Y = 0 }, Heading = 0 };
        var t1b = t1a with { Pos = new GeoPosition { Lat = 0, Lon = 0, X = 10, Y = 0 }, Heading = 10 };
        var t2b = t1a with { Id = "B", Pos = new GeoPosition { Lat = 0, Lon = 0, X = 99, Y = 99 } };
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        it.Push(Frame(0.00, 0, 0, 0, targets: new[] { t1a }), 100.00);
        it.Push(Frame(0.04, 0, 0, 0, targets: new[] { t1b, t2b }), 100.04);
        var s = it.Sample(100.02)!;
        Assert.Equal(2, s.Targets.Count);
        var a = s.Targets.First(x => x.Target.Id == "A");
        Assert.InRange(a.Pose.East, 4.9, 5.1);
        Assert.InRange(a.Pose.HeadingDeg, 4.9, 5.1);
        var b = s.Targets.First(x => x.Target.Id == "B");
        Assert.Equal(99, b.Pose.East);
    }

    [Fact]
    public void SampleBeforeAnyFrameIsNull()
    {
        Assert.Null(new StateInterpolator().Sample(0));
    }

    [Fact]
    public void SmokeSequenceMovesOwnShipNorthEastAtEightKnots()
    {
        var a = SmokeSequence.Frame(0);
        var b = SmokeSequence.Frame(250); // 10 s
        var d = Math.Sqrt(Math.Pow(b.Pos.X - a.Pos.X, 2) + Math.Pow(b.Pos.Y - a.Pos.Y, 2));
        Assert.InRange(d, 41.0, 41.3); // 8 kn ≈ 4.115 m/s × 10 s
        Assert.True(b.Pos.X > a.Pos.X && b.Pos.Y > a.Pos.Y);
        Assert.Equal(4, b.Targets!.Count);
    }
}
