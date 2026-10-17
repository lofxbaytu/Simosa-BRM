using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>時間表觸發條件與項目狀態機(情境引擎設計第 1.1.1、3.3、7.1 節)。</summary>
public class TimelineTriggerTests
{
    private const string RouteAndZones = """
        route:
          xtdLimit_m: 50
          waypoints:
            - { name: A, x: 0, y: -1000 }
            - { name: B, x: 0, y: 3000 }
            - { name: C, x: 2000, y: 3000 }
        zones:
          - id: box
            type: noGo
            polygon: [ { x: -500, y: 500 }, { x: 500, y: 500 }, { x: 500, y: 1500 }, { x: -500, y: 1500 } ]
        targets:
          - id: T1
            initial: { position: { x: 0, y: 3704 }, heading: 180, speed: 0 }
        """;

    private static Situation Sit(SimulationEngine e, double x = 0, double y = 0, double headingDeg = 0, double sog = 8, double stw = 8, double ukc = 20, TelegraphOrder tg = TelegraphOrder.HAH, bool ap = false)
        => new()
        {
            Tick = e.Tick, Time = e.Time, X = x, Y = y, HeadingRad = Units.DegToRad(headingDeg), SogKn = sog, StwKn = stw, RotDegPerMin = 0,
            RudderDeg = 0, RudderOrderDeg = 0, Telegraph = tg, Rpm = 0, ThrusterOrder = 0, AutopilotEnabled = ap, UkcM = ukc, SquatM = 0, WaterDepthM = 30,
            MaxDraftM = 5.0, LoaM = 100, VelE = 0, VelN = Units.KnToMps(sog),
        };

    private static TriggerCondition C(string yaml)
        => ContractJson.Deserialize<TriggerCondition>(YamlJson.ToJson(yaml))!;

