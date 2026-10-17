using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using Xunit.Abstractions;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 操縱量測 ManoeuvreTracker(docs/design/ship-familiarisation.md 第 2 節):對 FSB1 滿載 MMG 的 35° 迴旋、10/10 與 20/20 Z 形、緊急停船、慣性停船、
/// 艏側推迴轉,追蹤器(每秒取樣 + 內插)的量測值與 MmgGoldenTests 使用的逐步(50 Hz)量測一致,並與 trial_targets.json 比較(資料品質 trial、
/// 公差與黃金測試相同;已知偏差項目標記 withinTolerance = false)。另測啟用條件、穩定直航條件、舵機擺動、MOB 旋迴分類、快照還原與 bands 指標。
/// </summary>
public class ManoeuvreTrackerTests
{
    private readonly ITestOutputHelper _out;
    public ManoeuvreTrackerTests(ITestOutputHelper output) => _out = output;

    /// <summary>以必要事件引用 manoeuvreMeasured 啟用量測(物理與黃金測試相同:深水、無風流、航向北)。</summary>
    private static Scenario.Scenario Enabled(Scenario.Scenario sc)
    {
        sc.Assessment.Required.Add(new RequiredEvent
        {
            Id = "measured", After = new AfterSpec { Event = ManoeuvreEventKinds.Started }, Expect = new ExpectSpec { Event = new EventRef { Kind = ManoeuvreEventKinds.Measured } }, WithinS = 7200,
        });
        return sc;
    }

    private static (SimulationEngine Engine, List<SimEvent> Events) Engine(double speedKn, double? rpm = null, string? telegraph = null)
    {
        var e = MmgEngine(Enabled(MmgScenario(speedKn, rpm, telegraph)));
        return (e, TestData.Capture(e));
    }

    private static ManoeuvreRecord RunUntilMeasured(SimulationEngine e, List<SimEvent> events, double maxS, Action<SimulationEngine>? perStep = null)
    {
        var steps = (int)Math.Round(maxS / Dt);
        for (var i = 0; i < steps; i++)
        {
            perStep?.Invoke(e);
            e.Step();
            if (events.Any(x => x.Kind == ManoeuvreEventKinds.Measured)) break;
        }
        var rec = e.Assessment!.Manoeuvres.State.Records.LastOrDefault();
        Assert.NotNull(rec);
        Assert.Equal("measured", rec!.State);
        return rec;
    }

    private void Dump(ManoeuvreRecord r)
    {
        _out.WriteLine($"{r.Id} complete={r.Complete} quality={r.DataQuality}");
        foreach (var (k, v) in r.Values) _out.WriteLine($"  {k} = {v}");
        foreach (var c in r.Comparisons) _out.WriteLine($"  cmp {c.Key}: {c.Measured} vs {c.Target} ±{c.Tolerance:F1} within={c.WithinTolerance} comparable={c.Comparable} {c.DataQuality} {c.Source}");
        foreach (var n in r.Notes) _out.WriteLine($"  note {n}");
    }

    // ------------------------------------------------------------------ 迴旋

