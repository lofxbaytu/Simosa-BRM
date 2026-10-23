using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 靠離泊預演自動組題的固定範例(規劃書第 8.0 節、第 9.1 節;網頁 lib/berthing-rehearsal.ts 產生,
/// 夾具 data/scenarios/templates/examples/*.yaml 由 vitest 同步):載入與靜態檢查無錯誤(等同 --validate)、
/// 以 MMG 實跑 300 s 並紀錄 → 重播雜湊/事件/summary 一致;內嵌載入(loadScenario args.scenario)與檔案載入雜湊相同。
/// </summary>
public class BerthingRehearsalFixtureTests
{
    private static readonly Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg = (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static string ExamplePath(string name) => Path.Combine(TestData.Root, "data", "scenarios", "templates", "examples", name + ".yaml");

    public static IEnumerable<object[]> Examples() => new[]
    {
        new object[] { "berthing-rehearsal-berthing", "BR_berthing_MLO-OIL-3_20261113-0100" },
        new object[] { "berthing-rehearsal-unberthing", "BR_unberthing_MLO-SOB-1_20261114-0600" },
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_LoadsAndValidatesWithoutErrors(string name, string expectedId)
    {
        var path = ExamplePath(name);
        Assert.True(File.Exists(path), $"缺少夾具 {path}(src/Instruments.Web:SIMOSA_UPDATE_EXAMPLES=1 pnpm test 產生)");
        var sc = ScenarioLoader.Load(path);
        Assert.Equal(expectedId, sc.Id);
        Assert.NotNull(sc.Berth?.Definition);
        Assert.Equal(4, sc.Berth!.Lines.Count);
        Assert.NotNull(sc.ParameterSetInfo);
        Assert.Contains(sc.ParameterSetInfo!.Resolved, r => r.Key == "berthing.approachSpeed_mps");
        var ship = TestData.Ship(sc.Ship.Id);
        var issues = ScenarioValidator.Check(sc, ship, Path.Combine(TestData.Root, "data", "scenarios", sc.Id));
        Assert.DoesNotContain(issues, i => i.Severity == ScenarioIssueSeverity.Error);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_Runs300sRecordsAndReplays(string name, string expectedId)
    {
        var path = ExamplePath(name);
        var sc = ScenarioLoader.Load(path);
        var ship = TestData.Ship(sc.Ship.Id);
        var engine = new SimulationEngine(ship, sc, Mmg);
        var events = TestData.Capture(engine);
        var recordPath = Path.Combine(TestData.TestRecordsDirectory, $"{expectedId}-{Guid.NewGuid():N}.jsonl");
        var writer = new RecordWriter(recordPath, engine);
        var berthing = sc.Id.StartsWith("BR_berthing", StringComparison.Ordinal);
        for (var t = 1; t <= 300 && !engine.Ended; t++)
        {
            if (t == 30) engine.Enqueue(SimCommand.Acknowledge("checklist", "mpx", "pilot"));
            if (berthing)
            {
                if (t == 70) engine.Enqueue(SimCommand.TugControl("TG1", "push", 270, 30));
                if (t == 75) engine.Enqueue(SimCommand.Acknowledge("verbal", "tugOrderConfirm", "tug"));
                if (t == 90) engine.Enqueue(SimCommand.Rudder(5));
            }
            else
            {
                if (t == 40) engine.Enqueue(SimCommand.Mooring("HEAD", "letGo"));
                if (t == 50) engine.Enqueue(SimCommand.Mooring("STERN", "letGo"));
                if (t == 60) engine.Enqueue(SimCommand.Thruster(0.8));
            }
            var target = (long)Math.Round(t / engine.Dt);
            while (engine.Tick < target && !engine.Ended) engine.Step();
        }
        writer.Close();
        Assert.Contains(events, e => e.Kind == "timelineTriggered" && e.ItemId == "pilotMpx");
        Assert.Contains(events, e => e.Kind == "timelineTriggered" && e.ItemId == "tideWindow");
        Assert.Contains(events, e => e.Kind == "requiredEventMet" && e.ItemId == "mpx");
        if (berthing) Assert.Contains(events, e => e.Kind == "tugCommand");
        else Assert.Contains(events, e => e.Kind == "mooringCommand");
        Assert.NotNull(engine.LatestSummary?.Berthing);

        var r = Replayer.Replay(recordPath, TestData.Ship, Mmg);
        Assert.True(r.IntegrityOk);
        Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
        Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
        Assert.True(r.SummaryMatches);
        Assert.Null(r.Warning);

        // 內嵌載入(網頁以 loadScenario args.scenario 送出同一物件)與檔案載入得到相同雜湊
        var node = YamlJson.ToJsonNode(File.ReadAllText(path))!;
        using var doc = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
        var inline = ScenarioLoader.FromJsonElement(doc.RootElement, defaultId: "ignored");
        Assert.Equal(sc.ToJson(), inline.ToJson());
    }
}
