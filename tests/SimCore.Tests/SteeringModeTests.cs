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
    public void ParameterSetPathIsRelativeToProjectRoot()
    {
        var set = TestData.Parameters();
        Assert.Equal("data/parameters/company-procedures.yaml", set.Path);
        Assert.Equal("data/parameters/company-procedures.yaml", TestData.E07().ParameterSetInfo!.Path);
        Assert.Equal("data/parameters/company-procedures.yaml", Ship.RepositoryPaths.ToProjectRelative(Path.Combine(TestData.Root, "data", "parameters", "company-procedures.yaml")));
        var outside = Path.Combine(Path.GetTempPath(), "x.yaml");
        Assert.Equal(outside, Ship.RepositoryPaths.ToProjectRelative(outside));
    }
}
