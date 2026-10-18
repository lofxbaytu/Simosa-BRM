using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 情境庫收尾審查修正(E19、E20 模組組合、E22a/E22b;docs/design/scenario-library.md 文末「收尾審查修正」):
/// 合理(或更積極)的操作不得被誤扣分,兩個模組共用的故障不得互相解除。
/// </summary>
public class ScenarioLibraryFinalReviewFixTests
{
    private readonly ITestOutputHelper _out;
    public ScenarioLibraryFinalReviewFixTests(ITestOutputHelper output) => _out = output;

    private const string E19 = "E19_fire_isps_pilotage_kaohsiung";
    private const string E20 = "E20_comprehensive_assessment_kaohsiung";
    private const string E20D = "E20_comprehensive_assessment_default";
    private const string E22A = "E22a_handover_conflict";
    private const string E22B = "E22b_fatigue_misjudgement";

    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static double Param(string key) => TestData.Parameters().Parameters[key].NumberValue!.Value;

    private static (SimulationEngine Engine, List<SimEvent> Events, string? Path) Run(string id, double endS, Action<SimulationEngine, List<SimEvent>, int>? script = null, bool record = true)
    {
        var sc = Load(id);
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var events = TestData.Capture(e);
        string? path = null;
        RecordWriter? w = null;
        if (record)
        {
            path = Path.Combine(TestData.TestRecordsDirectory, $"{id}-review-{Guid.NewGuid():N}.jsonl");
            w = new RecordWriter(path, e);
        }
        for (var t = 1; t <= endS && !e.Ended; t++)
        {
            script?.Invoke(e, events, t);
            var target = (long)Math.Round(t / e.Dt);
            while (e.Tick < target && !e.Ended) e.Step();
        }
        w?.Close();
        return (e, events, path);
    }

    private void Dump(List<SimEvent> events, AssessmentSummary s)
    {
        foreach (var x in events.Where(x => x.Kind is SimEventKinds.FaultInjected or SimEventKinds.FaultCleared or SimEventKinds.TimelineTriggered or SimEventKinds.PhaseChanged
                     or SimEventKinds.RequiredEventPending or SimEventKinds.RequiredEventMet or SimEventKinds.RequiredEventMissed or SimEventKinds.AlertAcknowledged))
            _out.WriteLine($"{x.T:F1} {x.Source}/{x.Kind} {x.ItemId} {x.Message}");
        foreach (var r in s.Assessment.Required) _out.WriteLine($"required {r.Id}: {r.State} dt={r.DtS} by={r.By}");
        _out.WriteLine($"score {s.Assessment.Score} passed {s.Assessment.Passed}");
    }

    private static double? FirstT(List<SimEvent> ev, string kind, string? itemId = null)
        => ev.FirstOrDefault(x => x.Kind == kind && (itemId is null || x.ItemId == itemId))?.T;
    private static RequiredResult Req(AssessmentSummary s, string id) => s.Assessment.Required.Single(r => r.Id == id);

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

    /// <summary>循跡(自動舵修正流壓;同 ScenarioLibraryFinalTests 的 TrackKeeper)。</summary>
    private sealed class TrackKeeper
    {
        private double _set;
        public TrackKeeper(double initialHeading) => _set = initialHeading;
        public void Step(SimulationEngine eng, int t)
        {
            if (t % 20 != 0) return;
            var st = eng.BuildState();
            if (st.Route is not { LegCourseDeg: { } leg, XtdM: { } xtd } || st.Sog < 1) return;
            var drift = Units.RadToDeg(Units.WrapRadPi(Units.DegToRad(st.Heading - st.Cog)));
            var cmd = leg + drift - Math.Clamp(0.12 * xtd, -15, 15);
            var delta = Math.Clamp(Units.RadToDeg(Units.WrapRadPi(Units.DegToRad(cmd - _set))), -4, 4);
            if (Math.Abs(delta) < 1) return;
            _set = Math.Round(Units.NormalizeHeadingDeg(_set + delta));
            eng.Enqueue(SimCommand.Autopilot(true, _set));
        }
    }

    // ------------------------------------------------------------------ E20:E07 + E09(共用 steeringGear)

