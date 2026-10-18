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

    /// <summary>
    /// 測試紀錄檔目錄:每次測試執行一個子目錄 build/test-records/run-&lt;pid&gt;-&lt;時間&gt;,行程結束時刪除;
    /// 啟動時清掉先前執行(含當機)留下的紀錄。長情境的 25 Hz 狀態紀錄一份可達數百 MB,不清會在開發機
    /// (D:\Simosa BRM)累積數 GB(CLAUDE.md:輸出留在專案內,但不得無限制成長)。
    /// 需要保留以便除錯時設環境變數 SIMOSA_KEEP_TEST_RECORDS=1。
    /// </summary>
    public static string TestRecordsDirectory => RunRecordsDirectory.Value;

    private static readonly Lazy<string> RunRecordsDirectory = new(() =>
    {
        var root = Path.Combine(Root, "build", "test-records");
        Directory.CreateDirectory(root);
        var keep = System.Environment.GetEnvironmentVariable("SIMOSA_KEEP_TEST_RECORDS") == "1";
        if (!keep)
        {
            foreach (var f in Directory.GetFiles(root)) TryDelete(() => File.Delete(f));
            // testhost 結束時不一定觸發 ProcessExit,所以啟動時刪掉建立者行程已不存在的 run-* 目錄
            foreach (var d in Directory.GetDirectories(root, "run-*"))
                if (!OwnerAlive(Path.GetFileName(d))) TryDelete(() => Directory.Delete(d, recursive: true));
        }
        var dir = Path.Combine(root, $"run-{System.Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(dir);
        if (!keep) AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(() => Directory.Delete(dir, recursive: true));
        return dir;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>run-&lt;pid&gt;-&lt;時間&gt; 目錄的建立者行程是否仍在執行(其他測試行程正在用就不刪)。</summary>
    private static bool OwnerAlive(string name)
    {
        var parts = name.Split('-');
        if (parts.Length < 2 || !int.TryParse(parts[1], out var pid)) return false;
        if (pid == System.Environment.ProcessId) return true;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
