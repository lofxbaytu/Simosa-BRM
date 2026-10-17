using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// E09 全船失電(規劃書第 8.1 節 E09;情境引擎設計第 8.3 節):以時間表組合 mainEngine + steeringGear(modelled)與 blackout/gyroFailure/radarFailure 實作,不改引擎。
/// 感測器層加入後 gyroFailure 也是 modelled(指示航向無效、警報 gyroFailure;sensors-alerts.md),blackout/radarFailure 仍只記錄。
/// 固定:載入/驗證無錯誤、無人操作時的事件時刻(失電 T+120、應急電力 +45 s、主機 +360 s)、必要事件逾時與引水人催促、紀錄→重播雜湊/事件/summary 一致;
/// 有人操作(車鐘 STOP、下錨決策、VTS 報告、教官確認儀器重啟)時必要事件 met 且階段回 normal。
/// </summary>
public class BlackoutScenarioTests
{
    private static string E09Path => Path.Combine(TestData.Root, "data", "scenarios", "E09_blackout.yaml");
    private static Scenario.Scenario E09() => ScenarioLoader.Load(E09Path);

    /// <summary>與主控程序相同的 MMG 動力學(紀錄標頭 dynamics = mmg/1),使事件時刻與 Host 實跑一致。</summary>
    private static readonly Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg = (s, l) => MmgDynamics.Load(TestData.Root, s, l);

    private const double InjectAtS = 120;         // emergency.blackout.injectAt_s
    private const double EmergencyPowerS = 45;    // emergency.blackout.emergencyPower_s
    private const double MainEngineRestoreS = 360; // emergency.blackout.mainEngineRestore_s

