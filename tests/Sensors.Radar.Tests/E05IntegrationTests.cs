using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 與 SimCore 引擎直接整合(不經網路):E05 高雄港外交通(MMG、FSB1 滿載),X 頻雷達保護區 0.3–8 nm 自動擷取,
/// 跑 250 s 模擬時間;T2/T3(等速直線)穩定追蹤後 CPA 對核心真值誤差 &lt; 0.02 nm,T1(依 COLREG 轉向)仍維持追蹤並標示操船。
/// </summary>
public class E05IntegrationTests
{
    private readonly ITestOutputHelper _out;
    public E05IntegrationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void GuardZoneTracksE05TrafficAgainstCoreTruth()
    {
        var root = RepositoryPaths.FindRoot();
        var sc = ScenarioLoader.Load(Path.Combine(root, "data", "scenarios", "E05_pilot_traffic.yaml"));
        var ship = ShipParticulars.LoadFromDataRoot(root, "FSB1");
        var engine = new SimulationEngine(ship, sc, (s, l) => MmgDynamics.Load(root, s, l));
        var cfg = RadarConfig.XBand().ApplyParticulars(ship, LoadingCondition.Full);
        var radar = new RadarUnit(cfg, (ulong)sc.Seed, RadarEnvironment.Load(Path.Combine(root, "data", "areas")));
        radar.DisplayEnabled = false;
        Assert.True(radar.Apply("{\"cmd\":\"guardZone\",\"enabled\":true,\"inner_nm\":0.3,\"outer_nm\":8}").Ok);
        var t1Manoeuvre = false;
        RadarScene? scene = null;
        while (engine.Tick < 250 * 50)
        {
            engine.Step();
            if (engine.Tick % 2 != 0) continue; // 25 Hz 狀態
            scene = RadarScene.FromState(engine.BuildState(), RadarBand.X);
            radar.Process(scene);
            foreach (var m in ArpaTruthComparison.Compare(radar, scene))
                if (m.TruthId == "T1" && radar.Arpa.Tracks.Single(t => t.Id == m.TrackId).Manoeuvring) t1Manoeuvre = true;
        }
        var matches = ArpaTruthComparison.Compare(radar, scene!);
        foreach (var m in matches)
            _out.WriteLine($"{m.TrackId:00} ↔ {m.TruthId}:{m.State} {m.AgeS:F0}s CPA {m.ArpaCpaNm:F4}/{m.TruthCpaNm:F4}(誤差 {m.CpaErrorNm:F4})TCPA {m.ArpaTcpaMin:F2}/{m.TruthTcpaMin:F2} 航向誤差 {m.CourseErrorDeg:F2}°");
        Assert.Equal(new[] { "T1", "T2", "T3" }, matches.Select(m => m.TruthId).OrderBy(x => x).ToArray());
        Assert.All(matches, m => Assert.Equal("tracking", m.State));
        foreach (var id in new[] { "T2", "T3" })
        {
            var m = matches.Single(x => x.TruthId == id);
            Assert.True(m.Stable, $"{id} 應已穩定追蹤 3 分鐘");
            Assert.True(m.CpaErrorNm < 0.02, $"{id} CPA 誤差 {m.CpaErrorNm:F4} nm");
        }
        Assert.True(t1Manoeuvre, "T1 依 COLREG 轉向時應標示操船");
    }
}