    [Theory]
    [InlineData("port")]
    [InlineData("starboard")]
    public void Turning35_MatchesGoldenAndComparesWithTrial(string side)
    {
        var sign = side == "starboard" ? 1.0 : -1.0;
        var golden = TurningCircle(35.0, side, 14.5);
        var (e, events) = Engine(14.5);
        e.Enqueue(SimCommand.Rudder(sign * 35.0));
        var r = RunUntilMeasured(e, events, 1200);
        Dump(r);
        _out.WriteLine($"golden advance {golden.AdvanceM:F2} transfer {golden.TransferM:F2} TD {golden.TacticalDiameterM:F2} steadyD {golden.SteadyDiameterM:F2} steadyV {golden.SteadySpeedKn:F3}");
        Assert.Equal($"turning35.{side}", r.Id);
        Assert.True(r.Complete);
        Assert.True(r.Approach.Steady && r.Approach.FromInitial);
        Assert.InRange(r.Values["advance_m"], golden.AdvanceM - 0.5, golden.AdvanceM + 0.5);
        Assert.InRange(r.Values["transfer_m"], golden.TransferM - 0.5, golden.TransferM + 0.5);
        Assert.InRange(r.Values["tacticalDiameter_m"], golden.TacticalDiameterM - 0.5, golden.TacticalDiameterM + 0.5);
        Assert.InRange(r.Values["timeTo90_s"], golden.Marks[90].T - 0.1, golden.Marks[90].T + 0.1);
        Assert.InRange(r.Values["timeTo180_s"], golden.Marks[180].T - 0.1, golden.Marks[180].T + 0.1);
        Assert.InRange(r.Values["steadyDiameter_m"], golden.SteadyDiameterM * 0.97, golden.SteadyDiameterM * 1.03);
        Assert.InRange(r.Values["steadySpeed_kn"], golden.SteadySpeedKn - 0.15, golden.SteadySpeedKn + 0.15);

        Assert.Equal("trial", r.DataQuality);
        var adv = r.Comparisons.Single(c => c.Key == "advance_m");
        Assert.True(adv.Comparable);
        Assert.Equal($"identification.turningCircle35.{side}.advance_m", adv.Source);
        Assert.Equal(Fsb1Targets.GetProperty("identification").GetProperty("turningCircle35").GetProperty(side).GetProperty("advance_m").GetDouble(), adv.Target);
        Assert.Equal(TurningTolerance(adv.Target, Lpp), adv.Tolerance, 6);
        // 公差判定與黃金測試一致:右迴旋橫距為已知偏差(MmgGoldenTests Skip)
        foreach (var key in new[] { "advance_m", "transfer_m", "tacticalDiameter_m" })
        {
            var c = r.Comparisons.Single(x => x.Key == key);
            var goldenValue = key switch { "advance_m" => golden.AdvanceM, "transfer_m" => golden.TransferM, _ => golden.TacticalDiameterM };
            Assert.Equal(Math.Abs(goldenValue - c.Target) <= c.Tolerance, c.WithinTolerance);
        }
        Assert.Contains(r.Comparisons, c => c.Key == "steadySpeed_kn" && c.TargetMin == 4.6 && c.TargetMax == 4.9);
        Assert.Contains(events, x => x.Kind == ManoeuvreEventKinds.Started && x.ItemId == r.Id);
        Assert.Contains(events, x => x.Kind == ManoeuvreEventKinds.Measured && x.ItemId == r.Id && x.Source == SimEventSources.Assessment);
    }

    private static double Lpp => Fsb1Full.Reference.Length_m;

    // ------------------------------------------------------------------ Z 形

    /// <summary>與 MmgTestSupport.Zigzag 相同的逐步反舵(航向變化達 ±ψ 的那一步下反舵令),由追蹤器被動量測。</summary>
    private ManoeuvreRecord RunZigzag(double rudder, string firstSide, int nOvershoots, out MmgTestSupport.ZigzagResult golden)
    {
        golden = Zigzag(rudder, rudder, firstSide, 14.5, nOvershoots: nOvershoots);
        var (e, events) = Engine(14.5);
        var sign = firstSide == "port" ? -1.0 : 1.0;
        var psi0 = e.Motion.Psi;
        var cur = sign;
        e.Enqueue(SimCommand.Rudder(cur * rudder));
        var waiting = true;
        var r = RunUntilMeasured(e, events, 1200, eng =>
        {
            // 黃金測試在 Step 後檢查並 Enqueue,下一步套用;此處於 Step 前檢查(同一 tick 狀態),時序相同
            var dpsi = Units.RadToDeg(Units.WrapRadPi(eng.Motion.Psi - psi0));
            if (waiting && eng.Tick > 0 && cur * dpsi >= rudder)
            {
                cur = -cur;
                eng.Enqueue(SimCommand.Rudder(cur * rudder));
                waiting = false;
            }
            else if (!waiting && -cur * eng.Motion.R < 0.0) waiting = true;
        });
        Dump(r);
        _out.WriteLine("golden overshoots: " + string.Join(", ", golden.OvershootsDeg.Select(x => x.ToString("F3"))));
        return r;
    }

