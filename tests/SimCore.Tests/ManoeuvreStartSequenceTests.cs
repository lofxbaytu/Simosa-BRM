using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using Xunit.Abstractions;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 操縱量測審查修正(docs/design/ship-familiarisation.md 第 2.2–2.4 節):舵輪拖曳/鍵盤逐步/NFU 小幅增量的開始序列、逐步反舵、
/// 先停俥再倒俥、Z 形反舵時機與可比性、同 id 指標取可比量測、approach 車鐘與穩定直航秒數、低速停俥漂移、目標值雜湊。
/// FSB1 滿載 MMG、深水無風流、航向北(與 MmgGoldenTests 相同的物理)。
/// </summary>
public class ManoeuvreStartSequenceTests
{
    private readonly ITestOutputHelper _out;
    public ManoeuvreStartSequenceTests(ITestOutputHelper output) => _out = output;

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

    /// <summary>每道舵令後推進 interval 秒(網頁舵輪拖曳 50 ms、鍵盤連按約 0.2 s、NFU 100 ms)。</summary>
    private static void SendRamp(SimulationEngine e, IEnumerable<double> orders, double interval)
    {
        foreach (var o in orders)
        {
            e.Enqueue(SimCommand.Rudder(o));
            var until = (long)Math.Round((e.Time + interval) / e.Dt);
            while (e.Tick < until) e.Step();
        }
    }

    private static List<SimEvent> Mano(List<SimEvent> events) => events.Where(x => x.Kind.StartsWith("manoeuvre", StringComparison.Ordinal)).ToList();

    private void Dump(IEnumerable<SimEvent> events)
    {
        foreach (var x in events) _out.WriteLine($"{x.T,8:F2} {x.Kind} {x.ItemId} {x.Message}");
    }

    private void Dump(ManoeuvreRecord r)
    {
        _out.WriteLine($"{r.Id} {r.State} complete={r.Complete} start={r.StartS} order={r.Order} approach.telegraph={r.Approach.Telegraph} steadyFor={r.Approach.SteadyForS}");
        foreach (var (k, v) in r.Values) _out.WriteLine($"  {k} = {v}");
        foreach (var c in r.Comparisons) _out.WriteLine($"  cmp {c.Key}: {c.Measured} vs {c.Target} within={c.WithinTolerance} comparable={c.Comparable} {c.DataQuality}");
        foreach (var n in r.Notes) _out.WriteLine($"  note {n}");
    }

    // ------------------------------------------------------------------ 開始序列

