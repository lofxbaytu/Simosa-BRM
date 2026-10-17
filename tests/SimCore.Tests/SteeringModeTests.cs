using System.Text.Json;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>操舵模式契約(規劃書第 7.2 節;情境引擎設計 D32–D34):steeringMode 指令、state.steering、steeringModeChanged 事件、NFU/應急操舵行為、必要事件倒數、紀錄/重播/快照。</summary>
public class SteeringModeTests
{
    private const string Rules = """
        timeline:
          - id: jam
            when: { type: time, atLeast: 100 }
            actions: [ { injectFault: steeringGear } ]
        assessment:
          required:
            - id: first
              after: { event: faultInjected, itemId: jam }
              expect: { any: [ telegraphChange, steeringModeChange, { acknowledge: { kind: steeringMode } } ] }
              within_s: 60
              penalty: 20
        """;

    [Fact]
    public void DefaultStateIsFuPumpOneAndCommandParsesFromSchemaExample()
    {
        var e = TestData.MinimalEngine();
        e.Run(10);
        var st = e.BuildState();
        Assert.Equal(SteeringMode.Fu, st.Steering!.Mode);
        Assert.Equal("1", st.Steering.Pump);
        Assert.Null(st.Steering.NfuActive);
        var json = ContractJson.Serialize(st);
        Assert.Contains("\"steering\":{\"mode\":\"fu\",\"pump\":\"1\"}", json);

        var cmd = SimCommand.Parse("""{"type":"steeringMode","value":"nfu","args":{"pump":"both","direction":"starboard"}}""")!;
        Assert.Equal(SimCommandType.SteeringMode, cmd.Type);
        Assert.Equal("nfu", cmd.ValueAsString());
        Assert.Contains("\"type\":\"steeringMode\",\"value\":\"emergency\"", SimCommand.SteeringMode(SteeringMode.Emergency).ToJson());
    }

