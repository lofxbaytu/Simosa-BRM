using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class ScenarioTests
{
    [Fact]
    public void LoadsBaselineScenario()
    {
        var sc = TestData.Baseline();
        Assert.Equal("E01_baseline", sc.Id);
        Assert.Equal("FSB1", sc.Ship.Id);
        Assert.Equal(LoadingCondition.Ballast, sc.Ship.Loading);
        Assert.Equal(20260101, sc.Seed);
        Assert.NotNull(sc.Origin);
        Assert.Equal(23.80, sc.Origin!.Lat, 9);
        Assert.Equal(120.05, sc.Origin.Lon, 9);
        Assert.Equal(0.0, sc.Initial.Heading);
        Assert.Equal(7.8, sc.Initial.Speed);
        Assert.Equal("HAH", sc.Initial.Telegraph);
        Assert.Equal(15.0, sc.Environment.Wind.TrueSpeed);
        Assert.Equal(45.0, sc.Environment.Wind.TrueDir);
        Assert.Equal(0.1, sc.Environment.Wind.Gustiness);
        Assert.Equal(200.0, sc.Environment.Current.Set);
        Assert.Equal(0.5, sc.Environment.Current.Drift);
        Assert.Equal(30.0, sc.Environment.WaterDepth);
        Assert.Equal("2026-01-01T02:00:00Z", sc.StartTimeUtc);
    }

    [Fact]
    public void OriginDefaultsToInitialPosition()
    {
        var sc = ScenarioLoader.FromYaml("""
            id: T
            ship: { id: FSB2, loading: full }
            initial: { position: { lat: 24.1, lon: 120.4 }, heading: 90, speed: 5 }
            environment: { waterDepth: 20 }
            """).Validate();
        Assert.Equal(24.1, sc.Origin!.Lat);
        Assert.Equal(120.4, sc.Origin.Lon);
        Assert.Equal(LoadingCondition.Full, sc.Ship.Loading);
        Assert.Null(sc.Initial.Telegraph);
    }

    [Fact]
    public void LocalPositionRequiresOrigin()
    {
        var yaml = """
            id: T
            ship: { id: FSB1 }
            initial: { position: { x: 100, y: 200 }, heading: 0 }
            environment: { waterDepth: 20 }
            """;
        Assert.Throws<InvalidDataException>(() => ScenarioLoader.FromYaml(yaml).Validate());
    }

    [Fact]
    public void UnknownKeysAreIgnoredAndJsonRoundTrips()
    {
        var sc = ScenarioLoader.FromYaml("""
            id: T
            futureKey: 123
            ship: { id: FSB1, loading: ballast }
            initial: { position: { lat: 23.8, lon: 120.0 }, heading: 10, speed: 3, telegraph: SAH }
            environment: { wind: { trueSpeed: 5, trueDir: 90 }, waterDepth: 15 }
            """).Validate();
        var json = sc.ToJson();
        var back = Scenario.Scenario.FromJson(json);
        Assert.Equal(sc.Ship.Loading, back.Ship.Loading);
        Assert.Equal(sc.Initial.Telegraph, back.Initial.Telegraph);
        Assert.Equal(sc.Environment.Wind.TrueDir, back.Environment.Wind.TrueDir);
        Assert.Equal(sc.Origin!.Lat, back.Origin!.Lat);
    }

    [Fact]
    public void ScenarioSchemaIsValidJsonSchema()
    {
        var path = Path.Combine(TestData.Root, "src", "Contracts", "scenario.schema.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal("Scenario", root.GetProperty("title").GetString());
        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("id", required);
        Assert.Contains("ship", required);
        Assert.Contains("initial", required);
        Assert.Contains("environment", required);
        // 範例應能被 C# 情境模型讀取
        var example = root.GetProperty("examples")[0].GetRawText();
        var sc = Scenario.Scenario.FromJson(example);
        Assert.Equal("E01_baseline", sc.Id);
        Assert.Equal("HAH", sc.Initial.Telegraph);
    }
}
