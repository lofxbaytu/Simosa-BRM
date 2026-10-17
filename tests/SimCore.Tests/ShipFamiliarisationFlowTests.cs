using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 船舶熟悉流程的審查修正(docs/design/ship-familiarisation.md 第 3、4 節):學員的操作沒被辨識或做錯時,示範先回到穩定直航再下令,
/// 段落以量測完成(同段的 manoeuvreMeasured)或逾時略過結束,流程不會停住;F01 示範反舵時機、F02 bow blow-off、F03 全速退段的速限。
/// </summary>
public class ShipFamiliarisationFlowTests
{
    private readonly ITestOutputHelper _out;
    public ShipFamiliarisationFlowTests(ITestOutputHelper output) => _out = output;

    private static readonly Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg = (s, l) => MmgDynamics.Load(TestData.Root, s, l);

    private static (SimulationEngine Engine, List<SimEvent> Events) Engine(string id)
    {
        var sc = ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg, new EngineOptions { AutoSnapshotIntervalTicks = 0 });
        return (e, TestData.Capture(e));
    }

    private static bool Triggered(List<SimEvent> events, string itemId) => events.Any(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == itemId);
    private static double TriggeredAt(List<SimEvent> events, string itemId) => events.First(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == itemId).T;

    /// <summary>逐步推進,直到條件成立或逾時(每步前呼叫 perStep)。</summary>
    private static void RunUntil(SimulationEngine e, Func<bool> done, double maxS, Action? perStep = null)
    {
        var end = (long)Math.Round(maxS / e.Dt);
        while (e.Tick < end && !done() && !e.Ended)
        {
            perStep?.Invoke();
            e.Step();
        }
    }

    private static void SendRamp(SimulationEngine e, IEnumerable<double> orders, double interval)
    {
        foreach (var o in orders)
        {
            e.Enqueue(SimCommand.Rudder(o));
            var until = (long)Math.Round((e.Time + interval) / e.Dt);
            while (e.Tick < until) e.Step();
        }
    }

    private void Dump(List<SimEvent> events, double from = 0)
    {
        foreach (var x in events.Where(x => x.T >= from && (x.Kind.StartsWith("manoeuvre", StringComparison.Ordinal) || x.Kind is SimEventKinds.TimelineTriggered or SimEventKinds.ExerciseEnded
                     || x.Kind.StartsWith("required", StringComparison.Ordinal) || x.Kind.StartsWith("band", StringComparison.Ordinal) || x.Kind == "speedLimitExceeded")))
            _out.WriteLine($"{x.T,8:F2} {x.Kind} {x.ItemId} {x.Message}");
    }

    // ------------------------------------------------------------------ F01:學員操作未被辨識 / 做錯

    /// <summary>F01 左迴旋段:學員於提示後 10 s 以舵輪拖曳到左滿舵 35°(每 50 ms 一道):量測開始、不示範,量測完成後進入右迴旋段。</summary>
    [Fact]
    public void F01_WheelDragTurnIsMeasuredAndFlowContinues()
    {
        var (e, events) = Engine("F01_FSB1_full");
        RunUntil(e, () => Triggered(events, "executeTurnPort"), 400);
        TestData.RunTo(e, TriggeredAt(events, "executeTurnPort") + 10);
        SendRamp(e, Enumerable.Range(1, 10).Select(i => -3.5 * i), 0.05);
        RunUntil(e, () => Triggered(events, "predictTurnStbd"), 1500);
        Dump(events);
        Assert.False(Triggered(events, "demoPrepTurnPort"));
        Assert.False(Triggered(events, "demoTurnPort"));
        Assert.True(Triggered(events, "recoverTurnPort"));
        Assert.True(Triggered(events, "predictTurnStbd"));
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Assert.Equal(("turning35.port", true), (r.Id, r.Complete));
    }

    /// <summary>
    /// F01 左迴旋段:學員誤下右滿舵 35°。示範先回正舵、自動舵回 000° 直航(右迴旋中止或為未完整量測),穩定 30 s 後再示範左滿舵;
    /// 左迴旋量測完成 → recoverTurnPort;學員誤做的右迴旋不會讓右迴旋段的 recover 提前觸發。
    /// </summary>
    [Fact]
    public void F01_WrongSideRudderIsRecoveredByDemoAfterSteadying()
    {
        var (e, events) = Engine("F01_FSB1_full");
        RunUntil(e, () => Triggered(events, "executeTurnPort"), 400);
        var exec = TriggeredAt(events, "executeTurnPort");
        TestData.RunTo(e, exec + 5);
        e.Enqueue(SimCommand.Rudder(35));
        RunUntil(e, () => Triggered(events, "recoverTurnPort"), exec + 1500);
        Dump(events);
        Assert.True(Triggered(events, "demoPrepTurnPort"));
        Assert.True(Triggered(events, "demoTurnPort"));
        Assert.True(TriggeredAt(events, "demoTurnPort") >= TriggeredAt(events, "demoPrepTurnPort") + 30);
        Assert.True(Triggered(events, "recoverTurnPort"));
        var port = e.Assessment!.Manoeuvres.State.Records.Single(x => x.Id == "turning35.port");
        Assert.True(port.Complete && port.Approach.Steady);
        Assert.False(Triggered(events, "recoverTurnStbd"));
        RunUntil(e, () => Triggered(events, "predictTurnStbd"), e.Time + 600);
        Assert.True(Triggered(events, "predictTurnStbd"));
        Assert.False(Triggered(events, "recoverTurnStbd"));
    }

    /// <summary>F01 緊急停船段:學員先 STOP、3 s 後 EFAS:量測為 crashStop(STOP→EFAS),不示範,recoverCrashStop 觸發。</summary>
    [Fact]
    public void F01_StopThenEfasIsMeasuredAsCrashStop()
    {
        var (e, events) = Engine("F01_FSB1_full");
        RunUntil(e, () => Triggered(events, "executeCrashStop"), 4000);
        var exec = TriggeredAt(events, "executeCrashStop");
        TestData.RunTo(e, exec + 10);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
        TestData.RunTo(e, exec + 13);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        RunUntil(e, () => Triggered(events, "recoverCrashStop"), exec + 900);
        Dump(events, exec);
        Assert.True(Triggered(events, "recoverCrashStop"));
        Assert.False(Triggered(events, "demoCrashStop"));
        var r = e.Assessment!.Manoeuvres.State.Records.Single(x => x.Id == "crashStop");
        Assert.Equal("STOP→EFAS", r.Order);
    }

    /// <summary>逾時略過的期限(情境常數;未放入參數集,避免改變參數集 sha256 與既有黃金檔)</summary>
    private const double SegmentTimeoutS = 2400;

    /// <summary>F01 左迴旋段:示範準備後學員一直以 20° 舵迴轉(無法穩定直航、示範不下令):執行提示後 2,400 s 略過此段並進入下一段。</summary>
    [Fact]
    public void F01_SegmentIsSkippedAfterTimeoutInsteadOfStalling()
    {
        var (e, events) = Engine("F01_FSB1_full");
        RunUntil(e, () => Triggered(events, "demoPrepTurnPort"), 600);
        TestData.RunTo(e, e.Time + 2);
        e.Enqueue(SimCommand.Rudder(20));
        RunUntil(e, () => Triggered(events, "predictTurnStbd"), 6000);
        Dump(events);
        Assert.False(Triggered(events, "demoTurnPort"));
        Assert.True(Triggered(events, "skipTurnPort"));
        Assert.False(Triggered(events, "recoverTurnPort"));
        Assert.Equal(TriggeredAt(events, "executeTurnPort") + SegmentTimeoutS, TriggeredAt(events, "skipTurnPort"), 3);
        Assert.True(Triggered(events, "predictTurnStbd"));
        Assert.Contains(e.LatestSummary!.Assessment.Required, r => r.Id == "measuredTurnPort" && r.State == "missed");
    }

    /// <summary>F01 無人操作的 10/10 Z 形示範:三次反舵時航向與執行航向差 ≤ 1°(示範反舵扇區已依每秒評估提前),超越角可比且在公差內。</summary>
    [Fact]
    public void F01_DemoZigzagReversalsAreOnTime()
    {
        var (e, events) = Engine("F01_FSB1_full");
        RunUntil(e, () => events.Any(x => x.Kind == ManoeuvreEventKinds.Measured && x.ItemId == "zigzag10.portFirst"), 4000);
        var r = e.Assessment!.Manoeuvres.State.Records.Single(x => x.Id == "zigzag10.portFirst");
        foreach (var (k, v) in r.Values) _out.WriteLine($"{k} = {v}");
        foreach (var n in r.Notes) _out.WriteLine(n);
        for (var k = 1; k <= 3; k++) Assert.InRange(r.Values[$"reversalLate{k}_deg"], -ManoeuvreTracker.ReversalToleranceDeg, ManoeuvreTracker.ReversalToleranceDeg);
        Assert.Equal(3, r.Comparisons.Count(c => c.Comparable && c.WithinTolerance));
    }

    // ------------------------------------------------------------------ F02 / F03

    /// <summary>F02 bow blow-off 段:頂風改為風舷角約 25°(自動舵 020°),停俥漂移量測到明顯艏偏(不是 −0.014°),結束條件不是速度減半。</summary>
    [Theory]
    [InlineData("F02_FSB1_low_speed")]
    public void F02_BlowOffSegmentShowsHeadingChange(string id)
    {
        var (e, events) = Engine(id);
        RunUntil(e, () => events.Any(x => x.Kind == ManoeuvreEventKinds.Measured && x.ItemId == "inertiaStop"), 3600);
        var r = e.Assessment!.Manoeuvres.State.Records.Single(x => x.Id == "inertiaStop");
        foreach (var (k, v) in r.Values) _out.WriteLine($"{k} = {v}");
        foreach (var n in r.Notes) _out.WriteLine(n);
        Assert.True(Math.Abs(r.Values["maxHeadingChange_deg"]) >= 5.0, $"maxHeadingChange {r.Values["maxHeadingChange_deg"]}");
        Assert.True(r.Values.ContainsKey("headingChangeAt1min_deg"));
        Assert.Contains(r.Notes, n => n.Contains("停俥漂移"));
        Assert.InRange(r.Approach.HeadingDeg, 10, 30);
    }

    /// <summary>F03:全速退段在 WP2–WP3 航段(速限 8 kn)內執行,照情境腳本航行不會超速(無 speedLimitExceeded、speed 帶無 warn/fail),之後微速續航過 WP3。</summary>
    [Theory]
    [InlineData("F03_FSB1_channel")]
    [InlineData("F03_FSB2_channel")]
    public void F03_CrashStopLegHasNoSpeedLimitViolation(string id)
    {
        var (e, events) = Engine(id);
        RunUntil(e, () => false, 4000);
        Dump(events, 1800);
        Assert.True(e.Ended);
        Assert.DoesNotContain(events, x => x.Kind == "speedLimitExceeded");
        Assert.DoesNotContain(events, x => x.Kind is "bandWarn" or "bandFail" && x.ItemId == "speed");
        var crash = events.Single(x => x.Kind == ManoeuvreEventKinds.Started && x.ItemId == "crashStop");
        var wp3 = events.Where(x => x.Kind == "checkpointPassed").ElementAt(1);
        Assert.True(crash.T < wp3.T, "全速退在 WP3 之前(WP2–WP3 航段)");
        Assert.Equal(2, e.LatestSummary!.Track!.Waypoints.Count);
    }
}
