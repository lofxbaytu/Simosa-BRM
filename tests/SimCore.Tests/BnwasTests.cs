using SimosaBRM.SimCore.Alerts;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>BNWAS(MSC.128(75);sensors-alerts.md 第 4 節):各階段時序、與警報整合(caution → alarm)、重設方式、auto 模式、模式指令、評估次數、雜湊。</summary>
public class BnwasTests
{
    private static void Do(SimulationEngine e, SimCommand cmd)
    {
        e.Enqueue(cmd);
        e.ProcessPendingCommands();
    }

    private static void RunS(SimulationEngine e, double seconds) => TestData.RunTo(e, e.Time + seconds);

    [Fact]
    public void StagesFollowDormantPeriodAndDelays()
    {
        var e = TestData.MinimalEngine("bnwas: { mode: \"on\", dormant_s: 180 }");
        var events = TestData.Capture(e);
        e.Step(); // 第一步開始作動(休眠期自此起算)
        var s = e.BuildState().Bnwas!;
        Assert.Equal(BnwasMode.On, s.Mode);
        Assert.True(s.Active);
        Assert.Equal(0, s.Stage);
        Assert.Equal(180.0, s.RemainingS!.Value, 9);
        TestData.RunTo(e, 200);
        TestData.RunTo(e, 300);
        var stages = events.Where(x => x.Kind == SimEventKinds.BnwasStage).Select(x => ((int)x.DataNumber("stage")!.Value, Math.Round(x.T, 2))).ToList();
        Assert.Equal(new[] { (1, 180.02), (2, 195.02), (3, 285.02) }, stages);
        // 警報整合:第一階段 caution(視覺、無聲響)→ 第二階段升級為 alarm(駕駛台聲響)
        var raised = events.Single(x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == AlertIds.Bnwas);
        Assert.Equal("caution", raised.DataString("priority"));
        var esc = events.Single(x => x.Kind == SimEventKinds.AlertEscalated && x.ItemId == AlertIds.Bnwas);
        Assert.Equal(("caution", "alarm", "source"), (esc.DataString("from"), esc.DataString("to"), esc.DataString("reason")));
        Assert.Equal(195.02, Math.Round(esc.T, 2));
        var a = e.BuildState().Alerts!.Single(x => x.Id == AlertIds.Bnwas);
        Assert.Equal(AlertPriority.Alarm, a.Priority);
        Assert.True(a.Audible);
        Assert.Equal(3, e.BuildState().Bnwas!.Stage);
        Assert.Null(e.BuildState().Bnwas!.RemainingS);
        var summary = e.Assessment!.BuildSummary(e.Tick, false).Alerts.Bnwas;
        Assert.Equal((1, 1, 1, 0), (summary.Stage1, summary.Stage2, summary.Stage3, summary.Resets));
    }

