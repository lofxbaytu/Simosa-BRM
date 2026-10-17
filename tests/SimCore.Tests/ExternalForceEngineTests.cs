using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using Xunit;
using static SimosaBRM.SimCore.Tests.ExternalForceTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>外力模組與引擎:舊情境雜湊不變、只在使用時追加、快照/還原、紀錄/重播、暫代/不支援模型、契約欄位。</summary>
public class ExternalForceEngineTests
{
    /// <summary>舊版雜湊(與 HashCompatibilityTests 相同的獨立實作)。</summary>
    private static byte[] LegacyNext(byte[] prev, SimulationEngine e)
    {
        var doubles = new List<double>
        {
            e.Motion.U, e.Motion.V, e.Motion.R, e.Motion.X, e.Motion.Y, e.Motion.Psi,
            e.Actuators.RudderDeg, e.Actuators.RudderOrderDeg, e.Actuators.Rpm, e.Actuators.RpmOrder, e.Actuators.ThrusterActual, e.Environment.GustFactor,
            (double)e.Actuators.EngineMode, e.Actuators.EngineTimerSec, e.Actuators.RpmTarget, e.Actuators.RpmTau,
        };
        var buf = new byte[32 + 8 + doubles.Count * 8];
        prev.CopyTo(buf, 0);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(32), e.Tick);
        for (var i = 0; i < doubles.Count; i++)
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(40 + i * 8), BitConverter.DoubleToInt64Bits(doubles[i]));
        return SHA256.HashData(buf);
    }

    [Fact]
    public void UnusedSectionsKeepLegacyHashUntilAnchorIsUsed()
    {
        // seabed / anchors 設定存在但錨未用:雜湊配置與舊版相同(MMG 與暫代模型皆然);拋錨後才追加
        foreach (var mmg in new[] { true, false })
        {
            var sc = Sc("anchors: { holdingFactor: 1.3, dragSpeed_mps: 0.4 }", speedKn: 6, telegraph: "SAH", seabed: "mud");
            var e = mmg ? Mmg(sc) : new SimulationEngine(TestData.Fsb1(), sc);
            Assert.False(e.External.Active);
            var legacy = new byte[32];
            e.Stepped += eng => legacy = LegacyNext(legacy, eng);
            e.Enqueue(SimCommand.Rudder(10));
            e.Run(300);
            Assert.Equal(Convert.ToHexString(legacy), e.StateHash);
            Assert.Null(e.CreateSnapshot().External);
            // 被拒的外力指令不改變雜湊
            e.Enqueue(SimCommand.TugControl("none", "push", 90, 50));
            e.Run(10);
            Assert.Equal(Convert.ToHexString(legacy), e.StateHash);

            var used = mmg ? Mmg(sc) : new SimulationEngine(TestData.Fsb1(), sc);
            used.Enqueue(SimCommand.Rudder(10));
            used.Run(150);
            used.Enqueue(SimCommand.Anchor("port", "letGo", targetShackles: 3));
            used.Run(150);
            Assert.True(used.External.Active);
            Assert.NotEqual(e.StateHash, used.StateHash);
        }
    }

    [Fact]
    public void MinimalScenarioStillMatchesLegacyHash()
    {
        var e = TestData.MinimalEngine(seed: 11);
        var legacy = new byte[32];
        e.Stepped += eng => legacy = LegacyNext(legacy, eng);
        EngineDeterminismTests.RunScript(e, 500);
        Assert.Equal(Convert.ToHexString(legacy), e.StateHash);
        Assert.Null(e.BuildState().Tugs);
        Assert.Null(e.BuildState().Berth);
        Assert.Equal(2, e.BuildState().Anchors!.Count); // particulars 有錨資料 → 一律廣播(收存)
    }

    private const string Mixed = """
        tugs:
          - { id: TG1, bollardPull_t: 40, attach: fwd, riseTime_s: 25, initial: { mode: standby, direction_deg: 270 } }
          - { id: TG2, bollardPull_t: 35, attach: aft, riseTime_s: 25, initial: { mode: pull, direction_deg: 180, power_pct: 30 } }
        anchors: { port: { length_shackles: 3 } }
        """;

    private static (long Tick, SimCommand Cmd)[] MixedScript() => new[]
    {
        (50L, SimCommand.TugControl("TG1", "push", 270, 60)),
        (300L, SimCommand.Anchor("starboard", "letGo", targetShackles: 4)),
        (900L, SimCommand.InjectFault(FaultNames.TugLineBreak, new { id = "TG2" })),
        (1200L, SimCommand.TugControl("TG1", "pull", 0, 80)),
        (1700L, SimCommand.ClearFault(FaultNames.TugLineBreak)),
        (1800L, SimCommand.Anchor("port", "heave")),
        (2100L, SimCommand.TugControl("TG2", "pull", 200, 50)),
        (2600L, SimCommand.Anchor("starboard", "veer", lengthM: 150)),
    };

    private static SimulationEngine MixedEngine() => Mmg(Sc(Mixed + "\n" + LinearBerthYaml(lines: "{ id: L1, fairlead: midPort, bollard: B1 }").Replace("y: 30", "y: 400"), currentSet: 200, currentDriftKn: 0.8));

    [Fact]
    public void SnapshotRestoreContinuesBitIdentical()
    {
        var a = MixedEngine();
        var eventsA = TestData.Capture(a);
        EngineDeterminismTests.RunScript(a, 3500, MixedScript());
        Assert.Contains(eventsA, x => x.Kind == SimEventKinds.TugLineParted);
        Assert.Contains(eventsA, x => x.Kind == SimEventKinds.AnchorLetGo);

        var b = MixedEngine();
        EngineDeterminismTests.RunScript(b, 1500, MixedScript());
        var snap = EngineSnapshot.FromJson(b.CreateSnapshot().ToJson()); // JSON 往返
        Assert.NotNull(snap.External);
        Assert.Equal(2, snap.External!.Tugs!.Count);
        var c = MixedEngine();
        c.Restore(snap);
        Assert.Equal(b.StateHash, c.StateHash);
        var eventsC = TestData.Capture(c);
        var rest = MixedScript().Where(x => x.Tick >= 1500).ToArray();
        var remaining = 3500 - 1500;
        for (var i = 0; i < remaining; i++)
        {
            foreach (var (tick, cmd) in rest) if (tick == c.Tick) c.Enqueue(cmd);
            c.Step();
        }
        Assert.Equal(a.Tick, c.Tick);
        Assert.Equal(a.StateHash, c.StateHash);
        Assert.Equal(a.Motion, c.Motion);
        Assert.Equal(ContractJson.Serialize(a.BuildState().Anchors), ContractJson.Serialize(c.BuildState().Anchors));
        Assert.Equal(ContractJson.Serialize(a.BuildState().Tugs), ContractJson.Serialize(c.BuildState().Tugs));
        var tailA = eventsA.Where(x => x.Tick > 1500).Select(x => $"{x.Seq}:{x.Kind}:{x.ItemId}").ToList();
        Assert.Equal(tailA, eventsC.Select(x => $"{x.Seq}:{x.Kind}:{x.ItemId}").ToList());
    }

    [Fact]
    public void RecordReplayReproducesHashAndEvents()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"external-{Guid.NewGuid():N}.jsonl");
        var engine = MixedEngine();
        string hash;
        using (var writer = new RecordWriter(path, engine))
        {
            EngineDeterminismTests.RunScript(engine, 3000, MixedScript());
            hash = writer.Close().StateHash;
        }
        var rec = RecordReader.Read(path);
        Assert.Equal(MixedScript().Length, rec.Inputs.Count);
        Assert.Equal(2, rec.Header.Scenario.Tugs!.Count);
        Assert.NotNull(rec.Header.Scenario.Berth!.Definition);
        var result = Replayer.Replay(path, TestData.Ship, (s, l) => new MmgDynamics(s, l, MmgTestSupport.Fsb1Full));
        Assert.True(result.IntegrityOk);
        Assert.Equal(hash, result.ActualHash);
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
    }

    [Fact]
    public void PlaceholderAcceptsExternalForcesAndUnsupportedModelRejects()
    {
        var sc = Sc("tugs: [ { id: TG1, bollardPull_t: 40, attach: { x: 0, y: 0 }, riseTime_s: 10 } ]");
        var p = new SimulationEngine(TestData.Fsb1(), sc);
        Assert.True(p.Dynamics.SupportsExternalForces);
        Apply(p, SimCommand.TugControl("TG1", "push", 90, 100));
        p.Run(1500);
        Assert.True(p.Motion.V > 0.01, "暫代模型以概略質量接受外力(向右舷平移)");

        var n = new SimulationEngine(TestData.Fsb1(), sc, (s, l) => new NoExternalDynamics(new PlaceholderDynamics(s, l)));
        Assert.False(n.External.Active);
        var events = TestData.Capture(n);
        Apply(n, SimCommand.TugControl("TG1", "push", 90, 100));
        Apply(n, SimCommand.Anchor("port", "letGo"));
        Apply(n, SimCommand.Mooring("L1", "makeFast"));
        Assert.All(events.Where(x => x.Kind == SimEventKinds.CommandRejected), x => Assert.Equal(CommandRejectReasons.ExternalForcesUnsupported, x.DataString("reason")));
        Assert.Equal(3, events.Count(x => x.Kind == SimEventKinds.CommandRejected));
        n.Run(100);
        Assert.Equal(0.0, n.Motion.V);
    }

    [Fact]
    public void StateAndCommandContractsCoverNewFields()
    {
        var root = TestData.Root;
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "Contracts", "state.schema.json")));
        var props = state.RootElement.GetProperty("properties");
        foreach (var k in new[] { "tugs", "anchors", "berth" }) Assert.True(props.TryGetProperty(k, out _), k);
        var required = state.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.DoesNotContain("tugs", required);
        var anchorProps = props.GetProperty("anchors").GetProperty("items").GetProperty("properties");
        foreach (var k in new[] { "side", "length_m", "tension_t", "onBottom_m", "status", "position" }) Assert.True(anchorProps.TryGetProperty(k, out _), k);
        Assert.Equal(new[] { "stowed", "running", "holding", "dragging", "heaving", "aweigh" },
            anchorProps.GetProperty("status").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        var tugProps = props.GetProperty("tugs").GetProperty("items").GetProperty("properties");
        foreach (var k in new[] { "id", "mode", "force_t", "direction_deg", "status" }) Assert.True(tugProps.TryGetProperty(k, out _), k);
        var berthProps = props.GetProperty("berth").GetProperty("properties");
        Assert.True(berthProps.TryGetProperty("contacts", out _));
        foreach (var k in new[] { "id", "tension_t", "status" }) Assert.True(berthProps.GetProperty("lines").GetProperty("items").GetProperty("properties").TryGetProperty(k, out _), k);

        using var cmd = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "Contracts", "command.schema.json")));
        var types = cmd.RootElement.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToList();
        foreach (var t in new[] { "tugControl", "anchor", "mooring" }) Assert.Contains(t, types);
        var examples = cmd.RootElement.GetProperty("examples").EnumerateArray().Select(x => SimCommand.Parse(x.GetRawText())!).ToList();
        var tug = examples.First(x => x.Type == SimCommandType.TugControl);
        Assert.Equal("TG1", tug.ArgAsString("id"));
        Assert.Equal(270.0, tug.ArgAsDouble("direction_deg"));
        var an = examples.First(x => x.Type == SimCommandType.Anchor);
        Assert.Equal("letGo", an.ArgAsString("action"));
        Assert.Equal(4.0, an.ArgAsDouble("targetLength_shackles"));
        Assert.Contains(examples, x => x.Type == SimCommandType.Mooring && x.ArgAsString("action") == "makeFast");
        Assert.Contains("\"type\":\"tugControl\"", SimCommand.TugControl("TG1", "push", 90, 50).ToJson());
        Assert.Contains("\"power_pct\":50", SimCommand.TugControl("TG1", "push", 90, 50).ToJson());

        using var scen = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "Contracts", "scenario.schema.json")));
        var sp = scen.RootElement.GetProperty("properties");
        foreach (var k in new[] { "tugs", "anchors", "berth" }) Assert.True(sp.TryGetProperty(k, out _), k);
        Assert.True(sp.GetProperty("environment").GetProperty("properties").TryGetProperty("seabed", out _));

        // 狀態 JSON 往返(含新欄位)
        var e = MixedEngine();
        EngineDeterminismTests.RunScript(e, 400, MixedScript());
        var json = ContractJson.Serialize(e.BuildState());
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("tugs").GetArrayLength());
        Assert.Contains(doc.RootElement.GetProperty("anchors")[0].GetProperty("status").GetString(), new[] { "holding", "dragging" }); // 初始已錨泊的左錨
        Assert.True(doc.RootElement.GetProperty("berth").TryGetProperty("lines", out _));
        var back = ContractJson.Deserialize<OwnShipState>(json);
        Assert.Equal(json, ContractJson.Serialize(back));
    }
}
