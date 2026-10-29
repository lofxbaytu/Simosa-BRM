using System.Globalization;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using SimosaBRM.SimCore.Traffic;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 時變流場(情境 environment.current.table | tidal;情境引擎設計第 1.5.2 節;需求 R3「隨時間變化的流場」):流場表向量內插(方向跨 360°)、
/// 潮流由潮高變化率決定漲/退/平潮、引擎每步刷新與 state.currentSource、常數流情境輸出與快照位元不變、setEnvironment current 覆寫後時變停止
/// 且倒帶可恢復、紀錄/重播一致、結構檢查、C#↔Python 跨語言夾具(Golden/current_*.csv,export_current_reference.py 產生)與 E06b 潮流情境。
/// </summary>
public class TidalStreamTests
{
    private readonly ITestOutputHelper _out;
    public TidalStreamTests(ITestOutputHelper output) => _out = output;

    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static string GoldenDirectory => Path.Combine(TestData.Root, "tests", "SimCore.Tests", "Golden");
    private const string E06Flood = "E06_taichung_channel_flood";
    private const string E06b = "E06b_tidal_stream";

    /// <summary>內嵌情境:<paramref name="current"/> 為 environment.current 之下的區段(四格縮排),<paramref name="tide"/> 為 environment 之下的 tide 區段。</summary>
    private static Scenario.Scenario Yaml(string current, string tide = "", string startTimeUtc = "")
        => ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_current",
            "seed: 7",
            startTimeUtc.Length > 0 ? $"startTimeUtc: \"{startTimeUtc}\"" : "",
            "ship: { id: FSB1, loading: ballast }",
            "origin: { lat: 23.80, lon: 120.05 }",
            "initial: { position: { x: 0, y: 0 }, heading: 0, speed: 8, telegraph: HAH }",
            "environment:",
            "  wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }",
            "  current:",
            "    set: 350",
            "    drift: 1",
            current,
            "  waterDepth: 30",
            tide,
        }) + "\n");

    private const string Table = "    table:\n      - { t: 0, speed_kn: 1, direction_deg: 350 }\n      - { t: 10, speed_kn: 1, direction_deg: 20 }\n      - { t: 30, speed_kn: 2, direction_deg: 90 }";
    private const string HarmonicTideYaml = "  tide:\n    harmonic:\n      meanLevel_m: 1\n      constituents:\n        - { name: T1, amplitude_m: 1, period_s: 120, phase_deg: -60 }";
    private const string Tidal = "    tidal:\n      flood: { speed_kn: 2.5, direction_deg: 85 }\n      ebb: { speed_kn: 3.0, direction_deg: 265 }\n      slackWindow_min: 0.1";

    private static double AngleDiffDeg(double a, double b) => Math.Abs(Units.WrapDeg180(a - b));

    // ------------------------------------------------------------------ 模型

    [Fact]
    public void TideRateIsPiecewiseForTablesAndAnalyticForHarmonics()
    {
        var table = new TideTable(new[] { (0.0, 1.0), (10.0, 2.0), (30.0, 1.0), (40.0, 1.0) });
        Assert.Equal(0.1, table.RateAt(0.0), 12);          // 首點取第一段
        Assert.Equal(0.1, table.RateAt(5.0), 12);
        Assert.Equal(0.1, table.RateAt(10.0), 12);         // 節點取左段(與 HeightAt 相同的段落規則)
        Assert.Equal(-0.05, table.RateAt(20.0), 12);
        Assert.Equal(0.0, table.RateAt(35.0), 12);         // 平段
        Assert.Equal(0.0, table.RateAt(-1.0));             // 範圍外
        Assert.Equal(0.0, table.RateAt(41.0));
        Assert.Equal(0.1, table.MaxRate, 12);
        Assert.Equal(TideModel.M2PeriodS, table.ReferencePeriodS);

        var h = new HarmonicTide(0.5, new[] { (1.0, 100.0, Math.PI / 2), (0.25, 50.0, 0.0) });
        foreach (var t in new[] { 0.0, 7.3, 25.0, 61.0 })
        {
            var fd = (h.HeightAt(t + 1e-4) - h.HeightAt(t - 1e-4)) / 2e-4;
            Assert.Equal(fd, h.RateAt(t), 6);
        }
        Assert.Equal(1.0 * 2 * Math.PI / 100 + 0.25 * 2 * Math.PI / 50, h.MaxRate, 12);
        Assert.Equal(100.0, h.ReferencePeriodS);          // 最大振幅分潮
        Assert.Equal(TideModel.M2PeriodS, new HarmonicTide(0, Array.Empty<(double, double, double)>()).ReferencePeriodS);
    }

    [Fact]
    public void CurrentTableInterpolatesVectorsAcross360()
    {
        var sc = Yaml(Table).Validate();
        Assert.Empty(sc.StructuralIssues());
        var model = Assert.IsType<CurrentTable>(CurrentModel.FromScenario(sc.Environment.Current, null, null));
        Assert.Equal("table", model.Source);
        // 350° → 020°:向量內插,中點方向 005°、流速 cos 15°(不繞 330° 的遠路、不經過 185°)
        var (set5, drift5) = model.At(5.0);
        Assert.Equal(5.0, Units.RadToDeg(set5), 9);
        Assert.Equal(Units.KnToMps(1.0) * Math.Cos(Units.DegToRad(15)), drift5, 12);
        var (set0, drift0) = model.At(-1.0);                // 範圍外取端點
        Assert.Equal(350.0, Units.RadToDeg(set0), 9);
        Assert.Equal(Units.KnToMps(1.0), drift0, 12);
        var (set20, drift20) = model.At(20.0);              // 020° 1 kn → 090° 2 kn 的中點
        var e = (Units.KnToMps(1) * Math.Sin(Units.DegToRad(20)) + Units.KnToMps(2)) / 2;
        var n = Units.KnToMps(1) * Math.Cos(Units.DegToRad(20)) / 2;
        Assert.Equal(Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(e, n))), Units.RadToDeg(set20), 9);
        Assert.Equal(Math.Sqrt(e * e + n * n), drift20, 12);
        Assert.Equal(Units.KnToMps(2.0), model.At(1e6).DriftMps, 12);
    }

    [Fact]
    public void TidalCurrentFollowsTideRateWithSlackWindow()
    {
        var tide = new HarmonicTide(1.0, new[] { (1.0, 120.0, Units.DegToRad(-60)) });   // 高潮 t = 20、低潮 t = 80
        var m = new TidalCurrent(tide, Units.DegToRad(85), Units.KnToMps(2.5), Units.DegToRad(265), Units.KnToMps(3.0), 6.0);
        Assert.Equal("tidal", m.Source);
        Assert.Equal(Math.Sin(Math.PI * 6.0 / 120.0), m.SlackThreshold, 12);
        // 漲潮(t < 20):流向 085°;中漲(t = −10 ≡ 110)比例 1 → 2.5 kn
        Assert.Equal(85.0, Units.RadToDeg(m.At(0).SetRad), 9);
        Assert.True(m.At(0).DriftMps > 0 && m.At(0).DriftMps < Units.KnToMps(2.5));
        Assert.Equal(Units.KnToMps(2.5), m.At(110).DriftMps, 9);
        // 高潮前後各 6 s 內平潮(流速 0;方向為當時相位)
        Assert.Equal(0.0, m.At(19.0).DriftMps);
        Assert.Equal(85.0, Units.RadToDeg(m.At(19.0).SetRad), 9);
        Assert.Equal(0.0, m.At(21.0).DriftMps);
        Assert.Equal(265.0, Units.RadToDeg(m.At(21.0).SetRad), 9);
        Assert.True(m.At(27.0).DriftMps > 0.0);
        // 中退(t = 50)比例 1 → 3.0 kn 265°
        Assert.Equal(Units.KnToMps(3.0), m.At(50).DriftMps, 9);
        Assert.Equal(265.0, Units.RadToDeg(m.At(50).SetRad), 9);
        // 比例 → 流速為連續的線性映射:(ratio − s)/(1 − s)
        var (scale, flood) = m.ScaleAt(10.0);
        var ratio = Math.Abs(tide.RateAt(10.0)) / tide.MaxRate;
        Assert.True(flood);
        Assert.Equal((ratio - m.SlackThreshold) / (1 - m.SlackThreshold), scale, 12);
        // 門檻:無窗口 0、窗口 ≥ 半週期 1(全程平潮)
        Assert.Equal(0.0, TidalCurrent.Threshold(0.0, 120.0));
        Assert.Equal(1.0, TidalCurrent.Threshold(60.0, 120.0));
        Assert.Equal(0.0, new TidalCurrent(tide, 0, 1, 0, 1, 3600.0).At(50).DriftMps);
    }

    // ------------------------------------------------------------------ 引擎

    [Fact]
    public void EngineRefreshesCurrentEveryStepAndOutputsSource()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(Table).Validate());
        Assert.NotNull(e.Environment.CurrentModel);
        Assert.True(e.Environment.CurrentModelActive);
        var s0 = e.BuildState();
        Assert.Equal("table", s0.CurrentSource);
        Assert.Equal(350.0, s0.Current.Set, 9);
        Assert.Equal(1.0, s0.Current.Drift, 9);
        Assert.Contains("\"currentSource\":\"table\"", ContractJson.Serialize(s0));
        TestData.RunTo(e, 5);
        var s5 = e.BuildState();
        Assert.Equal(5.0, s5.Current.Set, 9);
        Assert.Equal(Math.Cos(Units.DegToRad(15)), s5.Current.Drift, 9);
        var (set, drift) = e.Environment.CurrentModel!.At(e.Time);
        Assert.Equal(set, e.Environment.CurrentSetRad);
        Assert.Equal(drift, e.Environment.CurrentDriftMps);
        // 動力學取樣與 state 一致(對地速度含流)
        var sample = e.Environment.SampleAt(e.Motion.X, e.Motion.Y);
        Assert.Equal(drift * Math.Sin(set), sample.CurrentEastMps, 12);
        Assert.Equal(drift * Math.Cos(set), sample.CurrentNorthMps, 12);
        TestData.RunTo(e, 40);
        Assert.Equal(90.0, e.BuildState().Current.Set, 9);  // 範圍外取端點
        Assert.Equal(2.0, e.BuildState().Current.Drift, 9);
    }

    [Fact]
    public void ConstantCurrentScenariosAreBitwiseUnchanged()
    {
        var e = TestData.MinimalEngine();
        Assert.Null(e.Environment.CurrentModel);
        Assert.False(e.Environment.CurrentModelActive);
        Assert.Null(e.Environment.CurrentSource);
        TestData.RunTo(e, 3);
        var s = e.BuildState();
        Assert.Null(s.CurrentSource);
        var json = ContractJson.Serialize(s);
        Assert.DoesNotContain("currentSource", json);
        var snap = ContractJson.Serialize(e.CreateSnapshot());
        Assert.DoesNotContain("currentOverridden", snap);
        Assert.Null(e.CreateSnapshot().Environment.CurrentOverridden);
        // 既有情境(常數流 + setEnvironment 漸變)載入後亦無模型
        var e06 = new SimulationEngine(TestData.Fsb1(), ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", E06Flood + ".yaml")), Mmg);
        Assert.Null(e06.Environment.CurrentModel);
        Assert.Null(e06.BuildState().CurrentSource);
    }

    [Fact]
    public void SetEnvironmentCurrentStopsTheModelAndRewindRestoresIt()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(Table).Validate());
        var ev = TestData.Capture(e);
        TestData.RunTo(e, 5);
        var snap = e.TakeSnapshot();
        TestData.RunTo(e, 10);
        // 只改風:時變流場不受影響
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 10, windTrueDirDeg: 90));
        e.Step();
        Assert.True(e.Environment.CurrentModelActive);
        Assert.False(ev.Last(x => x.Kind == SimEventKinds.EnvironmentChange).Data!.Value.TryGetProperty("currentModelStopped", out _));
        // 覆寫流:時變停止,之後為常數(漸變自目前模型值起算)
        e.Enqueue(SimCommand.SetEnvironment(currentSetDeg: 100, currentDriftKn: 0.5, rampS: 2));
        e.Step();
        Assert.True(e.Environment.CurrentOverridden);
        Assert.False(e.Environment.CurrentModelActive);
        Assert.Null(e.BuildState().CurrentSource);
        Assert.DoesNotContain("currentSource", ContractJson.Serialize(e.BuildState()));
        Assert.True(ev.Last(x => x.Kind == SimEventKinds.EnvironmentChange).Data!.Value.GetProperty("currentModelStopped").GetBoolean());
        TestData.RunTo(e, 15);
        Assert.Contains(ev, x => x.Kind == SimEventKinds.EnvironmentRampDone);
        Assert.Equal(100.0, e.BuildState().Current.Set, 9);
        Assert.Equal(0.5, e.BuildState().Current.Drift, 9);
        TestData.RunTo(e, 25);
        Assert.Equal(100.0, e.BuildState().Current.Set, 9);
        Assert.Equal(0.5, e.BuildState().Current.Drift, 9);
        Assert.Equal(true, e.CreateSnapshot().Environment.CurrentOverridden);
        // 倒帶到覆寫前的快照:旗標還原、時變恢復並以 Tick 重算
        e.Restore(snap);
        Assert.False(e.Environment.CurrentOverridden);
        Assert.Equal("table", e.BuildState().CurrentSource);
        Assert.Equal(5.0, e.BuildState().Current.Set, 9);
        TestData.RunTo(e, 20);
        Assert.Equal(Units.RadToDeg(e.Environment.CurrentModel!.At(20.0).SetRad), e.BuildState().Current.Set, 9);
        // 回到情境初始:時變恢復
        e.Enqueue(SimCommand.SetEnvironment(currentSetDeg: 100, currentDriftKn: 0.5));
        e.Step();
        Assert.True(e.Environment.CurrentOverridden);
        e.Enqueue(new SimCommand { Type = SimCommandType.Reset });
        e.Step();
        Assert.False(e.Environment.CurrentOverridden);
        Assert.Equal("table", e.BuildState().CurrentSource);
    }

    [Fact]
    public void OverrideAndRewindAreRecordReplayConsistent()
    {
        var sc = Yaml(Table).Validate();
        var e = new SimulationEngine(TestData.Fsb1(), sc);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"current-override-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        void RunTo(double s) { var target = (long)Math.Round(s / e.Dt); while (e.Tick < target) e.Step(); }
        RunTo(6);
        e.Enqueue(SimCommand.SetEnvironment(currentSetDeg: 100, currentDriftKn: 0.5, rampS: 2));
        RunTo(12);
        e.Enqueue(new SimCommand { Type = SimCommandType.Restore });   // 倒帶到最近的自動快照(10 s;覆寫後)
        RunTo(20);
        e.Enqueue(new SimCommand { Type = SimCommandType.Reset });     // 回到初始:時變恢復
        RunTo(26);
        w.Close();
        try
        {
            var r = Replayer.Replay(path, TestData.Ship);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SnapshotRestoreRecomputesTidalCurrentFromTick()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(Tidal, HarmonicTideYaml).Validate());
        Assert.IsType<TidalCurrent>(e.Environment.CurrentModel);
        TestData.RunTo(e, 10);
        var snap = e.TakeSnapshot();
        TestData.RunTo(e, 50);
        var (hash, s50) = (e.StateHash, e.BuildState());
        Assert.Equal("tidal", s50.CurrentSource);
        Assert.Equal(265.0, s50.Current.Set, 9);
        Assert.Equal(3.0, s50.Current.Drift, 6);
        e.Restore(snap);
        Assert.Equal(85.0, e.BuildState().Current.Set, 9);
        TestData.RunTo(e, 50);
        Assert.Equal(hash, e.StateHash);
        Assert.Equal(s50.Current, e.BuildState().Current);
    }

    // ------------------------------------------------------------------ 結構檢查

    [Theory]
    [InlineData("    table:\n      - { t: 0, speed_kn: 1, direction_deg: 0 }", "", "", "至少需要 2 點")]
    [InlineData("    table:\n      - { t: 0, speed_kn: 1, direction_deg: 0 }\n      - { t: 10, speed_kn: 1, direction_deg: 0 }\n      - { t: 10, speed_kn: 1, direction_deg: 0 }", "", "", "嚴格遞增")]
    [InlineData("    table:\n      - { t: 0, speed_kn: 1, direction_deg: 0 }\n      - { speed_kn: 1, direction_deg: 0 }", "", "", "需要 t 或 utc")]
    [InlineData("    table:\n      - { t: 0, utc: \"2026-12-08T05:00:00Z\", speed_kn: 1, direction_deg: 0 }\n      - { t: 10, speed_kn: 1, direction_deg: 0 }", "", "2026-12-08T05:00:00Z", "只能擇一")]
    [InlineData("    table:\n      - { utc: \"2026-12-08T05:00:00Z\", speed_kn: 1, direction_deg: 0 }\n      - { utc: \"2026-12-08T05:10:00Z\", speed_kn: 1, direction_deg: 0 }", "", "", "startTimeUtc")]
    [InlineData("    table:\n      - { t: 0, speed_kn: -1, direction_deg: 0 }\n      - { t: 10, speed_kn: 1, direction_deg: 0 }", "", "", "speed_kn 不可為負")]
    [InlineData("    tidal:\n      flood: { speed_kn: 1, direction_deg: 0 }\n      ebb: { speed_kn: 1, direction_deg: 180 }", "", "", "需要 environment.tide")]
    [InlineData("    tidal:\n      flood: { speed_kn: 1, direction_deg: 0 }", HarmonicTideYaml, "", "需要 ebb")]
    [InlineData("    tidal:\n      flood: { speed_kn: -1, direction_deg: 0 }\n      ebb: { speed_kn: 1, direction_deg: 180 }", HarmonicTideYaml, "", "speed_kn 不可為負")]
    [InlineData("    tidal:\n      flood: { speed_kn: 1, direction_deg: 0 }\n      ebb: { speed_kn: 1, direction_deg: 180 }\n      slackWindow_min: -5", HarmonicTideYaml, "", "slackWindow_min 不可為負")]
    [InlineData("    table:\n      - { t: 0, speed_kn: 1, direction_deg: 0 }\n      - { t: 10, speed_kn: 1, direction_deg: 0 }\n    tidal:\n      flood: { speed_kn: 1, direction_deg: 0 }\n      ebb: { speed_kn: 1, direction_deg: 180 }", HarmonicTideYaml, "", "只能擇一")]
    public void StructuralIssuesRejectBadCurrent(string current, string tide, string startTimeUtc, string message)
    {
        var sc = Yaml(current, tide, startTimeUtc);
        var issues = sc.StructuralIssues().ToList();
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Error && i.Path.StartsWith("environment.current", StringComparison.Ordinal) && i.Message.Contains(message));
        Assert.Throws<InvalidDataException>(() => sc.Validate());
    }

    [Fact]
    public void ValidCurrentHasNoStructuralIssuesAndUtcPointsAreRelativeToStart()
    {
        Assert.Empty(Yaml(Table).StructuralIssues());
        Assert.Empty(Yaml(Tidal, HarmonicTideYaml).StructuralIssues());
        Assert.Empty(Yaml("").StructuralIssues());
        const string utcTable = "    table:\n      - { utc: \"2026-12-08T05:00:00Z\", speed_kn: 1, direction_deg: 0 }\n      - { utc: \"2026-12-08T05:00:20Z\", speed_kn: 3, direction_deg: 0 }\n      - { t: 40, speed_kn: 3, direction_deg: 0 }";
        var sc = Yaml(utcTable, startTimeUtc: "2026-12-08T05:00:00Z").Validate();
        Assert.Empty(sc.StructuralIssues());
        var e = new SimulationEngine(TestData.Fsb1(), sc);
        TestData.RunTo(e, 10);
        Assert.Equal(2.0, e.BuildState().Current.Drift, 9);
    }

    // ------------------------------------------------------------------ 跨語言

    /// <summary>Python export_current_reference.py 的流向/流速/東北分量(tick,t,set_deg,drift_kn,east_mps,north_mps,currentSource)與 C# 推進到同 tick 的 BuildState 一致(1e-9)。</summary>
    [Theory]
    [InlineData("current_table")]
    [InlineData("current_tidal")]
    public void GoldenCurrentMatchesPythonReference(string name)
    {
        var rows = File.ReadLines(Path.Combine(GoldenDirectory, name + ".csv")).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("tick", StringComparison.Ordinal))
            .Select(l => l.Split(',')).Select(c => (Tick: long.Parse(c[0], CultureInfo.InvariantCulture), T: double.Parse(c[1], CultureInfo.InvariantCulture),
                Set: double.Parse(c[2], CultureInfo.InvariantCulture), Drift: double.Parse(c[3], CultureInfo.InvariantCulture),
                East: double.Parse(c[4], CultureInfo.InvariantCulture), North: double.Parse(c[5], CultureInfo.InvariantCulture), Source: c[6])).ToList();
        Assert.True(rows.Count >= 10);
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, name + ".yaml"));
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var seenEbb = false;
        foreach (var r in rows)
        {
            while (e.Tick < r.Tick) e.Step();
            var s = e.BuildState();
            var sample = e.Environment.SampleAt(e.Motion.X, e.Motion.Y);
            Assert.Equal(r.T, s.T, 9);
            Assert.Equal(r.Source, s.CurrentSource);
            Assert.True(AngleDiffDeg(r.Set, s.Current.Set) <= 1e-9, $"{name} tick {r.Tick}: set Python {r.Set:R} vs C# {s.Current.Set:R}");
            Assert.True(Math.Abs(r.Drift - s.Current.Drift) <= 1e-9, $"{name} tick {r.Tick}: drift Python {r.Drift:R} vs C# {s.Current.Drift:R}");
            Assert.True(Math.Abs(r.East - sample.CurrentEastMps) <= 1e-9, $"{name} tick {r.Tick}: east Python {r.East:R} vs C# {sample.CurrentEastMps:R}");
            Assert.True(Math.Abs(r.North - sample.CurrentNorthMps) <= 1e-9, $"{name} tick {r.Tick}: north Python {r.North:R} vs C# {sample.CurrentNorthMps:R}");
            if (name == "current_tidal" && r.Tick >= 1275) { Assert.Equal(265.0, s.Current.Set, 9); seenEbb = true; }
        }
        Assert.Equal(name == "current_tidal", seenEbb);
    }

    // ------------------------------------------------------------------ E06b

    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));

    private static (SimulationEngine Engine, List<SimEvent> Events, string Path, List<(double T, OwnShipState State)> Samples) Run(string id, double endS)
    {
        var sc = Load(id);
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var events = TestData.Capture(e);
        var samples = new List<(double, OwnShipState)>();
        var path = Path.Combine(TestData.TestRecordsDirectory, $"{id}-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        for (var t = 1; t <= endS && !e.Ended; t++)
        {
            var target = (long)Math.Round(t / e.Dt);
            while (e.Tick < target && !e.Ended) e.Step();
            if (t % 60 == 0) samples.Add((e.Time, e.BuildState()));
        }
        w.Close();
        return (e, events, path, samples);
    }

    private static void AssertReplays(string path)
    {
        try
        {
            var r = Replayer.Replay(path, TestData.Ship, Mmg);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.True(r.SummaryMatches);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void E06b_SharesE06GeometryAndExpressesTidalStreamAsModel()
    {
        var f = Load(E06Flood);
        var b = Load(E06b);
        Assert.Equal(f.Route!.Waypoints.Select(w => (w.Lat, w.Lon)), b.Route!.Waypoints.Select(w => (w.Lat, w.Lon)));
        Assert.Equal(f.Zones.Select(z => (z.Id, z.Type, z.WaterDepthM)), b.Zones.Select(z => (z.Id, z.Type, z.WaterDepthM)));
        Assert.Equal(f.Banks!.Definitions!.Select(d => d.Id), b.Banks!.Definitions!.Select(d => d.Id));
        Assert.Equal(f.Assessment.Required.Select(r => r.Id), b.Assessment.Required.Select(r => r.Id));
        Assert.Equal(f.Assessment.Bands.Select(r => r.Id), b.Assessment.Bands.Select(r => r.Id));
        Assert.Equal(f.Assessment.HardFail.Select(r => r.Id), b.Assessment.HardFail.Select(r => r.Id));
        Assert.Equal(f.Targets.Select(t => t.Id), b.Targets.Select(t => t.Id));
        Assert.Null(f.Environment.Current.Tidal);
        var tidal = b.Environment.Current.Tidal!;
        Assert.Equal((85.0, 2.5), (tidal.Flood!.DirectionDeg, tidal.Flood.SpeedKn));
        Assert.Equal((265.0, 3.0), (tidal.Ebb!.DirectionDeg, tidal.Ebb.SpeedKn));
        Assert.Equal(20.0, tidal.SlackWindowMin);
        Assert.NotNull(b.Environment.Tide?.Table);
        Assert.DoesNotContain(b.Timeline.SelectMany(t => t.Actions), a => a.Kind == "setEnvironment");   // 覆寫會停止時變
        Assert.Contains(f.Timeline.SelectMany(t => t.Actions), a => a.Kind == "setEnvironment");
        Assert.Equal("tideTurning", b.Timeline.Single(t => t.Phase == "degraded").Id);
        Assert.Empty(b.StructuralIssues());
        Assert.Empty(ScenarioValidator.Check(b, TestData.Fsb1(), null).Where(i => i.Severity == ScenarioIssueSeverity.Error));
    }

    /// <summary>
    /// 無人操作(自動舵 132° 不修正):潮流模型自動由漲(085° 2.5 kn)經平潮轉為退(265° 3.0 kn),無 environmentChange/environmentRampDone 事件,
    /// state.currentSource = tidal、state.current 隨時間變化、state.tideHeight_m 為表內插值;T+8 degraded、導燈遮蔽 +90 s、emergency 依距離/時間後備;紀錄/重播一致。
    /// </summary>
    [Fact]
    public void E06b_NoOperatorTidalStreamTurnsFromFloodToEbb()
    {
        var (e, ev, path, samples) = Run(E06b, 1800);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        foreach (var x in ev.Where(x => x.Kind is not (SimEventKinds.Message or SimEventKinds.VoiceScript)))
            _out.WriteLine($"{x.T:F1} {x.Source}/{x.Kind} {x.ItemId} {x.TargetId} {x.Message}");
        foreach (var (t, st) in samples)
            _out.WriteLine($"t={t:F0} current {st.Current.Set:F1}°/{st.Current.Drift:F2} kn src={st.CurrentSource} tide={st.TideHeightM:F3} xtd={st.Route?.XtdM:F0}");
        _out.WriteLine($"end t={e.Time} score={s.Assessment.Score} passed={s.Assessment.Passed} hardFails={string.Join(",", s.Assessment.HardFails.Select(h => h.Id))}");

        Assert.DoesNotContain(ev, x => x.Kind is SimEventKinds.EnvironmentChange or SimEventKinds.EnvironmentRampDone);
        Assert.All(samples, x => Assert.Equal("tidal", x.State.CurrentSource));
        OwnShipState At(double t) => samples.Single(x => Math.Abs(x.T - t) < 1e-6).State;
        Assert.Equal(85.0, At(300).Current.Set, 9);
        Assert.Equal(2.5, At(300).Current.Drift, 6);
        Assert.Equal(85.0, At(540).Current.Set, 9);
        Assert.InRange(At(540).Current.Drift, 0.8, 1.0);                 // T+8–11 漲潮減弱
        Assert.Equal(0.0, At(720).Current.Drift);                         // T+11–13 平潮
        Assert.Equal(265.0, At(900).Current.Set, 9);
        Assert.InRange(At(900).Current.Drift, 1.0, 1.2);                 // T+13–16 退潮開始
        Assert.Equal(265.0, At(1200).Current.Set, 9);
        Assert.Equal(3.0, At(1200).Current.Drift, 6);                     // T+16 起退潮全速
        Assert.Equal(4.222, At(720).TideHeightM!.Value, 9);
        Assert.Equal(14.0 + At(720).TideHeightM!.Value, At(720).WaterDepth!.Value, 9);
        var phases = ev.Where(x => x.Kind == SimEventKinds.PhaseChanged).Select(x => (x.T, x.DataString("to"))).ToList();
        Assert.Equal((480.0, "degraded"), phases[0]);
        Assert.Equal("emergency", phases[1].Item2);
        Assert.Equal(570.0, ev.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "leadingLightsObscured").T);
        Assert.Equal(phases[1].T, ev.Single(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "OUT1").T);
        Assert.Equal(662.0, phases[1].T);                                 // 距口門 0.8 nm(早於 degraded 後 300 s 的時間後備)
        Assert.Equal(999.0, ev.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "insideBreakwater").T);
        foreach (var r in new[] { "handoverChecklist", "leadingLightReport", "outboundResponse" })
            Assert.Equal("missed", s.Assessment.Required.Single(x => x.Id == r).State);
        // 漲潮把船壓向北側(XTD 約 −260 m),轉流後退潮把船壓回南側越過走廊:不擱淺、時間到結束(與 E06 漲潮版無人操作擱淺不同)
        Assert.DoesNotContain(ev, x => x.Kind == SimEventKinds.Grounding);
        Assert.Equal("timeUp", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.True(samples.Single(x => Math.Abs(x.T - 480) < 1e-6).State.Route!.XtdM < -200);
        Assert.True(samples.Single(x => Math.Abs(x.T - 1200) < 1e-6).State.Route!.XtdM > 200);
        Assert.True(e.Ended);
        AssertReplays(path);
    }
}