    [Theory]
    [InlineData(10.0, "port", 3)]
    [InlineData(10.0, "starboard", 3)]
    [InlineData(20.0, "port", 2)]
    [InlineData(20.0, "starboard", 2)]
    public void Zigzag_OvershootsMatchGoldenAndTrial(double rudder, string first, int n)
    {
        var r = RunZigzag(rudder, first, n, out var golden);
        Assert.Equal($"zigzag{rudder:F0}.{first}First", r.Id);
        Assert.True(r.Complete);
        Assert.Equal(rudder, r.CheckHeadingDeg);
        for (var i = 0; i < n; i++)
            Assert.InRange(r.Values[$"overshoot{i + 1}_deg"], golden.OvershootsDeg[i] - 0.05, golden.OvershootsDeg[i] + 0.05);
        Assert.InRange(r.Values["timeToExecute_s"], golden.ReversalTimesS[0] - 0.1, golden.ReversalTimesS[0] + 0.05);
        Assert.Equal(n, r.Marks.Count(m => m.Type == "overshoot"));
        var section = rudder == 10 ? "identification" : "validation";
        var trial = Fsb1Targets.GetProperty(section).GetProperty($"zigzag{rudder:F0}_{rudder:F0}").GetProperty($"{first}First").GetProperty("overshoots_deg").EnumerateArray().Select(x => x.GetDouble()).ToList();
        Assert.Equal(trial.Count, r.Comparisons.Count);
        for (var i = 0; i < trial.Count; i++)
        {
            var c = r.Comparisons.Single(x => x.Key == $"overshoot{i + 1}_deg");
            Assert.Equal(trial[i], c.Target);
            Assert.Equal(ZigzagTolerance(trial[i]), c.Tolerance, 6);
            Assert.True(c.Comparable && c.WithinTolerance, $"{c.Key} {c.Measured} vs {c.Target}");
        }
    }

    // ------------------------------------------------------------------ 停船

    [Fact]
    public void CrashStop_MatchesGoldenAndTrial()
    {
        var golden = CrashStop(14.5);
        var (e, events) = Engine(14.5);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        var r = RunUntilMeasured(e, events, 1500);
        Dump(r);
        _out.WriteLine($"golden stop {golden.StopTimeS:F2} track {golden.TrackReachM:F2} head {golden.HeadReachM:F2} side {golden.SideReachM:F2} astern {golden.AsternStartS:F2} hdg {golden.FinalHeadingChangeDeg:F2}");
        Assert.Equal("crashStop", r.Id);
        Assert.Equal("EFAS", r.Order);
        Assert.InRange(r.Values["stopTime_s"], golden.StopTimeS - 0.1, golden.StopTimeS + 0.05);
        Assert.InRange(r.Values["trackReach_m"], golden.TrackReachM - 1.0, golden.TrackReachM + 1.0);
        Assert.InRange(r.Values["headReach_m"], golden.HeadReachM - 1.0, golden.HeadReachM + 1.0);
        Assert.InRange(r.Values["sideReach_m"], golden.SideReachM - 1.0, golden.SideReachM + 1.0);
        Assert.InRange(r.Values["asternStart_s"], golden.AsternStartS - 1.0, golden.AsternStartS + 0.05);
        Assert.InRange(r.Values["headingChange_deg"], golden.FinalHeadingChangeDeg - 0.5, golden.FinalHeadingChangeDeg + 0.5);
        Assert.True(r.Values["headingChange_deg"] > 0, "右旋單俥倒俥:艏向右");
        var stop = r.Comparisons.Single(c => c.Key == "stopTime_s" && c.DataQuality == "trial");
        Assert.True(stop.Comparable && stop.WithinTolerance);
        Assert.Equal(StoppingTolerance(259), stop.Tolerance, 6);
        Assert.True(r.Comparisons.Single(c => c.Key == "trackReach_m" && c.DataQuality == "trial").WithinTolerance);
        // 海報值(滿載緊急停船 4.3 min / 6.4 鏈)另列,資料品質 poster
        Assert.Contains(r.Comparisons, c => c.Key == "trackReach_m" && c.DataQuality == "poster" && c.Target == 6.4 * ManoeuvreTargets.MetresPerCable);
    }