    [Fact]
    public void BridgeActivityResetsAndClearsTheAlert()
    {
        var e = TestData.MinimalEngine("bnwas: { mode: \"on\", dormant_s: 180 }");
        var events = TestData.Capture(e);
        TestData.RunTo(e, 100);
        Do(e, SimCommand.Rudder(5)); // 休眠中的操作:只重設計時,不發事件
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.BnwasReset);
        TestData.RunTo(e, 270);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.BnwasStage); // 自 100 s 重新起算
        TestData.RunTo(e, 296); // 第一階段 280 s、第二階段 295 s
        Assert.Equal(2, e.Bnwas.State.Stage);
        Do(e, SimCommand.Telegraph(TelegraphOrder.SAH));
        var reset = events.Single(x => x.Kind == SimEventKinds.BnwasReset);
        Assert.Equal(2.0, reset.DataNumber("fromStage"));
        Assert.Equal("telegraph", reset.DataString("by"));
        Assert.Equal(SimEventSources.Instructor, reset.Source);
        Assert.Equal(0, e.Bnwas.State.Stage);
        e.Step();
        Assert.Null(e.BuildState().Alerts); // BNWAS 重設即解除指示(不需再確認)
        Assert.Equal("normal", events.Single(x => x.Kind == SimEventKinds.AlertRectified && x.ItemId == AlertIds.Bnwas).DataString("state"));

        // 警報確認也是駕駛台操作;重設鍵在休眠中也發事件
        Do(e, SimCommand.BnwasReset());
        Assert.Equal(0.0, events.Last(x => x.Kind == SimEventKinds.BnwasReset).DataNumber("fromStage"));
        Assert.Equal("bnwasReset", events.Last(x => x.Kind == SimEventKinds.BnwasReset).DataString("by"));
        Assert.Equal(1, e.Assessment!.BuildSummary(e.Tick, false).Alerts.Bnwas.Resets); // 只計階段 ≥ 1 的重設
    }

    [Fact]
    public void AlertAcknowledgeResetsBnwasAndTimelineActionsDoNot()
    {
        var e = TestData.MinimalEngine("""
            bnwas: { mode: "on", dormant_s: 180 }
            timeline:
              - { id: rud, when: { type: time, atLeast: 185 }, actions: [ { ownShip: { rudder: 3 } } ] }
            """);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 190);
        Assert.Equal(1, e.Bnwas.State.Stage); // 時間表的 ownShip 覆寫不是駕駛台操作
        Do(e, SimCommand.AlertAcknowledge());
        Assert.Equal("alertAcknowledge", events.Single(x => x.Kind == SimEventKinds.BnwasReset).DataString("by"));
        Assert.Equal(0, e.Bnwas.State.Stage);
    }

    [Fact]
    public void AutoModeRequiresAutopilotAndModeCommandClamps()
    {
        var e = TestData.MinimalEngine("bnwas: { mode: auto, dormant_s: 180 }");
        var events = TestData.Capture(e);
        TestData.RunTo(e, 300);
        Assert.False(e.BuildState().Bnwas!.Active);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.BnwasStage);
        Do(e, SimCommand.Autopilot(true, heading: 0));
        TestData.RunTo(e, 300 + 180.5);
        Assert.Equal(1, e.Bnwas.State.Stage);
        // 自動舵因故障脫開(非駕駛台操作)→ 不作動,階段歸零(reason inactive)
        Do(e, SimCommand.InjectFault("autopilotFailure"));
        e.Step();
        Assert.Equal(0, e.Bnwas.State.Stage);
        Assert.Equal("inactive", events.Last(x => x.Kind == SimEventKinds.BnwasStage).DataString("reason"));
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.BnwasReset);
        // 學員解除自動舵本身是駕駛台操作:先重設(第 1 階段 → 0)再停止作動
        Do(e, SimCommand.ClearFault("autopilotFailure"));
        Do(e, SimCommand.Autopilot(true, heading: 0));
        TestData.RunTo(e, e.Time + 181);
        Assert.Equal(1, e.Bnwas.State.Stage);
        Do(e, SimCommand.Autopilot(false));
        var reset = events.Single(x => x.Kind == SimEventKinds.BnwasReset);
        Assert.Equal((1.0, "autopilot"), (reset.DataNumber("fromStage"), reset.DataString("by")));

        Do(e, SimCommand.BnwasMode(BnwasMode.On, dormantS: 1000));
        var mc = events.Single(x => x.Kind == SimEventKinds.BnwasModeChanged);
        Assert.Equal(("auto", "on"), (mc.DataString("from"), mc.DataString("to")));
        Assert.Equal(720.0, mc.DataNumber("dormant_s")); // MSC.128 上限 12 分鐘
        Assert.Equal(SimEventSources.Instructor, mc.Source);
        Do(e, new SimCommand { Type = SimCommandType.BnwasMode, Value = SimCommand.Str("off") });
        Assert.Equal(BnwasMode.Off, e.Bnwas.State.Mode);
    }

    [Fact]
    public void ScenarioRejectsDormantOutsideMsc128Range()
    {
        Assert.Throws<InvalidDataException>(() => TestData.Minimal("bnwas: { mode: \"on\", dormant_s: 60 }"));
        Assert.Throws<InvalidDataException>(() => TestData.Minimal("bnwas: { mode: \"on\", dormant_s: 900 }"));
        Assert.Equal(720.0, TestData.Minimal().Bnwas.DormantS);
        Assert.Equal(BnwasMode.Off, TestData.Minimal().Bnwas.Mode);
    }

    [Fact]
    public void BnwasSnapshotRoundTripsAndEntersHash()
    {
        var plain = TestData.MinimalEngine();
        var on = TestData.MinimalEngine("bnwas: { mode: \"on\", dormant_s: 180 }");
        plain.Run(100);
        on.Run(100);
        Assert.Equal(plain.Motion, on.Motion);
        Assert.NotEqual(plain.StateHash, on.StateHash);

        var reference = TestData.MinimalEngine("bnwas: { mode: \"on\", dormant_s: 180 }");
        TestData.RunTo(reference, 250);
        var a = TestData.MinimalEngine("bnwas: { mode: \"on\", dormant_s: 180 }");
        TestData.RunTo(a, 190);
        var snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
        Assert.Equal(1, snap.Bnwas!.Stage);
        var b = TestData.MinimalEngine("bnwas: { mode: \"on\", dormant_s: 180 }");
        b.Restore(snap);
        TestData.RunTo(b, 250);
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal(2, b.Bnwas.State.Stage); // 第二階段 195 s、第三階段 285 s
    }

    [Fact]
    public void BnwasResetCountsAsHandledNotUnacknowledged()
    {
        // 審查發現:BNWAS 重設即解除指示(第 3.2 節),該筆紀錄不得算進 alerts.unacknowledged
        var e = TestData.MinimalEngine("bnwas: { mode: \"on\", dormant_s: 180 }");
        TestData.RunTo(e, 182);
        Assert.Equal(1, e.Bnwas.State.Stage);
        Do(e, SimCommand.BnwasReset());
        e.Step();
        var a = e.Assessment!.BuildSummary(e.Tick, false).Alerts;
        Assert.Equal((1, 1, 0, 1), (a.Raised, a.Acknowledged, a.Unacknowledged, a.Bnwas.Resets));
        var rec = a.Records.Single(r => r.Id == AlertIds.Bnwas);
        Assert.Equal(182.0, rec.AcknowledgedAtS!.Value, 6);
        Assert.Equal(182.0 - rec.RaisedAtS, rec.AckTimeS!.Value, 6);

        // 一般駕駛台操作(舵令)重設第 2 階段也一樣
        TestData.RunTo(e, 182 + 180 + 16);
        Assert.Equal(2, e.Bnwas.State.Stage);
        Do(e, SimCommand.Rudder(3));
        e.Step();
        a = e.Assessment!.BuildSummary(e.Tick, false).Alerts;
        Assert.Equal((2, 2, 0, 2), (a.Raised, a.Acknowledged, a.Unacknowledged, a.Bnwas.Resets));
    }

    [Fact]
    public void BnwasModeOffSettingsEnterHash()
    {
        // 審查發現:off 模式下以 bnwasMode 改休眠期/計時起點,狀態雜湊須能區分
        var a = TestData.MinimalEngine();
        var b = TestData.MinimalEngine();
        TestData.RunTo(a, 5);
        TestData.RunTo(b, 5);
        Do(b, SimCommand.BnwasMode(BnwasMode.Off, dormantS: 180));
        TestData.RunTo(a, 60);
        TestData.RunTo(b, 60);
        Assert.Equal((720.0, 180.0), (a.BuildState().Bnwas!.DormantS, b.BuildState().Bnwas!.DormantS));
        Assert.True(a.Bnwas.IsDefault);
        Assert.NotEqual(a.StateHash, b.StateHash);

        // 快照還原後仍與原引擎一致
        var snap = EngineSnapshot.FromJson(b.CreateSnapshot().ToJson());
        var c = TestData.MinimalEngine();
        c.Restore(snap);
        TestData.RunTo(b, 80);
        TestData.RunTo(c, 80);
        Assert.Equal(b.StateHash, c.StateHash);
    }
}