    [Fact]
    public void LeafConditionsEvaluateAgainstState()
    {
        var e = TestData.MinimalEngine(RouteAndZones);
        var tl = e.Timeline!;
        TestData.RunTo(e, 10);
        var s = Sit(e);
        Assert.True(tl.Evaluate(C("{ type: time, atLeast: 10 }"), s));
        Assert.False(tl.Evaluate(C("{ type: time, atLeast: 11 }"), s));
        Assert.True(tl.Evaluate(C("{ type: ownDistance, point: { x: 0, y: 1852 }, lessThan: 1.1 }"), s));
        Assert.False(tl.Evaluate(C("{ type: ownDistance, point: { x: 0, y: 1852 }, lessThan: 0.9 }"), s));
        Assert.True(tl.Evaluate(C("{ type: ownDistance, waypoint: B, greaterThan: 1.5, lessThan: 1.7 }"), s));
        Assert.True(tl.Evaluate(C("{ type: ownDistance, target: T1, lessThan: 2.1 }"), s));
        Assert.True(tl.Evaluate(C("{ type: ownHeading, greaterThan: 350, lessThan: 10 }"), Sit(e, headingDeg: 5)));
        Assert.False(tl.Evaluate(C("{ type: ownHeading, greaterThan: 350, lessThan: 10 }"), Sit(e, headingDeg: 20)));
        Assert.True(tl.Evaluate(C("{ type: ownHeading, greaterThan: 90 }"), Sit(e, headingDeg: 100)));
        Assert.True(tl.Evaluate(C("{ type: ownSpeed, lessThan: 9 }"), s));
        Assert.True(tl.Evaluate(C("{ type: ownSpeed, metric: stw, greaterThan: 7 }"), Sit(e, sog: 2, stw: 8)));
        Assert.True(tl.Evaluate(C("{ type: ukc, lessThan: 1 }"), Sit(e, ukc: 0.5)));
        Assert.True(tl.Evaluate(C("{ type: ukc, lessThanFractionOfDraft: 0.2 }"), Sit(e, ukc: 0.9)));
        Assert.False(tl.Evaluate(C("{ type: ukc, lessThanFractionOfDraft: 0.2 }"), Sit(e, ukc: 1.1)));
        Assert.True(tl.Evaluate(C("{ type: routeLeg, atLeast: 0 }"), s));
        Assert.False(tl.Evaluate(C("{ type: routeLeg, atLeast: 1 }"), s));
        Assert.True(tl.Evaluate(C("{ type: routeLeg, equals: 0 }"), s));
        Assert.False(tl.Evaluate(C("{ type: routeLeg, passedWaypoint: B }"), s));
        Assert.False(tl.Evaluate(C("{ type: zone, id: box, inside: true }"), s));
        Assert.True(tl.Evaluate(C("{ type: zone, id: box, inside: false }"), s));
        Assert.True(tl.Evaluate(C("{ type: phase, equals: normal }"), s));
        Assert.False(tl.Evaluate(C("{ type: phase, equals: degraded }"), s));
        Assert.True(tl.Evaluate(C("{ type: fault, name: steeringGear, active: false }"), s));
        Assert.True(tl.Evaluate(C("{ type: telegraph, in: [HAH, FAH] }"), s));
        Assert.False(tl.Evaluate(C("{ type: telegraph, notIn: [HAH] }"), s));
        Assert.True(tl.Evaluate(C("{ type: autopilot, enabled: false }"), s));
        Assert.False(tl.Evaluate(C("{ type: manual }"), s));
        Assert.False(tl.Evaluate(C("{ type: event, kind: faultInjected }"), s));

        // 事件計數與 since
        e.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear));
        e.ProcessPendingCommands();
        Assert.True(tl.Evaluate(C("{ type: event, kind: faultInjected }"), s));
        Assert.True(tl.Evaluate(C("{ type: fault, name: steeringGear, active: true }"), s));
        Assert.False(tl.Evaluate(C("{ type: event, kind: faultInjected, count: { atLeast: 2 } }"), s));
        TestData.RunTo(e, 15);
        Assert.True(tl.Evaluate(C("{ type: time, atLeast: 5, since: { event: faultInjected } }"), Sit(e)));
        Assert.False(tl.Evaluate(C("{ type: time, atLeast: 6, since: { event: faultInjected } }"), Sit(e)));
        Assert.False(tl.Evaluate(C("{ type: time, atLeast: 0, since: { event: grounding } }"), Sit(e)));
        // xtd:自船在航段 0 右側 30 m
        TestData.RunTo(e, 16);
        var xtd = e.Assessment!.Route!.State.Xtd!.Value;
        Assert.True(tl.Evaluate(C("{ type: xtd, greaterThan: " + (Math.Abs(xtd) - 0.01).ToString(System.Globalization.CultureInfo.InvariantCulture) + " }"), Sit(e)));
        Assert.False(tl.Evaluate(C("{ type: xtd, greaterThan: 1000 }"), Sit(e)));
    }

    [Fact]
    public void CompositeConditionsCombine()
    {
        var e = TestData.MinimalEngine();
        var tl = e.Timeline!;
        TestData.RunTo(e, 5);
        var s = Sit(e);
        Assert.True(tl.Evaluate(C("{ all: [ { type: time, atLeast: 5 }, { type: ownSpeed, lessThan: 9 } ] }"), s));
        Assert.False(tl.Evaluate(C("{ all: [ { type: time, atLeast: 5 }, { type: ownSpeed, lessThan: 1 } ] }"), s));
        Assert.True(tl.Evaluate(C("{ any: [ { type: time, atLeast: 50 }, { type: ownSpeed, lessThan: 9 } ] }"), s));
        Assert.True(tl.Evaluate(C("{ not: { type: time, atLeast: 50 } }"), s));
        Assert.True(tl.Evaluate(C("{ all: [ { not: { any: [ { type: time, atLeast: 50 } ] } }, { type: phase, equals: normal } ] }"), s));
    }

    [Fact]
    public void HoldForRestartsWhenConditionBreaks()
    {
        var e = TestData.MinimalEngine("""
            timeline:
              - id: held
                holdFor_s: 3
                when: { type: fault, name: steeringGear, active: true }
                actions: [ { message: { to: instructor, text: held } } ]
            """);
        var events = TestData.Capture(e);
        e.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear)); e.ProcessPendingCommands();
        TestData.RunTo(e, 2);
        e.Enqueue(SimCommand.ClearFault(FaultNames.SteeringGear)); e.ProcessPendingCommands();
        TestData.RunTo(e, 4);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.TimelineTriggered);
        e.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear)); e.ProcessPendingCommands(); // t = 4
        TestData.RunTo(e, 10);
        var fired = events.Single(x => x.Kind == SimEventKinds.TimelineTriggered);
        Assert.Equal(8.0, fired.T); // 5,6,7 連續成立 → 8 s 觸發(4 s 評估步條件成立起算 + 3 s)
        Assert.Equal(1, e.Timeline!.StateOf("held")!.FireCount);
    }

    [Fact]
    public void RepeatModesAndCooldown()
    {
        var e = TestData.MinimalEngine("""
            timeline:
              - id: once
                when: { type: time, atLeast: 1 }
                actions: [ { message: { to: instructor, text: a } } ]
              - id: always
                repeat: always
                cooldown_s: 3
                when: { type: time, atLeast: 1 }
                actions: [ { message: { to: instructor, text: b } } ]
              - id: every
                repeat: { every_s: 2, max: 2 }
                when: { type: time, atLeast: 1 }
                actions: [ { message: { to: instructor, text: c } } ]
            """);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 12);
        var fires = events.Where(x => x.Kind == SimEventKinds.TimelineTriggered).ToList();
        Assert.Equal(new[] { 1.0 }, fires.Where(x => x.ItemId == "once").Select(x => x.T));
        Assert.Equal(new[] { 1.0, 4.0, 7.0, 10.0 }, fires.Where(x => x.ItemId == "always").Select(x => x.T));
        Assert.Equal(new[] { 1.0, 3.0 }, fires.Where(x => x.ItemId == "every").Select(x => x.T));
        Assert.Equal(2, fires.First(x => x.ItemId == "always" && x.T == 4.0).DataNumber("fireCount"));
    }

    [Fact]
    public void DelayedActionsRunLaterAndArmDisarmWork()
    {
        var e = TestData.MinimalEngine("""
            timeline:
              - id: delayed
                delay_s: 2
                when: { type: time, atLeast: 1 }
                actions: [ { injectFault: steeringGear } ]
              - id: gate
                armed: false
                when: { type: time, atLeast: 1 }
                actions: [ { message: { to: instructor, text: gated } } ]
              - id: opener
                when: { type: time, atLeast: 5 }
                actions: [ { arm: gate } ]
              - id: closer
                when: { type: time, atLeast: 7 }
                actions: [ { disarm: opener }, { disarm: gate } ]
            """);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 2.5);
        Assert.Contains(events, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "delayed" && x.T == 1.0);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.FaultInjected);
        Assert.Equal(1, e.BuildState().Scenario!.TimelinePending);
        TestData.RunTo(e, 4);
        var inj = events.Single(x => x.Kind == SimEventKinds.FaultInjected);
        Assert.Equal(3.0, inj.T);
        Assert.Equal(SimEventSources.Timeline, inj.Source);
        Assert.Equal("delayed", inj.ItemId);
        Assert.Contains(FaultNames.SteeringGear, e.Faults);
        Assert.DoesNotContain(events, x => x.ItemId == "gate" && x.Kind == SimEventKinds.TimelineTriggered);
        TestData.RunTo(e, 10);
        Assert.Contains(events, x => x.Kind == SimEventKinds.TimelineArmed && x.ItemId == "gate" && x.T == 5.0);
        Assert.Contains(events, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "gate" && x.T == 6.0); // gate 排在 opener 之前,下一秒才看到 armed
        Assert.Contains(events, x => x.Kind == SimEventKinds.TimelineDisarmed && x.ItemId == "gate" && x.T == 7.0);
        Assert.False(e.Timeline!.StateOf("gate")!.Armed);
    }

    [Fact]
    public void TriggerChainIsLimitedPerStep()
    {
        var items = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"  - id: i{i}\n    repeat: always\n    when: {{ type: time, atLeast: 0 }}\n    actions: [ {{ message: {{ to: instructor, text: x }} }} ]"));
        var e = TestData.MinimalEngine("timeline:\n" + items);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 1);
        Assert.Equal(TimelineEngine.MaxTriggersPerStep, events.Count(x => x.Kind == SimEventKinds.TimelineTriggered));
        Assert.Contains(events, x => x.Kind == SimEventKinds.Message && x.Message!.Contains("16"));
    }

    [Fact]
    public void ManualItemsOnlyFireByCommandAndForceOverridesRules()
    {
        var e = TestData.MinimalEngine("""
            timeline:
              - id: man
                manual: true
                actions: [ { phase: emergency } ]
              - id: auto
                when: { type: time, atLeast: 1000 }
                actions: [ { message: { to: all, text: hi } } ]
            """);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 3);
        Assert.Empty(events);
        e.Enqueue(SimCommand.TriggerTimeline("auto")); e.ProcessPendingCommands();
        Assert.False(events.Single(x => x.Kind == SimEventKinds.TimelineManual).DataBool("ok"));
        e.Enqueue(SimCommand.TriggerTimeline("man")); e.ProcessPendingCommands();
        Assert.Equal("emergency", e.Phase);
        Assert.Contains(events, x => x.Kind == SimEventKinds.PhaseChanged && x.DataString("to") == "emergency");
        Assert.True(events.Last(x => x.Kind == SimEventKinds.TimelineManual).DataBool("ok"));
        e.Enqueue(SimCommand.TriggerTimeline("man")); e.ProcessPendingCommands(); // once 已觸發
        Assert.False(events.Last(x => x.Kind == SimEventKinds.TimelineManual).DataBool("ok"));
        e.Enqueue(SimCommand.TriggerTimeline("man", force: true)); e.ProcessPendingCommands();
        Assert.True(events.Last(x => x.Kind == SimEventKinds.TimelineManual).DataBool("ok"));
        Assert.Equal(2, e.Timeline!.StateOf("man")!.FireCount);
        e.Enqueue(SimCommand.TriggerTimeline("auto", force: true)); e.ProcessPendingCommands();
        var msg = events.Last(x => x.Kind == SimEventKinds.Message);
        Assert.True(msg.Visible);
        Assert.Equal(SimEventSources.Timeline, msg.Source);
        Assert.Equal(3, e.BuildState().Scenario!.TimelineFired);
        // 手動觸發指令進 input 行(CommandApplied),時間表動作不進
        var applied = 0;
        e.CommandApplied += (_, _, _) => applied++;
        e.Enqueue(SimCommand.TriggerTimeline("man", force: true)); e.ProcessPendingCommands();
        Assert.Equal(1, applied);
    }


    /// <summary>E07 ecrPrompt「無反應時」以必要事件 steeringFirstAction 逾時判斷:故障前的確認不影響;故障後 60 s 無相關動作才觸發並進入 emergency。</summary>
    [Fact]
    public void E07EcrPromptFiresOnMissedFirstActionRegardlessOfEarlierAcknowledge()
    {
        var e = new SimulationEngine(TestData.Fsb1(), TestData.E07());
        var events = TestData.Capture(e);
        TestData.RunTo(e, 30);
        e.Enqueue(SimCommand.Acknowledge("verbal", role: "officer", note: "故障前的口頭辨識")); e.ProcessPendingCommands();
        TestData.RunTo(e, 35);
        e.Enqueue(SimCommand.TriggerTimeline("steeringFail", force: true)); e.ProcessPendingCommands();
        Assert.Contains(events, x => x.Kind == SimEventKinds.FaultInjected && x.ItemId == "steeringFail");
        TestData.RunTo(e, 90);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "ecrPrompt");
        TestData.RunTo(e, 200);
        var missed = events.Single(x => x.Kind == SimEventKinds.RequiredEventMissed && x.ItemId == "steeringFirstAction");
        var prompt = events.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "ecrPrompt");
        Assert.InRange(prompt.T - 35, 60, 63);
        Assert.True(prompt.Seq > missed.Seq);
        Assert.Equal("emergency", e.Phase);
        Assert.Contains(events, x => x.Kind == SimEventKinds.VoiceScript && x.ItemId == "ecrPrompt");

        // 60 s 內有車令 → 不觸發
        var e2 = new SimulationEngine(TestData.Fsb1(), TestData.E07());
        var ev2 = TestData.Capture(e2);
        TestData.RunTo(e2, 35);
        e2.Enqueue(SimCommand.TriggerTimeline("steeringFail", force: true)); e2.ProcessPendingCommands();
        TestData.RunTo(e2, 60);
        e2.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH)); e2.ProcessPendingCommands();
        TestData.RunTo(e2, 200);
        Assert.DoesNotContain(ev2, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "ecrPrompt");
        Assert.Equal("degraded", e2.Phase);
    }
}
