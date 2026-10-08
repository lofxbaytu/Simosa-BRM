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
}