    /// <summary>網頁舵輪拖曳:每 50 ms 送 −3.5°、−7°、…、−35°;以第一道離開正中的舵令為開始,確定為 35° 左迴旋(不因第二道舵令的原舵角 &gt; 5° 而不量測)。</summary>
    [Fact]
    public void WheelDragToHardOverStartsTurningAtFirstOrder()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 60);
        SendRamp(e, Enumerable.Range(1, 10).Select(i => -3.5 * i), 0.05);
        TestData.RunTo(e, 600);
        Dump(Mano(events));
        var started = Mano(events).Where(x => x.Kind == ManoeuvreEventKinds.Started).ToList();
        Assert.Single(started);
        Assert.Equal("turning35.port", started[0].ItemId);
        Assert.DoesNotContain(events, x => x.Kind == ManoeuvreEventKinds.Aborted);
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Dump(r);
        Assert.Equal(("turning35.port", "measured", true), (r.Id, r.State, r.Complete));
        Assert.Equal(60.0, r.StartS);
        Assert.Equal(-35.0, r.RudderDeg);
        Assert.True(r.Approach.Steady);
        Assert.Contains(r.Notes, n => n.Contains("逐步"));
        // 與一次下令的黃金值相差在下舵所需的 0.45 s 之內(前進距離約 3.4 m)
        var golden = TurningCircle(35.0, "port", 14.5);
        Assert.InRange(r.Values["advance_m"], golden.AdvanceM - 1.0, golden.AdvanceM + 8.0);
        Assert.InRange(r.Values["timeTo90_s"], golden.Marks[90].T - 0.1, golden.Marks[90].T + 0.6);
        Assert.Contains(r.Comparisons, c => c.Key == "advance_m" && c.Comparable);
    }

    /// <summary>鍵盤 Shift 每次 5°(−5、−10、…、−35,每 0.2 s):不會先開始 5/5 Z 形再以「不是反舵」中止,而是一次 35° 左迴旋。</summary>
    [Fact]
    public void KeyboardFiveDegreeStepsGiveOneTurningNotAbortedZigzag()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 60);
        SendRamp(e, Enumerable.Range(1, 7).Select(i => -5.0 * i), 0.2);
        TestData.RunTo(e, 600);
        Dump(Mano(events));
        Assert.DoesNotContain(events, x => x.Kind == ManoeuvreEventKinds.Aborted);
        Assert.Equal(new[] { "turning35.port" }, Mano(events).Where(x => x.Kind == ManoeuvreEventKinds.Started).Select(x => x.ItemId));
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Assert.Equal(("measured", true, 60.0), (r.State, r.Complete, r.StartS));
    }

    /// <summary>NFU 推桿(網頁每 100 ms 送一次約 0.18° 的舵令;每道 &lt; 1° 不是明確舵令):以舵令累計變化辨識,量測 35° 右迴旋。</summary>
    [Fact]
    public void NfuSmallIncrementsAreRecognisedByAccumulatedChange()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 60.3);
        var step = 35.0 / 19.2 * 0.1;                    // FSB1 舵機 35° / 19.2 s,每 100 ms
        var n = (int)Math.Ceiling(35.0 / step);
        SendRamp(e, Enumerable.Range(1, n).Select(i => Math.Min(35.0, step * i)), 0.1);
        TestData.RunTo(e, 700);
        Dump(Mano(events));
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Dump(r);
        Assert.Equal(("turning35.starboard", "measured", true), (r.Id, r.State, r.Complete));
        Assert.InRange(r.StartS, 60.3, 61.3);            // 每秒輪詢:開始時刻誤差 ≤ 1 s
        Assert.Contains(r.Notes, n => n.Contains("輪詢"));
    }

    /// <summary>
    /// 操舵模式 NFU 推桿(steeringMode nfu direction port:舵令設為滿舵、舵以舵機速率移動;放開 stop:舵令 = 當時舵角):
    /// 推桿時的滿舵令不是目標舵角,以放開時的舵角確定量測(舵到 35° 放開 → 35° 左迴旋,不因放開的舵令變化而中止)。
    /// </summary>
    [Fact]
    public void NfuLeverPushAndReleaseUsesRudderAtRelease()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 60);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "port"));
        while (e.Actuators.RudderDeg > -35.0 && e.Time < 120) e.Step();
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "stop"));
        TestData.RunTo(e, 600);
        Dump(Mano(events));
        Assert.DoesNotContain(events, x => x.Kind == ManoeuvreEventKinds.Aborted);
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Dump(r);
        Assert.Equal(("turning35.port", "measured", true, 60.0), (r.Id, r.State, r.Complete, r.StartS));
        Assert.InRange(r.RudderDeg!.Value, -35.5, -34.5);
    }

    /// <summary>舵令只到 1°–4° 的小修正(自動舵外的手操舵):開始序列放棄,不發任何量測事件。</summary>
    [Fact]
    public void SmallCorrectionsDoNotStartOrBlockMeasurements()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 60);
        SendRamp(e, new[] { 1.0, 2.0, 3.0 }, 0.3);
        TestData.RunTo(e, 70);
        Assert.Empty(Mano(events));
        Assert.Null(e.Assessment!.Manoeuvres.State.Active);
        Assert.DoesNotContain(e.LatestSummary!.Manoeuvres!, r => r.State == "inProgress");
        // 放棄後可照常開始緊急停船(序列中改車令也只放棄序列)
        e.Enqueue(SimCommand.Rudder(0));
        TestData.RunTo(e, 72);
        e.Enqueue(SimCommand.Rudder(-2));
        TestData.RunTo(e, 72.5);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        TestData.RunTo(e, 80);
        Assert.Contains(events, x => x.Kind == ManoeuvreEventKinds.Started && x.ItemId == "crashStop");
    }

    /// <summary>開始序列未確定時取快照、還原後重跑:暫存樣本與序列狀態進快照,量測結果與雜湊相同(確定性;summary 不輸出未確定的序列)。</summary>
    [Fact]
    public void SnapshotDuringPendingSequenceRestoresSameMeasurement()
    {
        var (e, _) = Engine(14.5);
        TestData.RunTo(e, 60);
        SendRamp(e, new[] { -5.0, -10.0, -15.0 }, 0.5);
        TestData.RunTo(e, 62);
        Assert.True(e.Assessment!.Manoeuvres.State.Active is { Pending: true });
        Assert.Empty(e.LatestSummary!.Manoeuvres!);
        var snap = e.TakeSnapshot();
        SendRamp(e, new[] { -20.0, -25.0, -30.0, -35.0 }, 0.5);
        TestData.RunTo(e, 500);
        var first = ContractJson.Serialize(e.Assessment.Manoeuvres.State.Records);
        var hash = e.StateHash;
        e.Restore(snap);
        Assert.True(e.Assessment.Manoeuvres.State.Active is { Pending: true, Buffer.Count: > 0 });
        SendRamp(e, new[] { -20.0, -25.0, -30.0, -35.0 }, 0.5);
        TestData.RunTo(e, 500);
        Assert.Equal(first, ContractJson.Serialize(e.Assessment.Manoeuvres.State.Records));
        Assert.Equal(hash, e.StateHash);
        Assert.Equal("turning35.port", e.Assessment.Manoeuvres.State.Records.Single().Id);
    }

    /// <summary>一次按 35° 後 3 s 內再按 70°(Schilling 滿舵):改判為 70° 迴旋,開始時刻與狀態不變,重發 manoeuvreStarted(supersedes)。</summary>
    [Fact]
    public void HardOverThenSchillingMaxWithinWindowRegradesTurning()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 60);
        e.Enqueue(SimCommand.Rudder(-35));
        TestData.RunTo(e, 61);
        e.Enqueue(SimCommand.Rudder(-70));
        TestData.RunTo(e, 400);
        Dump(Mano(events));
        Assert.DoesNotContain(events, x => x.Kind == ManoeuvreEventKinds.Aborted);
        var started = Mano(events).Where(x => x.Kind == ManoeuvreEventKinds.Started).ToList();
        Assert.Equal(new[] { "turning35.port", "turning70.port" }, started.Select(x => x.ItemId));
        Assert.Equal("turning35.port", started[1].DataString("supersedes"));
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Assert.Equal(("turning70.port", 60.0, -70.0), (r.Id, r.StartS, r.RudderDeg));
    }

    // ------------------------------------------------------------------ Z 形:逐步起舵與逐步反舵、反舵時機

    /// <summary>
    /// 以舵輪拖曳執行 10/10 Z 形(起舵與每次反舵都是每 50 ms 一道的中間舵令,於航向變化達 10° 的那一步開始拖):量測完整、三個超越角,
    /// 反舵時航向與執行航向差 &lt; 1°,超越角可比且與一次下令的黃金值相近。
    /// </summary>
    [Fact]
    public void WheelDraggedZigzagIsMeasuredWithReversalsAtFirstStep()
    {
        var golden = Zigzag(10.0, 10.0, "port", 14.5, nOvershoots: 3);
        var (e, events) = Engine(14.5);
        var psi0 = e.Motion.Psi;
        SendRamp(e, new[] { -3.5, -7.0, -10.0 }, 0.05);
        var cur = -1.0;
        var waiting = true;
        for (var i = 0; i < 600 * 50 && !events.Any(x => x.Kind == ManoeuvreEventKinds.Measured); i++)
        {
            var dpsi = Units.RadToDeg(Units.WrapRadPi(e.Motion.Psi - psi0));
            if (waiting && cur * dpsi >= 10.0)
            {
                cur = -cur;
                SendRamp(e, new[] { -cur * 6.5, -cur * 3.0, cur * 0.5, cur * 4.0, cur * 7.5, cur * 10.0 }, 0.05);
                waiting = false;
                continue;
            }
            if (!waiting && -cur * e.Motion.R < 0.0) waiting = true;
            e.Step();
        }
        Dump(Mano(events));
        Assert.DoesNotContain(events, x => x.Kind == ManoeuvreEventKinds.Aborted);
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Dump(r);
        Assert.Equal(("zigzag10.portFirst", "measured", true), (r.Id, r.State, r.Complete));
        Assert.Equal(3, r.Marks.Count(m => m.Type == "reversal"));
        for (var k = 1; k <= 3; k++) Assert.InRange(r.Values[$"reversalLate{k}_deg"], -0.2, 1.0);
        for (var k = 1; k <= 3; k++) Assert.InRange(r.Values[$"overshoot{k}_deg"], golden.OvershootsDeg[k - 1] - 0.5, golden.OvershootsDeg[k - 1] + 1.0);
        Assert.All(r.Comparisons, c => Assert.True(c.Comparable, c.Key));
    }

    /// <summary>學員於航向變化 15° 才反舵(10/10):記錄 reversalLate{n}_deg ≈ 5°、加註記,超越角不與試俥比較(不把操作誤差誤報為船舶特性偏離)。</summary>
    [Fact]
    public void LateReversalIsRecordedAndMakesOvershootsNotComparable()
    {
        var (e, events) = Engine(14.5);
        var psi0 = e.Motion.Psi;
        var cur = -1.0;
        e.Enqueue(SimCommand.Rudder(-10));
        var waiting = true;
        for (var i = 0; i < 600 * 50 && !events.Any(x => x.Kind == ManoeuvreEventKinds.Measured); i++)
        {
            var dpsi = Units.RadToDeg(Units.WrapRadPi(e.Motion.Psi - psi0));
            if (waiting && e.Tick > 0 && cur * dpsi >= 15.0) { cur = -cur; e.Enqueue(SimCommand.Rudder(cur * 10)); waiting = false; }
            else if (!waiting && -cur * e.Motion.R < 0.0) waiting = true;
            e.Step();
        }
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Dump(r);
        Assert.Equal("zigzag10.portFirst", r.Id);
        Assert.InRange(r.Values["reversalLate1_deg"], 4.9, 5.2);
        Assert.Contains(r.Notes, n => n.Contains("第 1 次反舵晚於執行航向"));
        Assert.Contains(r.Marks, m => m.Type == "reversal" && m.Index == 1 && m.Value is > 4.9 and < 5.2);
        Assert.NotEmpty(r.Comparisons);
        Assert.All(r.Comparisons, c => Assert.False(c.Comparable, c.Key));
        Assert.Contains(r.Notes, n => n.Contains("不可比") && n.Contains("反舵"));
    }

    // ------------------------------------------------------------------ 停船

    /// <summary>先 STOP 再 3 s 後 EFAS:不中止,改判為緊急停船(開始取 STOP,order = "STOP→EFAS"、stopToAstern_s = 3)並量測到船停。</summary>
    [Fact]
    public void StopThenFullAsternBecomesCrashStopFromStop()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 60);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
        TestData.RunTo(e, 63);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        TestData.RunTo(e, 600);
        Dump(Mano(events));
        Assert.DoesNotContain(events, x => x.Kind == ManoeuvreEventKinds.Aborted);
        var started = Mano(events).Where(x => x.Kind == ManoeuvreEventKinds.Started).ToList();
        Assert.Equal(new[] { "inertiaStop", "crashStop" }, started.Select(x => x.ItemId));
        Assert.Equal("inertiaStop", started[1].DataString("supersedes"));
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Dump(r);
        Assert.Equal(("crashStop", "measured", true, 60.0), (r.Id, r.State, r.Complete, r.StartS));
        Assert.Equal("STOP→EFAS", r.Order);
        Assert.Equal(3.0, r.Values["stopToAstern_s"]);
        Assert.Equal("NAVF", r.Approach.Telegraph);
        var golden = CrashStop(14.5);
        Assert.InRange(r.Values["stopTime_s"], golden.StopTimeS, golden.StopTimeS + 6.0);
        // 停俥 ≤ 5 s 後倒俥:與試俥條件差異小,仍可比並註明
        Assert.True(r.Comparisons.Single(c => c.Key == "stopTime_s" && c.DataQuality == "trial").Comparable);
        Assert.Contains(r.Notes, n => n.Contains("先停俥再倒俥"));
    }

    /// <summary>停俥滑行 40 s 後才倒俥:慣性停船(&lt; 60 s)中止後,緊急停船仍自停俥滑行中的倒俥開始(條件不可比)。</summary>
    [Fact]
    public void AsternWhileCoastingAfterLongStopStillMeasuresCrashStop()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 60);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
        TestData.RunTo(e, 100);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        TestData.RunTo(e, 600);
        Dump(Mano(events));
        var crash = e.Assessment!.Manoeuvres.State.Records.Single(x => x.Id == "crashStop");
        Dump(crash);
        Assert.Equal(("measured", 100.0), (crash.State, crash.StartS));
        Assert.Equal("STOP", crash.Approach.Telegraph);
        Assert.All(crash.Comparisons, c => Assert.False(c.Comparable));
    }

    /// <summary>approach.telegraph 為開始前的車鐘(不是剛下的 EFAS/STOP);fromInitial 的 steadyFor_s 不超過檢查視窗。</summary>
    [Fact]
    public void ApproachTelegraphIsBeforeOrderAndSteadyForIsCapped()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 240);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        TestData.RunTo(e, 241);
        var r = e.Assessment!.Manoeuvres.State.Active!.Record;
        Assert.Equal("crashStop", r.Id);
        Assert.Equal("EFAS", r.Order);
        Assert.Equal("NAVF", r.Approach.Telegraph);
        Assert.True(r.Approach.FromInitial);
        Assert.Equal(ManoeuvreTracker.SteadyWindowS, r.Approach.SteadyForS);
        var data = events.Single(x => x.Kind == ManoeuvreEventKinds.Started).Data!.Value;
        Assert.Equal("NAVF", data.GetProperty("approach").GetProperty("telegraph").GetString());
        Assert.Equal(240.0, data.GetProperty("start_s").GetDouble());
    }

    /// <summary>低速(&lt; 7 kn)停俥為「停俥漂移」:不以速度減半結束,觀察 300 s 或減至 0.5 kn,記錄每分鐘航向變化。</summary>
    [Fact]
    public void LowSpeedStopIsObservedAsDriftWithHeadingPerMinute()
    {
        var (e, events) = Engine(3.5);
        TestData.RunTo(e, 60);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
        TestData.RunTo(e, 600);
        Dump(Mano(events));
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        Dump(r);
        Assert.Equal(("inertiaStop", "measured", true), (r.Id, r.State, r.Complete));
        Assert.True(r.Values["time_s"] > 200.0 || r.Values["endSpeed_kn"] <= 0.5, "不是速度減半");
        Assert.True(r.Values.ContainsKey("headingChangeAt1min_deg"));
        Assert.True(r.Values.ContainsKey("maxHeadingChange_deg"));
        Assert.Contains(r.Marks, m => m.Type == "minute" && m.HeadingChangeDeg is not null);
        Assert.Contains(r.Notes, n => n.Contains("停俥漂移"));
    }

    // ------------------------------------------------------------------ 指標與目標值

    /// <summary>標準 35° 左迴旋後,低速(約 6.7 kn)再以 35° 左舵迴轉 180°:manoeuvres.turning35.port.* 取可比且完整的那一筆(標準迴旋)。</summary>
    [Fact]
    public void MetricPrefersComparableCompleteRecordOfSameId()
    {
        var (e, events) = Engine(14.5);
        TestData.RunTo(e, 60);
        e.Enqueue(SimCommand.Rudder(-35));
        TestData.RunTo(e, 400);
        var sit = (Situation?)null;
        var std = e.Assessment!.Manoeuvres.State.Records.Single();
        var adv = e.Assessment.MetricValue("manoeuvres.turning35.port.advance_m", sit!);
        Assert.Equal(std.Values["advance_m"], adv);
        e.Enqueue(SimCommand.Rudder(0));
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
        e.Enqueue(SimCommand.Autopilot(true, 0));
        TestData.RunTo(e, 1500);
        e.Enqueue(SimCommand.Rudder(-35));
        TestData.RunTo(e, 1900);
        e.Enqueue(SimCommand.Rudder(0));
        TestData.RunTo(e, 1905);
        var recs = e.Assessment.Manoeuvres.State.Records.Where(r => r.Id == "turning35.port").ToList();
        Assert.Equal(2, recs.Count);
        Assert.False(recs[1].Complete);
        Assert.Equal(adv, e.Assessment.MetricValue("manoeuvres.turning35.port.advance_m", sit!));
        Assert.Equal(std.Values["timeTo90_s"], e.Assessment.MetricValue("manoeuvres.turning35.port.timeTo90_s", sit!));
        // 只有不完整的那一筆時仍取得到值(退而取最近一次 measured)
        Assert.NotNull(e.Assessment.MetricValue("manoeuvres.turning35.port.tacticalDiameter_m", sit!));
    }

    /// <summary>比較所用的 trial_targets.json 以 sha256 記入每筆量測(目標值日後更新時,重播摘要差異可追溯到目標檔版本)。</summary>
    [Fact]
    public void RecordCarriesTargetsSha256()
    {
        var (e, _) = Engine(14.5);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        TestData.RunTo(e, 400);
        var r = e.Assessment!.Manoeuvres.State.Records.Single();
        var path = Path.Combine(TestData.Root, "data", "ships", "FSB1", "trial_targets.json");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Assert.Equal(expected, r.TargetsSha256);
        Assert.Equal(expected, ManoeuvreTargets.For("FSB1")!.Sha256);
    }
}