    [Fact]
    public void LoadsAndValidatesWithoutErrors()
    {
        var sc = E09();
        Assert.Equal("E09_blackout", sc.Id);
        Assert.Equal("FSB1", sc.Ship.Id);
        Assert.Equal(LoadingCondition.Full, sc.Ship.Loading);
        Assert.Equal(9, sc.Timeline.Count);
        Assert.Equal(4, sc.Route!.Waypoints.Count);
        Assert.Equal(3, sc.Zones.Count);
        Assert.Equal(2, sc.Targets.Count);

        var blackout = sc.Timeline.Single(t => t.Id == "blackout");
        Assert.Equal("emergency", blackout.Phase);
        Assert.Equal(new[] { "mainEngine", "steeringGear", "blackout", "gyroFailure", "radarFailure" },
            blackout.Actions.Where(a => a.Kind == "injectFault").Select(a => a.AsString()));
        Assert.Equal(InjectAtS, blackout.When!.AtLeast);
        var power = sc.Timeline.Single(t => t.Id == "emergencyPower");
        Assert.Equal(EmergencyPowerS, power.When!.AtLeast);
        Assert.Equal("timelineTriggered", power.When.Since!.Event);
        Assert.Equal("blackout", power.When.Since.ItemId);
        Assert.Equal("steeringGear", power.Actions.Single(a => a.Kind == "clearFault").AsString());
        var restore = sc.Timeline.Single(t => t.Id == "engineRestored");
        Assert.Equal(MainEngineRestoreS, restore.When!.AtLeast);
        Assert.Equal("mainEngine", restore.Actions.Single(a => a.Kind == "clearFault").AsString());
        Assert.Equal("degraded", restore.Phase);
        Assert.True(sc.Timeline.Single(t => t.Id == "instrumentsRestored").IsManual);

        // 評估:硬性失敗(UKC、淺灘禁區)、CPA 容許帶、速限參數、必要事件(可自動偵測)
        Assert.Contains(sc.Assessment.HardFail, h => h.Rule == "zoneEntered" && h.ZoneId == "shoal_SE");
        Assert.Contains(sc.Assessment.HardFail, h => h.Rule == "ukc");
        Assert.Contains(sc.Assessment.Bands, b => b.Id == "minCpa");
        Assert.Equal(new[] { "blackoutFirstAction", "anchorDecision", "notifyVts" }, sc.Assessment.Required.Select(r => r.Id));
        var first = sc.Assessment.Required[0];
        Assert.Equal("faultInjected", first.After.Event);
        Assert.Equal("blackout", first.After.ItemId);
        Assert.Equal(new[] { "telegraphChange", "steeringModeChange", "acknowledge:checklist" }, first.Expect.Leaves().Select(l => l.Label));
        Assert.Equal(60.0, first.WithinS);
        Assert.Equal("acknowledge:anchorReady", sc.Assessment.Required[1].Expect.Label);
        Assert.Equal(180.0, sc.Assessment.Required[1].WithinS);
        Assert.Equal(10.0, sc.Route.Waypoints[0].SpeedLimitKn);  // port.kaohsiung.channelSpeedLimit_kn
        Assert.Equal(5.0, sc.Route.Waypoints[3].SpeedLimitKn);   // port.kaohsiung.berthSpeedLimit_kn

        // 新參數標來源「假設值,待公司程序」
        var resolved = ScenarioLoader.LastResolvedParameters;
        Assert.Contains(resolved, r => r.Key == "emergency.blackout.emergencyPower_s" && r.Source == "ASSUMED-E09");
        Assert.Contains(resolved, r => r.Key == "emergency.blackout.anchorReady_s" && r.Source == "SMS-EMG-005");
        var pset = TestData.Parameters();
        Assert.StartsWith("假設值,待公司程序", pset.Sources["ASSUMED-E09"].Title);

        var issues = ScenarioValidator.Check(sc, TestData.Fsb1());
        Assert.DoesNotContain(issues, i => i.Severity == ScenarioIssueSeverity.Error);
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Warning && i.Message.Contains("blackout") && i.Message.Contains("只記錄"));
    }

    /// <summary>無人操作 600 s:事件時刻、必要事件逾時、引水人催促、summary;紀錄 → 重播一致(對應主控程序 --duration 600 --record / --replay)。</summary>
    [Fact]
    public void HandsOffRunHasExpectedTimelineAndReplays()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"e09-{Guid.NewGuid():N}.jsonl");
        var engine = new SimulationEngine(TestData.Fsb1(), E09(), Mmg);
        var events = TestData.Capture(engine);
        RecordFooter footer;
        using (var writer = new RecordWriter(path, engine))
        {
            TestData.RunTo(engine, 600);
            footer = writer.Close();
        }
        Assert.Equal(1, footer.SummaryLines);

        var injected = events.Where(e => e.Kind == SimEventKinds.FaultInjected).ToList();
        Assert.Equal(5, injected.Count);
        Assert.All(injected, e => { Assert.Equal(InjectAtS, e.T); Assert.Equal("blackout", e.ItemId); Assert.Equal(SimEventSources.Timeline, e.Source); });
        Assert.Equal(new[] { FaultNames.MainEngine, FaultNames.SteeringGear }, injected.Take(2).Select(e => e.DataString("fault")));
        Assert.Equal(3, injected.Count(e => ScenarioValidator.ModelledFaults.Contains(e.DataString("fault")))); // mainEngine、steeringGear、gyroFailure(感測器層)
        // 失電的警報:主機、舵機、電羅經(BAM;sensors-alerts.md 第 3 節),依警報目錄順序在注入後下一步成立
        var raised = events.Where(e => e.Kind == SimEventKinds.AlertRaised).ToList();
        Assert.Equal(new[] { FaultNames.GyroFailure, FaultNames.SteeringGear, FaultNames.MainEngine }, raised.Take(3).Select(e => e.ItemId));
        Assert.All(raised.Take(3), e => Assert.Equal(InjectAtS + engine.Dt, e.T, 9));
        Assert.Equal(new[] { "blackout", "gyroFailure", "radarFailure" }, injected.Skip(2).Select(e => e.DataString("fault")));

        var cleared = events.Where(e => e.Kind == SimEventKinds.FaultCleared).ToList();
        Assert.Equal(2, cleared.Count);
        Assert.Equal(FaultNames.SteeringGear, cleared[0].DataString("fault"));
        Assert.Equal(InjectAtS + EmergencyPowerS, cleared[0].T);
        Assert.Equal("emergencyPower", cleared[0].ItemId);
        Assert.Equal(FaultNames.MainEngine, cleared[1].DataString("fault"));
        Assert.Equal(InjectAtS + MainEngineRestoreS, cleared[1].T);
        Assert.Equal("engineRestored", cleared[1].ItemId);
        Assert.Equal(new[] { "blackout", "gyroFailure", "radarFailure" }, engine.Faults); // 只記錄的故障留到教官確認

        var phases = events.Where(e => e.Kind == SimEventKinds.PhaseChanged).ToList();
        Assert.Equal(2, phases.Count);
        Assert.Equal(("normal", "emergency", InjectAtS), (phases[0].DataString("from"), phases[0].DataString("to"), phases[0].T));
        Assert.Equal(("emergency", "degraded", InjectAtS + MainEngineRestoreS), (phases[1].DataString("from"), phases[1].DataString("to"), phases[1].T));
        Assert.Equal("degraded", engine.Phase);

        var triggered = events.Where(e => e.Kind == SimEventKinds.TimelineTriggered).Select(e => (e.ItemId, e.T)).ToList();
        Assert.Equal(new (string?, double)[] { ("pilotBrief", 30.0), ("blackout", InjectAtS), ("emergencyPower", 165.0), ("pilotPrompt", 182.0), ("vtsCall", 210.0), ("engineRestored", 480.0) }, triggered);
        Assert.Contains(events, e => e.Kind == SimEventKinds.VoiceScript && e.ItemId == "blackout" && e.DataString("text")!.StartsWith("機艙:全船失電"));
        Assert.Contains(events, e => e.Kind == SimEventKinds.Message && e.ItemId == "emergencyPower" && e.Message!.Contains("操舵恢復"));
        Assert.Equal(3, events.Count(e => e.Kind == SimEventKinds.RequiredEventPending && e.T == InjectAtS));
        var missedFirst = events.Single(e => e.Kind == SimEventKinds.RequiredEventMissed && e.ItemId == "blackoutFirstAction");
        Assert.Equal(InjectAtS + 60 + 1, missedFirst.T); // 整秒評估:期限後的下一秒判 missed
        Assert.True(events.Single(e => e.Kind == SimEventKinds.TimelineTriggered && e.ItemId == "pilotPrompt").Seq > missedFirst.Seq);
        Assert.Contains(events, e => e.Kind == SimEventKinds.RequiredEventMissed && e.ItemId == "anchorDecision");
        Assert.Contains(events, e => e.Kind == SimEventKinds.RequiredEventMissed && e.ItemId == "notifyVts");
        Assert.DoesNotContain(events, e => e.Kind == SimEventKinds.HardFail);
        Assert.DoesNotContain(events, e => e.Kind == Traffic.TrafficEventKinds.CpaAlarm && e.T < InjectAtS); // 失電前錨泊船與出港船不應造成 CPA 警報雜訊

        var summary = engine.LatestSummary;
        Assert.NotNull(summary);
        Assert.Equal("E09_blackout", summary!.ScenarioId);
        Assert.False(summary.Assessment.Passed);
        Assert.False(summary.Assessment.HardFailed);
        Assert.All(summary.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.Equal("ok", summary.Assessment.Bands.Single(b => b.Id == "minCpa").State); // 出港船左舷對左舷通過、錨泊船在航道外
        Assert.Equal("fail", summary.Assessment.Bands.Single(b => b.Id == "xtdMax").State); // 無人操作:失電後漂出走廊
        Assert.Contains(summary.Assessment.Parameters, p => p.Key == "emergency.blackout.firstAction_s" && p.Source == "ASSUMED-E09");

        var rec = RecordReader.Read(path);
        Assert.True(rec.IntegrityOk);
        Assert.Equal("E09_blackout", rec.Header.Scenario.Id);
        Assert.Equal(9, rec.Header.Scenario.Timeline.Count);
        Assert.Equal(120.0, rec.Header.Scenario.Timeline.Single(t => t.Id == "blackout").When!.AtLeast); // 參數已代入
        Assert.Empty(rec.Inputs);
        Assert.DoesNotContain(rec.Inputs, i => i.Command.Type == SimCommandType.InjectFault); // 時間表動作不進 input 行
        Assert.Equal(events.Count, rec.Events.Count);
        Assert.Single(rec.Summaries);
        Assert.True(rec.Summary!.Summary.Final);

        var result = Replayer.Replay(path, TestData.Ship, Mmg);
        Assert.True(result.IntegrityOk);
        Assert.Null(result.Warning);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} vs {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.Equal(result.EventsRecorded, result.EventsReplayed);
        Assert.True(result.SummaryMatches);
        File.Delete(path);
    }

    /// <summary>有人操作:失電後車鐘 STOP(首個相關動作)、下錨決策、VTS 報告、教官確認儀器重啟 → 必要事件 met、階段回 normal、故障全清;重播一致。</summary>
    [Fact]
    public void CrewResponseMeetsRequiredEventsAndReplays()
    {
        var script = new (long Tick, SimCommand Cmd)[]
        {
            (50 * 135, SimCommand.Telegraph(TelegraphOrder.STOP)),
            (50 * 170, SimCommand.Acknowledge("anchorReady", itemId: "blackout", role: "master", note: "備便雙錨,決定下錨")),
            (50 * 230, SimCommand.Acknowledge("report", itemId: "blackout", role: "officer", note: "VTS 已報告失電")),
            (50 * 520, SimCommand.TriggerTimeline("instrumentsRestored")),
        };
        var path = Path.Combine(TestData.TestRecordsDirectory, $"e09-crew-{Guid.NewGuid():N}.jsonl");
        var engine = new SimulationEngine(TestData.Fsb1(), E09(), Mmg);
        var events = TestData.Capture(engine);
        using (var writer = new RecordWriter(path, engine))
        {
            EngineDeterminismTests.RunScript(engine, 50 * 600, script);
            engine.Enqueue(SimCommand.EndExercise("測試結束"));
            engine.ProcessPendingCommands();
            writer.Close();
        }

        var met = events.Where(e => e.Kind == SimEventKinds.RequiredEventMet).Select(e => (e.ItemId, e.T)).ToList();
        Assert.Equal(new (string?, double)[] { ("blackoutFirstAction", 135.0), ("anchorDecision", 170.0), ("notifyVts", 230.0) }, met);
        Assert.DoesNotContain(events, e => e.Kind == SimEventKinds.RequiredEventMissed);
        Assert.DoesNotContain(events, e => e.Kind == SimEventKinds.TimelineTriggered && e.ItemId == "pilotPrompt"); // 有反應則不催促
        Assert.Contains(events, e => e.Kind == SimEventKinds.TimelineManual && e.ItemId == "instrumentsRestored" && e.DataBool("ok") == true);
        Assert.Equal(5, events.Count(e => e.Kind == SimEventKinds.FaultCleared));
        Assert.Empty(engine.Faults);
        Assert.Equal("normal", engine.Phase);
        Assert.Equal(new[] { "normal→emergency", "emergency→degraded", "degraded→normal" },
            events.Where(e => e.Kind == SimEventKinds.PhaseChanged).Select(e => $"{e.DataString("from")}→{e.DataString("to")}"));

        var summary = engine.LatestSummary!;
        Assert.All(summary.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.Equal("telegraphChange", summary.Assessment.Required.Single(r => r.Id == "blackoutFirstAction").By);
        Assert.Equal(15.0, summary.Assessment.Required.Single(r => r.Id == "blackoutFirstAction").DtS);

        var rec = RecordReader.Read(path);
        Assert.Equal(script.Length + 1, rec.Inputs.Count);
        Assert.Equal(2, rec.Summaries.Count);
        var result = Replayer.Replay(path, TestData.Ship, Mmg);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} vs {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.True(result.SummaryMatches);
        File.Delete(path);
    }
}
