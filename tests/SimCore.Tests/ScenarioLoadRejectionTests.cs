using System.Text.Json;
using System.Text.Json.Nodes;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// loadScenario 的驗證閘門(交接紀錄待辦第 1 項;規劃書第 9.1 節自動檢查):檔案或 args.scenario 內嵌情境在套用前以
/// <see cref="Scenario.Scenario.LoadIssues"/> 列出全部結構 error,任何載入/驗證失敗都不改變引擎狀態(tick、雜湊、情境、事件序號、紀錄),
/// 並以帶外事件 scenarioLoadRejected(不進序號/雜湊/紀錄)回報教官站;有效情境照常載入。
/// </summary>
public class ScenarioLoadRejectionTests
{
    private const string MinimalYaml = """
        id: T_inline
        seed: 3
        ship: { id: FSB1, loading: ballast }
        origin: { lat: 23.80, lon: 120.05 }
        initial: { position: { x: 0, y: 0 }, heading: 0, speed: 8, telegraph: HAH }
        environment: { wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }, current: { set: 0, drift: 0 }, waterDepth: 30 }
        """;

    /// <summary>兩個結構 error:多邊形只有 2 點、hardFail 引用不存在的區域。</summary>
    private const string InvalidExtra = """
        zones:
          - id: bad
            type: noGo
            polygon:
              - { x: 0, y: 0 }
              - { x: 100, y: 0 }
        assessment:
          hardFail:
            - { id: noGo, rule: zoneEntered, zoneId: missing }
        """;

    private static JsonElement Element(string yaml)
    {
        var node = YamlJson.ToJsonNode(yaml)!;
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static SimCommand InlineCommand(JsonElement scenario, string? id = null)
    {
        var args = new JsonObject { ["scenario"] = JsonNode.Parse(scenario.GetRawText()) };
        if (id is not null) args["id"] = id;
        return SimCommand.Parse($$"""{"type":"loadScenario","args":{{args.ToJsonString()}}}""")!;
    }

    /// <summary>與主控程序 Program.cs 相同的處理者:TryFromJsonElement → TryApplyScenarioLoad。</summary>
    private static void AttachHostHandlers(SimulationEngine e, Func<string, ShipParticulars>? shipLoader = null)
    {
        var loader = shipLoader ?? TestData.Ship;
        e.InlineScenarioLoadRequested += (eng, el, id) =>
            eng.TryApplyScenarioLoad(ScenarioLoader.TryFromJsonElement(el, null, Path.Combine(TestData.Root, "data", "scenarios"), id), loader, "inline");
        e.ScenarioLoadRequested += (eng, path) =>
            eng.TryApplyScenarioLoad(ScenarioLoader.TryLoad(Path.IsPathRooted(path) ? path : Path.Combine(TestData.Root, path)), loader, "file");
    }

    private static List<SimEvent> CaptureRejections(SimulationEngine e)
    {
        var list = new List<SimEvent>();
        e.ScenarioLoadRejected += (_, ev) => list.Add(ev);
        return list;
    }

    private static List<(string Path, string Message, string Severity)> Issues(SimEvent ev)
        => ev.Data!.Value.GetProperty("issues").EnumerateArray()
            .Select(i => (i.GetProperty("path").GetString()!, i.GetProperty("message").GetString()!, i.GetProperty("severity").GetString()!)).ToList();

    [Fact]
    public void LoadIssues_ListsAllStructuralErrors_WhileValidateThrowsFirst()
    {
        var sc = ScenarioLoader.FromYaml(MinimalYaml + "\n" + InvalidExtra);
        var issues = sc.LoadIssues();
        Assert.Equal(new[] { "zones[0].polygon", "assessment.hardFail[0]" }, issues.Select(i => i.Path));
        Assert.All(issues, i => Assert.Equal(ScenarioIssueSeverity.Error, i.Severity));
        var ex = Assert.Throws<InvalidDataException>(() => sc.Validate());
        Assert.StartsWith("zones[0].polygon:", ex.Message);
        // 必要欄位缺失:只回該項(path scenario)
        var missing = ScenarioLoader.FromYaml(MinimalYaml.Replace("initial: { position: { x: 0, y: 0 }, ", "initial: { "));
        var one = Assert.Single(missing.LoadIssues());
        Assert.Equal("scenario", one.Path);
        Assert.Contains("initial.position", one.Message);
        Assert.Empty(ScenarioLoader.FromYaml(MinimalYaml).LoadIssues());
    }

    [Fact]
    public void TryLoad_CollectsIssues_AndTryFromJsonElement_ReportsScenarioId()
    {
        var dir = Path.Combine(TestData.TestRecordsDirectory, "invalid-scenarios");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "T_bad.yaml");
        File.WriteAllText(path, MinimalYaml.Replace("id: T_inline", "") + "\n" + InvalidExtra);
        var r = ScenarioLoader.TryLoad(path);
        Assert.False(r.Ok);
        Assert.Null(r.Scenario);
        Assert.Equal("T_bad", r.ScenarioId);                                   // 無 id 時以檔名代入,與 Load 相同
        Assert.Equal(path, r.Path);
        Assert.Equal(2, r.Issues.Count);
        Assert.Throws<InvalidDataException>(() => ScenarioLoader.Load(path));

        var missing = ScenarioLoader.TryLoad(Path.Combine(dir, "nope.yaml"));
        Assert.False(missing.Ok);
        Assert.Equal("nope", missing.ScenarioId);
        Assert.Equal("scenario", Assert.Single(missing.Issues).Path);        // 讀檔例外成為單一 error 項

        var inline = ScenarioLoader.TryFromJsonElement(Element(MinimalYaml + "\n" + InvalidExtra));
        Assert.False(inline.Ok);
        Assert.Equal("T_inline", inline.ScenarioId);
        Assert.Equal(2, inline.Issues.Count);
        using var arr = JsonDocument.Parse("[1,2]");
        var notObject = ScenarioLoader.TryFromJsonElement(arr.RootElement, defaultId: "rehearsal-x");
        Assert.False(notObject.Ok);
        Assert.Equal("rehearsal-x", notObject.ScenarioId);
        var ok = ScenarioLoader.TryFromJsonElement(Element(MinimalYaml));
        Assert.True(ok.Ok);
        Assert.NotNull(ok.Scenario!.Origin);
    }