    [Fact]
    public void InertiaStop_MatchesGoldenAndFlagsKnownDeviation()
    {
        var golden = InertiaStop(14.1, 5.0);
        var (e, events) = Engine(14.1);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
        var r = RunUntilMeasured(e, events, 2400);
        Dump(r);
        _out.WriteLine($"golden time {golden.TimeToTargetS:F2} dist {golden.DistanceToTargetM:F2} head {golden.HeadReachM:F2}");
        Assert.Equal("inertiaStop", r.Id);
        Assert.Equal(5.0, r.Values["endSpeed_kn"]);
        Assert.InRange(r.Values["time_s"], golden.TimeToTargetS - 0.1, golden.TimeToTargetS + 0.05);
        Assert.InRange(r.Values["distance_m"], golden.DistanceToTargetM - 1.0, golden.DistanceToTargetM + 1.0);
        Assert.InRange(r.Values["headReach_m"], golden.HeadReachM - 1.0, golden.HeadReachM + 1.0);
        Assert.True(r.Values.ContainsKey("speedAt1min_kn"));
        // 試俥慣性停船 258 s:模型偏長為已知偏差(Python xfail / C# Skip),比較結果公差外但條件可比
        var c = r.Comparisons.Single(x => x.Key == "time_s");
        Assert.True(c.Comparable);
        Assert.False(c.WithinTolerance);
        Assert.Contains(r.Notes, n => n.Contains("已知偏差"));
    }

    [Fact]
    public void ThrusterTurn_MatchesGolden()
    {
        var golden = ThrusterTurn("starboard", 90.0, 1.0);
        var (e, events) = Engine(1.0, rpm: 0.0);
        e.Enqueue(SimCommand.Thruster(1.0));
        var r = RunUntilMeasured(e, events, 1800);
        Dump(r);
        Assert.Equal("thrusterTurn.starboard", r.Id);
        Assert.InRange(r.Values["time90_s"], golden.TimeS - 0.1, golden.TimeS + 0.05);
        var c = r.Comparisons.Single(x => x.Key == "time90_s");
        Assert.True(c.Comparable && c.WithinTolerance, $"{c.Measured} vs {c.Target}");
    }

    // ------------------------------------------------------------------ 啟用條件與穩定直航

    [Fact]
    public void DisabledWhenScenarioDoesNotReferenceManoeuvres_PhysicsAndSummaryUnchanged()
    {
        var plain = MmgEngine(MmgScenario(14.5));
        var events = TestData.Capture(plain);
        var tracked = MmgEngine(Enabled(MmgScenario(14.5)));
        Assert.False(plain.Assessment!.Manoeuvres.Enabled);
        Assert.True(tracked.Assessment!.Manoeuvres.Enabled);
        plain.Enqueue(SimCommand.Rudder(-35));
        tracked.Enqueue(SimCommand.Rudder(-35));
        for (var i = 0; i < 400 * 50; i++) { plain.Step(); tracked.Step(); }
        // 量測只讀引擎狀態:物理完全相同
        Assert.Equal(plain.Motion, tracked.Motion);
        Assert.DoesNotContain(events, e => e.Kind.StartsWith("manoeuvre", StringComparison.Ordinal));
        Assert.Null(plain.LatestSummary!.Manoeuvres);
        Assert.DoesNotContain("\"manoeuvres\"", plain.LatestSummary.ToJson());
        Assert.Null(plain.Assessment.CreateSnapshot().Manoeuvres);
        Assert.Single(tracked.LatestSummary!.Manoeuvres!);
    }

