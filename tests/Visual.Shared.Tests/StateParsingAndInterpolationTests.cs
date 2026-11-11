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
        // 橫搖(state.roll;煙霧序列每幀帶示意橫搖)
        Assert.NotNull(s.Roll);
        Assert.Equal(SmokeSequence.SmokeRollPeriodS, s.Roll!.NaturalPeriodS);
        Assert.Equal(SmokeSequence.SmokeRollStaticHeelDeg, s.Roll.StaticHeelDeg);
    }

    [Fact]
    public void StateWithoutRollParsesWithNullRollAndZeroPose()
    {
        // 無 roll 的狀態幀(未啟用橫搖的情境、舊核心):Roll 為 null、姿態橫搖 0(視景畫面與未實作橫搖前相同)
        var json = ContractJson.Serialize(SmokeSequence.Frame(3) with { Roll = null });
        Assert.DoesNotContain("\"roll\"", json);
        var s = StateFrameParser.TryParseState(json)!;
        Assert.Null(s.Roll);
        var pose = StateInterpolator.PoseOf(s);
        Assert.Equal(0.0, pose.RollDeg);
        Assert.Equal(0.0, pose.RollRateDegps);
    }

    [Fact]
    public void RollBlockParsesAllFourFields()
    {
        var json = ContractJson.Serialize(SmokeSequence.Frame(0) with { Roll = new RollState { AngleDeg = -4.25, RateDegps = 1.5, NaturalPeriodS = 11.2, StaticHeelDeg = -1.0 } });
        var s = StateFrameParser.TryParseState(json)!;
        Assert.Equal(-4.25, s.Roll!.AngleDeg);
        Assert.Equal(1.5, s.Roll.RateDegps);
        Assert.Equal(11.2, s.Roll.NaturalPeriodS);
        Assert.Equal(-1.0, s.Roll.StaticHeelDeg);
        Assert.Equal(-4.25, StateInterpolator.PoseOf(s).RollDeg);
    }

    [Fact]
    public void StateWithoutWaveMotionParsesWithNullAndZeroPose()
    {
        // 無 waveMotion 的狀態幀(情境未啟用 ship.seakeeping、無浪、舊核心):WaveMotion 為 null、姿態升沉/縱搖 0(畫面與未實作前相同)
        var json = ContractJson.Serialize(SmokeSequence.Frame(3) with { WaveMotion = null });
        Assert.DoesNotContain("waveMotion", json);
        var s = StateFrameParser.TryParseState(json)!;
        Assert.Null(s.WaveMotion);
        Assert.NotNull(s.Roll); // 橫搖與浪一階運動各自獨立
        var pose = StateInterpolator.PoseOf(s);
        Assert.Equal(0.0, pose.HeaveM);
        Assert.Equal(0.0, pose.PitchDeg);
        Assert.Equal(0.0, pose.HeaveRateMps);
        Assert.Equal(0.0, pose.PitchRateDegps);
        // 明確的 null 視同省略
        var withNull = json.Insert(json.LastIndexOf('}'), ",\"waveMotion\":null");
        var n = StateFrameParser.TryParseState(withNull)!;
        Assert.Null(n.WaveMotion);
        Assert.Equal(0.0, StateInterpolator.PoseOf(n).HeaveM);
    }

    [Fact]
    public void WaveMotionBlockParsesContractFieldNames()
    {
        // 契約欄位名(state.schema.json waveMotion):heave_m 向上為正、pitch_deg 艏仰為正、heaveRate_mps、pitchRate_dps —
        // 以手寫 JSON 釘住線上格式(不靠 C# 屬性名來回序列化)
        var baseJson = ContractJson.Serialize(SmokeSequence.Frame(0) with { WaveMotion = null, Roll = null });
        var json = baseJson.Insert(baseJson.LastIndexOf('}'), ",\"waveMotion\":{\"heave_m\":-0.35,\"pitch_deg\":1.25,\"heaveRate_mps\":0.12,\"pitchRate_dps\":-0.3}");
        Assert.Equal(FrameKind.State, StateFrameParser.Classify(json));
        var s = StateFrameParser.TryParseState(json)!;
        Assert.NotNull(s.WaveMotion);
        Assert.Equal(-0.35, s.WaveMotion!.HeaveM);
        Assert.Equal(1.25, s.WaveMotion.PitchDeg);
        Assert.Equal(0.12, s.WaveMotion.HeaveRateMps);
        Assert.Equal(-0.3, s.WaveMotion.PitchRateDps);
        var pose = StateInterpolator.PoseOf(s);
        Assert.Equal(-0.35, pose.HeaveM);
        Assert.Equal(1.25, pose.PitchDeg);
        Assert.Equal(0.12, pose.HeaveRateMps);
        Assert.Equal(-0.3, pose.PitchRateDegps);
        Assert.Equal(0.0, pose.RollDeg); // 沒有 roll 也能有 waveMotion
        // 來回序列化後欄位名不變
        var round = ContractJson.Serialize(s);
        Assert.Contains("\"waveMotion\":{\"heave_m\":-0.35,\"pitch_deg\":1.25,\"heaveRate_mps\":0.12,\"pitchRate_dps\":-0.3}", round);
    }

    [Fact]
    public void MalformedWaveMotionRejectsTheFrameInsteadOfThrowing()
    {
        // 型別錯誤的 waveMotion 與其他區塊相同處理:整幀視為壞框(回傳 null、不丟例外;StateClient 記入 BadFrames),視景沿用前一幀
        var baseJson = ContractJson.Serialize(SmokeSequence.Frame(0) with { WaveMotion = null });
        foreach (var bad in new[] { "\"rough\"", "{\"heave_m\":\"abc\",\"pitch_deg\":0,\"heaveRate_mps\":0,\"pitchRate_dps\":0}", "[1,2]" })
        {
            var json = baseJson.Insert(baseJson.LastIndexOf('}'), ",\"waveMotion\":" + bad);
            Assert.Null(StateFrameParser.TryParseState(json));
        }
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

/// <summary>橫搖(state.roll)的插值與外推:角度走最短弧、角速度線性、無 roll 時為 0。</summary>
public class RollInterpolationTests
{
    private static OwnShipState Frame(double t, double? rollDeg, double rate = 0)
        => SmokeSequence.Frame(0) with
        {
            T = t,
            Tick = (long)(t * 50),
            Roll = rollDeg is { } a ? new RollState { AngleDeg = a, RateDegps = rate, NaturalPeriodS = 9, StaticHeelDeg = 0.5 } : null,
            RadarEnvironment = null,
        };

    [Fact]
    public void InterpolatesRollAngleAndRateBetweenFrames()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        it.Push(Frame(0.00, -5, 2), 100.00);
        it.Push(Frame(0.04, 5, 4), 100.04);
        it.Push(Frame(0.08, 7, 0), 100.08);
        var s = it.Sample(100.02)!;
        Assert.InRange(s.Own.RollDeg, -0.01, 0.01);
        Assert.InRange(s.Own.RollRateDegps, 2.99, 3.01);
        Assert.False(s.Extrapolating);
    }

    [Fact]
    public void RollLerpTakesShortestArcAndKeepsSign()
    {
        Assert.InRange(StateInterpolator.LerpAngle(-3, 3, 0.5), -0.01, 0.01);
        Assert.InRange(StateInterpolator.LerpAngle(-30, -20, 0.25), -27.51, -27.49);
        var wrapped = StateInterpolator.LerpAngle(170, -170, 0.5); // 不會經過 0
        Assert.True(Math.Abs(Math.Abs(wrapped) - 180) < 0.01);
    }

    [Fact]
    public void ExtrapolationAdvancesRollByRate()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0, MaxExtrapolationSeconds = 0.25 };
        it.Push(Frame(0.00, 1.0, 2.0), 100.00);
        it.Push(Frame(0.04, 1.08, 2.0), 100.04);
        var s = it.Sample(100.14)!; // 0.1 s 超前
        Assert.True(s.Extrapolating);
        Assert.InRange(s.Own.RollDeg, 1.08 + 0.2 - 0.01, 1.08 + 0.2 + 0.01);
        var p = StateInterpolator.Extrapolate(new Pose(0, 0, 0, 0, 0), 0, 1.0);
        Assert.Equal(0.0, p.RollDeg); // 無橫搖:不變
    }

    [Fact]
    public void FramesWithoutRollGiveZeroRollEverywhere()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        it.Push(Frame(0.00, null), 100.00);
        it.Push(Frame(0.04, null), 100.04);
        Assert.Equal(0.0, it.Sample(100.02)!.Own.RollDeg);
        Assert.Equal(0.0, it.Sample(100.10)!.Own.RollDeg);
        foreach (var (_, pose) in it.Sample(100.02)!.Targets) Assert.Equal(0.0, pose.RollDeg);
    }

    [Fact]
    public void SmokeRollIsBoundedSinusoidWithStaticHeel()
    {
        var a = SmokeSequence.SmokeRoll(0);
        Assert.Equal(SmokeSequence.SmokeRollStaticHeelDeg, a.AngleDeg, 9);
        var quarter = SmokeSequence.SmokeRoll(SmokeSequence.SmokeRollPeriodS / 4);
        Assert.InRange(quarter.AngleDeg, SmokeSequence.SmokeRollStaticHeelDeg + SmokeSequence.SmokeRollAmplitudeDeg - 1e-9, SmokeSequence.SmokeRollStaticHeelDeg + SmokeSequence.SmokeRollAmplitudeDeg + 1e-9);
        Assert.InRange(quarter.RateDegps, -1e-9, 1e-9);
        for (var k = 0; k < 250; k++) Assert.InRange(Math.Abs(SmokeSequence.Frame(k).Roll!.AngleDeg), 0, SmokeSequence.SmokeRollStaticHeelDeg + SmokeSequence.SmokeRollAmplitudeDeg + 1e-9);
    }
}