    [Fact]
    public void NfuDrivesRudderAtGearRateAndStopHoldsAngle()
    {
        var e = TestData.MinimalEngine();
        var events = TestData.Capture(e);
        var rate = e.Actuators.Parameters.RudderRateDegPerSec;
        e.Run(50);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, pump: "both", direction: "starboard"));
        e.ProcessPendingCommands();
        var changed = Assert.Single(events, x => x.Kind == SimEventKinds.SteeringModeChanged);
        Assert.Equal(SimEventSources.Instructor, changed.Source);
        Assert.Equal("fu", changed.DataString("from"));
        Assert.Equal("nfu", changed.DataString("to"));
        Assert.Equal("both", changed.DataString("pump"));
        Assert.Equal(SteeringMode.Nfu, e.SteeringMode);
        Assert.Equal(1, e.NfuDirection);
        Assert.True(e.BuildState().Steering!.NfuActive);
        e.Run(100); // 2 s:舵以舵機速率右轉(暫代模型:純速率限制)
        Assert.Equal(2.0 * rate, e.Actuators.RudderDeg, 6);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "stop"));
        e.ProcessPendingCommands();
        var held = e.Actuators.RudderDeg;
        Assert.Equal(0, e.NfuDirection);
        Assert.False(e.BuildState().Steering!.NfuActive);
        e.Run(100);
        Assert.Equal(held, e.Actuators.RudderDeg, 6); // 推桿放開舵停在原處
        Assert.Single(events, x => x.Kind == SimEventKinds.SteeringModeChanged); // 同模式內的推桿不發模式事件
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "port"));
        e.ProcessPendingCommands();
        e.Run(50 * 10);
        Assert.True(e.Actuators.RudderDeg < held - 5.0);
        // 隨動舵令切回 fu
        e.Enqueue(SimCommand.Rudder(0));
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Fu, e.SteeringMode);
        Assert.Equal("nfu", events.Last(x => x.Kind == SimEventKinds.SteeringModeChanged).DataString("from"));
        Assert.Equal("both", e.SteeringPump); // 泵選擇保留
    }

    [Fact]
    public void EmergencyModeMovesJammedRudderAtHalfRate()
    {
        var e = TestData.MinimalEngine();
        var rate = e.Actuators.Parameters.RudderRateDegPerSec;
        Assert.Null(TestData.Fsb1().Rudder.SteeringGear?.EmergencyHardOverTime35_s);
        Assert.Equal(rate / 2.0, e.EmergencyRudderRateDegPerSec, 9);
        e.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear));
        e.Enqueue(SimCommand.Rudder(20));
        e.ProcessPendingCommands();
        e.Run(100);
        Assert.Equal(0.0, e.Actuators.RudderDeg); // 卡舵
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Emergency));
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Emergency, e.SteeringMode);
        e.Run(200); // 4 s × 半速率
        Assert.Equal(4.0 * rate / 2.0, e.Actuators.RudderDeg, 6);
        Assert.Contains(FaultNames.SteeringGear, e.Faults); // 故障仍在,只是改走應急路徑
        e.Enqueue(SimCommand.Rudder(-10)); // 電話下舵令:仍為 emergency
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Emergency, e.SteeringMode);
        e.Enqueue(SimCommand.ClearFault(FaultNames.SteeringGear));
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Fu));
        e.ProcessPendingCommands();
        e.Run(50);
        Assert.Equal(SteeringMode.Fu, e.SteeringMode);
    }

    [Fact]
    public void AutopilotModeIsEquivalentToAutopilotCommandAndStaysInSync()
    {
        var e = TestData.MinimalEngine(headingDeg: 90);
        var events = TestData.Capture(e);
        e.Run(50);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Autopilot, heading: 120));
        e.ProcessPendingCommands();
        Assert.True(e.Autopilot.Enabled);
        Assert.Equal(120.0, Units.RadToDeg(e.Autopilot.HeadingRad), 9);
        Assert.Equal(SteeringMode.Autopilot, e.BuildState().Steering!.Mode);
        e.Enqueue(SimCommand.Autopilot(false));
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Fu, e.SteeringMode);
        e.Enqueue(SimCommand.Autopilot(true, heading: 100));
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Autopilot, e.SteeringMode);
        e.Enqueue(SimCommand.Rudder(5));
        e.ProcessPendingCommands();
        Assert.False(e.Autopilot.Enabled);
        Assert.Equal(SteeringMode.Fu, e.SteeringMode);
        var kinds = events.Where(x => x.Kind == SimEventKinds.SteeringModeChanged).Select(x => $"{x.DataString("from")}>{x.DataString("to")}").ToList();
        Assert.Equal(new[] { "fu>autopilot", "autopilot>fu", "fu>autopilot", "autopilot>fu" }, kinds);
        e.Run(50);
        Assert.Equal(4, e.LatestSummary is null ? 4 : e.Assessment!.Metrics.State.AutopilotSwitches);
    }

    [Fact]
    public void SteeringModeChangeIsFirstFaultActionAndMeetsRequiredEvent()
    {
        var e = TestData.MinimalEngine(Rules);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 130);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Emergency, pump: "2"));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 140);
        var rec = e.Assessment!.Faults.State.Records.Single();
        Assert.Equal(ActionKinds.SteeringModeChange, rec.FirstAction!.Kind);
        Assert.Equal(30.0, rec.FirstAction.DtS, 9);
        Assert.True(rec.WithinFirstAction);
        var req = e.LatestSummary!.Assessment.Required.Single();
        Assert.Equal("met", req.State);
        Assert.Equal(ActionKinds.SteeringModeChange, req.By);
        Assert.Equal(30.0, req.DtS!.Value, 9);
        var met = events.Single(x => x.Kind == SimEventKinds.RequiredEventMet);
        Assert.Equal(60.0, met.DataNumber("within_s"));

        // 無規則時 steeringMode 亦為預設的相關動作
        var plain = TestData.MinimalEngine();
        plain.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear));
        plain.ProcessPendingCommands();
        TestData.RunTo(plain, 5);
        plain.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu));
        plain.ProcessPendingCommands();
        Assert.Equal(ActionKinds.SteeringModeChange, plain.Assessment!.Faults.State.Records.Single().FirstAction!.Kind);
    }

    [Fact]
    public void RequiredEventPendingCarriesDeadlineAndFollowsTriggeringEvent()
    {
        var e = TestData.MinimalEngine(Rules);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 170);
        var fault = events.Single(x => x.Kind == SimEventKinds.FaultInjected);
        var pending = events.Single(x => x.Kind == SimEventKinds.RequiredEventPending);
        Assert.Equal("first", pending.ItemId);
        Assert.Equal(SimEventSources.Assessment, pending.Source);
        Assert.Equal(fault.Seq + 1, pending.Seq); // 緊接在觸發事件之後(seq 與紀錄順序一致)
        Assert.Equal(fault.Tick, pending.Tick);
        Assert.Equal(SimEventKinds.FaultInjected, pending.DataString("after"));
        Assert.Equal("jam", pending.DataString("afterItemId"));
        Assert.Equal(fault.Seq, (long)pending.DataNumber("afterSeq")!.Value);
        Assert.Equal(100.0, pending.DataNumber("start_t"));
        Assert.Equal(60.0, pending.DataNumber("within_s"));
        Assert.Equal(160.0, pending.DataNumber("deadline_t"));
        Assert.Equal(20.0, pending.DataNumber("penalty"));
        var missed = events.Single(x => x.Kind == SimEventKinds.RequiredEventMissed);
        Assert.True(missed.T > pending.DataNumber("deadline_t"));
        Assert.Equal(60.0, missed.DataNumber("within_s"));
        Assert.Equal(1, e.LatestSummary!.Events.ByKind[SimEventKinds.RequiredEventPending]);
    }

    [Fact]
    public void SteeringModeIsRecordedReplayedSnapshottedAndHashed()
    {
        var script = new (long Tick, SimCommand Cmd)[]
        {
            (100, SimCommand.SteeringMode(SteeringMode.Nfu, pump: "both", direction: "port")),
            (300, SimCommand.SteeringMode(SteeringMode.Nfu, direction: "stop")),
            (500, SimCommand.InjectFault(FaultNames.SteeringGear)),
            (700, SimCommand.SteeringMode(SteeringMode.Emergency)),
            (900, SimCommand.Rudder(10)),
            (1300, SimCommand.SteeringMode(SteeringMode.Autopilot, heading: 30)),
            (1700, SimCommand.SteeringMode(SteeringMode.Fu)),
        };
        var path = Path.Combine(TestData.TestRecordsDirectory, $"steering-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.MinimalEngine(Rules, seed: 11);
        using (var writer = new RecordWriter(path, engine))
        {
            EngineDeterminismTests.RunScript(engine, 2000, script);
            writer.Close();
        }
        var rec = RecordReader.Read(path);
        Assert.Equal(7, rec.Inputs.Count);
        Assert.Equal(5, rec.Inputs.Count(i => i.Command.Type == SimCommandType.SteeringMode));
        Assert.Contains(rec.Events, x => x.Event.Kind == SimEventKinds.SteeringModeChanged && x.Event.DataString("to") == "emergency");
        var result = Replayer.Replay(path, TestData.Ship);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} vs {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        File.Delete(path);

        // 快照往返(JSON)保留模式/泵/推桿;還原後接續雜湊一致
        var reference = TestData.MinimalEngine(Rules, seed: 11);
        EngineDeterminismTests.RunScript(reference, 2000, script);
        var a = TestData.MinimalEngine(Rules, seed: 11);
        EngineDeterminismTests.RunScript(a, 200, script);
        var snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
        Assert.Equal(SteeringMode.Nfu, snap.Steering!.Mode);
        Assert.Equal("both", snap.Steering.Pump);
        Assert.Equal(-1, snap.Steering.NfuDirection);
        var b = TestData.MinimalEngine(Rules, seed: 11);
        b.Restore(snap);
        Assert.Equal(SteeringMode.Nfu, b.SteeringMode);
        Assert.Equal(-1, b.NfuDirection);
        EngineDeterminismTests.RunScript(b, 1800, script.Where(s => s.Tick >= 200));
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal(reference.Motion, b.Motion);

        // 非預設操舵模式進雜湊:nfu(推桿停、舵令等於舵角)與 fu 的物理狀態相同,雜湊不同
        var fu = TestData.MinimalEngine(seed: 3);
        var nfu = TestData.MinimalEngine(seed: 3);
        fu.Run(50);
        nfu.Run(50);
        nfu.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu));
        nfu.Run(50);
        fu.Run(50);
        Assert.Equal(fu.Motion, nfu.Motion);
        Assert.Equal(fu.Actuators.RudderDeg, nfu.Actuators.RudderDeg);
        Assert.NotEqual(fu.StateHash, nfu.StateHash);
    }

    [Fact]
    public void LeavingNfuBySteeringModeHoldsRudderInsteadOfRunningToHardOver()
    {
        // 審查發現:NFU 推桿中以 steeringMode fu/emergency 離開(非 rudder 指令)時舵令仍為滿舵,舵在新模式下繼續走到滿舵(D35)
        var e = TestData.MinimalEngine();
        var rate = e.Actuators.Parameters.RudderRateDegPerSec;
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "starboard"));
        e.ProcessPendingCommands();
        e.Run(100); // 2 s
        var held = e.Actuators.RudderDeg;
        Assert.Equal(2.0 * rate, held, 6);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Fu));
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Fu, e.SteeringMode);
        Assert.Equal(held, e.Actuators.RudderOrderDeg, 6); // 舵令同步到目前舵角
        Assert.Null(e.BuildState().Steering!.NfuActive);
        e.Run(500);
        Assert.Equal(held, e.Actuators.RudderDeg, 6); // 舵停在原處,不再往 35° 走
        e.Enqueue(SimCommand.Rudder(10)); // 隨動舵令照常生效
        e.ProcessPendingCommands();
        e.Run(500);
        Assert.Equal(10.0, e.Actuators.RudderDeg, 6);

        // nfu port → emergency:同樣停在原處;之後 rudder(電話下令)以應急速率移動
        var f = TestData.MinimalEngine();
        f.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "port"));
        f.ProcessPendingCommands();
        f.Run(100);
        var heldPort = f.Actuators.RudderDeg;
        Assert.Equal(-2.0 * rate, heldPort, 6);
        f.Enqueue(SimCommand.SteeringMode(SteeringMode.Emergency));
        f.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Emergency, f.SteeringMode);
        Assert.Equal(heldPort, f.Actuators.RudderOrderDeg, 6);
        f.Run(500);
        Assert.Equal(heldPort, f.Actuators.RudderDeg, 6);

        // nfu → autopilot:自動舵之後逐步覆寫舵令,但切換瞬間同樣不留滿舵舵令
        var g = TestData.MinimalEngine();
        g.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "starboard"));
        g.ProcessPendingCommands();
        g.Run(100);
        g.Enqueue(SimCommand.SteeringMode(SteeringMode.Autopilot));
        g.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Autopilot, g.SteeringMode);
        Assert.Equal(g.Actuators.RudderDeg, g.Actuators.RudderOrderDeg, 6);
    }

    [Fact]
    public void EnteringNfuWithUnknownDirectionHoldsRudderAndDoesNotKeepFuOrder()
    {
        // 審查發現:首次進入 nfu 時 direction 非法 → 舵令沿用 FU 舵令 20°、舵繼續走但 nfuActive=false(D35:進入 nfu 先同步舵令;不明方向視同 stop)
        var e = TestData.MinimalEngine();
        var rate = e.Actuators.Parameters.RudderRateDegPerSec;
        e.Enqueue(SimCommand.Rudder(20));
        e.ProcessPendingCommands();
        e.Run(50); // 1 s
        var held = e.Actuators.RudderDeg;
        Assert.Equal(rate, held, 6);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "left"));
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Nfu, e.SteeringMode);
        Assert.Equal(0, e.NfuDirection);
        Assert.False(e.BuildState().Steering!.NfuActive);
        Assert.Equal(held, e.Actuators.RudderOrderDeg, 6);
        e.Run(500);
        Assert.Equal(held, e.Actuators.RudderDeg, 6); // 舵停在進入時的角度,不走到 20°

        // 推桿中送不明方向:視同放開推桿
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "starboard"));
        e.ProcessPendingCommands();
        e.Run(100);
        var held2 = e.Actuators.RudderDeg;
        Assert.True(held2 > held + 1.0);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu, direction: "right"));
        e.ProcessPendingCommands();
        Assert.Equal(0, e.NfuDirection);
        e.Run(100);
        Assert.Equal(held2, e.Actuators.RudderDeg, 6);

        // 未給方向進入 nfu(既有語意):同樣停在原處
        var f = TestData.MinimalEngine();
        f.Enqueue(SimCommand.Rudder(20));
        f.ProcessPendingCommands();
        f.Run(50);
        var heldF = f.Actuators.RudderDeg;
        f.Enqueue(SimCommand.SteeringMode(SteeringMode.Nfu));
        f.ProcessPendingCommands();
        f.Run(500);
        Assert.Equal(heldF, f.Actuators.RudderDeg, 6);
    }

    [Fact]
    public void AutopilotCannotBeEnabledInEmergencySteering()
    {
        // 審查發現:emergency + steeringGear 故障下 autopilot{enabled:true} 把模式改成 autopilot,舵再度卡住而自動舵「啟用」卻無效(D35:拒絕並發 commandRejected)
        var e = TestData.MinimalEngine();
        var events = TestData.Capture(e);
        var rate = e.Actuators.Parameters.RudderRateDegPerSec;
        e.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear));
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Emergency));
        e.Enqueue(SimCommand.Rudder(20));
        e.ProcessPendingCommands();
        e.Run(50);
        var before = e.Actuators.RudderDeg;
        Assert.Equal(rate / 2.0, before, 6);
        var modeEventsBefore = events.Count(x => x.Kind == SimEventKinds.SteeringModeChanged);

        e.Enqueue(SimCommand.Autopilot(true, heading: 60));
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Emergency, e.SteeringMode);
        Assert.False(e.Autopilot.Enabled);
        Assert.Equal(20.0, e.Actuators.RudderOrderDeg); // 指令整個不生效(航向也不改)
        var rejected = Assert.Single(events, x => x.Kind == SimEventKinds.CommandRejected);
        Assert.Equal(SimEventSources.Instructor, rejected.Source);
        Assert.Equal("autopilot", rejected.DataString("command"));
        Assert.Equal(CommandRejectReasons.EmergencySteering, rejected.DataString("reason"));
        Assert.Equal(modeEventsBefore, events.Count(x => x.Kind == SimEventKinds.SteeringModeChanged));
        e.Run(200);
        Assert.Equal(before + 4.0 * rate / 2.0, e.Actuators.RudderDeg, 6); // 舵繼續以應急速率走向 20°,沒有再卡住

        // steeringMode autopilot 走同一規則;autopilot {enabled:false} 照常(無事可做、不拒絕)
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Autopilot, heading: 60));
        e.Enqueue(SimCommand.Autopilot(false));
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Emergency, e.SteeringMode);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.CommandRejected));
        Assert.Equal("steeringMode", events.Last(x => x.Kind == SimEventKinds.CommandRejected).DataString("command"));
        Assert.Equal(0, e.Assessment!.Metrics.State.AutopilotSwitches);

        // 時間表的 autopilot 動作亦拒絕(source timeline、無 autopilotOverride)
        var t = TestData.MinimalEngine("""
            timeline:
              - id: ap
                when: { type: time, atLeast: 5 }
                actions: [ { autopilot: { enabled: true, heading: 90 } } ]
            """);
        var tEvents = TestData.Capture(t);
        t.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear));
        t.Enqueue(SimCommand.SteeringMode(SteeringMode.Emergency));
        t.ProcessPendingCommands();
        TestData.RunTo(t, 10);
        Assert.Equal(SteeringMode.Emergency, t.SteeringMode);
        Assert.False(t.Autopilot.Enabled);
        Assert.DoesNotContain(tEvents, x => x.Kind == SimEventKinds.AutopilotOverride);
        var tr = Assert.Single(tEvents, x => x.Kind == SimEventKinds.CommandRejected);
        Assert.Equal(SimEventSources.Timeline, tr.Source);
        Assert.Equal("ap", tr.ItemId);

        // 先切回 fu(故障已清)再啟用自動舵才有效
        e.Enqueue(SimCommand.ClearFault(FaultNames.SteeringGear));
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Fu));
        e.Enqueue(SimCommand.Autopilot(true, heading: 60));
        e.ProcessPendingCommands();
        Assert.Equal(SteeringMode.Autopilot, e.SteeringMode);
        Assert.True(e.Autopilot.Enabled);
    }

    [Fact]
    public void SteeringModeAutopilotHeadingChangeIsObservedLikeAutopilotCommand()
    {
        // 審查發現:steeringMode autopilot {heading} 不觀察 autopilotHeading,經此路徑改航向的學員不會被記到 encounterFirstAction(D35)
        const string headOn = """
            targets:
              - id: T1
                initial: { position: { x: 0, y: 7408 }, heading: 180, speed: 6 }
            assessment:
              minCpa_nm: 0.5
              encounter: { detectionRange_nm: 3 }
            """;
        var e = TestData.MinimalEngine(headOn, speedKn: 8);
        var events = TestData.Capture(e);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Autopilot, heading: 0));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 400);
        Assert.Single(events, x => x.Kind == SimEventKinds.EncounterStarted && x.TargetId == "T1");
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.EncounterFirstAction);
        e.Enqueue(SimCommand.SteeringMode(SteeringMode.Autopilot, heading: 40));
        e.ProcessPendingCommands();
        Assert.Equal(40.0, Units.RadToDeg(e.Autopilot.HeadingRad), 9);
        var first = Assert.Single(events, x => x.Kind == SimEventKinds.EncounterFirstAction && x.TargetId == "T1");
        Assert.Equal("autopilot", first.DataString("action")); // EncounterTracker 以指令名稱記首次行動

        // 與 autopilot 指令同:未給 heading(保持艏向)不算改航向;< 1° 不算
        var f = TestData.MinimalEngine(headOn, speedKn: 8);
        var fEvents = TestData.Capture(f);
        f.Enqueue(SimCommand.Autopilot(true, heading: 0));
        f.ProcessPendingCommands();
        TestData.RunTo(f, 400);
        f.Enqueue(SimCommand.SteeringMode(SteeringMode.Autopilot, pump: "2"));
        f.Enqueue(SimCommand.SteeringMode(SteeringMode.Autopilot, heading: 0.5));
        f.ProcessPendingCommands();
        Assert.DoesNotContain(fEvents, x => x.Kind == SimEventKinds.EncounterFirstAction);
        f.Enqueue(SimCommand.Autopilot(true, heading: 40));
        f.ProcessPendingCommands();
        Assert.Single(fEvents, x => x.Kind == SimEventKinds.EncounterFirstAction && x.TargetId == "T1");
    }

    [Fact]
    public void ParameterSetPathIsRelativeToProjectRoot()
    {
        var set = TestData.Parameters();
        Assert.Equal("data/parameters/company-procedures.yaml", set.Path);
        Assert.Equal("data/parameters/company-procedures.yaml", TestData.E07().ParameterSetInfo!.Path);
        Assert.Equal("data/parameters/company-procedures.yaml", Ship.RepositoryPaths.ToProjectRelative(Path.Combine(TestData.Root, "data", "parameters", "company-procedures.yaml")));
        var outside = Path.Combine(Path.GetTempPath(), "x.yaml");
        Assert.Equal(outside, Ship.RepositoryPaths.ToProjectRelative(outside));
    }

    [Fact]
    public void SteeringModeAutopilotWithoutHeadingHoldsIndicatedHeadingUnderGyroDrift()
    {
        // 審查發現:漂移中以 steeringMode autopilot(不帶 heading)接自動舵,設定航向須取電羅經指示值(自動舵與偏航警報都用指示值)
        var e = TestData.MinimalEngine("alerts: { offHeading_deg: 5, offHeadingDelay_s: 10 }", speedKn: 10);
        var events = TestData.Capture(e);
        void Do(SimCommand c) { e.Enqueue(c); e.ProcessPendingCommands(); }
        Do(SimCommand.Autopilot(true, heading: 0));
        TestData.RunTo(e, 10);
        Do(SimCommand.InjectFault("gyroDrift", new { rate_degPerMin = 1.0 }));
        TestData.RunTo(e, 610);
        Do(SimCommand.Rudder(0)); // 下舵令解除自動舵
        Assert.False(e.BuildState().Autopilot!.Enabled);
        TestData.RunTo(e, 640);
        Do(SimCommand.SteeringMode(SteeringMode.Autopilot));
        var s = e.BuildState();
        Assert.True(s.Autopilot!.Enabled);
        Assert.Equal(s.Heading, s.Autopilot.SetHeading, 6); // 保持當下羅經讀數
        var truthAtEngage = s.Truth!.Heading;
        TestData.RunTo(e, 760);
        s = e.BuildState();
        Assert.InRange(Units.WrapDeg180(s.Truth!.Heading - truthAtEngage), -6.0, 6.0); // 120 s 內不會因設定航向錯誤而大幅轉向
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "offHeading");
    }
}
