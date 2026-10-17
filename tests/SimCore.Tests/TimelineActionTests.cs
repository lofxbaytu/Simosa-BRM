using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>每種時間表動作的事件與引擎狀態變化(情境引擎設計第 1.1.2、3.4 節)。</summary>
public class TimelineActionTests
{
    private static SimulationEngine WithActions(string actionsYaml, string extra = "")
        => TestData.MinimalEngine($$"""
            timeline:
              - id: it
                when: { type: time, atLeast: 1 }
                actions:
            {{actionsYaml}}
            {{extra}}
            """);

    [Fact]
    public void InjectAndClearFaultRaiseEventsFromTimeline()
    {
        var e = WithActions("""
                  - injectFault: steeringGear
                  - injectFault: { name: gyroDrift, args: { rate_degPerMin: 0.5 } }
                  - injectFault: steeringGear
                  - clearFault: steeringGear
            """);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 2);
        var inj = events.Where(x => x.Kind == SimEventKinds.FaultInjected).ToList();
        Assert.Equal(3, inj.Count);
        Assert.All(inj, x => Assert.Equal(SimEventSources.Timeline, x.Source));
        Assert.All(inj, x => Assert.Equal("it", x.ItemId));
        Assert.Equal(0.5, inj[1].Data!.Value.GetProperty("args").GetProperty("rate_degPerMin").GetDouble());
        Assert.True(inj[2].DataBool("alreadyActive"));
        Assert.Contains(events, x => x.Kind == SimEventKinds.FaultCleared && x.DataString("fault") == "steeringGear");
        Assert.Equal(new[] { "gyroDrift" }, e.Faults);
        Assert.Equal(50, inj[0].Tick);
    }

    [Fact]
    public void SetEnvironmentImmediateAndTargetControlForwarded()
    {
        var e = WithActions("""
                  - setEnvironment: { wind: { trueSpeed: 20, trueDir: 90 }, current: { set: 180, drift: 1 }, waterDepth: 25, visibility_nm: 2 }
                  - targetControl: { id: T1, heading: 90, speed: 3 }
            """, """
            targets:
              - id: T1
                initial: { position: { x: 0, y: 5000 }, heading: 180, speed: 5 }
            """);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 2);
        Assert.Equal(Units.KnToMps(20), e.Environment.WindTrueSpeedMps, 9);
        Assert.Equal(Math.PI / 2, e.Environment.WindTrueDirFromRad, 9);
        Assert.Equal(Units.KnToMps(1), e.Environment.CurrentDriftMps, 9);
        Assert.Equal(25.0, e.Environment.Depth.DepthAt(0, 0));
        Assert.Equal(2.0, e.VisibilityNm);
        Assert.Equal(2.0, e.BuildState().VisibilityNm);
        Assert.Empty(e.Ramps);
        var env = events.Single(x => x.Kind == SimEventKinds.EnvironmentChange);
        Assert.Equal(0.0, env.DataNumber("ramp_s"));
        Assert.Equal(SimEventSources.Timeline, env.Source);
        Assert.True(e.Traffic.Find("T1")!.Manual);
        Assert.Contains(events, x => x.Kind == Traffic.TrafficEventKinds.Control && x.TargetId == "T1");
    }

    [Fact]
    public void VoiceScriptMessagePhaseFreezeAutopilotOwnShipTimeScale()
    {
        var e = WithActions("""
                  - voiceScript: { role: ecr, channel: phone, text: hello, lang: zh-TW, file: a.ogg }
                  - message: { to: all, text: notice, severity: warning }
                  - phase: degraded
                  - autopilot: { enabled: true, heading: 45 }
                  - ownShip: { telegraph: SAH, thruster: 0.5 }
                  - timeScale: 2
                  - freeze: { reason: pause }
            """);
        var events = TestData.Capture(e);
        var inputs = 0;
        e.CommandApplied += (_, _, _) => inputs++;
        TestData.RunTo(e, 1);
        e.Step();
        Assert.Equal(50, e.Tick); // 凍結
        Assert.True(e.Frozen);
        Assert.Equal(0, inputs); // 時間表動作不寫 input 行
        var vs = events.Single(x => x.Kind == SimEventKinds.VoiceScript);
        Assert.Equal("hello", vs.Message);
        Assert.Equal("ecr", vs.DataString("role"));
        Assert.Equal("a.ogg", vs.DataString("file"));
        var msg = events.Single(x => x.Kind == SimEventKinds.Message);
        Assert.True(msg.Visible);
        Assert.Equal("warning", msg.DataString("severity"));
        Assert.Equal("degraded", e.Phase);
        Assert.Equal("degraded", events.Single(x => x.Kind == SimEventKinds.PhaseChanged).DataString("to"));
        Assert.True(e.Autopilot.Enabled);
        Assert.Equal(Units.DegToRad(45), e.Autopilot.HeadingRad, 9);
        Assert.Contains(events, x => x.Kind == SimEventKinds.AutopilotOverride);
        Assert.Equal(TelegraphOrder.SAH, e.Telegraph);
        Assert.Equal(0.5, e.Actuators.ThrusterOrder);
        Assert.Contains(events, x => x.Kind == SimEventKinds.OwnShipOverride);
        Assert.Equal(2.0, e.TimeScale);
        Assert.Contains(events, x => x.Kind == SimEventKinds.TimeScaleChanged);
        Assert.Contains(events, x => x.Kind == SimEventKinds.Frozen && x.Message == "pause");
        Assert.Equal("degraded", e.BuildState().Scenario!.Phase);
        // 事件皆帶階段與遞增序號
        Assert.Equal(Enumerable.Range(0, events.Count).Select(i => (long)i), events.Select(x => x.Seq));
        Assert.All(events.Where(x => x.Seq > events.Single(y => y.Kind == SimEventKinds.PhaseChanged).Seq), x => Assert.Equal("degraded", x.Phase));
        e.Enqueue(SimCommand.Resume()); e.ProcessPendingCommands();
        Assert.False(e.Frozen);
        Assert.Contains(events, x => x.Kind == SimEventKinds.Resumed && x.Source == SimEventSources.Instructor);
    }

    [Fact]
    public void EndExerciseFreezesAndFinalisesSummary()
    {
        var e = WithActions("""
                  - endExercise: { reason: done }
            """);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 1);
        Assert.True(e.Ended);
        Assert.True(e.Frozen);
        Assert.True(e.LatestSummary!.Final);
        Assert.Equal("done", events.Single(x => x.Kind == SimEventKinds.ExerciseEnded).DataString("reason"));
        Assert.True(e.BuildState().Scenario!.Ended);
        e.Enqueue(SimCommand.Resume()); e.ProcessPendingCommands();
        Assert.True(e.Frozen); // 結束後不可續跑
        e.Enqueue(SimCommand.EndExercise("again")); e.ProcessPendingCommands();
        Assert.Single(events, x => x.Kind == SimEventKinds.ExerciseEnded);
    }

    [Fact]
    public void InstructorCommandsAlsoRaiseEvents()
    {
        var e = TestData.MinimalEngine();
        var events = TestData.Capture(e);
        e.Enqueue(SimCommand.InjectFault(FaultNames.MainEngine));
        e.Enqueue(SimCommand.InjectFault("gyroDrift", new { rate = 1 }));
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 10, visibilityNm: 3));
        e.Enqueue(SimCommand.Freeze());
        e.Enqueue(SimCommand.Resume());
        e.Enqueue(SimCommand.Acknowledge("notifyEcr", role: "master", note: "called"));
        e.Enqueue(SimCommand.ClearFault("all"));
        e.ProcessPendingCommands();
        Assert.All(events, x => Assert.Equal(SimEventSources.Instructor, x.Source));
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.FaultInjected));
        Assert.Equal("gyroDrift", events[1].DataString("fault"));
        Assert.Contains(events, x => x.Kind == SimEventKinds.EnvironmentChange && x.DataNumber("visibility_nm") == 3);
        Assert.Contains(events, x => x.Kind == SimEventKinds.Frozen);
        Assert.Contains(events, x => x.Kind == SimEventKinds.Resumed);
        var ack = events.Single(x => x.Kind == SimEventKinds.Acknowledge);
        Assert.Equal("notifyEcr", ack.DataString("kind"));
        Assert.Equal("master", ack.DataString("role"));
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.FaultCleared));
        Assert.Empty(e.Faults);
        Assert.Equal(3.0, e.VisibilityNm);
    }

    [Fact]
    public void ForbiddenAndUnknownActionsAreRejectedByValidate()
    {
        var ex = Assert.Throws<InvalidDataException>(() => TestData.Minimal("""
            timeline:
              - id: bad
                when: { type: time, atLeast: 1 }
                actions: [ { reset: true } ]
            """));
        Assert.Contains("不允許", ex.Message);
        Assert.Throws<InvalidDataException>(() => TestData.Minimal("""
            timeline:
              - id: bad
                when: { type: time, atLeast: 1 }
                actions: [ { explode: true } ]
            """));
        Assert.Throws<InvalidDataException>(() => TestData.Minimal("""
            timeline:
              - id: a
                when: { type: time, atLeast: 1 }
                actions: [ { arm: nope } ]
            """));
        Assert.Throws<InvalidDataException>(() => TestData.Minimal("""
            timeline:
              - id: a
                when: { type: time, atLeast: 1 }
                actions: [ { message: { to: all, text: x } } ]
              - id: a
                when: { type: time, atLeast: 2 }
                actions: [ { message: { to: all, text: y } } ]
            """));
        Assert.Throws<InvalidDataException>(() => TestData.Minimal("""
            timeline:
              - id: a
                when: { type: zone, id: nowhere }
                actions: [ { message: { to: all, text: x } } ]
            """));
    }


    /// <summary>endExercise 由時間表動作觸發:summary 定稿後不再被覆寫(兩行 summary 相同),同一步內後續項目與待執行動作不再觸發。</summary>
    [Fact]
    public void EndExerciseFromTimelineFreezesSummaryAndStopsLaterItems()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"tl-end-{Guid.NewGuid():N}.jsonl");
        var e = TestData.MinimalEngine("""
            timeline:
              - id: stop
                when: { type: time, atLeast: 5 }
                actions: [ { endExercise: { reason: 測試 } } ]
              - id: later
                when: { type: time, atLeast: 5 }
                actions: [ { message: { to: all, text: 不應出現 } } ]
              - id: delayed
                when: { type: time, atLeast: 3 }
                delay_s: 5
                actions: [ { message: { to: all, text: 延遲動作不應執行 } } ]
            """);
        var events = TestData.Capture(e);
        using (var writer = new RecordWriter(path, e, includeStates: false))
        {
            e.Run(50 * 20); // endExercise 後凍結,Step 不再推進
            writer.Close();
        }
        var ended = events.Single(x => x.Kind == SimEventKinds.ExerciseEnded);
        Assert.Equal("stop", ended.ItemId);
        Assert.Equal(ended.Seq, events[^1].Seq);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.Message);
        Assert.Contains(events, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "delayed");
        Assert.True(e.Ended && e.Frozen);
        var final = e.LatestSummary!;
        Assert.True(final.Final);
        Assert.Equal(250, (long)Math.Round(final.General.DurationS / e.Dt));

        var rec = RecordReader.Read(path);
        Assert.Equal(2, rec.Summaries.Count);
        Assert.Equal(ContractJson.Serialize(rec.Summaries[0].Summary), ContractJson.Serialize(rec.Summaries[1].Summary));
        Assert.Equal(ContractJson.Serialize(final), ContractJson.Serialize(rec.Summaries[1].Summary));
        // 定稿於 exerciseEnded 發出之前:final summary 的事件計數不含 exerciseEnded 本身,也不含之後(不會再有)的事件
        Assert.False(rec.Summaries[0].Summary.Events.ByKind.ContainsKey("exerciseEnded"));
        Assert.False(rec.Summaries[1].Summary.Events.ByKind.ContainsKey("message"));
        var replay = Replayer.Replay(path, TestData.Ship);
        Assert.True(replay.HashMatches && replay.EventSequenceMatches, replay.FirstEventMismatch);
        Assert.True(replay.SummaryMatches);
        File.Delete(path);
    }
}