/// <summary>
/// 浪一階運動(state.waveMotion:升沉向上為正、縱搖艏仰為正;需求 R2)的插值與外推:升沉/縱搖與速率皆線性、
/// 以速率外推、無 waveMotion 時為 0(與 roll 相互獨立)。
/// </summary>
public class WaveMotionInterpolationTests
{
    private static OwnShipState Frame(double t, (double Heave, double Pitch, double HeaveRate, double PitchRate)? motion, double? rollDeg = null)
        => SmokeSequence.Frame(0) with
        {
            T = t,
            Tick = (long)(t * 50),
            Roll = rollDeg is { } a ? new RollState { AngleDeg = a, RateDegps = 0, NaturalPeriodS = 9, StaticHeelDeg = 0 } : null,
            WaveMotion = motion is { } m ? new WaveMotionState { HeaveM = m.Heave, PitchDeg = m.Pitch, HeaveRateMps = m.HeaveRate, PitchRateDps = m.PitchRate } : null,
            RadarEnvironment = null,
        };

    [Fact]
    public void InterpolatesHeaveAndPitchLinearlyBetweenFrames()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        it.Push(Frame(0.00, (-0.40, 1.0, 0.2, -0.5)), 100.00);
        it.Push(Frame(0.04, (0.20, -1.0, 0.6, 0.5)), 100.04);
        it.Push(Frame(0.08, (0.30, -1.2, 0.0, 0.0)), 100.08);
        var s = it.Sample(100.02)!; // 前兩幀的中點
        Assert.False(s.Extrapolating);
        Assert.Equal(-0.10, s.Own.HeaveM, 9);
        Assert.Equal(0.0, s.Own.PitchDeg, 9);
        Assert.Equal(0.4, s.Own.HeaveRateMps, 9);
        Assert.Equal(0.0, s.Own.PitchRateDegps, 9);
        var q = it.Sample(100.05)!; // 第二、三幀之間 1/4
        Assert.Equal(0.225, q.Own.HeaveM, 9);
        Assert.Equal(-1.05, q.Own.PitchDeg, 9);
        // 直接內插:f 夾在 0–1
        var a = new Pose(0, 0, 0, 0, 0, HeaveM: 1.0, PitchDeg: -2.0, HeaveRateMps: 0.5, PitchRateDegps: 1.0);
        var b = new Pose(0, 0, 0, 0, 0, HeaveM: 2.0, PitchDeg: 2.0, HeaveRateMps: -0.5, PitchRateDegps: 3.0);
        Assert.Equal(new Pose(0, 0, 0, 0, 0, HeaveM: 1.25, PitchDeg: -1.0, HeaveRateMps: 0.25, PitchRateDegps: 1.5), StateInterpolator.Lerp(a, b, 0.25));
        Assert.Equal(b, StateInterpolator.Lerp(a, b, 1.7));
    }

    [Fact]
    public void ExtrapolationAdvancesHeaveAndPitchByTheirRates()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0, MaxExtrapolationSeconds = 0.25 };
        it.Push(Frame(0.00, (0.10, 0.50, 0.5, -1.0)), 100.00);
        it.Push(Frame(0.04, (0.12, 0.46, 0.5, -1.0)), 100.04);
        var s = it.Sample(100.14)!; // 超前 0.1 s
        Assert.True(s.Extrapolating);
        Assert.Equal(0.12 + 0.5 * 0.1, s.Own.HeaveM, 6);
        Assert.Equal(0.46 - 1.0 * 0.1, s.Own.PitchDeg, 6);
        // 超過外推上限:停在上限(0.25 s)
        var stalled = it.Sample(101.00)!;
        Assert.True(stalled.Stalled);
        Assert.Equal(0.12 + 0.5 * 0.25, stalled.Own.HeaveM, 9);
        Assert.Equal(0.46 - 1.0 * 0.25, stalled.Own.PitchDeg, 9);
        // 無浪一階運動:外推不變
        var p = StateInterpolator.Extrapolate(new Pose(0, 0, 0, 0, 0), 0, 1.0);
        Assert.Equal(0.0, p.HeaveM);
        Assert.Equal(0.0, p.PitchDeg);
    }

    [Fact]
    public void FramesWithoutWaveMotionGiveZeroHeaveAndPitchEverywhere()
    {
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        it.Push(Frame(0.00, null, rollDeg: 3.0), 100.00);
        it.Push(Frame(0.04, null, rollDeg: 3.0), 100.04);
        foreach (var wall in new[] { 100.02, 100.10, 101.0 })
        {
            var s = it.Sample(wall)!;
            Assert.Equal(0.0, s.Own.HeaveM);
            Assert.Equal(0.0, s.Own.PitchDeg);
            Assert.Equal(3.0, s.Own.RollDeg, 9); // 橫搖不受影響
            Assert.Null(s.Latest.WaveMotion);
            foreach (var (_, pose) in s.Targets) { Assert.Equal(0.0, pose.HeaveM); Assert.Equal(0.0, pose.PitchDeg); }
        }
    }

    [Fact]
    public void WaveMotionAppearingOrDisappearingFadesOverOneFrameInterval()
    {
        // 練習中開浪/關浪:一側無 waveMotion 視為 0,於一個幀距內線性過渡(不跳動、不沿用舊值)
        var it = new StateInterpolator { DelaySeconds = 0.0 };
        it.Push(Frame(0.00, null), 100.00);
        it.Push(Frame(0.04, (0.40, -1.0, 0, 0)), 100.04);
        it.Push(Frame(0.08, null), 100.08);
        Assert.Equal(0.20, it.Sample(100.02)!.Own.HeaveM, 9);
        Assert.Equal(-0.50, it.Sample(100.02)!.Own.PitchDeg, 9);
        Assert.Equal(0.10, it.Sample(100.07)!.Own.HeaveM, 9);
        var end = it.Sample(100.08)!;
        Assert.Equal(0.0, end.Own.HeaveM, 9);
        Assert.Equal(0.0, end.Own.PitchDeg, 9);
        Assert.Null(end.Latest.WaveMotion);
    }

    [Fact]
    public void SmokeWaveMotionIsBoundedAndRatesAreDerivatives()
    {
        var a = SmokeSequence.SmokeWaveMotion(0);
        Assert.Equal(0.0, a.HeaveM, 12);
        Assert.Equal(SmokeSequence.SmokePitchAmplitudeDeg, a.PitchDeg, 12);
        const double h = 1e-5;
        foreach (var t in new[] { 0.3, 1.9, 4.2, 6.6 })
        {
            var m = SmokeSequence.SmokeWaveMotion(t);
            var (lo, hi) = (SmokeSequence.SmokeWaveMotion(t - h), SmokeSequence.SmokeWaveMotion(t + h));
            Assert.Equal((hi.HeaveM - lo.HeaveM) / (2 * h), m.HeaveRateMps, 6);
            Assert.Equal((hi.PitchDeg - lo.PitchDeg) / (2 * h), m.PitchRateDps, 6);
        }
        for (var k = 0; k < 250; k++)
        {
            var m = SmokeSequence.Frame(k).WaveMotion!;
            Assert.InRange(Math.Abs(m.HeaveM), 0, SmokeSequence.SmokeHeaveAmplitudeM + 1e-9);
            Assert.InRange(Math.Abs(m.PitchDeg), 0, SmokeSequence.SmokePitchAmplitudeDeg + 1e-9);
        }
        // 煙霧幀序列化後帶 waveMotion,解析回來一致(視景煙霧路徑)
        var s = StateFrameParser.TryParseState(ContractJson.Serialize(SmokeSequence.Frame(10)))!;
        Assert.Equal(SmokeSequence.SmokeWaveMotion(10 / SmokeSequence.Hz).HeaveM, s.WaveMotion!.HeaveM, 12);
    }
}

