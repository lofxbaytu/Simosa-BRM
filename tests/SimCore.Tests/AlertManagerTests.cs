using SimosaBRM.SimCore.Alerts;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 警報管理(BAM,MSC.302(87);sensors-alerts.md 第 3 節):狀態機(成立 → 靜音 → 重響 → 確認 → 解除)、rectifiedUnacknowledged、
/// warning 升級、未知 id、門檻類警報(淺水、偏航、航向監視)、評估摘要的確認時間與故障應變的警報確認、快照。
/// </summary>
public class AlertManagerTests
{
    private static void Do(SimulationEngine e, SimCommand cmd)
    {
        e.Enqueue(cmd);
        e.ProcessPendingCommands();
    }

    private static void RunS(SimulationEngine e, double seconds) => TestData.RunTo(e, e.Time + seconds);

    private static AlertStatus Alert(SimulationEngine e, string id) => e.BuildState().Alerts!.Single(a => a.Id == id);

    [Fact]
    public void AlarmLifecycleSilenceReannunciateAcknowledgeRectify()
    {
        var e = TestData.MinimalEngine();
        var events = TestData.Capture(e);
        RunS(e, 10);
        Do(e, SimCommand.InjectFault("mainEngine"));
        e.Step();
        var raised = events.Single(x => x.Kind == SimEventKinds.AlertRaised);
        Assert.Equal("mainEngine", raised.ItemId);
        Assert.Equal(SimEventSources.Engine, raised.Source);
        Assert.True(raised.Visible);
        Assert.Equal("alarm", raised.DataString("priority"));
        Assert.Equal("B", raised.DataString("category"));
        Assert.Equal("activeUnacknowledged", raised.DataString("state"));
        var a = Alert(e, "mainEngine");
        Assert.Equal(AlertState.ActiveUnacknowledged, a.State);
        Assert.True(a.Audible);
        Assert.Equal(9, a.Number);
        Assert.Equal("主機停車", a.Text);

        // 靜音 30 s(MSC.302)→ 期滿未確認則重響
        RunS(e, 5);
        Do(e, SimCommand.AlertSilence());
        a = Alert(e, "mainEngine");
        Assert.Equal(AlertState.ActiveSilenced, a.State);
        Assert.False(a.Audible);
        Assert.Equal(e.Time + 30.0, a.SilencedUntilS!.Value, 9);
        RunS(e, 29);
        Assert.Equal(AlertState.ActiveSilenced, Alert(e, "mainEngine").State);
        RunS(e, 1.5);
        Assert.Equal(AlertState.ActiveUnacknowledged, Alert(e, "mainEngine").State);
        Assert.Single(events, x => x.Kind == SimEventKinds.AlertReannunciated);

        // 確認:ackTime_s = 確認 − 成立
        Do(e, SimCommand.AlertAcknowledge("mainEngine"));
        var ack = events.Single(x => x.Kind == SimEventKinds.AlertAcknowledged);
        Assert.Equal(e.Time - raised.T, ack.DataNumber("ackTime_s")!.Value, 9);
        a = Alert(e, "mainEngine");
        Assert.Equal(AlertState.ActiveAcknowledged, a.State);
        Assert.Equal(e.Time, a.AcknowledgedAtS!.Value, 9);

        // 條件解除:已確認 → normal(自清單移除)
        Do(e, SimCommand.ClearFault("mainEngine"));
        e.Step();
        var rect = events.Single(x => x.Kind == SimEventKinds.AlertRectified);
        Assert.Equal("normal", rect.DataString("state"));
        Assert.Null(e.BuildState().Alerts);
        Assert.True(e.Alerts.State.IsEmpty);

        // 評估摘要
        var summary = e.Assessment!.BuildSummary(e.Tick, final: false);
        Assert.Equal(1, summary.Alerts.Raised);
        Assert.Equal(1, summary.Alerts.Acknowledged);
        Assert.Equal(1, summary.Alerts.Silences);
        Assert.Equal(ack.DataNumber("ackTime_s")!.Value, summary.Alerts.MeanAckTimeS!.Value, 9);
        var rec = Assert.Single(summary.Alerts.Records);
        Assert.Equal(1, rec.Silences);
        Assert.NotNull(rec.RectifiedAtS);
        // 故障應變:警報 id = 故障名稱 → 記錄警報確認時間(第 8.3 節第 5 類)
        var fr = Assert.Single(summary.FaultResponses);
        Assert.Equal(ack.T, fr.AlertAckAtS!.Value, 9);
        Assert.Equal(ack.T - 10.0, fr.AlertAckDtS!.Value, 9);
    }

