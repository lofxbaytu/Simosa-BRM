using SimosaBRM.SimCore.Alerts;
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
/// E08 電羅經失效(規劃書第 8.1 節 E08;sensors-alerts.md 第 7 節):夜間沿岸、電羅經每分鐘漂移 1° 且無警報 → 7 分鐘後完全失效;自動舵跟隨錯誤航向。
/// 固定:載入/驗證、無人操作 600 s 的完整事件序列(對應主控程序 --duration 600 --record)與重播一致、有人操作時必要事件 met 與警報確認時間。
/// </summary>
public class GyroFailureScenarioTests
{
    private static string E08Path => Path.Combine(TestData.Root, "data", "scenarios", "E08_gyro_failure.yaml");
    private static Scenario.Scenario E08() => ScenarioLoader.Load(E08Path);

    /// <summary>與主控程序相同的 MMG 動力學(紀錄標頭 dynamics = mmg/1)。</summary>
    private static readonly Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg = (s, l) => MmgDynamics.Load(TestData.Root, s, l);

    [Fact]
    public void LoadsAndValidatesWithoutErrors()
    {
        var sc = E08();
        Assert.Equal("E08_gyro_failure", sc.Id);
        Assert.Equal(LoadingCondition.Full, sc.Ship.Loading);
        Assert.Equal(BnwasMode.On, sc.Bnwas.Mode);
        Assert.Equal(540.0, sc.Bnwas.DormantS);
        Assert.Equal(10.0, sc.Alerts.OffHeadingDeg);
        Assert.Null(sc.Alerts.HeadingMonitorDeg); // 規劃書 E08:漂移無警報
        Assert.Equal(1.0, sc.Sensors.MagneticCompassErrorDeg);
        var drift = sc.Timeline.Single(t => t.Id == "gyroDrift");
        Assert.Equal(60.0, drift.When!.AtLeast);
        var inj = drift.Actions.Single(a => a.Kind == "injectFault");
        Assert.Equal("gyroDrift", inj.AsString());
        Assert.Equal(-1.0, SimCommand.AsDouble(inj.FaultArgs()!.Value.GetProperty("rate_degPerMin")));
        var fail = sc.Timeline.Single(t => t.Id == "gyroFail");
        Assert.Equal("gyroFailure", fail.Actions.Single(a => a.Kind == "injectFault").AsString());
        Assert.Equal(new[] { "driftDetected", "alarmAck", "manualSteering", "callMaster" }, sc.Assessment.Required.Select(r => r.Id));
        Assert.Equal(new[] { "autopilotOff", "steeringModeChange", "rudderOrder", "acknowledge:gyroCheck" }, sc.Assessment.Required[0].Expect.Leaves().Select(l => l.Label));
        Assert.Equal(300.0, sc.Assessment.Required[0].WithinS);
        Assert.Equal("event:alertAcknowledged", sc.Assessment.Required[1].Expect.Label);
        var resolved = ScenarioLoader.LastResolvedParameters;
        Assert.Contains(resolved, r => r.Key == "emergency.gyroFailure.driftRate_degPerMin" && r.Source == "ASSUMED-E08" && r.Unit == "degPerMin");
        Assert.StartsWith("假設值,待公司程序", TestData.Parameters().Sources["ASSUMED-E08"].Title);
        Assert.Equal(540.0, TestData.Parameters().Parameters["bridge.bnwas.dormantPeriod_s"].Value.GetDouble());

        var issues = ScenarioValidator.Check(sc, TestData.Fsb1());
        Assert.DoesNotContain(issues, i => i.Severity == ScenarioIssueSeverity.Error);
        Assert.DoesNotContain(issues, i => i.Message.Contains("只記錄")); // gyroDrift/gyroFailure 皆 modelled
    }

    /// <summary>無人操作 600 s 的事件序列(kind, itemId, t);改動情境、警報目錄或物理時需更新並說明。</summary>
    private static readonly (string Kind, string? ItemId, double T)[] HandsOffEvents =
    {
        ("timelineTriggered", "autopilotOn", 1.0), ("autopilotOverride", "autopilotOn", 1.0), ("steeringModeChanged", "autopilotOn", 1.0), ("message", "autopilotOn", 1.0),
        ("timelineTriggered", "masterOrders", 20.0), ("voiceScript", "masterOrders", 20.0),
        ("timelineTriggered", "gyroDrift", 60.0), ("phaseChanged", "gyroDrift", 60.0), ("faultInjected", "gyroDrift", 60.0),
        ("requiredEventPending", "driftDetected", 60.0), ("message", "gyroDrift", 60.0),
        ("requiredEventMissed", "driftDetected", 361.0), ("timelineTriggered", "lookoutCue", 362.0), ("voiceScript", "lookoutCue", 362.0),
        ("timelineTriggered", "gyroFail", 480.0), ("phaseChanged", "gyroFail", 480.0), ("faultInjected", "gyroFail", 480.0),
        ("requiredEventPending", "alarmAck", 480.0), ("requiredEventPending", "manualSteering", 480.0), ("requiredEventPending", "callMaster", 480.0),
        ("autopilotTripped", null, 480.0), ("steeringModeChanged", null, 480.0), ("message", "gyroFail", 480.0),
        ("alertRaised", "gyroFailure", 480.02),
        ("requiredEventMissed", "alarmAck", 511.0), ("bandWarn", "headingDev", 523.0),
        ("bnwasStage", null, 540.02), ("alertRaised", "bnwas", 540.02),
        ("requiredEventMissed", "manualSteering", 541.0),
        ("bnwasStage", null, 555.02), ("alertEscalated", "bnwas", 555.02),
    };