    /// <summary>
    /// E07 先(舵卡在右 10 附近)、30 s 後 E09:應急發電機供電(E09 + 45 s)不得解除 E07 尚未處理的卡舵;
    /// 舵令左 20 時舵仍卡住,直到教官確認應急操舵(e07EmergencySteering)才恢復;e07EmergencySteering met。
    /// </summary>
    [Fact]
    public void E20_E07ThenE09_EmergencyPowerKeepsE07JammedRudder()
    {
        var samples = new Dictionary<int, (double Rudder, bool Jammed)>();
        var (e, ev, path) = Run(E20, 700, (eng, events, t) =>
        {
            switch (t)
            {
                case 398: eng.Enqueue(SimCommand.Rudder(10)); break;
                case 400: eng.Enqueue(SimCommand.TriggerTimeline("injectE07")); break;
                case 405: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); break;
                case 410: eng.Enqueue(SimCommand.Acknowledge("notifyEcr", note: "舵機故障,請機控室派人到舵機房")); break;
                case 430: eng.Enqueue(SimCommand.TriggerTimeline("injectE09")); break;
                case 500: eng.Enqueue(SimCommand.Rudder(-20)); break;
                case 560: eng.Enqueue(SimCommand.TriggerTimeline("e07EmergencySteering")); break;
            }
            if (t is 440 or 480 or 520 or 555 or 600) samples[t] = (eng.BuildState().Rudder, eng.Faults.Contains(FaultNames.SteeringGear));
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var e09 = FirstT(ev, SimEventKinds.TimelineTriggered, "injectE09")!.Value;
        // 應急發電機供電的時刻照常(E09 + 45 s),但走「E07 卡舵仍在」分支:不清除 steeringGear
        Assert.Equal(e09 + Param("emergency.blackout.emergencyPower_s"), FirstT(ev, SimEventKinds.TimelineTriggered, "e09EmergencyPowerSteeringJammed"));
        Assert.Null(FirstT(ev, SimEventKinds.TimelineTriggered, "e09EmergencyPower"));
        Assert.DoesNotContain(ev, x => x.Kind == SimEventKinds.FaultCleared && x.Message == FaultNames.SteeringGear && x.ItemId != "e07EmergencySteering");
        foreach (var t in new[] { 480, 520, 555 })
        {
            Assert.True(samples[t].Jammed, $"t={t} steeringGear 應仍在作用中");
            Assert.InRange(samples[t].Rudder, samples[440].Rudder - 0.5, samples[440].Rudder + 0.5);   // 舵令左 20 也不動
        }
        Assert.False(samples[600].Jammed);
        Assert.InRange(samples[600].Rudder, -20.5, -19.5);                                                  // 應急操舵後跟隨舵令
        Assert.Equal("met", Req(s, "e07EmergencySteering").State);
        AssertReplays(path!);
    }

    /// <summary>E09 先、20 s 內 E07:E09 + 45 s 應急發電機供電時 E07 卡舵仍在 → 不解除,教官確認應急操舵後才恢復。</summary>
    [Fact]
    public void E20_E09ThenE07Within45s_EmergencyPowerKeepsE07JammedRudder()
    {
        var jammedAt = new Dictionary<int, bool>();
        var (e, ev, _) = Run(E20, 650, (eng, events, t) =>
        {
            switch (t)
            {
                case 400: eng.Enqueue(SimCommand.TriggerTimeline("injectE09")); break;
                case 405: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); break;
                case 420: eng.Enqueue(SimCommand.TriggerTimeline("injectE07")); break;
                case 560: eng.Enqueue(SimCommand.TriggerTimeline("e07EmergencySteering")); break;
            }
            if (t is 450 or 500 or 555 or 565) jammedAt[t] = eng.Faults.Contains(FaultNames.SteeringGear);
        }, record: false);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var e09 = FirstT(ev, SimEventKinds.TimelineTriggered, "injectE09")!.Value;
        Assert.Equal(e09 + Param("emergency.blackout.emergencyPower_s"), FirstT(ev, SimEventKinds.TimelineTriggered, "e09EmergencyPowerSteeringJammed"));
        Assert.Null(FirstT(ev, SimEventKinds.TimelineTriggered, "e09EmergencyPower"));
        Assert.True(jammedAt[450] && jammedAt[500] && jammedAt[555]);
        Assert.False(jammedAt[565]);
    }