/// <summary>審查發現(visual):幀成批到達(啟動第一幀、網路停頓後 TCP 一次送達)時速率不得膨脹、顯示時間不得倒退。</summary>
public class StateInterpolatorBurstTests
{
    private static OwnShipState Frame(int k) => SmokeSequence.Frame(k) with { RadarEnvironment = null };

    [Fact]
    public void FourFramesAtSameWallThenOneLaterDoesNotRegress()
    {
        // 4 幀同牆鐘 0.12 + 第 5 幀於 0.17(每 0.05 s 一幀 → 真實速率 0.8)
        var it = new StateInterpolator();
        for (var k = 0; k < 4; k++) it.Push(Frame(k), 0.12);
        var first = it.Sample(0.12)!;
        Assert.InRange(it.Rate, 0.99, 1.01); // 牆鐘無資訊 → 沿用 1.0
        var prev = first.SimTime;
        var prevEast = first.Own.East;
        for (var k = 4; k < 16; k++)
        {
            var wall = 0.12 + (k - 3) * 0.05;
            it.Push(Frame(k), wall);
            Assert.InRange(it.Rate, 0.0, 1.05);
            var s = it.Sample(wall)!;
            Assert.True(s.SimTime >= prev - 1e-9, $"第 {k} 幀:顯示時間 {s.SimTime:F3} < 前次 {prev:F3}");
            Assert.True(s.Own.East >= prevEast - 1e-6, $"第 {k} 幀:自船東向倒退");
            Assert.False(s.Stalled);
            prev = s.SimTime; prevEast = s.Own.East;
        }
        Assert.InRange(it.Rate, 0.75, 0.85); // 成批幀離開環形緩衝後收斂到真實速率
    }

