using SimosaBRM.SimCore.Alerts;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 警報工作站與責任轉移(MSC.302(87) 第 6、7.3 節;sensors-alerts.md 第 3.6 節):Category A 只能在負責站確認、
/// alertTransfer 的允許/拒絕規則、state.alerts[] 的 station/transferredTo、未帶 station 的既有指令行為不變、
/// 雜湊只在使用站別時追加、快照還原與紀錄重播。
/// </summary>
public class AlertStationTests
{
    private static void Do(SimulationEngine e, SimCommand cmd)
    {
        e.Enqueue(cmd);
        e.ProcessPendingCommands();
    }

    private static void RunS(SimulationEngine e, double seconds) => TestData.RunTo(e, e.Time + seconds);

    private static AlertStatus Alert(SimulationEngine e, string id) => e.BuildState().Alerts!.Single(a => a.Id == id);

    private static SimEvent? LastReject(List<SimEvent> events)
        => events.LastOrDefault(x => x.Kind == SimEventKinds.CommandRejected);

    /// <summary>淺水(Category A)+ 主機停車(Category B)同時成立的引擎。</summary>
    private static SimulationEngine WithShallowAndEngine(out List<SimEvent> events)
    {
        var e = TestData.MinimalEngine("alerts: { shallowWater_m: 10 }", waterDepth: 30);
        events = TestData.Capture(e);
        RunS(e, 2);
        Do(e, SimCommand.SetEnvironment(waterDepth: 12));
        Do(e, SimCommand.InjectFault("mainEngine"));
        e.Step();
        Assert.Equal(AlertCategory.A, Alert(e, AlertIds.ShallowWater).Category);
        Assert.Equal(AlertCategory.B, Alert(e, "mainEngine").Category);
        return e;
    }

