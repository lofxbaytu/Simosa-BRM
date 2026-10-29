using System.Globalization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 潮汐模型(情境 environment.tide;情境引擎設計第 1.5.1 節):潮高表線性內插/端點、調和潮公式、有效水深 = 基底(含 zones 覆寫與
/// setEnvironment 漸變)+ 潮高(t)、state.tideHeight_m、無 tide 時輸出不變、快照還原後潮高由 Tick 重算、結構檢查、
/// C#↔Python 跨語言夾具(Golden/tide_*.csv,export_tide_reference.py 產生)與 E18b 潮高表情境的紀錄/重播一致。
/// </summary>
public class TideTests
{
    private readonly ITestOutputHelper _out;
    public TideTests(ITestOutputHelper output) => _out = output;

    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static string GoldenDirectory => Path.Combine(TestData.Root, "tests", "SimCore.Tests", "Golden");
    private const string E18b = "E18b_tide_table";

    /// <summary>內嵌情境:<paramref name="tide"/> 為 environment 之下的 tide 區段(兩格縮排),<paramref name="extra"/> 為頂層附加區段。</summary>
    private static Scenario.Scenario Yaml(string tide, string extra = "", string startTimeUtc = "", double waterDepth = 30.0)
        => ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_tide",
            "seed: 7",
            startTimeUtc.Length > 0 ? $"startTimeUtc: \"{startTimeUtc}\"" : "",
            "ship: { id: FSB1, loading: ballast }",
            "origin: { lat: 23.80, lon: 120.05 }",
            "initial: { position: { x: 0, y: 0 }, heading: 0, speed: 8, telegraph: HAH }",
            "environment:",
            "  wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }",
            "  current: { set: 0, drift: 0 }",
            $"  waterDepth: {waterDepth.ToString(CultureInfo.InvariantCulture)}",
            tide,
            extra,
        }) + "\n");

    private const string TableTide = "  tide:\n    table:\n      - { t: 0, height_m: 1 }\n      - { t: 10, height_m: 3 }\n      - { t: 30, height_m: 2 }";

    [Fact]
    public void TableInterpolatesLinearlyAndClampsEnds()
    {
        var t = new TideTable(new[] { (0.0, 1.0), (10.0, 2.0), (30.0, 1.0) });
        Assert.Equal(1.0, t.HeightAt(-5));
        Assert.Equal(1.0, t.HeightAt(0));
        Assert.Equal(1.5, t.HeightAt(5), 12);
        Assert.Equal(2.0, t.HeightAt(10));
        Assert.Equal(1.5, t.HeightAt(20), 12);
        Assert.Equal(1.25, t.HeightAt(25), 12);
        Assert.Equal(1.0, t.HeightAt(30));
        Assert.Equal(1.0, t.HeightAt(1e6));
    }

    [Fact]
    public void HarmonicMatchesFormula()
    {
        var h = new HarmonicTide(0.5, new[] { (1.0, 100.0, Math.PI / 2), (0.25, 50.0, 0.0) });
        // t = 25:cos(π/2 + π/2) = −1;cos(π) = −1
        Assert.Equal(0.5 - 1.0 - 0.25, h.HeightAt(25), 12);
        Assert.Equal(0.5 + 1.0 * Math.Cos(Math.PI / 2) + 0.25, h.HeightAt(0), 12);
    }

    [Fact]
    public void EngineAddsTideToBaseAndZoneDepthAndSetEnvironmentChangesBaseOnly()
    {
        const string zone = "zones:\n  - id: shoal\n    type: ukcPolicy\n    waterDepth_m: 20\n    polygon: [ { x: -200, y: -200 }, { x: 200, y: -200 }, { x: 200, y: 200 }, { x: -200, y: 200 } ]";
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(TableTide, zone).Validate());
        Assert.NotNull(e.Environment.Tide);
        var s0 = e.BuildState();
        Assert.Equal(1.0, s0.TideHeightM);
        Assert.Equal(21.0, s0.WaterDepth);                      // 區域覆寫 20 + 潮高 1
        TestData.RunTo(e, 5);
        var s5 = e.BuildState();
        Assert.Equal(2.0, s5.TideHeightM!.Value, 12);
        Assert.Equal(22.0, s5.WaterDepth!.Value, 12);
        Assert.Equal(e.Environment.Tide!.HeightAt(e.Time), e.Environment.TideHeightM);
        // setEnvironment waterDepth 改基底(海圖水深),潮高另加:基底 15 < 區域 20 → 有效 15 + 潮高
        e.Enqueue(SimCommand.SetEnvironment(waterDepth: 15));
        e.ProcessPendingCommands();
        e.Step();
        var s = e.BuildState();
        Assert.Equal(15.0, ((ZoneDepth)e.Environment.Depth).Base.Depth);
        Assert.Equal(15.0 + e.Environment.TideHeightM, s.WaterDepth!.Value, 12);
        Assert.Equal(s.WaterDepth!.Value - TestData.Fsb1().GetLoading(LoadingCondition.Ballast).MaxDraft_m - s.Squat!.Value, s.Truth!.DepthBelowKeel, 9);
        // t ≥ 30 之後取端點值 2 m
        TestData.RunTo(e, 40);
        Assert.Equal(2.0, e.BuildState().TideHeightM!.Value, 12);
    }

    [Fact]
    public void WithoutTideOutputsAreUnchanged()
    {
        var e = TestData.MinimalEngine();
        Assert.Null(e.Environment.Tide);
        TestData.RunTo(e, 3);
        var s = e.BuildState();
        Assert.Null(s.TideHeightM);
        Assert.Equal(30.0, s.WaterDepth);
        Assert.Equal(0.0, e.Environment.TideHeightM);
        Assert.DoesNotContain("tideHeight_m", ContractJson.Serialize(s));
    }

    [Fact]
    public void UtcTablePointsAreRelativeToStartTimeUtc()
    {
        const string tide = "  tide:\n    table:\n      - { utc: \"2026-11-04T05:00:00Z\", height_m: 1 }\n      - { utc: \"2026-11-04T05:00:20Z\", height_m: 3 }\n      - { t: 40, height_m: 3 }";
        var sc = Yaml(tide, startTimeUtc: "2026-11-04T05:00:00Z").Validate();
        Assert.Empty(sc.StructuralIssues());
        var e = new SimulationEngine(TestData.Fsb1(), sc);
        TestData.RunTo(e, 10);
        Assert.Equal(2.0, e.BuildState().TideHeightM!.Value, 12);
        Assert.Equal(32.0, e.BuildState().WaterDepth!.Value, 12);
    }

    [Theory]
    [InlineData("  tide:\n    table:\n      - { t: 0, height_m: 1 }", "", "至少需要 2 點")]
    [InlineData("  tide:\n    table:\n      - { t: 0, height_m: 1 }\n      - { t: 10, height_m: 2 }\n      - { t: 10, height_m: 3 }", "", "嚴格遞增")]
    [InlineData("  tide:\n    table:\n      - { t: 0, height_m: 1 }\n      - { height_m: 2 }", "", "需要 t 或 utc")]
    [InlineData("  tide:\n    table:\n      - { t: 0, utc: \"2026-11-04T05:00:00Z\", height_m: 1 }\n      - { t: 10, height_m: 2 }", "2026-11-04T05:00:00Z", "只能擇一")]
    [InlineData("  tide:\n    table:\n      - { utc: \"2026-11-04T05:00:00Z\", height_m: 1 }\n      - { utc: \"2026-11-04T05:10:00Z\", height_m: 2 }", "", "startTimeUtc")]
    [InlineData("  tide:\n    harmonic:\n      meanLevel_m: 0\n      constituents:\n        - { amplitude_m: 1, period_s: 0 }", "", "period_s 必須為正")]
    [InlineData("  tide:\n    harmonic:\n      meanLevel_m: 0\n      constituents:\n        - { amplitude_m: -1, period_s: 100 }", "", "不可為負")]
    [InlineData("  tide:\n    harmonic:\n      meanLevel_m: 0\n      constituents: []", "", "至少需要 1 個分潮")]
    [InlineData("  tide:\n    table:\n      - { t: 0, height_m: 1 }\n      - { t: 10, height_m: 2 }\n    harmonic:\n      constituents:\n        - { amplitude_m: 1, period_s: 100 }", "", "擇一")]
    [InlineData("  tide: {}", "", "擇一")]
    public void StructuralIssuesRejectBadTide(string tide, string startTimeUtc, string message)
    {
        var sc = Yaml(tide, startTimeUtc: startTimeUtc);
        var issues = sc.StructuralIssues().ToList();
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Error && i.Path.StartsWith("environment.tide", StringComparison.Ordinal) && i.Message.Contains(message));
        Assert.Throws<InvalidDataException>(() => sc.Validate());
    }

    [Fact]
    public void ValidTideHasNoStructuralIssuesAndValidatorUsesTideForInitialUkc()
    {
        Assert.Empty(Yaml(TableTide).StructuralIssues());
        // 基底 4 m + 潮高(0) 2 m = 6 m > FSB1 壓載最大吃水(5.41 m):只看基底會誤判初始擱淺
        const string tide = "  tide:\n    table:\n      - { t: 0, height_m: 2 }\n      - { t: 600, height_m: 0 }";
        var sc = Yaml(tide, waterDepth: 4.0).Validate();
        var issues = ScenarioValidator.Check(sc, TestData.Fsb1(), null);
        Assert.DoesNotContain(issues, i => i.Message.Contains("初始 UKC"));
        var e = new SimulationEngine(TestData.Fsb1(), sc);
        Assert.False(e.Aground);
        Assert.Equal(6.0, e.BuildState().WaterDepth);
    }

    [Fact]
    public void SnapshotRestoreRecomputesTideFromTick()
    {
        const string tide = "  tide:\n    harmonic:\n      meanLevel_m: 0.5\n      constituents:\n        - { name: M2, amplitude_m: 1.5, period_s: 60, phase_deg: 30 }";
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(tide).Validate());
        TestData.RunTo(e, 10);
        var snap = e.TakeSnapshot();
        TestData.RunTo(e, 20);
        var (hash, s20) = (e.StateHash, e.BuildState());
        e.Restore(snap);
        Assert.Equal(e.Environment.Tide!.HeightAt(10.0), e.Environment.TideHeightM, 12);
        TestData.RunTo(e, 20);
        Assert.Equal(hash, e.StateHash);
        var r20 = e.BuildState();
        Assert.Equal(s20.TideHeightM, r20.TideHeightM);
        Assert.Equal(s20.WaterDepth, r20.WaterDepth);
        Assert.Equal(e.Environment.Tide.HeightAt(20.0), r20.TideHeightM!.Value, 12);
    }

    /// <summary>跨語言:Python export_tide_reference.py 的潮高與有效水深(tick,t,tideHeight_m,waterDepth_m)與 C# 引擎推進到同 tick 的 BuildState 一致(1e-9)。</summary>
    [Theory]
    [InlineData("tide_table")]
    [InlineData("tide_harmonic")]
    public void GoldenTideMatchesPythonReference(string name)
    {
        var rows = File.ReadLines(Path.Combine(GoldenDirectory, name + ".csv")).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("tick", StringComparison.Ordinal))
            .Select(l => l.Split(',')).Select(c => (Tick: long.Parse(c[0], CultureInfo.InvariantCulture), T: double.Parse(c[1], CultureInfo.InvariantCulture),
                Tide: double.Parse(c[2], CultureInfo.InvariantCulture), Depth: double.Parse(c[3], CultureInfo.InvariantCulture))).ToList();
        Assert.True(rows.Count >= 10);
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, name + ".yaml"));
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        foreach (var r in rows)
        {
            while (e.Tick < r.Tick) e.Step();
            var s = e.BuildState();
            Assert.Equal(r.T, s.T, 9);
            Assert.True(Math.Abs(r.Tide - s.TideHeightM!.Value) <= 1e-9, $"{name} tick {r.Tick}: tide Python {r.Tide:R} vs C# {s.TideHeightM:R}");
            Assert.True(Math.Abs(r.Depth - s.WaterDepth!.Value) <= 1e-9, $"{name} tick {r.Tick}: depth Python {r.Depth:R} vs C# {s.WaterDepth:R}");
        }
    }

    // ------------------------------------------------------------------ E18b

    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));

    private static (SimulationEngine Engine, List<SimEvent> Events, string Path) Run(string id, double endS)
    {
        var sc = Load(id);
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var events = TestData.Capture(e);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"{id}-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        for (var t = 1; t <= endS && !e.Ended; t++)
        {
            var target = (long)Math.Round(t / e.Dt);
            while (e.Tick < target && !e.Ended) e.Step();
        }
        w.Close();
        return (e, events, path);
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
    public void E18b_SharesE18GeometryAndExpressesTideAsTable()
    {
        var e18 = Load("E18_ukc_squat_anping");
        var e18b = Load(E18b);
        Assert.Equal(e18.Route!.Waypoints.Select(w => (w.Lat, w.Lon)), e18b.Route!.Waypoints.Select(w => (w.Lat, w.Lon)));
        Assert.Equal(e18.Zones.Select(z => (z.Id, z.Type, z.WaterDepthM)), e18b.Zones.Select(z => (z.Id, z.Type, z.WaterDepthM)));
        Assert.Equal(e18.Timeline.Select(t => t.Id), e18b.Timeline.Select(t => t.Id));
        Assert.Equal(7.0, e18b.Environment.WaterDepth);                                  // 海圖水深,潮高另加
        Assert.Null(e18.Environment.Tide);
        var table = e18b.Environment.Tide!.Table!;
        Assert.Equal(new[] { 1.9, 1.85, 1.6, 1.5, 1.4, 1.2 }, table.Select(p => p.HeightM));
        Assert.DoesNotContain(e18b.Timeline.SelectMany(t => t.Actions), a => a.Kind == "setEnvironment");
        Assert.Empty(ScenarioValidator.Check(e18b, TestData.Fsb2(), null).Where(i => i.Severity == ScenarioIssueSeverity.Error));
    }

    /// <summary>
    /// 無人操作(保持 HAH):潮高表驅動有效水深(無 environmentChange/environmentRampDone 事件),T+5 degraded、通過 BAR emergency、
    /// 淺水警報、UKC 低於政策、到達迴船池;state.tideHeight_m = 表內插值、waterDepth = 7.0 + 潮高;紀錄/重播一致。
    /// </summary>
    [Fact]
    public void E18b_NoOperatorTideTableDrivesUkc()
    {
        var (e, ev, path) = Run(E18b, 3300);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        foreach (var x in ev.Where(x => x.Kind is not (SimEventKinds.Message or SimEventKinds.VoiceScript)))
            _out.WriteLine($"{x.T:F1} {x.Source}/{x.Kind} {x.ItemId} {x.Message}");
        _out.WriteLine($"ukc min={s.Ukc.MinM:F2} at={s.Ukc.MinAtS} squat={s.Ukc.MinSquatM} below={s.Ukc.TimeBelowLimitS} score={s.Assessment.Score} passed={s.Assessment.Passed} end t={e.Time}");
        foreach (var b in s.Assessment.Bands) _out.WriteLine($"band {b.Id}: {b.State} value={b.Value}");
        Assert.DoesNotContain(ev, x => x.Kind is SimEventKinds.EnvironmentRampDone or SimEventKinds.EnvironmentChange);
        var phases = ev.Where(x => x.Kind == SimEventKinds.PhaseChanged).Select(x => (x.T, x.DataString("to"))).ToList();
        Assert.Equal((300.0, "degraded"), phases[0]);
        Assert.Equal("emergency", phases[1].Item2);
        Assert.Equal(ev.Where(x => x.Kind == SimEventKinds.CheckpointPassed).Select(x => x.T).ElementAt(1), phases[1].T);  // 通過 BAR
        Assert.Contains(ev, x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "shallowWater");
        Assert.Contains(ev, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "ukcBelowPolicy");
        Assert.Equal("arrived", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.DoesNotContain(ev, x => x.Kind == SimEventKinds.Grounding);
        var tide = e.Environment.Tide!;
        var st = e.BuildState();
        Assert.Equal(tide.HeightAt(e.Time), st.TideHeightM!.Value, 12);
        Assert.Equal(7.0 + st.TideHeightM.Value, st.WaterDepth!.Value, 12);
        Assert.InRange(st.TideHeightM.Value, 1.2, 1.4);
        Assert.Equal(1.4 - 0.2 * (e.Time - 2100.0) / 1200.0, st.TideHeightM.Value, 9);
        Assert.InRange(s.Ukc.MinM!.Value, 0.0, 0.72);
        Assert.False(s.Assessment.Passed);
        AssertReplays(path);
    }
}
