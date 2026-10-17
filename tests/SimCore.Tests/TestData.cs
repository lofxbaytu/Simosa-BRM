using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Tests;

/// <summary>測試共用:專案根目錄、FSB1 船舶資料、E01 基線情境。</summary>
internal static class TestData
{
    public static string Root { get; } = RepositoryPaths.FindRoot();

    public static ShipParticulars Fsb1() => ShipParticulars.LoadFromDataRoot(Root, "FSB1");
    public static ShipParticulars Fsb2() => ShipParticulars.LoadFromDataRoot(Root, "FSB2");
    public static ShipParticulars Ship(string id) => ShipParticulars.LoadFromDataRoot(Root, id);

    public static string BaselineScenarioPath => Path.Combine(Root, "data", "scenarios", "E01_baseline.yaml");
    public static Scenario.Scenario Baseline() => ScenarioLoader.Load(BaselineScenarioPath);
    public static string E07Path => Path.Combine(Root, "data", "scenarios", "E07_steering_failure.yaml");
    public static Scenario.Scenario E07() => ScenarioLoader.Load(E07Path);
    public static string ParameterSetPath => Path.Combine(Root, "data", "parameters", "company-procedures.yaml");
    public static SimosaBRM.SimCore.Parameters.ParameterSet Parameters() => SimosaBRM.SimCore.Parameters.ParameterSetLoader.Load(ParameterSetPath);

    /// <summary>內嵌最小情境(無時間表/航線/區域/規則:雜湊配置與舊版相同);<paramref name="extra"/> 為附加的 YAML 區段(timeline、route、zones、assessment…)。</summary>
    public static Scenario.Scenario Minimal(string extra = "", int seed = 7, double speedKn = 8.0, double headingDeg = 0.0, double waterDepth = 30.0, string loading = "ballast")
        => ScenarioLoader.FromYaml($$"""
            id: T_min
            seed: {{seed}}
            ship: { id: FSB1, loading: {{loading}} }
            origin: { lat: 23.80, lon: 120.05 }
            initial: { position: { x: 0, y: 0 }, heading: {{headingDeg}}, speed: {{speedKn}}, telegraph: HAH }
            environment: { wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }, current: { set: 0, drift: 0 }, waterDepth: {{waterDepth}} }
            {{extra}}
            """).Validate();

    public static SimulationEngine MinimalEngine(string extra = "", int seed = 7, double speedKn = 8.0, double headingDeg = 0.0, double waterDepth = 30.0, string loading = "ballast", EngineOptions? options = null)
        => new(Fsb1(), Minimal(extra, seed, speedKn, headingDeg, waterDepth, loading), options: options);

    /// <summary>收集引擎事件。</summary>
    public static List<SimEvent> Capture(SimulationEngine engine)
    {
        var list = new List<SimEvent>();
        engine.EventRaised += (_, e) => list.Add(e);
        return list;
    }

    /// <summary>推進到指定模擬秒數。</summary>
    public static void RunTo(SimulationEngine engine, double seconds)
    {
        var target = (long)Math.Round(seconds / engine.Dt);
        while (engine.Tick < target) engine.Step();
    }

    /// <summary>以 E01 為基礎的引擎;可覆寫種子、速度、車鐘。</summary>
    public static SimulationEngine Engine(int? seed = null, double? speedKn = null, string? telegraph = null, EngineOptions? options = null)
    {
        var sc = Baseline();
        if (seed is { } s) sc.Seed = s;
        if (speedKn is { } v) sc.Initial.Speed = v;
        if (telegraph is { } t) sc.Initial.Telegraph = t;
        return new SimulationEngine(Fsb1(), sc, options: options);
    }

    public static string TestRecordsDirectory
    {
        get
        {
            var dir = Path.Combine(Root, "build", "test-records");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
