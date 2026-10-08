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
