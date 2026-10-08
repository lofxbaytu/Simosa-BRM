using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Traffic;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>情境 targets 載入(E01、E05、schema 範例)、觸發、含目標船的確定性/快照/紀錄重播、狀態契約。</summary>
public class TrafficScenarioTests
{
    private static string E05Path => Path.Combine(TestData.Root, "data", "scenarios", "E05_pilot_traffic.yaml");

    [Fact]
    public void BaselineScenarioHasHeadOnAndAnchoredTargets()
    {
        var sc = TestData.Baseline();
        Assert.Equal(2, sc.Targets.Count);
        Assert.Equal(0.5, sc.Assessment.MinCpaNm);
        var t1 = sc.Targets[0];
        Assert.Equal("T1", t1.Id);
        Assert.Equal(416123456, t1.Mmsi);
        Assert.Equal(TargetBehaviourMode.Colreg, t1.Behaviour.Mode);
        Assert.True(t1.Behaviour.Colreg!.Obey);
        Assert.Equal(180.0, t1.Initial.Heading);
        Assert.Equal(-100.0, t1.Initial.Position!.X);
        var t2 = sc.Targets[1];
        Assert.Equal(TargetBehaviourMode.Anchored, t2.Behaviour.Mode);
        Assert.Equal(15.0, t2.Behaviour.Anchor!.Swing);
        // YAML → JSON → YAML 往返保留目標船(紀錄檔標頭用)
        var back = Scenario.Scenario.FromJson(sc.ToJson());
        Assert.Equal(2, back.Targets.Count);
        Assert.Equal(TargetBehaviourMode.Colreg, back.Targets[0].Behaviour.Mode);
        Assert.Equal(0.5, back.Assessment.MinCpaNm);
        var yaml = ScenarioLoader.ToYaml(sc);
        Assert.Contains("minCpa_nm: 0.5", yaml);
        Assert.Equal(2, ScenarioLoader.FromYaml(yaml).Targets.Count);
    }

    [Fact]
    public void PilotTrafficScenarioLoadsAndPilotBoatAppearsNearStation()
    {
        var sc = ScenarioLoader.Load(E05Path);
        Assert.Equal("E05_pilot_traffic", sc.Id);
        Assert.Equal(4, sc.Targets.Count);
        Assert.Equal(LoadingCondition.Full, sc.Ship.Loading);
        var pilot = sc.Targets.Single(t => t.Id == "PILOT");
        Assert.Equal(TargetBehaviourMode.Follow, pilot.Behaviour.Mode);
        Assert.Equal(TargetTriggerType.OwnDistance, pilot.Trigger!.Type);
        Assert.Equal(1.5, pilot.Trigger.LessThan);
        Assert.False(sc.Targets.Single(t => t.Id == "T3").Behaviour.Colreg!.Obey);

        var e = new SimulationEngine(TestData.Fsb1(), sc);
        var events = new List<TrafficEvent>();
        e.TrafficEventRaised += (_, ev) => events.Add(ev);
        Assert.Equal(3, e.BuildState().Targets!.Count); // 引水船未出現
        e.Run(50 * 900); // 15 分鐘:自船 8 kn 接近引水站
        var activated = events.Single(ev => ev.Kind == TrafficEventKinds.Activated && ev.TargetId == "PILOT");
        Assert.InRange(activated.T, 300.0, 800.0);
        var s = e.BuildState();
        Assert.Equal(4, s.Targets!.Count);
        var pb = s.Targets.Single(t => t.Id == "PILOT");
        Assert.True(pb.RangeNm < 0.3, $"引水船應已靠近自船:{pb.RangeNm:F2} nm");
        Assert.Equal("pilot", pb.Lights);
        Assert.Equal(50, pb.Ais!.ShipType);
        // 對遇的 T1 與橫越直航的 T2 已有 COLREG 判定;T3 不守規則
        Assert.Contains(events, ev => ev.Kind == TrafficEventKinds.Colreg && ev.TargetId == "T1" && ev.Message!.StartsWith("headOn"));
        Assert.Contains(events, ev => ev.Kind == TrafficEventKinds.Colreg && ev.TargetId == "T2" && ev.Message!.Contains("standOn"));
        Assert.Contains(events, ev => ev.Kind == TrafficEventKinds.Colreg && ev.TargetId == "T3" && ev.Message!.Contains("(ignored)"));
        Assert.Equal(135.0, s.Targets.Single(t => t.Id == "T3").Heading, 3);
    }