    [Fact]
    public void StallThenBurstOnLiveStreamKeepsDisplayTimeMonotonic()
    {
        // 25 Hz 正常 10 幀,120 ms 停頓後 3 幀於 0.52 同到;60 Hz 取樣
        var it = new StateInterpolator();
        for (var k = 0; k < 10; k++) it.Push(Frame(k), k * 0.04);
        var prev = double.NegativeInfinity;
        var regressions = 0;
        var fed = 10;
        for (var i = 0; i <= 60; i++)
        {
            var now = 0.36 + i / 60.0;
            if (now >= 0.52 && fed == 10) { for (; fed < 13; fed++) it.Push(Frame(fed), 0.52); }
            else if (now >= 0.56 && fed >= 13 && now >= 0.52 + (fed - 12) * 0.04) { it.Push(Frame(fed), now); fed++; }
            var s = it.Sample(now)!;
            if (s.SimTime < prev - 1e-9) regressions++;
            Assert.InRange(it.Rate, 0.8, 1.1); // 60 Hz 取樣的餵入量化造成 ±25% 抖動;舊程式在此為 1.5–3
            prev = s.SimTime;
        }
        Assert.Equal(0, regressions);
    }

    [Fact]
    public void RateStillTracksTimescaleAndFreezeWithRobustEstimate()
    {
        var it = new StateInterpolator();
        for (var k = 0; k < 8; k++) it.Push(Frame(k * 10), k * 0.04); // timescale 10
        Assert.InRange(it.Rate, 9.9, 10.1);
        var frozen = new StateInterpolator();
        for (var k = 0; k < 8; k++) frozen.Push(Frame(5), k * 0.04);
        Assert.Equal(0.0, frozen.Rate);
        // 到達抖動 ±25%:速率仍在 1 附近
        var jitter = new StateInterpolator();
        var wall = 0.0;
        for (var k = 0; k < 8; k++) { wall += k % 2 == 0 ? 0.03 : 0.05; jitter.Push(Frame(k), wall); }
        Assert.InRange(jitter.Rate, 0.85, 1.15);
    }
}
