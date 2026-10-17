using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class ContractsTests
{
    [Fact]
    public void OwnShipStateContainsAllRequiredSchemaFields()
    {
        var schemaPath = Path.Combine(TestData.Root, "src", "Contracts", "state.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(required);

        var engine = TestData.Engine();
        engine.Run(10);
        var json = ContractJson.Serialize(engine.BuildState());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var key in required)
            Assert.True(root.TryGetProperty(key, out _), $"缺少必要欄位 {key}");

        // 列舉與特殊名稱依 schema
        Assert.Equal("HAH", root.GetProperty("telegraph").GetString());
        Assert.Equal("ballast", root.GetProperty("loading").GetString());
        Assert.True(root.GetProperty("engine").TryGetProperty("load_pct", out _));
        Assert.Equal("running", root.GetProperty("engine").GetProperty("state").GetString());
        Assert.True(root.GetProperty("pos").TryGetProperty("lat", out _));
        Assert.InRange(root.GetProperty("heading").GetDouble(), 0.0, 360.0);
        Assert.InRange(root.GetProperty("cog").GetDouble(), 0.0, 360.0);
        Assert.False(root.GetProperty("flags").GetProperty("frozen").GetBoolean());
    }

    [Fact]
    public void OwnShipStateRoundTrips()
    {
        var engine = TestData.Engine();
        engine.Run(5);
        var state = engine.BuildState();
        var json = ContractJson.Serialize(state);
        var back = ContractJson.Deserialize<OwnShipState>(json);
        Assert.NotNull(back);
        Assert.Equal(json, ContractJson.Serialize(back));
        Assert.Equal(state.Pos, back!.Pos);
        Assert.Equal(state.Wind, back.Wind);
    }

    [Fact]
    public void SimCommandExamplesFromSchemaParse()
    {
        var schemaPath = Path.Combine(TestData.Root, "src", "Contracts", "command.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var examples = schema.RootElement.GetProperty("examples").EnumerateArray().Select(e => e.GetRawText()).ToList();
        Assert.True(examples.Count >= 4, "前四個範例為基本指令;其後為 targetControl 範例(TrafficScenarioTests 另驗)");

        var rudder = SimCommand.Parse(examples[0])!;
        Assert.Equal(SimCommandType.Rudder, rudder.Type);
        Assert.Equal(-20.0, rudder.ValueAsDouble());

        var telegraph = SimCommand.Parse(examples[1])!;
        Assert.Equal(SimCommandType.Telegraph, telegraph.Type);
        Assert.Equal("HAH", telegraph.ValueAsString());

        var ap = SimCommand.Parse(examples[2])!;
        Assert.Equal(SimCommandType.Autopilot, ap.Type);
        Assert.True(ap.ArgAsBool("enabled"));
        Assert.Equal(275.0, ap.ArgAsDouble("heading"));
        Assert.Equal(15.0, ap.ArgAsDouble("rotLimit"));

        var env = SimCommand.Parse(examples[3])!;
        Assert.Equal(SimCommandType.SetEnvironment, env.Type);
        Assert.Equal(12.0, env.ArgAsDouble("waterDepth"));
        Assert.Equal(20.0, SimCommand.AsDouble(env.Arg("wind")!.Value.GetProperty("trueSpeed")));
    }

    [Fact]
    public void SimCommandSerializesWithSchemaNames()
    {
        var json = SimCommand.SetEnvironment(windTrueSpeedKn: 20, windTrueDirDeg: 40).ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("setEnvironment", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(20.0, doc.RootElement.GetProperty("args").GetProperty("wind").GetProperty("trueSpeed").GetDouble());
        Assert.False(doc.RootElement.TryGetProperty("value", out _));

        var t = SimCommand.Telegraph(TelegraphOrder.NAVF).ToJson();
        Assert.Contains("\"value\":\"NAVF\"", t);
    }

    [Fact]
    public void ScenarioEngineStateFieldsAreOptionalAndPresent()
    {
        var schemaPath = Path.Combine(TestData.Root, "src", "Contracts", "state.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.DoesNotContain("scenario", required);
        Assert.DoesNotContain("route", required);
        Assert.DoesNotContain("visibility_nm", required);
        Assert.DoesNotContain("steering", required);
        var props = schema.RootElement.GetProperty("properties");
        Assert.Equal(new[] { "fu", "nfu", "autopilot", "emergency" }, props.GetProperty("steering").GetProperty("properties").GetProperty("mode").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.True(props.TryGetProperty("scenario", out var sc));
        Assert.Equal(new[] { "id", "phase" }, sc.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        foreach (var key in new[] { "legIndex", "nextWaypoint", "xtd_m", "xtdLimit_m", "distanceToWaypoint_nm", "bearingToWaypoint_deg", "legCourse_deg", "speedLimit_kn", "complete" })
            Assert.True(props.GetProperty("route").GetProperty("properties").TryGetProperty(key, out _), key);

        var engine = new SimosaBRM.SimCore.Engine.SimulationEngine(TestData.Fsb1(), TestData.E07());
        engine.Run(60);
        using var doc = JsonDocument.Parse(ContractJson.Serialize(engine.BuildState()));
        var root = doc.RootElement;
        Assert.Equal(6.0, root.GetProperty("visibility_nm").GetDouble());
        var scen = root.GetProperty("scenario");
        Assert.Equal("E07_steering_failure", scen.GetProperty("id").GetString());
        Assert.Equal("normal", scen.GetProperty("phase").GetString());
        Assert.Equal("2026.10.1", scen.GetProperty("parameterSetVersion").GetString());
        Assert.False(scen.GetProperty("ended").GetBoolean());
        Assert.Equal("fu", root.GetProperty("steering").GetProperty("mode").GetString());
        Assert.Equal("1", root.GetProperty("steering").GetProperty("pump").GetString());
        var route = root.GetProperty("route");
        Assert.Equal("WP2", route.GetProperty("nextWaypoint").GetString()); // 航段 0 = WP1 → WP2
        Assert.Equal(50.0, route.GetProperty("xtdLimit_m").GetDouble());
        Assert.True(route.TryGetProperty("xtd_m", out _));
        var back = ContractJson.Deserialize<OwnShipState>(root.GetRawText());
        Assert.Equal(root.GetRawText(), ContractJson.Serialize(back));
    }

    [Fact]
    public void NewCommandExamplesParseAndSerialize()
    {
        var schemaPath = Path.Combine(TestData.Root, "src", "Contracts", "command.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var types = schema.RootElement.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        foreach (var t in new[] { "triggerTimeline", "acknowledge", "endExercise", "getAssessment", "steeringMode" }) Assert.Contains(t, types);
        var examples = schema.RootElement.GetProperty("examples").EnumerateArray().Select(e => SimCommand.Parse(e.GetRawText())!).ToList();
        var trig = examples.Single(c => c.Type == SimCommandType.TriggerTimeline);
        Assert.Equal("emergencySteering", trig.ArgAsString("id"));
        Assert.False(trig.ArgAsBool("force"));
        var ack = examples.Single(c => c.Type == SimCommandType.Acknowledge);
        Assert.Equal("notifyEcr", ack.ArgAsString("kind"));
        Assert.Equal("steeringFail", ack.ArgAsString("itemId"));
        var inj = examples.Last(c => c.Type == SimCommandType.InjectFault);
        Assert.Equal("steeringGear", inj.FaultName());
        Assert.Equal(15.0, SimCommand.AsDouble(inj.Arg("args")!.Value.GetProperty("jamAt_deg")));
        var env = examples.Last(c => c.Type == SimCommandType.SetEnvironment);
        Assert.Equal(180.0, env.ArgAsDouble("ramp_s"));
        Assert.Equal(2.0, env.ArgAsDouble("visibility_nm"));
        Assert.Contains(examples, c => c.Type == SimCommandType.GetAssessment);
        var sm = examples.First(c => c.Type == SimCommandType.SteeringMode);
        Assert.Equal("nfu", sm.ValueAsString());
        Assert.Equal("both", sm.ArgAsString("pump"));
        Assert.Equal("starboard", sm.ArgAsString("direction"));
        Assert.Equal("emergency", examples.Last(c => c.Type == SimCommandType.SteeringMode).ValueAsString());
        Assert.Contains("\"type\":\"triggerTimeline\"", SimCommand.TriggerTimeline("x", true).ToJson());
        Assert.Contains("\"ramp_s\":5", SimCommand.SetEnvironment(windTrueSpeedKn: 1, rampS: 5).ToJson());
    }

    [Fact]
    public void ParameterSetSchemaExampleLoads()
    {
        var schemaPath = Path.Combine(TestData.Root, "src", "Contracts", "parameter-set.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        Assert.Equal("ParameterSet", schema.RootElement.GetProperty("title").GetString());
        var example = schema.RootElement.GetProperty("examples")[0].GetRawText();
        var set = ContractJson.Deserialize<SimosaBRM.SimCore.Parameters.ParameterSet>(example)!.Validate();
        Assert.Equal(3, set.Parameters.Count);
        Assert.Equal("nm", set.Parameters["navigation.cpa.openSea_nm"].Unit);
        // 事件紀錄行的 event 物件為 SimEvent(舊欄位不變)
        var ev = ContractJson.Deserialize<SimosaBRM.SimCore.Engine.SimEvent>("""{"kind":"cpaAlarm","tick":100,"t":2.0,"targetId":"T1","cpa_nm":0.3}""")!;
        Assert.Equal("cpaAlarm", ev.Kind);
        Assert.Equal("engine", ev.Source);
        Assert.Equal(0.3, ev.CpaNm);
    }
}