    [Fact]
    public void RectifiedUnacknowledgedNeedsAcknowledgeAndCanReRaise()
    {
        var e = TestData.MinimalEngine();
        var events = TestData.Capture(e);
        Do(e, SimCommand.InjectFault("steeringGear"));
        e.Step();
        Do(e, SimCommand.ClearFault("steeringGear"));
        e.Step();
        Assert.Equal(AlertState.RectifiedUnacknowledged, Alert(e, "steeringGear").State);
        Assert.False(Alert(e, "steeringGear").Audible); // 已解除:只剩閃爍
        // 再成立:同一筆紀錄回到 activeUnacknowledged、count 2、repeat = true
        Do(e, SimCommand.InjectFault("steeringGear"));
        e.Step();
        var a = Alert(e, "steeringGear");
        Assert.Equal(AlertState.ActiveUnacknowledged, a.State);
        Assert.Equal(2, a.Count);
        Assert.Equal(true, events.Last(x => x.Kind == SimEventKinds.AlertRaised).DataBool("repeat"));
        Do(e, SimCommand.ClearFault("steeringGear"));
        e.Step();
        Do(e, SimCommand.AlertAcknowledge()); // 全部
        Assert.Null(e.BuildState().Alerts);
        Assert.Equal("normal", events.Last(x => x.Kind == SimEventKinds.AlertAcknowledged).DataString("state"));
        var summary = e.Assessment!.BuildSummary(e.Tick, final: false);
        Assert.Equal(2, summary.Alerts.Raised);
        Assert.Equal(1, summary.Alerts.Acknowledged);
        Assert.Equal(1, summary.Alerts.Unacknowledged);
    }

    [Fact]
    public void WarningEscalatesToAlarmWhenUnacknowledged()
    {
        var e = TestData.MinimalEngine("alerts: { escalate_s: 120 }");
        var events = TestData.Capture(e);
        Do(e, SimCommand.InjectFault("bowThruster"));
        e.Step();
        Assert.Equal(AlertPriority.Warning, Alert(e, "bowThruster").Priority);
        RunS(e, 60);
        Do(e, SimCommand.AlertSilence("bowThruster")); // 靜音不算確認,升級計時繼續
        RunS(e, 61);
        var esc = events.Single(x => x.Kind == SimEventKinds.AlertEscalated);
        Assert.Equal(("warning", "alarm", "unacknowledged"), (esc.DataString("from"), esc.DataString("to"), esc.DataString("reason")));
        Assert.Equal(120.0, esc.DataNumber("after_s")!.Value, 9);
        var a = Alert(e, "bowThruster");
        Assert.Equal(AlertPriority.Alarm, a.Priority);
        Assert.True(a.Escalated);
        Assert.Equal(AlertState.ActiveUnacknowledged, a.State);
        Assert.True(a.Audible);
        Assert.Equal(1, e.Assessment!.BuildSummary(e.Tick, false).Alerts.Escalations);
    }

