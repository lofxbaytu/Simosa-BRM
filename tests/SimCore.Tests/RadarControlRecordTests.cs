using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 雷達控制操作進核心紀錄(指令 radarControl,由雷達服務轉送;規劃書第 8.3 節第 7 類、radar-arpa.md 第 5.1 節):
/// 只寫 input 行並發 radarControl 事件、不影響物理與(無情境引擎時的)雜湊、重播只重發事件、指標 radar.*。
/// </summary>
public class RadarControlRecordTests
{
    private static SimCommand Range(double nm, bool ok = true) => SimCommand.RadarControl("x", "c1", "range", ok, value: nm, request: new { value = nm });
    private static SimCommand Acquire(int id) => SimCommand.RadarControl("x", "c1", "acquire", true, id: id, request: new { bearing = 45.0, range = 3.0 });

    [Fact]
    public void RadarControlOnlyRaisesEventAndIsRecorded()
    {
        var plain = TestData.MinimalEngine(seed: 3);
        var radar = TestData.MinimalEngine(seed: 3);
        var events = TestData.Capture(radar);
        plain.Run(100);
        radar.Run(100);
        radar.Enqueue(Range(3));
        radar.Enqueue(Acquire(1));
        radar.Enqueue(SimCommand.RadarControl("s", "c2", "acquire", false, error: "追蹤目標已達上限 40"));
        radar.ProcessPendingCommands();
        plain.Run(100);
        radar.Run(100);
        Assert.Equal(plain.Motion, radar.Motion);
        Assert.Equal(plain.StateHash, radar.StateHash); // 無情境引擎:雜湊完全不受影響
        var rc = events.Where(x => x.Kind == SimEventKinds.RadarControl).ToList();
        Assert.Equal(3, rc.Count);
        Assert.Equal(SimEventSources.Instructor, rc[0].Source);
        Assert.Equal("x", rc[0].ItemId);
        Assert.Equal("range", rc[0].DataString("cmd"));
        Assert.Equal(3.0, rc[0].DataNumber("value"));
        Assert.Equal("c1", rc[0].DataString("client"));
        Assert.True(rc[0].DataBool("ok"));
        Assert.Equal(1.0, rc[1].DataNumber("id"));
        Assert.Equal("s", rc[2].ItemId);
        Assert.False(rc[2].DataBool("ok"));
        Assert.Contains("失敗", rc[2].Message);
        Assert.Contains("追蹤目標已達上限", rc[2].DataString("error"));
        Assert.False(rc[0].Visible);
    }