    [Fact]
    public void IsReferencedByBandsTimelineRequiredHardFailOrMobTarget()
    {
        Scenario.Scenario Yaml(string extra) => TestData.Minimal(extra);
        Assert.False(ManoeuvreTracker.IsReferenced(Yaml("")));
        Assert.True(ManoeuvreTracker.IsReferenced(Yaml("assessment:\n  bands:\n    - { id: a, metric: manoeuvres.turning35.port.advance_m, warn: 300 }")));
        Assert.True(ManoeuvreTracker.IsReferenced(Yaml("timeline:\n  - { id: a, when: { type: event, kind: manoeuvreMeasured, itemId: crashStop }, actions: [ { phase: x } ] }")));
        Assert.True(ManoeuvreTracker.IsReferenced(Yaml("timeline:\n  - { id: a, when: { type: time, atLeast: 5, since: { event: manoeuvreStarted } }, actions: [ { phase: x } ] }")));
        Assert.True(ManoeuvreTracker.IsReferenced(Yaml("assessment:\n  hardFail:\n    - { id: a, rule: event, kind: manoeuvreAborted }")));
        Assert.True(ManoeuvreTracker.IsReferenced(Yaml("targets:\n  - { id: MOB1, initial: { position: { x: 10, y: 10 }, heading: 0, speed: 0 }, behaviour: { mode: hold } }")));
        // bands 的 manoeuvres.* 指標為已知名稱(結構檢查通過),指標值由追蹤器提供
        Assert.True(MetricNames.IsKnown("manoeuvres.turning35.port.tacticalDiameter_m"));
        Assert.True(MetricNames.IsKnown("manoeuvres.crashStop.trackReach_m"));
        Assert.True(MetricNames.IsKnown("manoeuvres.outsideTolerance"));
        Assert.DoesNotContain(Yaml("assessment:\n  bands:\n    - { id: a, metric: manoeuvres.crashStop.trackReach_m, warn: 1300 }").StructuralIssues(), i => i.Severity == ScenarioIssueSeverity.Error);
    }