    [Fact]
    public void UnknownAlertIdIsRejected()
    {
        var e = TestData.MinimalEngine();
        var events = TestData.Capture(e);
        Do(e, SimCommand.AlertAcknowledge("noSuchAlert"));
        Do(e, SimCommand.AlertSilence("noSuchAlert"));
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.CommandRejected && x.DataString("reason") == CommandRejectReasons.UnknownAlert));
        Do(e, SimCommand.AlertAcknowledge()); // 無警報時全部確認:無事件、不拒絕
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ShallowWaterAlarmUsesIndicatedDepthWithHysteresis()
    {
        var e = TestData.MinimalEngine("alerts: { shallowWater_m: 10 }", waterDepth: 30);
        var events = TestData.Capture(e);
        RunS(e, 5);
        Assert.Null(e.BuildState().Alerts);
        Do(e, SimCommand.SetEnvironment(waterDepth: 12));
        e.Step();
        Assert.True(e.BuildState().DepthBelowKeel < 10);
        var a = Alert(e, "shallowWater");
        Assert.Equal(AlertCategory.A, a.Category);
        Assert.Equal(AlertPriority.Alarm, a.Priority);
        // 測深儀失效:指示水深無效 → 淺水條件不成立(解除),改由 echoSounderFailure 警報
        Do(e, SimCommand.InjectFault("echoSounderFailure"));
        e.Step();
        Assert.Equal(AlertState.RectifiedUnacknowledged, Alert(e, "shallowWater").State);
        Assert.Equal(AlertState.ActiveUnacknowledged, Alert(e, "echoSounderFailure").State);
        Assert.Contains(events, x => x.Kind == SimEventKinds.AlertRectified && x.ItemId == "shallowWater");
    }

    [Fact]
    public void OffHeadingAlarmNeedsPersistenceAndHeadingMonitorDetectsDrift()
    {
        var e = TestData.MinimalEngine("alerts: { offHeading_deg: 10, offHeadingDelay_s: 10, headingMonitor_deg: 5 }");
        var events = TestData.Capture(e);
        Do(e, SimCommand.Autopilot(true, heading: 0));
        RunS(e, 20);
        Do(e, SimCommand.Autopilot(true, heading: 60)); // 改向 60°:誤差 > 10° 持續 10 s 才發偏航警報
        RunS(e, 9.9);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "offHeading");
        RunS(e, 0.2);
        var off = events.Single(x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "offHeading");
        Assert.Equal("A", off.DataString("category"));
        RunS(e, 300);
        Assert.Contains(events, x => x.Kind == SimEventKinds.AlertRectified && x.ItemId == "offHeading");

        // 航向監視:電羅經漂移 2°/分 → 約 2.5 分後與磁羅經差 > 5°(warning);自動舵跟隨漂移,偏航警報不會成立
        var n = events.Count(x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "offHeading");
        var t0 = e.Time;
        Do(e, SimCommand.InjectFault("gyroDrift", new { rate_degPerMin = 2.0 }));
        RunS(e, 200);
        var hm = events.Single(x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "headingMonitor");
        Assert.Equal("warning", hm.DataString("priority"));
        Assert.InRange(hm.T - t0, 150.0, 151.0);
        Assert.Equal(n, events.Count(x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "offHeading"));
    }

    [Fact]
    public void AlertStateIsSnapshottedAndRestored()
    {
        SimulationEngine Make()
        {
            var e = TestData.MinimalEngine("alerts: { escalate_s: 60 }");
            e.Enqueue(SimCommand.InjectFault("bowThruster"));
            e.Enqueue(SimCommand.InjectFault("mainEngine"));
            return e;
        }
        var script = new (long, SimCommand)[] { (50 * 20, SimCommand.AlertSilence()), (50 * 70, SimCommand.AlertAcknowledge("mainEngine")) };
        var reference = Make();
        EngineDeterminismTests.RunScript(reference, 50 * 120, script);

        var a = Make();
        EngineDeterminismTests.RunScript(a, 50 * 35, script);
        var snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
        Assert.Equal(2, snap.Alerts!.Alerts.Count);
        Assert.Equal(AlertState.ActiveSilenced, snap.Alerts.Alerts[0].State);
        var b = TestData.MinimalEngine("alerts: { escalate_s: 60 }");
        b.Restore(snap);
        EngineDeterminismTests.RunScript(b, 50 * 85, script.Select(x => (x.Item1, x.Item2)).Where(x => x.Item1 >= 50 * 35));
        Assert.Equal(reference.Tick, b.Tick);
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal(ContractJson.Serialize(reference.BuildState().Alerts), ContractJson.Serialize(b.BuildState().Alerts));
    }
}
