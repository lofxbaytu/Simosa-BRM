using System.Text.Json;
using SimosaBRM.SimCore.Alerts;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>感測器/警報/BNWAS 的契約(state/command/scenario schema、fault-catalogue.json)與 C# 實作一致(sensors-alerts.md 第 8 節)。</summary>
public class SensorAlertContractsTests
{
    private static JsonDocument Schema(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", name)));

    private static IEnumerable<string> Enum(JsonElement e) => e.GetProperty("enum").EnumerateArray().Select(x => x.GetString()!);

    [Fact]
    public void StateSchemaDescribesNewOptionalBlocks()
    {
        using var schema = Schema("state.schema.json");
        var root = schema.RootElement;
        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        var props = root.GetProperty("properties");
        foreach (var key in new[] { "truth", "sensors", "autopilot", "alerts", "bnwas" })
        {
            Assert.True(props.TryGetProperty(key, out _), key);
            Assert.DoesNotContain(key, required);
        }
        var alertItem = props.GetProperty("alerts").GetProperty("items").GetProperty("properties");
        Assert.Equal(Enum<AlertPriority>(), Enum(alertItem.GetProperty("priority")));
        Assert.Equal(new[] { "A", "B", "C" }, Enum(alertItem.GetProperty("category")));
        Assert.Equal(Enum<AlertState>().Where(s => s != "normal"), Enum(alertItem.GetProperty("state")));
        Assert.Equal(Enum<BnwasMode>(), Enum(props.GetProperty("bnwas").GetProperty("properties").GetProperty("mode")));

        // 實際輸出的鍵都在 schema 內,且必要鍵齊全
        var e = TestData.MinimalEngine("bnwas: { mode: \"on\", dormant_s: 180 }");
        e.Enqueue(SimCommand.Autopilot(true, heading: 0));
        e.Enqueue(SimCommand.InjectFault("gyroFailure"));
        e.Run(50 * 200);
        using var before = JsonDocument.Parse(ContractJson.Serialize(e.BuildState()));
        var alertsBefore = before.RootElement.GetProperty("alerts");
        Assert.Equal(2, alertsBefore.GetArrayLength());
        Assert.Equal("alarm", alertsBefore[0].GetProperty("priority").GetString());
        Assert.Equal("gyroFailure", alertsBefore[0].GetProperty("id").GetString());
        Assert.Equal("bnwas", alertsBefore[1].GetProperty("id").GetString()); // 第二階段已升級為 alarm、較晚成立
        Assert.True(alertsBefore[1].GetProperty("escalated").GetBoolean());
        Assert.Equal(2, before.RootElement.GetProperty("bnwas").GetProperty("stage").GetInt32());
        // 靜音是駕駛台操作:BNWAS 重設(其警報隨即解除),電羅經警報進入 activeSilenced
        e.Enqueue(SimCommand.AlertSilence());
        e.Run(5);
        using var doc = JsonDocument.Parse(ContractJson.Serialize(e.BuildState()));
        var state = doc.RootElement;
        foreach (var block in new[] { "truth", "sensors", "autopilot", "bnwas" })
        {
            var spec = props.GetProperty(block);
            foreach (var p in state.GetProperty(block).EnumerateObject())
                Assert.True(spec.GetProperty("properties").TryGetProperty(p.Name, out _), $"{block}.{p.Name} 不在 schema");
            foreach (var r in spec.GetProperty("required").EnumerateArray())
                Assert.True(state.GetProperty(block).TryGetProperty(r.GetString()!, out _), $"{block} 缺少 {r.GetString()}");
        }
        foreach (var a in alertsBefore.EnumerateArray().Concat(state.GetProperty("alerts").EnumerateArray()))
            foreach (var p in a.EnumerateObject()) Assert.True(alertItem.TryGetProperty(p.Name, out _), $"alerts[].{p.Name}");
        var only = Assert.Single(state.GetProperty("alerts").EnumerateArray());
        Assert.Equal("activeSilenced", only.GetProperty("state").GetString());
        Assert.True(only.TryGetProperty("silencedUntil_s", out _));
        Assert.Equal("on", state.GetProperty("bnwas").GetProperty("mode").GetString());
        Assert.Equal(0, state.GetProperty("bnwas").GetProperty("stage").GetInt32());
        Assert.False(state.GetProperty("sensors").GetProperty("headingValid").GetBoolean());
        var back = ContractJson.Deserialize<OwnShipState>(state.GetRawText());
        Assert.Equal(state.GetRawText(), ContractJson.Serialize(back));
    }

    private static IEnumerable<string> Enum<T>() where T : struct, System.Enum => System.Enum.GetValues<T>().Select(v => ContractJson.EnumName(v));

    [Fact]
    public void CommandSchemaListsNewTypesAndExamplesParse()
    {
        using var schema = Schema("command.schema.json");
        var types = Enum(schema.RootElement.GetProperty("properties").GetProperty("type")).ToList();
        foreach (var t in new[] { SimCommandType.AlertAcknowledge, SimCommandType.AlertSilence, SimCommandType.BnwasMode, SimCommandType.BnwasReset })
            Assert.Contains(ContractJson.EnumName(t), types);
        var examples = schema.RootElement.GetProperty("examples").EnumerateArray().Select(x => SimCommand.Parse(x.GetRawText())!).ToList();
        Assert.Equal("gyroFailure", examples.Single(c => c.Type == SimCommandType.AlertAcknowledge).ArgAsString("id"));
        Assert.Null(examples.Single(c => c.Type == SimCommandType.AlertSilence).Args);
        var bm = examples.Single(c => c.Type == SimCommandType.BnwasMode);
        Assert.Equal("on", bm.ValueAsString());
        Assert.Equal(540.0, bm.ArgAsDouble("dormant_s"));
        Assert.Contains(examples, c => c.Type == SimCommandType.BnwasReset);
        Assert.Equal("""{"type":"alertAcknowledge","args":{"id":"gyroFailure"}}""", SimCommand.AlertAcknowledge("gyroFailure").ToJson());
        Assert.Equal("""{"type":"bnwasMode","value":"auto","args":{"dormant_s":360}}""", SimCommand.BnwasMode(BnwasMode.Auto, 360).ToJson());
        Assert.Equal("""{"type":"bnwasReset"}""", SimCommand.BnwasReset().ToJson());
    }

    [Fact]
    public void ScenarioSchemaHasBridgeSectionsAndGyroCheck()
    {
        using var schema = Schema("scenario.schema.json");
        var props = schema.RootElement.GetProperty("properties");
        Assert.True(props.GetProperty("sensors").GetProperty("properties").TryGetProperty("magneticCompassError_deg", out _));
        foreach (var k in new[] { "escalate_s", "silence_s", "offHeading_deg", "offHeadingDelay_s", "headingMonitor_deg", "shallowWater_m" })
            Assert.True(props.GetProperty("alerts").GetProperty("properties").TryGetProperty(k, out _), k);
        foreach (var k in new[] { "mode", "dormant_s", "stage2_s", "stage3_s" })
            Assert.True(props.GetProperty("bnwas").GetProperty("properties").TryGetProperty(k, out _), k);
        Assert.Contains("\"gyroCheck\"", File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", "scenario.schema.json")));
    }

    [Fact]
    public void FaultCatalogueMatchesEngine()
    {
        using var cat = Schema("fault-catalogue.json");
        var faults = cat.RootElement.GetProperty("faults").EnumerateArray().ToList();
        Assert.Equal(ScenarioValidator.FaultCatalogue, faults.Select(f => f.GetProperty("name").GetString()));
        Assert.Equal(FaultNames.Modelled.OrderBy(x => x), faults.Where(f => f.GetProperty("modelled").GetBoolean()).Select(f => f.GetProperty("name").GetString()!).OrderBy(x => x));
        foreach (var f in faults.Where(f => f.TryGetProperty("alert", out _)))
        {
            var def = AlertCatalogue.Find(f.GetProperty("name").GetString()!);
            Assert.NotNull(def);
            Assert.Equal(ContractJson.EnumName(def!.Priority), f.GetProperty("alert").GetString());
        }
        // 每個故障類警報都在目錄中標示 alert
        foreach (var d in AlertCatalogue.All.Where(d => FaultNames.Modelled.Contains(d.Id)))
            Assert.Contains(faults, f => f.GetProperty("name").GetString() == d.Id && f.TryGetProperty("alert", out _));
        // NMEA 文字為 ASCII、編號唯一
        Assert.All(AlertCatalogue.All, d => Assert.True(d.NmeaText.All(c => c >= 0x20 && c < 0x7F)));
        Assert.Equal(AlertCatalogue.All.Length, AlertCatalogue.All.Select(d => d.Number).Distinct().Count());
    }
}