    [Fact]
    public void CategoryAOnlyAcknowledgedAtResponsibleStation()
    {
        var e = WithShallowAndEngine(out var events);
        // 機控室確認淺水(Category A,負責站 bridge)→ 拒絕,狀態不變
        Do(e, SimCommand.AlertAcknowledge(AlertIds.ShallowWater, AlertStations.Ecr));
        var rej = LastReject(events)!;
        Assert.Equal(CommandRejectReasons.AlertStationRequired, rej.DataString("reason"));
        Assert.Equal(AlertState.ActiveUnacknowledged, Alert(e, AlertIds.ShallowWater).State);
        // 右翼也不是負責站
        Do(e, SimCommand.AlertAcknowledge(AlertIds.ShallowWater, AlertStations.StbdWing));
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.CommandRejected));
        // Category B 可在任一站(中央警報面板)確認;事件 data.ackStation 記錄非 bridge 的站
        Do(e, SimCommand.AlertAcknowledge("mainEngine", AlertStations.Ecr));
        var ack = events.Last(x => x.Kind == SimEventKinds.AlertAcknowledged);
        Assert.Equal("mainEngine", ack.ItemId);
        Assert.Equal(AlertStations.Ecr, ack.DataString("ackStation"));
        Assert.Equal(AlertState.ActiveAcknowledged, Alert(e, "mainEngine").State);
        // 未帶 station 的既有指令 = bridge:可確認 Category A;data 不帶 ackStation(既有事件內容不變)
        Do(e, SimCommand.AlertAcknowledge(AlertIds.ShallowWater));
        Assert.Equal(AlertState.ActiveAcknowledged, Alert(e, AlertIds.ShallowWater).State);
        Assert.Null(events.Last(x => x.Kind == SimEventKinds.AlertAcknowledged).DataString("ackStation"));
        // 未知站別
        Do(e, SimCommand.AlertAcknowledge("mainEngine", "engineRoom"));
        Assert.Equal(CommandRejectReasons.UnknownStation, LastReject(events)!.DataString("reason"));
    }

    [Fact]
    public void AcknowledgeAllFromOtherStationSkipsStationBoundAlerts()
    {
        var e = WithShallowAndEngine(out var events);
        // 機控室「全部確認」:只確認可在該站確認者(mainEngine),淺水略過、不拒絕
        Do(e, SimCommand.AlertAcknowledge(null, AlertStations.Ecr));
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.CommandRejected);
        Assert.Equal(AlertState.ActiveAcknowledged, Alert(e, "mainEngine").State);
        Assert.Equal(AlertState.ActiveUnacknowledged, Alert(e, AlertIds.ShallowWater).State);
        // 已確認的警報再從別站指定確認:無事可做、不拒絕(與既有語意一致)
        Do(e, SimCommand.AlertAcknowledge("mainEngine", AlertStations.PortWing));
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.CommandRejected);
    }

    [Fact]
    public void TransferRulesAndResponsibility()
    {
        var e = WithShallowAndEngine(out var events);
        Do(e, SimCommand.InjectFault("gyroFailure"));
        e.Step();
        // 主機停車(engine 來源)可轉機控室
        Do(e, SimCommand.AlertTransfer("mainEngine", AlertStations.Ecr));
        var tr = events.Last(x => x.Kind == SimEventKinds.AlertTransferred);
        Assert.Equal("mainEngine", tr.ItemId);
        Assert.Equal(SimEventSources.Engine, tr.Source); // 與其他警報事件相同(itemId = 警報 id、學員站可見)
        Assert.True(tr.Visible);
        Assert.Equal(AlertStations.Ecr, tr.DataString("station"));
        Assert.Equal(AlertStations.Bridge, tr.DataString("from"));
        Assert.Equal(AlertStations.Ecr, tr.DataString("transferredTo"));
        var a = Alert(e, "mainEngine");
        Assert.Equal(AlertStations.Bridge, a.Station);
        Assert.Equal(AlertStations.Ecr, a.TransferredTo);
        Assert.Contains("\"transferredTo\":\"ecr\"", ContractJson.Serialize(a));
        // 轉移後駕駛台(未帶 station)不能確認,接收站可以
        Do(e, SimCommand.AlertAcknowledge("mainEngine"));
        Assert.Equal(CommandRejectReasons.AlertStationRequired, LastReject(events)!.DataString("reason"));
        Assert.Equal(AlertState.ActiveUnacknowledged, Alert(e, "mainEngine").State);
        Do(e, SimCommand.AlertAcknowledge("mainEngine", AlertStations.Ecr));
        Assert.Equal(AlertState.ActiveAcknowledged, Alert(e, "mainEngine").State);
        // 電羅經(導航感測器)不可轉機控室,可轉駕駛台翼
        Do(e, SimCommand.AlertTransfer("gyroFailure", AlertStations.Ecr));
        Assert.Equal(CommandRejectReasons.AlertNotTransferable, LastReject(events)!.DataString("reason"));
        Assert.Null(Alert(e, "gyroFailure").TransferredTo);
        Do(e, SimCommand.AlertTransfer("gyroFailure", AlertStations.PortWing));
        Assert.Equal(AlertStations.PortWing, Alert(e, "gyroFailure").TransferredTo);
        Do(e, SimCommand.AlertAcknowledge("gyroFailure", AlertStations.StbdWing));
        Assert.Equal(CommandRejectReasons.AlertStationRequired, LastReject(events)!.DataString("reason"));
        // 淺水(Category A)亦可轉到翼;轉回原站即解除轉移(transferredTo 省略)
        Do(e, SimCommand.AlertTransfer(AlertIds.ShallowWater, AlertStations.StbdWing));
        Assert.Equal(AlertStations.StbdWing, Alert(e, AlertIds.ShallowWater).TransferredTo);
        Do(e, SimCommand.AlertTransfer(AlertIds.ShallowWater, AlertStations.Bridge));
        Assert.Null(Alert(e, AlertIds.ShallowWater).TransferredTo);
        Assert.DoesNotContain("transferredTo", ContractJson.Serialize(Alert(e, AlertIds.ShallowWater)));
        Assert.Null(events.Last(x => x.Kind == SimEventKinds.AlertTransferred).DataString("transferredTo"));
        // 未知站別、不存在的警報、BNWAS 不可轉移
        Do(e, SimCommand.AlertTransfer("mainEngine", "wheelhouse"));
        Assert.Equal(CommandRejectReasons.UnknownStation, LastReject(events)!.DataString("reason"));
        Do(e, SimCommand.AlertTransfer("bowThruster", AlertStations.Ecr));
        Assert.Equal(CommandRejectReasons.UnknownAlert, LastReject(events)!.DataString("reason"));
        Assert.Empty(AlertCatalogue.TransferTargets(AlertCatalogue.Find(AlertIds.Bnwas)!));
        Assert.Equal(AlertStations.All, AlertCatalogue.TransferTargets(AlertCatalogue.Find("steeringGear")!));
        Assert.Equal(AlertStations.BridgeStations, AlertCatalogue.TransferTargets(AlertCatalogue.Find(AlertIds.OffHeading)!));
    }

    [Fact]
    public void HashOnlyChangesWhileStationsAreInUse()
    {
        SimulationEngine Make()
        {
            var e = TestData.MinimalEngine();
            RunS(e, 1);
            Do(e, SimCommand.InjectFault("mainEngine"));
            e.Step();
            return e;
        }
        var plain = Make();
        var moved = Make();
        Assert.Equal(plain.StateHash, moved.StateHash);
        Do(moved, SimCommand.AlertTransfer("mainEngine", AlertStations.Ecr));
        plain.Step();
        moved.Step();
        Assert.Equal(plain.Motion, moved.Motion);
        Assert.NotEqual(plain.StateHash, moved.StateHash); // 轉移中:站別區塊進雜湊
        // 轉回 bridge 後站別區塊不再寫入;雜湊鏈已分岔(前一步不同)但區塊大小與內容回到舊版配置
        Do(moved, SimCommand.AlertTransfer("mainEngine", AlertStations.Bridge));
        Assert.Equal(plain.Alerts.HashSize, moved.Alerts.HashSize);
        Assert.Equal((AlertManager.HashLongsHeader + AlertManager.HashLongsPerAlert) * 8, plain.Alerts.HashSize);
        var bufA = new byte[plain.Alerts.HashSize];
        var bufB = new byte[moved.Alerts.HashSize];
        plain.Alerts.WriteHash(bufA);
        moved.Alerts.WriteHash(bufB);
        Assert.Equal(bufA, bufB);
    }

    [Fact]
    public void TransferSurvivesSnapshotAndReplay()
    {
        SimulationEngine Make()
        {
            var e = TestData.MinimalEngine();
            e.Enqueue(SimCommand.InjectFault("mainEngine"));
            e.Enqueue(SimCommand.InjectFault("bowThruster"));
            return e;
        }
        var script = new (long, SimCommand)[]
        {
            (50 * 10, SimCommand.AlertTransfer("mainEngine", AlertStations.Ecr)),
            (50 * 20, SimCommand.AlertAcknowledge("mainEngine")), // 被拒(已轉移)
            (50 * 30, SimCommand.AlertAcknowledge("mainEngine", AlertStations.Ecr)),
            (50 * 40, SimCommand.AlertTransfer("bowThruster", AlertStations.PortWing)),
        };
        var reference = Make();
        EngineDeterminismTests.RunScript(reference, 50 * 60, script);
        Assert.Equal(AlertStations.PortWing, Alert(reference, "bowThruster").TransferredTo);
        Assert.Equal(AlertStations.Ecr, Alert(reference, "mainEngine").TransferredTo);

        // 快照在轉移之後、確認之前
        var a = Make();
        EngineDeterminismTests.RunScript(a, 50 * 15, script);
        var snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
        Assert.Equal(AlertStations.Ecr, snap.Alerts!.Alerts.Single(x => x.Id == "mainEngine").TransferredTo);
        var b = TestData.MinimalEngine();
        b.Restore(snap);
        EngineDeterminismTests.RunScript(b, 50 * 45, script.Where(x => x.Item1 >= 50 * 15));
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal(ContractJson.Serialize(reference.BuildState().Alerts), ContractJson.Serialize(b.BuildState().Alerts));

        // 紀錄與重播:input 行含 alertTransfer / 帶 station 的 alertAcknowledge,重播雜湊與事件序列一致
        var path = Path.Combine(TestData.TestRecordsDirectory, $"alert-station-{Guid.NewGuid():N}.jsonl");
        var rec = Make();
        using (var writer = new RecordWriter(path, rec, includeStates: false))
        {
            EngineDeterminismTests.RunScript(rec, 50 * 60, script);
            writer.Close();
        }
        var result = Replayer.Replay(path, TestData.Ship);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} / {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.Equal(6, result.InputsReplayed);
        var lines = RecordReader.Read(path);
        Assert.Contains(lines.Inputs, i => i.Command.Type == SimCommandType.AlertTransfer && i.Command.ArgAsString("station") == AlertStations.Ecr);
        Assert.Contains(lines.Events, ev => ev.Event.Kind == SimEventKinds.AlertTransferred);
        Assert.Contains(lines.Events, ev => ev.Event.Kind == SimEventKinds.CommandRejected && ev.Event.DataString("reason") == CommandRejectReasons.AlertStationRequired);
    }

    [Fact]
    public void ExistingCommandsWithoutStationBehaveAsBefore()
    {
        // 未帶 station:Category A 與 B 皆可確認,state.alerts[] station = bridge、無 transferredTo;事件 data 無站別欄位
        var e = WithShallowAndEngine(out var events);
        Do(e, SimCommand.AlertAcknowledge());
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.CommandRejected);
        Assert.All(e.BuildState().Alerts!, a =>
        {
            Assert.Equal(AlertState.ActiveAcknowledged, a.State);
            Assert.Equal(AlertStations.Bridge, a.Station);
            Assert.Null(a.TransferredTo);
        });
        foreach (var ev in events.Where(x => x.Kind is SimEventKinds.AlertRaised or SimEventKinds.AlertAcknowledged))
        {
            Assert.Null(ev.DataString("station"));
            Assert.Null(ev.DataString("ackStation"));
        }
    }

    /// <summary>
    /// BNWAS(MSC.128(75) 4.1.4)只由駕駛台上可辨識的值班官操作重設:機控室(ecr)的確認/靜音不是駕駛台操作,不重設、不發 bnwasReset;
    /// 駕駛台三站(省略 station = bridge、兩翼)未被拒絕的確認才重設(sensors-alerts.md 第 3.3/4 節)。
    /// </summary>
    [Fact]
    public void EcrAcknowledgeDoesNotResetBnwas()
    {
        var e = TestData.MinimalEngine("alerts: { shallowWater_m: 10 }\nbnwas: { mode: \"on\", dormant_s: 180 }", waterDepth: 30);
        var events = TestData.Capture(e);
        RunS(e, 2);
        Do(e, SimCommand.SetEnvironment(waterDepth: 12));
        Do(e, SimCommand.InjectFault("mainEngine"));
        TestData.RunTo(e, 200);
        Assert.Equal(2, e.Bnwas.State.Stage);
        // 機控室確認 Category B(核心接受):警報已確認,但 BNWAS 階段不變、無 bnwasReset
        Do(e, SimCommand.AlertAcknowledge("mainEngine", AlertStations.Ecr));
        Assert.Equal(AlertState.ActiveAcknowledged, Alert(e, "mainEngine").State);
        Assert.Equal(2, e.Bnwas.State.Stage);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.BnwasReset);
        // 機控室靜音(靜音不受站別限制,但不是駕駛台操作)
        Do(e, SimCommand.AlertSilence(AlertIds.ShallowWater, AlertStations.Ecr));
        Assert.Equal(AlertState.ActiveSilenced, Alert(e, AlertIds.ShallowWater).State);
        Assert.Equal(2, e.Bnwas.State.Stage);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.BnwasReset);
        // 右翼確認淺水(Category A,負責站 bridge)被拒 → 不重設
        Do(e, SimCommand.AlertAcknowledge(AlertIds.ShallowWater, AlertStations.StbdWing));
        Assert.Equal(CommandRejectReasons.AlertStationRequired, LastReject(events)!.DataString("reason"));
        Assert.Equal(2, e.Bnwas.State.Stage);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.BnwasReset);
        // 駕駛台(省略 station)確認 → 重設:bnwasReset {by: alertAcknowledge, fromStage: 2},發在 alertAcknowledged 之後(先處理確認再視為駕駛台操作)
        Do(e, SimCommand.AlertAcknowledge(AlertIds.ShallowWater));
        var reset = events.Single(x => x.Kind == SimEventKinds.BnwasReset);
        Assert.Equal("alertAcknowledge", reset.DataString("by"));
        Assert.Equal(2.0, reset.DataNumber("fromStage"));
        Assert.Equal(0, e.Bnwas.State.Stage);
        Assert.True(events.IndexOf(reset) > events.FindLastIndex(x => x.Kind == SimEventKinds.AlertAcknowledged));
        // 兩翼確認也是駕駛台操作:轉移到右翼的 mainEngine 在右翼確認 → 重設
        TestData.RunTo(e, 400);
        Assert.Equal(2, e.Bnwas.State.Stage);
        Do(e, SimCommand.InjectFault("bowThruster"));
        e.Step(); // 警報在下一步成立
        Do(e, SimCommand.AlertTransfer("bowThruster", AlertStations.StbdWing));
        Do(e, SimCommand.AlertAcknowledge("bowThruster", AlertStations.StbdWing));
        Assert.Equal(0, e.Bnwas.State.Stage);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.BnwasReset));
    }

    /// <summary>被拒絕的確認/靜音(已轉移者在原站確認、未知警報、未知站)都不是可辨識的駕駛台操作:不重設 BNWAS、不計入 summary.alerts.bnwas.resets。</summary>
    [Fact]
    public void RejectedAcknowledgeDoesNotResetBnwas()
    {
        var e = TestData.MinimalEngine("bnwas: { mode: \"on\", dormant_s: 180 }");
        var events = TestData.Capture(e);
        Do(e, SimCommand.InjectFault("mainEngine"));
        e.Step(); // 警報在下一步成立
        Do(e, SimCommand.AlertTransfer("mainEngine", AlertStations.Ecr));
        TestData.RunTo(e, 200);
        Assert.Equal(2, e.Bnwas.State.Stage);
        Do(e, SimCommand.AlertAcknowledge("mainEngine")); // 已轉移到 ecr → alertStationRequired
        Do(e, SimCommand.AlertAcknowledge("noSuchAlert")); // unknownAlert
        Do(e, SimCommand.AlertAcknowledge("mainEngine", "engineRoom")); // unknownStation
        Do(e, SimCommand.AlertSilence("noSuchAlert")); // unknownAlert
        Assert.Equal(4, events.Count(x => x.Kind == SimEventKinds.CommandRejected));
        Assert.Equal(AlertState.ActiveUnacknowledged, Alert(e, "mainEngine").State);
        Assert.Equal(2, e.Bnwas.State.Stage);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.BnwasReset);
        TestData.RunTo(e, 201);
        Assert.Equal(0, e.Assessment!.LatestSummary!.Alerts!.Bnwas.Resets);
        // 駕駛台靜音(靜音不受站別限制,省略 station = bridge)是駕駛台操作 → 重設並計次
        Do(e, SimCommand.AlertSilence("mainEngine"));
        Assert.Equal(AlertState.ActiveSilenced, Alert(e, "mainEngine").State);
        Assert.Equal(0, e.Bnwas.State.Stage);
        Assert.Equal("alertSilence", events.Single(x => x.Kind == SimEventKinds.BnwasReset).DataString("by"));
        TestData.RunTo(e, 202);
        Assert.Equal(1, e.Assessment!.LatestSummary!.Alerts!.Bnwas.Resets);
    }
}