    [Fact]
    public void TurningNeedsSteadyApproach_NotStartedRightAfterAnOrder()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 20);
        e.Enqueue(SimCommand.Rpm(150));          // 車令變更後 30 s 內不是穩定直航
        TestData.RunTo(e, 30);
        e.Enqueue(SimCommand.Rudder(35));
        TestData.RunTo(e, 60);
        Assert.DoesNotContain(events, x => x.Kind == ManoeuvreEventKinds.Started);
        var st = e.Assessment!.Manoeuvres.Steadiness(e.Tick);
        Assert.False(st.Steady);
        // 回正舵、等 30 s 以上的穩定直航後再下舵令即開始(steadyFor_s = 視窗長度)
        e.Enqueue(SimCommand.Rudder(0));
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.NAVF));
        e.Enqueue(SimCommand.Autopilot(true, 0));
        TestData.RunTo(e, 900);
        e.Enqueue(SimCommand.Rudder(35));
        TestData.RunTo(e, 905);
        var started = events.Single(x => x.Kind == ManoeuvreEventKinds.Started);
        Assert.Equal("turning35.starboard", started.ItemId);
        var rec = e.Assessment.Manoeuvres.State.Active!.Record;
        Assert.True(rec.Approach.Steady);
        Assert.False(rec.Approach.FromInitial);
        Assert.Equal(ManoeuvreTracker.SteadyWindowS, rec.Approach.SteadyForS);
        Assert.True(rec.Approach.Autopilot);
    }

    [Fact]
    public void AbortedTurnEmitsAbortedAndUsablePartialIsMeasured()
    {
        var (e, events) = Engine(14.5);
        e.Enqueue(SimCommand.Rudder(-35));
        TestData.RunTo(e, 20);                    // 未到 90°:中止
        e.Enqueue(SimCommand.Rudder(0));
        TestData.RunTo(e, 25);
        var aborted = events.Single(x => x.Kind == ManoeuvreEventKinds.Aborted);
        Assert.Equal("turning35.port", aborted.ItemId);
        Assert.Equal("aborted", e.Assessment!.Manoeuvres.State.Records.Single().State);
        Assert.Null(e.Assessment.MetricValue("manoeuvres.turning35.port.advance_m", null!));
    }

    // ------------------------------------------------------------------ 舵機擺動(單泵/雙泵)

    [Theory]
    [InlineData("1", 28.0)]
    [InlineData("both", 14.0)]
    public void RudderSwing_Fsb2PumpTimesComparedWithParticulars(string pump, double drawing)
    {
        var ship = TestData.Fsb2();
        var sc = Enabled(MmgScenario(12.8));
        sc.Ship.Id = "FSB2";
        var e = new SimulationEngine(ship, sc, (s, l) => MmgDynamics.Load(TestData.Root, s, l), new EngineOptions { Dt = Dt, AutoSnapshotIntervalTicks = 0 });
        var events = TestData.Capture(e);
        if (pump == "both") e.Enqueue(SimCommand.SteeringMode(SteeringMode.Fu, pump: "both"));
        TestData.RunTo(e, 5);
        e.Enqueue(SimCommand.Rudder(-35));
        TestData.RunTo(e, 45);
        e.Enqueue(SimCommand.Rudder(35));
        TestData.RunTo(e, 120);
        var r = e.Assessment!.Manoeuvres.State.Records.Single(x => x.Kind == ManoeuvreKinds.RudderSwing);
        Dump(r);
        Assert.Equal("rudderSwing.toStarboard", r.Id);
        Assert.Equal(pump, r.Pump);
        var c = r.Comparisons.Single();
        Assert.Equal(drawing, c.Target);
        Assert.Equal("particulars", c.DataQuality);
        Assert.True(c.Comparable && c.WithinTolerance, $"{c.Measured} vs {c.Target}");
        Assert.True(r.Values["timeTo30_s"] < r.Values["time35to35_s"]);
        Assert.Contains(events, x => x.Kind == ManoeuvreEventKinds.Measured && x.ItemId == "rudderSwing.toStarboard");
        Assert.Contains(events, x => x.Kind == ManoeuvreEventKinds.Aborted && x.ItemId == "turning35.port"); // 擺動前的左滿舵被視為迴旋開始,反舵後中止
    }

    // ------------------------------------------------------------------ MOB 旋迴方式

    [Theory]
    [InlineData(new[] { -62.0, 238.0 }, "williamson")]
    [InlineData(new[] { 60.0, -240.0 }, "williamson")]
    [InlineData(new[] { -255.0 }, "anderson")]
    [InlineData(new[] { 245.0, -62.0 }, "scharnow")]
    [InlineData(new[] { -30.0, 30.0 }, "other")]
    [InlineData(new double[0], "other")]
    public void ClassifiesMobManoeuvre(double[] segments, string expected)
        => Assert.Equal(expected, ManoeuvreTracker.ClassifyMob(segments));

    // ------------------------------------------------------------------ 快照與指標

    [Fact]
    public void SnapshotRestoreMidTurnGivesSameMeasurement()
    {
        var (e, events) = Engine(14.5);
        e.Enqueue(SimCommand.Rudder(35));
        TestData.RunTo(e, 100);
        var snap = e.TakeSnapshot();
        Assert.NotNull(snap.Assessment!.Manoeuvres!.Active);
        var first = RunUntilMeasured(e, events, 600);
        var firstJson = ContractJson.Serialize(first);
        var hash = e.StateHash;
        var tick = e.Tick;
        e.Restore(snap);
        Assert.Null(e.Assessment!.Manoeuvres.State.Records.FirstOrDefault(r => r.State == "measured"));
        while (e.Tick < tick) e.Step();
        var second = e.Assessment.Manoeuvres.State.Records.Single(r => r.State == "measured");
        Assert.Equal(firstJson, ContractJson.Serialize(second));
        Assert.Equal(hash, e.StateHash);
        // bands 指標:最近一次 measured 的量測值與彙總
        var sit = (Situation?)null;
        Assert.Equal(second.Values["tacticalDiameter_m"], e.Assessment.MetricValue("manoeuvres.turning35.starboard.tacticalDiameter_m", sit!));
        Assert.Equal(1, e.Assessment.MetricValue("manoeuvres.count", sit!));
        Assert.Equal(second.Comparisons.Count(c => c.Comparable), e.Assessment.MetricValue("manoeuvres.compared", sit!));
        Assert.Equal(1, e.Assessment.MetricValue("manoeuvres.outsideTolerance", sit!)); // 右迴旋橫距(已知偏差)
        Assert.NotNull(e.Assessment.MetricValue("manoeuvres.maxAbsDeviation_pct", sit!));
        Assert.Null(e.Assessment.MetricValue("manoeuvres.crashStop.stopTime_s", sit!));
    }

    [Fact]
    public void TargetsOfBothShipsLoadWithDataGrades()
    {
        Assert.Equal("trial", ManoeuvreTargets.For("FSB1")!.Grade);
        Assert.Equal("poster", ManoeuvreTargets.For("FSB2")!.Grade);
        Assert.Null(ManoeuvreTargets.For("NOSUCHSHIP"));
    }
}
