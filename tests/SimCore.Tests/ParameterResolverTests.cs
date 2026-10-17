using System.Text.Json.Nodes;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Scenario;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>情境中 ${params:…} 的解析(情境引擎設計第 2.3 節)。</summary>
public class ParameterResolverTests
{
    private const string ScenarioYaml = """
        id: T
        ship: { id: FSB1 }
        initial: { position: { lat: 23.8, lon: 120.0 }, heading: 0, speed: 5 }
        environment: { waterDepth: 20 }
        route:
          xtdLimit_m: ${params:navigation.xtd.channel_m}
          waypoints:
            - { name: A, x: 0, y: 0 }
            - { name: B, x: 0, y: 5000, speedLimit_kn: "${params:port.mailiao.speedLimit_kn}" }
        assessment:
          minCpa_nm: ${params:navigation.cpa.coastal_nm}
          bands:
            - { id: xtd, metric: track.maxXtd_m, warn: "${params:navigation.xtd.channel_m}", fail: 2x, penalty: 10 }
          scoring: { passMark: "${params:assessment.passMark}" }
        """;

    [Fact]
    public void ResolvesValuesAndRecordsReferences()
    {
        var sc = ScenarioLoader.FromYaml(ScenarioYaml, TestData.Parameters()).Validate();
        Assert.Equal(50.0, sc.Route!.XtdLimitM);
        Assert.Equal(8.0, sc.Route.Waypoints[1].SpeedLimitKn);
        Assert.Equal(0.5, sc.Assessment.MinCpaNm);
        Assert.Equal(50.0, sc.Assessment.Bands[0].Warn);
        Assert.Equal(100.0, sc.Assessment.Bands[0].FailValue);
        Assert.Equal(70.0, sc.Assessment.Scoring!.PassMark);
        var info = sc.ParameterSetInfo!;
        Assert.Equal("simosa-company-procedures", info.Id);
        Assert.Equal(5, info.Resolved.Count);
        var r = info.RefAt("assessment.bands[0].warn")!;
        Assert.Equal("navigation.xtd.channel_m", r.Key);
        Assert.Equal("SMS-NAV-012", r.Source);
        Assert.Equal("2", r.Revision);
        Assert.Equal("4.1", r.Clause);
        Assert.Equal("m", r.Unit);
        Assert.Equal("route.waypoints[1].speedLimit_kn", ScenarioLoader.LastResolvedParameters.First(x => x.Key == "port.mailiao.speedLimit_kn").Path);
    }

    [Fact]
    public void UnknownKeyReportsKeyAndPath()
    {
        var yaml = ScenarioYaml.Replace("${params:navigation.cpa.coastal_nm}", "${params:navigation.cpa.nope_nm}");
        var ex = Assert.Throws<InvalidDataException>(() => ScenarioLoader.FromYaml(yaml, TestData.Parameters()));
        Assert.Contains("navigation.cpa.nope_nm", ex.Message);
        Assert.Contains("assessment.minCpa_nm", ex.Message);
    }

    [Fact]
    public void UnitSuffixOfFieldMustMatchParameter()
    {
        var yaml = ScenarioYaml.Replace("${params:navigation.cpa.coastal_nm}", "${params:navigation.xtd.channel_m}");
        var ex = Assert.Throws<InvalidDataException>(() => ScenarioLoader.FromYaml(yaml, TestData.Parameters()));
        Assert.Contains("單位", ex.Message);
    }

    [Fact]
    public void ScenarioWithoutReferencesNeedsNoParameterSet()
    {
        var sc = ScenarioLoader.FromYaml("""
            id: T
            ship: { id: FSB1 }
            initial: { position: { lat: 23.8, lon: 120.0 }, heading: 0 }
            environment: { waterDepth: 20 }
            assessment: { minCpa_nm: 0.7 }
            """).Validate();
        Assert.Null(sc.ParameterSetInfo);
        Assert.Equal(0.7, sc.Assessment.MinCpaNm);
        Assert.Empty(ScenarioLoader.LastResolvedParameters);
    }

    [Fact]
    public void ReferenceInIdIsRejected()
    {
        var yaml = ScenarioYaml.Replace("id: T", "id: ${params:assessment.passMark}");
        var ex = Assert.Throws<InvalidDataException>(() => ScenarioLoader.FromYaml(yaml, TestData.Parameters()));
        Assert.Contains("不允許", ex.Message);
    }

    [Fact]
    public void MissingParameterSetGivesHint()
    {
        var root = (JsonObject)YamlJson.ToJsonNode(ScenarioYaml)!;
        var ex = Assert.Throws<InvalidDataException>(() => ParameterResolver.Resolve(root, null));
        Assert.Contains("--parameters", ex.Message);
    }

    [Fact]
    public void RescoreSubstitutesNewValuesAtRecordedPaths()
    {
        var sc = ScenarioLoader.FromYaml(ScenarioYaml, TestData.Parameters()).Validate();
        var newSet = ParameterSetLoader.FromYaml(File.ReadAllText(TestData.ParameterSetPath).Replace("value: 50,   unit: m", "value: 40,   unit: m").Replace("\"2026.10.1\"", "\"2026.11.1\""));
        var node = JsonNode.Parse(sc.ToJson())!;
        var resolved = ParameterResolver.Rescore(node, sc.ParameterSetInfo!.Resolved, newSet);
        var back = Scenario.Scenario.FromJson(node.ToJsonString());
        Assert.Equal(40.0, back.Route!.XtdLimitM);
        Assert.Equal(40.0, back.Assessment.Bands[0].Warn);
        Assert.Equal(5, resolved.Count);
    }
}