    /// <summary>只有 E09(或 E07 已以應急操舵處理):應急發電機供電照舊解除 steeringGear(既有行為不變)。</summary>
    [Fact]
    public void E20_E09Only_EmergencyPowerRestoresSteering()
    {
        var (e, ev, _) = Run(E20, 520, (eng, events, t) =>
        {
            if (t == 400) eng.Enqueue(SimCommand.TriggerTimeline("injectE09"));
        }, record: false);
        var e09 = FirstT(ev, SimEventKinds.TimelineTriggered, "injectE09")!.Value;
        Assert.Equal(e09 + Param("emergency.blackout.emergencyPower_s"), FirstT(ev, SimEventKinds.TimelineTriggered, "e09EmergencyPower"));
        Assert.Null(FirstT(ev, SimEventKinds.TimelineTriggered, "e09EmergencyPowerSteeringJammed"));
        Assert.Contains(ev, x => x.Kind == SimEventKinds.FaultCleared && x.ItemId == "e09EmergencyPower" && x.Message == FaultNames.SteeringGear);
        Assert.DoesNotContain(FaultNames.SteeringGear, e.Faults);
    }

    // ------------------------------------------------------------------ E20:E08 + E09(共用 gyroFailure)

    /// <summary>
    /// E08 漂移中(或之前)E09 已使電羅經失效且警報已確認:E08 的「完全失效」不再重複注入 gyroFailure(無新警報可確認),
    /// 改以 e08GyroFailMasked 通知教官;e08AlarmAck 等失效階段必要事件為 notApplicable 而非 missed。
    /// </summary>
    [Theory]
    [InlineData("injectE08", "injectE09")]
    [InlineData("injectE09", "injectE08")]
    public void E20_E08AndE09_GyroAlreadyFailedDoesNotPenaliseAlarmAck(string first, string second)
    {
        var (e, ev, path) = Run(E20, 800, (eng, events, t) =>
        {
            switch (t)
            {
                case 100: eng.Enqueue(SimCommand.TriggerTimeline(first)); break;
                case 200: eng.Enqueue(SimCommand.TriggerTimeline(second)); break;
                case 205: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); break;
                case 210: eng.Enqueue(SimCommand.AlertAcknowledge("gyroFailure")); break;
                case 215: eng.Enqueue(SimCommand.Rudder(0)); break;
                case 530: eng.Enqueue(SimCommand.AlertAcknowledge("gyroFailure")); eng.Enqueue(SimCommand.AlertAcknowledge()); break;
            }
        }, record: first == "injectE08");
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var e08 = FirstT(ev, SimEventKinds.TimelineTriggered, "injectE08")!.Value;
        Assert.Null(FirstT(ev, SimEventKinds.TimelineTriggered, "e08GyroFail"));
        Assert.Equal(e08 + Param("emergency.gyroFailure.failAfter_s"), FirstT(ev, SimEventKinds.TimelineTriggered, "e08GyroFailMasked"));
        foreach (var id in new[] { "e08AlarmAck", "e08ManualSteering", "e08CallMaster" })
            Assert.Equal("notApplicable", Req(s, id).State);
        Assert.Contains(ev, x => x.Kind == SimEventKinds.AlertAcknowledged && x.ItemId == "gyroFailure");
        if (path is not null) AssertReplays(path);
    }

    /// <summary>只有 E08:漂移 420 s 後照舊注入 gyroFailure(警報),e08GyroFailMasked 不觸發、e08AlarmAck 照常計時(既有行為不變)。</summary>
    [Fact]
    public void E20_E08Only_GyroFailStillInjectsAlarm()
    {
        var (e, ev, _) = Run(E20, 560, (eng, events, t) =>
        {
            if (t == 100) eng.Enqueue(SimCommand.TriggerTimeline("injectE08"));
        }, record: false);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        var e08 = FirstT(ev, SimEventKinds.TimelineTriggered, "injectE08")!.Value;
        Assert.Equal(e08 + Param("emergency.gyroFailure.failAfter_s"), FirstT(ev, SimEventKinds.TimelineTriggered, "e08GyroFail"));
        Assert.Null(FirstT(ev, SimEventKinds.TimelineTriggered, "e08GyroFailMasked"));
        Assert.Contains(ev, x => x.Kind == SimEventKinds.FaultInjected && x.ItemId == "e08GyroFail" && x.Message == FaultNames.GyroFailure);
        Assert.Equal("missed", Req(s, "e08AlarmAck").State);
    }

    /// <summary>
    /// 教官確認 E09 儀器重啟(e09InstrumentsRestored)時 E08 漂移仍在:電羅經重新啟動一併排除漂移(同 e08GyroRestored),
    /// 「電羅經恢復正常」屬實,之後 E08 不再失效。
    /// </summary>
    [Fact]
    public void E20_E09InstrumentsRestoredAlsoClearsE08Drift()
    {
        var (e, ev, _) = Run(E20, 900, (eng, events, t) =>
        {
            switch (t)
            {
                case 100: eng.Enqueue(SimCommand.TriggerTimeline("injectE08")); break;
                case 200: eng.Enqueue(SimCommand.TriggerTimeline("injectE09")); break;
                case 205: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); break;
                case 210: eng.Enqueue(SimCommand.AlertAcknowledge("gyroFailure")); break;
                case 650: eng.Enqueue(SimCommand.TriggerTimeline("e09InstrumentsRestored")); break;
            }
        }, record: false);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.Contains(ev, x => x.Kind == SimEventKinds.FaultCleared && x.ItemId == "e09InstrumentsRestored" && x.Message == FaultNames.GyroDrift);
        Assert.DoesNotContain(FaultNames.GyroDrift, e.Faults);
        Assert.DoesNotContain(FaultNames.GyroFailure, e.Faults);
        Assert.Null(FirstT(ev, SimEventKinds.TimelineTriggered, "e08GyroFail"));
        Assert.Equal("notApplicable", Req(s, "e08AlarmAck").State);
    }

    /// <summary>兩檔(教官選擇版本與預設組合)的修正項目一致(ScenarioLibraryFinalTests.E20DefaultDiffersOnlyInDefaultTriggers 另比對全文)。</summary>
    [Theory]
    [InlineData(E20)]
    [InlineData(E20D)]
    public void E20_BothFilesCarryModuleInteractionItems(string id)
    {
        var sc = Load(id);
        foreach (var item in new[] { "e09EmergencyPower", "e09EmergencyPowerSteeringJammed", "e08GyroFail", "e08GyroFailMasked" })
            Assert.Contains(sc.Timeline, t => t.Id == item);
        Assert.Contains(sc.Timeline.Single(t => t.Id == "e09InstrumentsRestored").Actions, a => a.Kind == "clearFault" && a.AsString() == FaultNames.GyroDrift);
        Assert.DoesNotContain(sc.Timeline.Single(t => t.Id == "e09EmergencyPowerSteeringJammed").Actions, a => a.Kind == "clearFault");
    }

    // ------------------------------------------------------------------ E19

    /// <summary>
    /// 同 E19 合理操作腳本,但在回覆機艙「主機保持,先減速等候」(690 s)時即減至 DSAH(VTS 等候指示 750 s 之前):
    /// VTS 指示時已在減速 → holdSpeedReduction met(不需再下車令)、100 分。
    /// </summary>
    [Fact]
    public void E19_ReducingSpeedBeforeVtsHoldMeetsHoldSpeedReduction()
    {
        var keeper = new TrackKeeper(74);
        var (e, ev, path) = Run(E19, 1800, (eng, events, t) =>
        {
            switch (t)
            {
                case 20: eng.Enqueue(SimCommand.Acknowledge("checklist")); break;
                case 260: eng.Enqueue(SimCommand.Acknowledge("notifyCompany", note: "授權大副依 SSP 檢查舵機房限制區並通報 CSO;駕駛台專注引航")); break;
                case 500: eng.Enqueue(SimCommand.Acknowledge("callMaster", note: "機艙煙感火警")); break;
                case 530: eng.Enqueue(SimCommand.Acknowledge("notifyEcr", note: "機艙確認火警位置,隨時回報")); break;
                case 660: eng.Enqueue(SimCommand.Acknowledge("verbal", note: "全船警報;消防隊集合由大副現場指揮,舵工、瞭望、二副留駕駛台")); break;
                case 690:
                    eng.Enqueue(SimCommand.Acknowledge("notifyEcr", note: "主機保持運轉,先減速等候"));
                    eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH));
                    break;
                case 720: eng.Enqueue(SimCommand.Acknowledge("report", note: "向 VTS 與引水人報告機艙火災,請求拖船/消防船")); break;
            }
            keeper.Step(eng, t);
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var hold = FirstT(ev, SimEventKinds.TimelineTriggered, "vtsHold")!.Value;
        Assert.Equal(hold, FirstT(ev, SimEventKinds.TimelineTriggered, "holdAlreadyReduced"));
        var r = Req(s, "holdSpeedReduction");
        Assert.Equal("met", r.State);
        Assert.Equal("event:timelineTriggered", r.By);
        Assert.All(s.Assessment.Required, x => Assert.Equal("met", x.State));
        Assert.Equal(100.0, s.Assessment.Score);
        AssertReplays(path!);
    }

    /// <summary>VTS 指示時仍 HAH 全速前進:holdAlreadyReduced 不觸發(之後仍須下車令,見 ScenarioLibraryFinalTests 合理操作與無人操作)。</summary>
    [Fact]
    public void E19_NotReducedAtVtsHoldStillRequiresTelegraph()
    {
        var (e, ev, _) = Run(E19, 1000, record: false);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Assert.Null(FirstT(ev, SimEventKinds.TimelineTriggered, "holdAlreadyReduced"));
        Assert.Equal("missed", Req(s, "holdSpeedReduction").State);
    }

    // ------------------------------------------------------------------ E22a / E22b

    /// <summary>E22a:看到航海日誌(60 s)與口頭說明不符即提出查證(75 s,ARPA 提示 90 s 之前)→ conflictQuery met、100 分。</summary>
    [Fact]
    public void E22a_QueryAfterLogbookBeforeArpaCounts()
    {
        var (e, ev, path) = Run(E22A, 900, (eng, events, t) =>
        {
            if (t == 75) eng.Enqueue(SimCommand.Acknowledge("verbal", note: "日誌 07:30 改向、07:40 X1 CPA 1.0 nm 與口頭說明不符,請交班者留下"));
            if (t == 200) eng.Enqueue(SimCommand.Acknowledge("checklist"));
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.Equal("met", Req(s, "conflictQuery").State);
        Assert.Equal(100.0, s.Assessment.Score);
        // 期限不變:自 logbookEntry(60 s)起算,與原「ARPA 90 s + 180 s」同為 270 s
        Assert.Equal(60.0 + Param("bridge.e22.conflictQueryFromLogbook_s"), 90.0 + Param("bridge.e22.conflictQuery_s"));
        AssertReplays(path!);
    }

    /// <summary>E22a:日誌訊息之前(開場 30 s)的一般提問不算資訊衝突查證。</summary>
    [Fact]
    public void E22a_QueryBeforeAnyConflictDoesNotCount()
    {
        var (e, _, _) = Run(E22A, 900, (eng, events, t) =>
        {
            if (t == 30) eng.Enqueue(SimCommand.Acknowledge("verbal", note: "交通狀況?"));
        }, record: false);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Assert.Equal("missed", Req(s, "conflictQuery").State);
    }

    /// <summary>E22b:交班時(30 s,疲勞提示 120 s 之前)即誠實說明疲勞並依夜令叫船長 → fatigueCallMaster met、100 分。</summary>
    [Fact]
    public void E22b_CallMasterAtHandoverCounts()
    {
        var (e, ev, path) = Run(E22B, 900, (eng, events, t) =>
        {
            if (t == 30) eng.Enqueue(SimCommand.Acknowledge("callMaster", note: "連續工作 15 小時,依夜令請船長上駕駛台"));
            if (t == 60) eng.Enqueue(SimCommand.Acknowledge("checklist"));
            if (t == 230) eng.Enqueue(SimCommand.Acknowledge("report", note: "雷達確認:右舷貨船橫越"));
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.Equal("met", Req(s, "fatigueCallMaster").State);
        Assert.Equal(100.0, s.Assessment.Score);
        // 期限不變:自交班起算 = 疲勞提示時刻 120 s + 原期限 300 s
        Assert.Equal(120.0 + Param("bridge.e22.fatigueCallMaster_s"), Param("bridge.e22.fatigueCallMasterFromHandover_s"));
        AssertReplays(path!);
    }
}