    [Fact]
    public void SchemaExampleWithTargetsLoadsAndInvalidTargetsAreRejected()
    {
        var path = Path.Combine(TestData.Root, "src", "Contracts", "scenario.schema.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var example = doc.RootElement.GetProperty("examples")[0].GetRawText();
        var sc = Scenario.Scenario.FromJson(example);
        Assert.Equal(3, sc.Targets.Count);
        Assert.Equal(TargetBehaviourMode.Follow, sc.Targets[2].Behaviour.Mode);
        Assert.Equal(TargetTriggerType.OwnDistance, sc.Targets[2].Trigger!.Type);
        Assert.True(doc.RootElement.GetProperty("properties").TryGetProperty("targets", out _));
        Assert.True(doc.RootElement.GetProperty("$defs").TryGetProperty("target", out _));

        Assert.Throws<InvalidDataException>(() => ScenarioLoader.FromYaml("""
            id: T
            ship: { id: FSB1 }
            initial: { position: { lat: 23.8, lon: 120.0 }, heading: 0 }
            environment: { waterDepth: 20 }
            targets:
              - id: A
                initial: { position: { x: 0, y: 1000 } }
              - id: A
                initial: { position: { x: 0, y: 2000 } }
            """).Validate());
        Assert.Throws<InvalidDataException>(() => ScenarioLoader.FromYaml("""
            id: T
            ship: { id: FSB1 }
            initial: { position: { lat: 23.8, lon: 120.0 }, heading: 0 }
            environment: { waterDepth: 20 }
            targets:
              - id: A
                initial: { position: { x: 0, y: 1000 } }
                behaviour: { mode: waypoints }
            """).Validate());
        Assert.Throws<InvalidDataException>(() => ScenarioLoader.FromYaml("""
            id: T
            ship: { id: FSB1 }
            initial: { position: { lat: 23.8, lon: 120.0 }, heading: 0 }
            environment: { waterDepth: 20 }
            targets:
              - id: A
                initial: { position: { x: 0, y: 1000 } }
                trigger: { type: ownDistance, lessThan: 1 }
            """).Validate());
    }

    [Fact]
    public void StateSchemaTargetFieldsArePresent()
    {
        var schemaPath = Path.Combine(TestData.Root, "src", "Contracts", "state.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var required = schema.RootElement.GetProperty("$defs").GetProperty("target").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        var engine = TestData.Engine();
        engine.Run(10);
        var json = ContractJson.Serialize(engine.BuildState());
        using var doc = JsonDocument.Parse(json);
        var targets = doc.RootElement.GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());
        foreach (var t in targets.EnumerateArray())
            foreach (var key in required)
                Assert.True(t.TryGetProperty(key, out _), $"targets[] 缺少 {key}");
        var first = targets[0];
        Assert.Equal("T1", first.GetProperty("id").GetString());
        Assert.True(first.TryGetProperty("bcr_nm", out _));
        Assert.True(first.GetProperty("ais").TryGetProperty("navStatus", out _));
        Assert.Equal("colreg", first.GetProperty("behaviour").GetString());
        Assert.Equal("anchored", targets[1].GetProperty("behaviour").GetString());
        // 往返
        var back = ContractJson.Deserialize<OwnShipState>(json);
        Assert.Equal(json, ContractJson.Serialize(back));
        // 指令 schema 列舉含 targetControl,範例可解析
        using var cmdSchema = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", "command.schema.json")));
        Assert.Contains("targetControl", cmdSchema.RootElement.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        foreach (var ex in cmdSchema.RootElement.GetProperty("examples").EnumerateArray())
        {
            var cmd = SimCommand.Parse(ex.GetRawText());
            Assert.NotNull(cmd);
            if (cmd!.Type == SimCommandType.TargetControl) Assert.NotNull(cmd.Args);
        }
    }

    // ------------------------------------------------------------------ 確定性 / 快照 / 重播

    private static readonly (long Tick, SimCommand Cmd)[] TargetScript =
    {
        (0, SimCommand.Rudder(5)),
        (100, SimCommand.TargetOverride("T1", headingDeg: 150, speedKn: 11)),
        (600, SimCommand.TargetControl(new { id = "T1", aisError = new { positionOffset = 300.0, headingError = 10.0 } })),
        (900, SimCommand.TargetRelease("T1")),
        (1200, SimCommand.TargetControl(new { add = new { id = "TUG", shipType = "tug", loa = 30, beam = 9, initial = new { position = new { x = 300, y = 800 }, heading = 0, speed = 6 }, behaviour = new { mode = "follow", follow = new { leader = "own", bearing = 150, range = 120 } }, motion = new { speedTau = 10 } } })),
        (1500, SimCommand.TargetControl(new { id = "T2", sound = "fiveShort", lights = "notUnderCommand" })),
        (1700, SimCommand.TargetControl(new { id = "T1", colreg = "giveWay" })),
    };

    [Fact]
    public void SameInputsWithTargetsGiveSameHashAndTargetsAffectHash()
    {
        var a = TestData.Engine();
        var b = TestData.Engine();
        EngineDeterminismTests.RunScript(a, 2000, TargetScript);
        EngineDeterminismTests.RunScript(b, 2000, TargetScript);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(ContractJson.Serialize(a.BuildState()), ContractJson.Serialize(b.BuildState()));
        Assert.Equal(3, a.BuildState().Targets!.Count);

        // 目標船指令不同 → 雜湊不同(目標狀態在雜湊鏈內)
        var c = TestData.Engine();
        EngineDeterminismTests.RunScript(c, 2000, TargetScript.Where(s => s.Tick != 100));
        Assert.NotEqual(a.StateHash, c.StateHash);
        Assert.Equal(a.Motion, c.Motion); // 自船動力學不受目標船影響

        // 無目標船的情境:雜湊緩衝區與舊版相同(目標船欄位接在既有欄位之後)
        var plain = TestData.Baseline();
        plain.Targets.Clear();
        var p1 = new SimulationEngine(TestData.Fsb1(), plain);
        var p2 = new SimulationEngine(TestData.Fsb1(), plain);
        EngineDeterminismTests.RunScript(p1, 500);
        EngineDeterminismTests.RunScript(p2, 500);
        Assert.Equal(p1.StateHash, p2.StateHash);
    }

    [Fact]
    public void SnapshotRestoreWithTargetsMatchesUninterruptedRun()
    {
        var reference = TestData.Engine();
        EngineDeterminismTests.RunScript(reference, 2500, TargetScript);

        var a = TestData.Engine();
        EngineDeterminismTests.RunScript(a, 1300, TargetScript);
        var json = a.CreateSnapshot().ToJson();
        var snap = EngineSnapshot.FromJson(json);
        Assert.NotNull(snap.Traffic);
        Assert.Equal(3, snap.Traffic!.Targets.Count);
        Assert.Equal("TUG", snap.Traffic.Targets[2].Spec.Id);
        Assert.Contains(snap.Traffic.Targets, t => t.Spec.AisError is { PositionOffset: 300.0 });

        var b = TestData.Engine();
        b.Restore(snap);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(3, b.Traffic.Targets.Count);
        EngineDeterminismTests.RunScript(b, 1200, TargetScript.Where(s => s.Tick >= 1300));
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal(ContractJson.Serialize(reference.BuildState()), ContractJson.Serialize(b.BuildState()));
        Assert.Equal(reference.Traffic.Find("T1")!.ColregPhase, b.Traffic.Find("T1")!.ColregPhase);
    }

    [Fact]
    public void RecordWithTargetsReplaysToSameHashAndKeepsEvents()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"traffic-{Guid.NewGuid():N}.jsonl");
        var engine = TestData.Engine();
        string hash;
        using (var writer = new RecordWriter(path, engine))
        {
            EngineDeterminismTests.RunScript(engine, 2000, TargetScript);
            hash = writer.Close().StateHash;
        }
        var rec = RecordReader.Read(path);
        Assert.True(rec.IntegrityOk);
        Assert.Equal(TargetScript.Length, rec.Inputs.Count);
        Assert.Equal(2, rec.Header.Scenario.Targets.Count); // 標頭情境不含執行中新增的 TUG(由 input 重播)
        Assert.Contains(rec.Events, ev => ev.Event.Kind == TrafficEventKinds.Control && ev.Event.TargetId == "TUG");
        Assert.Contains(rec.Events, ev => ev.Event.Kind == TrafficEventKinds.Sound && ev.Event.TargetId == "T2");
        Assert.Contains(rec.Events, ev => ev.Event.Kind == TrafficEventKinds.Colreg && ev.Event.TargetId == "T1");
        Assert.Contains(rec.Events, ev => ev.Event.Kind == TrafficEventKinds.Activated);
        Assert.Contains(rec.Snapshots, s => s.Snapshot.Traffic is { Targets.Count: 3 });
        var states = RecordReader.ReadStates(path).ToList();
        Assert.All(states, s => Assert.True(s.Targets is { Count: >= 2 }));

        var result = Replayer.Replay(path, TestData.Ship);
        Assert.True(result.IntegrityOk);
        Assert.Null(result.Warning);
        Assert.Equal(hash, result.ActualHash);
        Assert.True(result.HashMatches);
        File.Delete(path);
    }

    [Fact]
    public void ResetAndLoadScenarioRebuildTargetsFromScenarioNotMutatedSpecs()
    {
        var e = TestData.Engine();
        e.Enqueue(SimCommand.TargetAis("T1", false));
        e.Enqueue(SimCommand.TargetRemove("T2"));
        e.Run(50);
        Assert.Single(e.BuildState().Targets!);
        Assert.True(e.Scenario.Targets[0].AisOn, "指令不得修改情境物件");
        e.Enqueue(SimCommand.Reset());
        e.ProcessPendingCommands();
        Assert.Equal(2, e.Traffic.Targets.Count);
        Assert.True(e.Traffic.Find("T1")!.Spec.AisOn);
        e.LoadScenario(ScenarioLoader.Load(E05Path));
        Assert.Equal(4, e.Traffic.Targets.Count);
        Assert.Equal(0.5, e.Traffic.MinCpaThresholdNm);
    }
}