    [Fact]
    public void ReplayReemitsRadarControlEventsAndMatchesHash()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"radar-control-{Guid.NewGuid():N}.jsonl");
        var script = new (long, SimCommand)[]
        {
            (50 * 5, Range(3)),
            (50 * 8, SimCommand.Rudder(10)),
            (50 * 12, Acquire(1)),
            (50 * 20, SimCommand.RadarControl("x", "c1", "guardZone", true, request: new { enabled = true, inner_nm = 0.5, outer_nm = 3 })),
        };
        var engine = TestData.MinimalEngine("assessment:\n  hardFail: [ { id: c, rule: collision } ]", seed: 5);
        using (var writer = new RecordWriter(path, engine, includeStates: false))
        {
            EngineDeterminismTests.RunScript(engine, 50 * 30, script);
            writer.Close();
        }
        var rec = RecordReader.Read(path);
        Assert.Equal(3, rec.Inputs.Count(i => i.Command.Type == SimCommandType.RadarControl));
        Assert.Equal(3, rec.Events.Count(e => e.Event.Kind == SimEventKinds.RadarControl));
        var input = rec.Inputs.First(i => i.Command.Type == SimCommandType.RadarControl);
        Assert.Equal(250, input.Tick);
        Assert.Equal("range", input.Command.ArgAsString("cmd"));
        Assert.Equal("x", input.Command.ArgAsString("radar"));
        // 重播:input 行重發事件(不需雷達服務),雜湊與事件序列一致(含情境引擎的事件序號)
        var replayed = new List<SimEvent>();
        var result = Replayer.Replay(path, TestData.Ship, configure: e => e.EventRaised += (_, ev) => replayed.Add(ev));
        Assert.True(result.HashMatches, $"{result.ExpectedHash} / {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.Equal(3, replayed.Count(e => e.Kind == SimEventKinds.RadarControl));
        Assert.Equal(4, result.InputsReplayed);
    }

    /// <summary>
    /// 契約(command.schema.json、radar-arpa.md 第 1/5.1 節):radarControl 不影響物理;無情境引擎時雜湊完全不變(RadarControlOnlyRaisesEventAndIsRecorded),
    /// 有情境引擎(時間表/評估規則)時與 acknowledge 相同、只有事件序號計入雜湊:同一指令序列(含 ok=false 的失敗指令)在任何引擎得到相同雜湊(確定性、重播一致),
    /// 但有無轉送雷達操作的雜湊不同;黃金雜湊比對須以相同的 input 行(含 radarControl)重播。
    /// </summary>
    [Fact]
    public void ScenarioEngineHashCountsRadarControlEventSequenceDeterministically()
    {
        const string yaml = "assessment:\n  hardFail: [ { id: c, rule: collision } ]";
        SimulationEngine Make()
        {
            var e = TestData.MinimalEngine(yaml, seed: 5);
            e.Run(100);
            return e;
        }
        var plain = Make();
        var a = Make();
        var b = Make();
        foreach (var e in new[] { a, b })
        {
            e.Enqueue(Range(3));
            e.Enqueue(SimCommand.RadarControl("x", "c1", "acquire", false, error: "雷達未發射"));
            e.ProcessPendingCommands();
        }
        foreach (var e in new[] { plain, a, b }) e.Run(100);
        Assert.Equal(plain.Motion, a.Motion);
        Assert.Equal(plain.EventSeq + 2, a.EventSeq);
        Assert.Equal(a.StateHash, b.StateHash); // 同序列(含失敗指令)→ 同雜湊
        Assert.NotEqual(plain.StateHash, a.StateHash); // 事件序號計入:與 acknowledge 相同
    }

    [Fact]
    public void RadarMetricsCountSuccessfulControls()
    {
        var e = TestData.MinimalEngine("assessment:\n  hardFail: [ { id: c, rule: collision } ]", seed: 9);
        e.Run(50);
        foreach (var n in new[] { "radar.controlCount", "radar.acquisitions", "radar.rangeChanges" })
        {
            Assert.True(MetricNames.IsKnown(n), n);
            Assert.Equal(0.0, e.Assessment!.MetricValue(n, null!));
        }
        e.Enqueue(Range(3));
        e.Enqueue(Range(6));
        e.Enqueue(Acquire(1));
        e.Enqueue(Acquire(2));
        e.Enqueue(SimCommand.RadarControl("x", "c1", "acquire", false, error: "雷達未發射")); // 失敗不計
        e.Enqueue(SimCommand.RadarControl("x", "c1", "gain", true, value: 60));
        e.Enqueue(SimCommand.RadarControl("x", "c1", "getTracks", true)); // 查詢不計
        e.ProcessPendingCommands();
        e.Run(50);
        Assert.Equal(5.0, e.Assessment!.MetricValue("radar.controlCount", null!));
        Assert.Equal(2.0, e.Assessment.MetricValue("radar.acquisitions", null!));
        Assert.Equal(2.0, e.Assessment.MetricValue("radar.rangeChanges", null!));
        // Python scenario.py METRIC_NAMES 同步(容許帶結構檢查)
        var py = File.ReadAllText(Path.Combine(TestData.Root, "src", "Tools.Calibration", "simosa_brm", "scenario.py"));
        foreach (var n in new[] { "radar.controlCount", "radar.acquisitions", "radar.rangeChanges" }) Assert.Contains($"\"{n}\"", py);
    }

    /// <summary>summary.radar {controlCount, acquisitions, rangeChanges, byRadar{x,s}}:無雷達操作時省略(舊紀錄 summary 不變);快照還原後計數一致。</summary>
    [Fact]
    public void SummaryRadarBlockPerRadarAndOmittedWithoutOperations()
    {
        var e = TestData.MinimalEngine("assessment:\n  hardFail: [ { id: c, rule: collision } ]", seed: 11);
        e.Run(50);
        Assert.Null(e.Assessment!.BuildSummary(e.Tick, final: false).Radar);
        Assert.DoesNotContain("\"radar\"", e.Assessment.BuildSummary(e.Tick, final: false).ToJson());
        e.Enqueue(Range(3));
        e.Enqueue(Acquire(1));
        e.Enqueue(SimCommand.RadarControl("s", "c2", "acquire", true, id: 1, request: new { bearing = 10.0, range = 2.0 }));
        e.Enqueue(SimCommand.RadarControl("s", "c2", "range", true, value: 12, request: new { value = 12 }));
        e.Enqueue(SimCommand.RadarControl("S", "c2", "gain", true, value: 50));
        e.Enqueue(SimCommand.RadarControl("s", "c2", "acquire", false, error: "x"));
        e.ProcessPendingCommands();
        e.Enqueue(SimCommand.Snapshot());
        e.ProcessPendingCommands();
        e.Run(50);
        var r = e.Assessment.BuildSummary(e.Tick, final: false).Radar;
        Assert.NotNull(r);
        Assert.Equal(5, r!.ControlCount);
        Assert.Equal(2, r.Acquisitions);
        Assert.Equal(2, r.RangeChanges);
        Assert.Equal(new[] { "s", "x" }, r.ByRadar.Keys.ToArray());
        Assert.Equal((2, 1, 1), (r.ByRadar["x"].ControlCount, r.ByRadar["x"].Acquisitions, r.ByRadar["x"].RangeChanges));
        Assert.Equal((3, 1, 1), (r.ByRadar["s"].ControlCount, r.ByRadar["s"].Acquisitions, r.ByRadar["s"].RangeChanges));
        var json = e.Assessment.BuildSummary(e.Tick, final: false).ToJson();
        Assert.Contains("\"radar\":{\"controlCount\":5,\"acquisitions\":2,\"rangeChanges\":2,\"byRadar\":{\"s\":", json);
        // 倒帶到快照(所有操作已計入)後再推進:計數不變;繼續操作後計入
        e.Enqueue(SimCommand.Restore());
        e.ProcessPendingCommands();
        e.Run(50);
        Assert.Equal(5, e.Assessment.BuildSummary(e.Tick, final: false).Radar!.ControlCount);
        e.Enqueue(Acquire(2));
        e.ProcessPendingCommands();
        e.Run(50);
        var r2 = e.Assessment.BuildSummary(e.Tick, final: false).Radar!;
        Assert.Equal(6, r2.ControlCount);
        Assert.Equal(3, r2.ByRadar["x"].ControlCount);
    }
}