    [Fact]
    public void HandsOffRunHasFixedEventSequenceAndReplays()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"e08-{Guid.NewGuid():N}.jsonl");
        var engine = new SimulationEngine(TestData.Fsb1(), E08(), Mmg);
        var events = TestData.Capture(engine);
        using (var writer = new RecordWriter(path, engine))
        {
            TestData.RunTo(engine, 600);
            writer.Close();
        }

        Assert.Equal(HandsOffEvents, events.Select(e => (e.Kind, e.ItemId, Math.Round(e.T, 2))).ToArray());
        Assert.Equal(Enumerable.Range(0, events.Count).Select(i => (long)i), events.Select(e => e.Seq));

        // 漂移期間:自動舵跟隨指示航向,真航向偏右(往岸側);失效後指示航向無效並保持
        var s = engine.BuildState();
        Assert.False(s.Sensors!.HeadingValid);
        Assert.False(engine.Autopilot.Enabled);
        Assert.InRange(s.Truth!.HeadingErrorDeg, -12.0, -6.0);
        Assert.Equal("gyroFailure", s.Alerts!.First().Id);
        Assert.Equal(AlertState.ActiveUnacknowledged, s.Alerts!.First().State);
        Assert.Equal(2, s.Bnwas!.Stage);

        var summary = engine.LatestSummary!;
        Assert.False(summary.Assessment.Passed);
        Assert.Equal(new[] { "missed", "missed", "missed", "pending" }, summary.Assessment.Required.Select(r => r.State));
        Assert.Equal(2, summary.Alerts.Raised);
        Assert.Equal(0, summary.Alerts.Acknowledged);
        Assert.Equal((1, 1, 0), (summary.Alerts.Bnwas.Stage1, summary.Alerts.Bnwas.Stage2, summary.Alerts.Bnwas.Stage3));
        Assert.Null(summary.FaultResponses.Single(f => f.Fault == "gyroFailure").AlertAckAtS);

        var rec = RecordReader.Read(path);
        Assert.True(rec.IntegrityOk);
        Assert.Equal(BnwasMode.On, rec.Header.Scenario.Bnwas.Mode);
        Assert.Equal(events.Count, rec.Events.Count);
        var result = Replayer.Replay(path, TestData.Ship, Mmg);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} vs {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.True(result.SummaryMatches);
        File.Delete(path);
    }

    [Fact]
    public void CrewResponseMeetsRequiredEventsAndRecordsAlarmAckTime()
    {
        var script = new (long Tick, SimCommand Cmd)[]
        {
            (50 * 200, SimCommand.Acknowledge("gyroCheck", itemId: "gyroDrift", role: "officer", note: "電羅經與磁羅經差 3°")),
            (50 * 205, SimCommand.Autopilot(false)),
            (50 * 210, SimCommand.Rudder(-5)),
            (50 * 260, SimCommand.Rudder(0)),
            (50 * 485, SimCommand.AlertAcknowledge("gyroFailure")),
            (50 * 490, SimCommand.Rudder(-3)),
            (50 * 520, SimCommand.Acknowledge("callMaster", role: "officer", note: "電話報告船長")),
            (50 * 560, SimCommand.TriggerTimeline("gyroRestored")),
        };
        var path = Path.Combine(TestData.TestRecordsDirectory, $"e08-crew-{Guid.NewGuid():N}.jsonl");
        var engine = new SimulationEngine(TestData.Fsb1(), E08(), Mmg);
        var events = TestData.Capture(engine);
        using (var writer = new RecordWriter(path, engine))
        {
            EngineDeterminismTests.RunScript(engine, 50 * 600, script);
            engine.Enqueue(SimCommand.EndExercise("測試結束"));
            engine.ProcessPendingCommands();
            writer.Close();
        }

        var met = events.Where(e => e.Kind == SimEventKinds.RequiredEventMet).Select(e => (e.ItemId, e.T, e.DataString("by"))).ToList();
        Assert.Equal(new (string?, double, string?)[]
        {
            ("driftDetected", 200.0, "acknowledge:gyroCheck"), ("alarmAck", 485.0, "event:alertAcknowledged"),
            ("manualSteering", 490.0, "rudderOrder"), ("callMaster", 520.0, "acknowledge:callMaster"),
        }, met);
        Assert.DoesNotContain(events, e => e.Kind == SimEventKinds.RequiredEventMissed);
        Assert.DoesNotContain(events, e => e.Kind == SimEventKinds.TimelineTriggered && e.ItemId == "lookoutCue");
        Assert.DoesNotContain(events, e => e.Kind == SimEventKinds.AutopilotTripped); // 學員已先解除自動舵
        Assert.DoesNotContain(events, e => e.Kind == SimEventKinds.BnwasStage);        // 駕駛台操作持續重設 BNWAS
        Assert.Empty(engine.Faults);
        Assert.Equal("normal", engine.Phase);

        var summary = engine.LatestSummary!;
        Assert.True(summary.Final);
        Assert.All(summary.Assessment.Required, r => Assert.Equal("met", r.State));
        var ackRec = summary.Alerts.Records.Single(r => r.Id == "gyroFailure");
        Assert.Equal(485.0 - 480.02, ackRec.AckTimeS!.Value, 9);
        Assert.Equal(ackRec.AckTimeS, summary.Alerts.MeanAckTimeS);
        var fr = summary.FaultResponses.Single(f => f.Fault == "gyroFailure");
        Assert.Equal(5.0, fr.AlertAckDtS!.Value, 9);
        var drift = summary.FaultResponses.Single(f => f.Fault == "gyroDrift");
        Assert.Equal("acknowledge:gyroCheck", drift.FirstAction!.Kind);
        Assert.Equal(140.0, drift.FirstAction.DtS, 9);

        var result = Replayer.Replay(path, TestData.Ship, Mmg);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} vs {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.True(result.SummaryMatches);
        File.Delete(path);
    }
}
