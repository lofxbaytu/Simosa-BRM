using System.Text.Json;
using System.Text.Json.Nodes;
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
/// 內嵌情境載入(loadScenario args.scenario;規劃書第 9.1 節靠離泊預演自動組題):與檔案載入走同一條
/// 「節點樹 → 參數替換 → JSON → Scenario」路徑,同一內容得到相同雜湊與事件序列;紀錄標頭保存完整情境,重播一致。
/// </summary>
public class InlineScenarioTests
{
    private static readonly Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg = (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static string E13Path => Path.Combine(TestData.Root, "data", "scenarios", "E13_tug_berthing_mailiao.yaml");

    /// <summary>E13 YAML → 通用節點樹(與網頁 YAML 解析得到的物件相同結構)→ JsonElement,如同 loadScenario 指令的 args.scenario。</summary>
    private static JsonElement E13Element()
    {
        var node = YamlJson.ToJsonNode(File.ReadAllText(E13Path))!;
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static SimCommand InlineCommand(JsonElement scenario, string? id)
    {
        var args = new JsonObject { ["scenario"] = JsonNode.Parse(scenario.GetRawText()) };
        if (id is not null) args["id"] = id;
        return SimCommand.Parse($$"""{"type":"loadScenario","args":{{args.ToJsonString()}}}""")!;
    }

    /// <summary>與主控程序相同的腳本:拖船推頂、停俥滑行(E13 引擎每秒一筆指令)。</summary>
    private static void Script(SimulationEngine e, int t)
    {
        if (t == 10) e.Enqueue(SimCommand.TugControl("TG1", "push", 270, 40));
        if (t == 12) e.Enqueue(SimCommand.TugControl("TG2", "push", 270, 40));
        if (t == 60) e.Enqueue(SimCommand.Acknowledge("checklist", "mpx", "pilot"));
        if (t == 120) e.Enqueue(SimCommand.Rudder(10));
        if (t == 200) e.Enqueue(SimCommand.TugControl("TG1", "pull", 90, 30));
    }

    private static void RunSeconds(SimulationEngine e, int endS)
    {
        for (var t = 1; t <= endS && !e.Ended; t++)
        {
            Script(e, t);
            var target = (long)Math.Round(t / e.Dt);
            while (e.Tick < target && !e.Ended) e.Step();
        }
    }

    [Fact]
    public void FromJsonElement_SameContentAsFile_SameHashAndEvents()
    {
        var fromFile = ScenarioLoader.Load(E13Path);
        var inline = ScenarioLoader.FromJsonElement(E13Element());
        Assert.Equal(fromFile.Id, inline.Id);
        Assert.Equal(fromFile.ToJson(), inline.ToJson());                       // 參數引用與泊位參照解析結果相同
        Assert.NotNull(inline.Berth?.Definition);
        Assert.Equal(fromFile.ParameterSetInfo?.Sha256, inline.ParameterSetInfo?.Sha256);

        var a = new SimulationEngine(TestData.Fsb1(), fromFile, Mmg);
        var b = new SimulationEngine(TestData.Fsb1(), inline, Mmg);
        var ea = TestData.Capture(a);
        var eb = TestData.Capture(b);
        RunSeconds(a, 300);
        RunSeconds(b, 300);
        Assert.Equal(a.Tick, b.Tick);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(ea.Select(x => (x.Seq, x.Kind, x.Tick, x.ItemId)), eb.Select(x => (x.Seq, x.Kind, x.Tick, x.ItemId)));
        Assert.Contains(ea, x => x.Kind == "tugCommand");
    }

    [Fact]
    public void LoadScenarioCommand_WithInlineObject_RaisesInlineEventAndReplays()
    {
        string? pathRequested = null;
        JsonElement? received = null;
        string? receivedId = null;
        var recordPath = Path.Combine(TestData.TestRecordsDirectory, $"inline-{Guid.NewGuid():N}.jsonl");
        RecordWriter? writer = null;
        // E13 需要外力(拖船/泊位):以 MMG 工廠的引擎(E01 基線)接收 loadScenario 內嵌情境
        var mmgEngine = new SimulationEngine(TestData.Fsb1(), TestData.Baseline(), Mmg);
        mmgEngine.ScenarioLoadRequested += (_, p) => pathRequested = p;
        mmgEngine.InlineScenarioLoadRequested += (e, el, id) =>
        {
            received = el;
            receivedId = id;
            // 與主控程序相同:解析 → LoadScenario(船舶不同時給 particulars)→ 紀錄另開新檔
            var sc = ScenarioLoader.FromJsonElement(el, null, Path.Combine(TestData.Root, "data", "scenarios"), id);
            e.LoadScenario(sc, string.Equals(sc.Ship.Id, e.Ship.Id, StringComparison.OrdinalIgnoreCase) ? null : TestData.Ship(sc.Ship.Id));
            writer = new RecordWriter(recordPath, e);
        };
        TestData.RunTo(mmgEngine, 1);
        mmgEngine.Enqueue(InlineCommand(E13Element(), "rehearsal-test"));
        mmgEngine.ProcessPendingCommands();
        Assert.Null(pathRequested);                                              // 有 args.scenario 時不走檔案路徑
        Assert.NotNull(received);
        Assert.Equal("rehearsal-test", receivedId);
        Assert.Equal("E13_tug_berthing_mailiao", mmgEngine.Scenario.Id);        // 情境自帶 id 優先於 args.id
        Assert.Equal(0, mmgEngine.Tick);
        Assert.NotNull(mmgEngine.Scenario.Berth?.Definition);

        RunSeconds(mmgEngine, 300);
        writer!.Close();
        var r = Replayer.Replay(recordPath, TestData.Ship, Mmg);
        Assert.True(r.IntegrityOk);
        Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
        Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
        Assert.True(r.SummaryMatches);
        Assert.Null(r.Warning);
        // 與檔案載入的引擎跑同一腳本得到相同雜湊
        var file = new SimulationEngine(TestData.Fsb1(), ScenarioLoader.Load(E13Path), Mmg);
        RunSeconds(file, 300);
        Assert.Equal(file.StateHash, mmgEngine.StateHash);
    }

    [Fact]
    public void FromJsonElement_UsesDefaultIdWhenScenarioHasNone_AndRejectsNonObject()
    {
        var node = (JsonObject)YamlJson.ToJsonNode(File.ReadAllText(TestData.BaselineScenarioPath))!;
        node.Remove("id");
        using var doc = JsonDocument.Parse(node.ToJsonString());
        var sc = ScenarioLoader.FromJsonElement(doc.RootElement, defaultId: "berthing-rehearsal-20261113");
        Assert.Equal("berthing-rehearsal-20261113", sc.Id);
        using var none = JsonDocument.Parse(node.ToJsonString());
        Assert.Equal("inline", ScenarioLoader.FromJsonElement(none.RootElement).Id);
        using var arr = JsonDocument.Parse("[1,2]");
        Assert.Throws<InvalidDataException>(() => ScenarioLoader.FromJsonElement(arr.RootElement));
    }

    [Fact]
    public void LoadScenarioCommand_InlineWithoutId_UsesScenarioIdOrInline()
    {
        var engine = TestData.Engine();
        string? gotId = "unset";
        engine.InlineScenarioLoadRequested += (_, _, id) => gotId = id;
        using var doc = JsonDocument.Parse("""{"id":"x","ship":{"id":"FSB1"}}""");
        engine.Enqueue(InlineCommand(doc.RootElement, null));
        engine.ProcessPendingCommands();
        Assert.Null(gotId);
    }
}