    [Fact]
    public void InvalidInlineScenario_RaisesScenarioLoadRejected_AndEngineStateIsUnchanged()
    {
        var engine = TestData.Engine();
        var control = TestData.Engine();
        AttachHostHandlers(engine);
        var rejections = CaptureRejections(engine);
        var events = TestData.Capture(engine);
        var controlEvents = TestData.Capture(control);
        var recordPath = Path.Combine(TestData.TestRecordsDirectory, $"reject-{Guid.NewGuid():N}.jsonl");
        var writer = new RecordWriter(recordPath, engine);
        TestData.RunTo(engine, 5);
        TestData.RunTo(control, 5);
        var (tick, hash, id) = (engine.Tick, engine.StateHash, engine.Scenario.Id);
        engine.Enqueue(SimCommand.Rudder(10));                                 // 同批的其他指令照常生效(可能自帶事件,例如 BNWAS 重設)
        engine.Enqueue(InlineCommand(Element(MinimalYaml + "\n" + InvalidExtra), "rehearsal-bad"));
        control.Enqueue(SimCommand.Rudder(10));
        engine.ProcessPendingCommands();
        control.ProcessPendingCommands();

        var ev = Assert.Single(rejections);
        Assert.Equal(SimEventKinds.ScenarioLoadRejected, ev.Kind);
        Assert.Equal(SimEventSources.Instructor, ev.Source);
        Assert.Equal("T_inline", ev.ItemId);
        Assert.Equal(tick, ev.Tick);
        Assert.Equal(-1, ev.Seq);                                              // 帶外:不計事件序號
        Assert.Contains("T_inline", ev.Message);
        Assert.Contains("zones[0].polygon", ev.Message);
        Assert.Equal("T_inline", ev.DataString("scenarioId"));
        Assert.Equal("inline", ev.DataString("origin"));
        Assert.Equal(2, ev.DataNumber("issueCount"));
        var issues = Issues(ev);
        Assert.Equal(new[] { "zones[0].polygon", "assessment.hardFail[0]" }, issues.Select(i => i.Path));
        Assert.All(issues, i => Assert.Equal("error", i.Severity));

        // 引擎狀態不變:情境、tick、雜湊;事件序號與一般事件與對照引擎(只收 rudder)完全相同;紀錄未關閉、也沒寫拒絕事件
        Assert.Equal(id, engine.Scenario.Id);
        Assert.Equal(tick, engine.Tick);
        Assert.Equal(hash, engine.StateHash);
        Assert.Equal(control.EventSeq, engine.EventSeq);
        Assert.Equal(controlEvents.Select(x => (x.Seq, x.Kind, x.Tick)), events.Select(x => (x.Seq, x.Kind, x.Tick)));
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.ScenarioLoadRejected);
        Assert.False(writer.Closed);
        Assert.Equal(10.0, engine.Actuators.RudderOrderDeg, 6);
        TestData.RunTo(engine, 20);
        TestData.RunTo(control, 20);
        Assert.Equal(control.StateHash, engine.StateHash);                     // 與從未收到該指令的引擎完全一致(確定性)
        writer.Close();
        var text = File.ReadAllText(recordPath);
        Assert.DoesNotContain("scenarioLoadRejected", text);
        Assert.DoesNotContain("\"loadScenario\"", text);
        Assert.Contains("\"rudder\"", text);
    }

    [Fact]
    public void ValidInlineScenario_LoadsAsBefore_NoRejection()
    {
        var engine = TestData.Engine();
        AttachHostHandlers(engine);
        var rejections = CaptureRejections(engine);
        TestData.RunTo(engine, 3);
        engine.Enqueue(InlineCommand(Element(MinimalYaml.Replace("id: T_inline", "")), "rehearsal-ok"));
        engine.ProcessPendingCommands();
        Assert.Empty(rejections);
        Assert.Equal("rehearsal-ok", engine.Scenario.Id);                      // 情境無 id → args.id
        Assert.Equal(0, engine.Tick);
        Assert.Equal(3, engine.Scenario.Seed);
        TestData.RunTo(engine, 2);
        Assert.Equal(100, engine.Tick);
    }

    [Fact]
    public void MissingFile_AndUnknownShip_AreRejectedWithoutStateChange()
    {
        var engine = TestData.Engine();
        AttachHostHandlers(engine, id => throw new FileNotFoundException($"no particulars for {id}"));
        var rejections = CaptureRejections(engine);
        TestData.RunTo(engine, 2);
        var (tick, hash, id) = (engine.Tick, engine.StateHash, engine.Scenario.Id);

        engine.Enqueue(SimCommand.Parse("""{"type":"loadScenario","value":"data/scenarios/does_not_exist.yaml"}""")!);
        engine.ProcessPendingCommands();
        var file = Assert.Single(rejections);
        Assert.Equal("file", file.DataString("origin"));
        Assert.Equal("does_not_exist", file.DataString("scenarioId"));
        Assert.EndsWith("does_not_exist.yaml", file.DataString("path"));
        Assert.Equal("scenario", Assert.Single(Issues(file)).Path);

        // 情境本身有效但船舶 particulars 取不到:也拒絕,引擎的船與情境不變
        engine.Enqueue(InlineCommand(Element(MinimalYaml.Replace("id: FSB1", "id: FSB2"))));
        engine.ProcessPendingCommands();
        Assert.Equal(2, rejections.Count);
        var ship = rejections[1];
        Assert.Equal("ship.id", Assert.Single(Issues(ship)).Path);
        Assert.Contains("FSB2", ship.Message);
        Assert.Equal("FSB1", engine.Ship.Id);
        Assert.Equal(id, engine.Scenario.Id);
        Assert.Equal(tick, engine.Tick);
        Assert.Equal(hash, engine.StateHash);
    }

    [Fact]
    public void LoadScenarioCommand_WithoutHandlerOrPayload_IsRejected()
    {
        var engine = TestData.Engine();
        var rejections = CaptureRejections(engine);
        TestData.RunTo(engine, 1);
        engine.Enqueue(SimCommand.Parse("""{"type":"loadScenario","value":"E07"}""")!);           // 無訂閱者
        engine.Enqueue(SimCommand.Parse("""{"type":"loadScenario"}""")!);                        // 無路徑也無內嵌
        engine.Enqueue(SimCommand.Parse("""{"type":"loadScenario","args":{"id":"x","scenario":[1]}}""")!); // 內嵌非物件
        engine.ProcessPendingCommands();
        Assert.Equal(3, rejections.Count);
        Assert.Equal("E07", rejections[0].DataString("scenarioId"));
        Assert.Contains("未處理", rejections[0].Message);
        Assert.Equal("loadScenario", Assert.Single(Issues(rejections[1])).Path);
        Assert.Equal("args.scenario", Assert.Single(Issues(rejections[2])).Path);
        Assert.Equal("x", rejections[2].DataString("scenarioId"));
        Assert.Equal("E01_baseline", engine.Scenario.Id);
        Assert.Equal(50, engine.Tick);
    }

    [Fact]
    public void LoadScenario_ShipMismatch_ThrowsBeforeTouchingState()
    {
        var engine = TestData.Engine();
        TestData.RunTo(engine, 1);
        var hash = engine.StateHash;
        var loading = engine.Loading;
        var fsb2 = ScenarioLoader.FromYaml(MinimalYaml.Replace("id: FSB1", "id: FSB2").Replace("loading: ballast", "loading: full"));
        bool loading_raised = false;
        engine.ScenarioLoading += (_, _) => loading_raised = true;
        Assert.Throws<InvalidOperationException>(() => engine.LoadScenario(fsb2));
        Assert.False(loading_raised);
        Assert.Equal("E01_baseline", engine.Scenario.Id);                      // 原本先指派 Scenario 再丟例外,狀態會不一致
        Assert.Equal(loading, engine.Loading);
        Assert.Equal(50, engine.Tick);
        Assert.Equal(hash, engine.StateHash);
        // 動力學工廠失敗(例如 MMG 係數檔不存在)亦在改動狀態前丟出
        var failing = new SimulationEngine(TestData.Fsb1(), TestData.Baseline(), (s, l) => l == LoadingCondition.Full ? throw new FileNotFoundException("coefficients") : new Physics.PlaceholderDynamics(s, l));
        TestData.RunTo(failing, 1);
        var fullLoading = ScenarioLoader.FromYaml(MinimalYaml.Replace("loading: ballast", "loading: full"));
        Assert.Throws<FileNotFoundException>(() => failing.LoadScenario(fullLoading));
        Assert.Equal("E01_baseline", failing.Scenario.Id);
        Assert.Equal(LoadingCondition.Ballast, failing.Loading);
        Assert.Equal(50, failing.Tick);
    }
}
