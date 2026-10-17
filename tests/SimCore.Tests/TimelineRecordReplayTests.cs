using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>E07 紀錄 → 重播:雜湊、事件序列、summary 一致;時間表動作不進 input 行(情境引擎設計第 3.4、7.1 節)。</summary>
public class TimelineRecordReplayTests
{
    internal static readonly (long Tick, SimCommand Cmd)[] Script =
    {
        (50 * 30, SimCommand.Rudder(-3)),
        (50 * 120, SimCommand.Acknowledge("verbal", role: "officer", note: "舵令複誦")),
        (50 * 200, SimCommand.Telegraph(TelegraphOrder.SAH)),
        (50 * 260, SimCommand.Acknowledge("notifyEcr", itemId: "steeringFail", role: "master")),
        (50 * 300, SimCommand.TriggerTimeline("emergencySteering")),
        (50 * 330, SimCommand.InjectFault("gyroDrift")),
        (50 * 400, SimCommand.SetEnvironment(windTrueSpeedKn: 25, rampS: 60)),
        (50 * 450, SimCommand.Freeze()),
        (50 * 450, SimCommand.Resume()),
        (50 * 500, SimCommand.Autopilot(true, heading: 52)),
    };

    [Fact]
    public void E07ReplayMatchesHashEventsAndSummary()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"e07-{Guid.NewGuid():N}.jsonl");
        var engine = new SimulationEngine(TestData.Fsb1(), TestData.E07());
        var events = TestData.Capture(engine);
        RecordFooter footer;
        using (var writer = new RecordWriter(path, engine))
        {
            EngineDeterminismTests.RunScript(engine, 50 * 600, Script);
            engine.Enqueue(SimCommand.EndExercise("測試結束"));
            engine.ProcessPendingCommands();
            footer = writer.Close();
        }
        Assert.Equal(2, footer.SummaryLines); // endExercise 一行 + Close 一行
        Assert.True(events.Count > 10);
        Assert.Contains(events, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "pilotBoarded");
        Assert.Contains(events, x => x.Kind == SimEventKinds.TimelineManual && x.DataBool("ok") == true);
        Assert.Contains(events, x => x.Kind == SimEventKinds.Acknowledge);
        Assert.Contains(events, x => x.Kind == SimEventKinds.ExerciseEnded);

        var rec = RecordReader.Read(path);
        Assert.True(rec.IntegrityOk);
        Assert.Equal(2, rec.Header.Version);
        Assert.NotNull(rec.Header.ParameterSet);
        Assert.Equal("2026.10.1", rec.Header.ParameterSet!.Version);
        Assert.True(rec.Header.ParameterSet.Resolved.Count > 10);
        Assert.Equal(7, rec.Header.Scenario.Timeline.Count); // 解析後完整情境
        Assert.Equal(50.0, rec.Header.Scenario.Route!.XtdLimitM);
        Assert.Equal(Script.Length + 1, rec.Inputs.Count);
        Assert.DoesNotContain(rec.Inputs, i => i.Command.Type == SimCommandType.InjectFault && i.Command.FaultName() == FaultNames.SteeringGear); // 時間表動作不進 input 行
        Assert.Contains(rec.Inputs, i => i.Command.Type == SimCommandType.TriggerTimeline);
        Assert.Contains(rec.Inputs, i => i.Command.Type == SimCommandType.Acknowledge);
        Assert.Equal(events.Count, rec.Events.Count);
        Assert.Equal(events.Select(x => x.Seq), rec.Events.Select(x => x.Event.Seq));
        Assert.Equal(2, rec.Summaries.Count);
        Assert.True(rec.Summary!.Summary.Final);
        Assert.Equal(ContractJson.Serialize(rec.Summaries[0].Summary), ContractJson.Serialize(rec.Summaries[1].Summary));
        Assert.Equal("E07_steering_failure", rec.Summary.Summary.ScenarioId);
        Assert.Equal("simosa-company-procedures", rec.Summary.Summary.ParameterSet!.Id);

        var result = Replayer.Replay(path, TestData.Ship);
        Assert.True(result.IntegrityOk);
        Assert.Null(result.Warning);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} vs {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.Equal(result.EventsRecorded, result.EventsReplayed);
        Assert.True(result.SummaryMatches);
        File.Delete(path);
    }

    [Fact]
    public void RescoreWithNewParameterSetKeepsHashButChangesBands()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"rescore-{Guid.NewGuid():N}.jsonl");
        var engine = new SimulationEngine(TestData.Fsb1(), TestData.E07());
        using (var writer = new RecordWriter(path, engine, includeStates: false))
        {
            TestData.RunTo(engine, 400);
            writer.Close();
        }
        var rec = RecordReader.Read(path);
        var maxXtd = rec.Summary!.Summary.Track!.MaxXtdM;
        Assert.True(maxXtd > 20 && maxXtd < 100, $"maxXtd {maxXtd}");
        var newSet = Parameters.ParameterSetLoader.FromYaml(File.ReadAllText(TestData.ParameterSetPath)
            .Replace("navigation.xtd.channel_m:       { value: 50,", "navigation.xtd.channel_m:       { value: 10,").Replace("\"2026.10.1\"", "\"2026.11.1\""));
        var result = Replayer.Replay(path, TestData.Ship, rescore: newSet);
        Assert.True(result.HashMatches, "重新評分以影子評估模組重算,物理/交通/時間表與雜湊不變");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch); // 影子模組的事件不對外發出,紀錄的事件序列原樣重現
        Assert.Null(result.SummaryMatches);
        Assert.Equal("2026.11.1", result.Summary!.ParameterSet!.Version);
        var band = result.Summary.Assessment.Bands.Single(b => b.Id == "xtdMax");
        Assert.Equal(10.0, band.Warn);
        Assert.Equal(20.0, band.Fail);
        Assert.Equal("fail", band.State);
        Assert.Equal("navigation.xtd.channel_m", band.ParamKey);
        Assert.Equal("SMS-NAV-012", band.Source);
        File.Delete(path);
    }


    /// <summary>
    /// 改版餵入交通/時間表條件的參數(E01 的 assessment.minCpa_nm → cpaAlarm 門檻 → cpaCoach 時間表項目;E07 的速限 → speedLimitExceeded → speedCoach):
    /// 重新評分時雜湊與事件序列仍與紀錄一致,只有 summary 依新門檻改變(設計文件 D23)。
    /// </summary>
    [Fact]
    public void RescoreOfParametersFeedingTrafficAndTimelineKeepsHashAndEvents()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"rescore-cpa-{Guid.NewGuid():N}.jsonl");
        var engine = new SimulationEngine(TestData.Fsb1(), TestData.Baseline());
        using (var writer = new RecordWriter(path, engine, includeStates: false))
        {
            TestData.RunTo(engine, 300);
            writer.Close();
        }
        var rec = RecordReader.Read(path);
        Assert.Contains(rec.Events, e => e.Event.Kind == Traffic.TrafficEventKinds.CpaAlarm); // E01 對遇目標觸發 cpaAlarm 與 cpaCoach
        Assert.Contains(rec.Events, e => e.Event.Kind == SimEventKinds.TimelineTriggered && e.Event.ItemId == "cpaCoach");
        var yaml = File.ReadAllText(TestData.ParameterSetPath)
            .Replace("navigation.cpa.coastal_nm:      { value: 0.5,", "navigation.cpa.coastal_nm:      { value: 0.1,").Replace("\"2026.10.1\"", "\"2026.12.1\"");
        var newSet = Parameters.ParameterSetLoader.FromYaml(yaml);
        var result = Replayer.Replay(path, TestData.Ship, rescore: newSet);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} vs {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.Equal(result.EventsRecorded, result.EventsReplayed);
        Assert.Equal("2026.12.1", result.Summary!.ParameterSet!.Version);
        var band = result.Summary.Assessment.Bands.Single(b => b.Id == "minCpa");
        Assert.Equal(0.1, band.Fail);
        Assert.Contains(result.Summary.Assessment.Parameters, p => p.Key == "navigation.cpa.coastal_nm" && p.Value.GetDouble() == 0.1);
        File.Delete(path);

        // E07:速限 8 → 3 kn 使 speedLimitExceeded 與 speedCoach 在重新評分時「本來會」不同;紀錄的事件序列仍原樣重現
        var path2 = Path.Combine(TestData.TestRecordsDirectory, $"rescore-speed-{Guid.NewGuid():N}.jsonl");
        var e2 = new SimulationEngine(TestData.Fsb1(), TestData.E07());
        using (var writer = new RecordWriter(path2, e2, includeStates: false))
        {
            EngineDeterminismTests.RunScript(e2, 50 * 400, Script);
            writer.Close();
        }
        var speedSet = Parameters.ParameterSetLoader.FromYaml(File.ReadAllText(TestData.ParameterSetPath)
            .Replace("port.mailiao.speedLimit_kn:     { value: 8,", "port.mailiao.speedLimit_kn:     { value: 3,"));
        var r2 = Replayer.Replay(path2, TestData.Ship, rescore: speedSet);
        Assert.True(r2.HashMatches, $"{r2.ExpectedHash} vs {r2.ActualHash}");
        Assert.True(r2.EventSequenceMatches, r2.FirstEventMismatch);
        var speedBand = r2.Summary!.Assessment.Bands.Single(b => b.Id == "speed");
        Assert.True(speedBand.Value > 0, "新速限 3 kn 下應累計超速秒數");
        Assert.Equal("fail", speedBand.State);
        File.Delete(path2);
    }
}
