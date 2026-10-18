using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.Sensors.Radar.Arpa;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// S 頻單獨故障(radarFailureS / radarDegradedS,fault-catalogue.json;只記錄於核心、雷達服務反應)與情境雨區/海況
/// (scenario environment.radar → state.radarEnvironment → RadarEnvironment.ApplyScenario;docs/design/radar-arpa.md 第 2.1、7 節)。
/// </summary>
public class ScenarioWeatherAndSBandFaultTests
{
    private static int CountAbove(byte[][] img, int thr, int k0 = 0, int k1 = int.MaxValue)
    {
        var n = 0;
        for (var k = Math.Max(0, k0); k < Math.Min(img.Length, k1); k++)
            foreach (var v in img[k])
                if (v >= thr) n++;
        return n;
    }

    private static byte[][] Scan(RadarUnit r, LinearScene s) => EchoTests.OneScan(r, s).Image;

    private static GeoPosition P(double x, double y) => new() { Lat = 0, Lon = 0, X = x, Y = y };

    /// <summary>正北 3 nm、邊長 3000 m 的方形雨區(或同中心半徑 1500 m 的圓)。</summary>
    private static RadarEnvironmentState Rain(bool polygon, double intensity = 1.0, double? seaState = null)
    {
        const double cy = 3.0 * 1852;
        var area = polygon
            ? new RadarRainAreaState { Id = "R1", Intensity = intensity, RateMmH = 16 * intensity, Polygon = new[] { P(-1500, cy - 1500), P(1500, cy - 1500), P(1500, cy + 1500), P(-1500, cy + 1500) } }
            : new RadarRainAreaState { Id = "R1", Intensity = intensity, RateMmH = 16 * intensity, Center = P(0, cy), RadiusM = 1500 };
        return new RadarEnvironmentState { RainAreas = new[] { area }, SeaState = seaState };
    }

    private static RadarUnit Make(bool s, double rain = 0)
    {
        var cfg = s ? RadarConfig.SBand(18) : RadarConfig.XBand(20);
        cfg.ShadowSectors.Clear();
        var r = new RadarUnit(cfg, 42);
        r.Apply("{\"cmd\":\"range\",\"value\":6}");
        r.Apply($"{{\"cmd\":\"rain\",\"value\":{rain}}}");
        return r;
    }

    [Fact]
    public void SBandFailureStopsOnlySBand()
    {
        var scene = new LinearScene();
        scene.Faults.Add("radarFailureS");
        var x = RadarFactory.X();
        var s = RadarFactory.S();
        var xr = scene.Run(x, 0, 3);
        var sr = scene.Run(s, 0, 3);
        Assert.True(s.Failed);
        Assert.False(s.Transmitting);
        Assert.True(s.Status().Failed);
        Assert.All(sr, r => Assert.Empty(r.Frames));
        Assert.Contains(sr.SelectMany(r => r.Events), e => e.Kind == "radarFailure");
        Assert.False(x.Failed);
        Assert.True(x.Transmitting);
        Assert.Contains(xr, r => r.Frames.Count > 0);
        scene.Faults.Clear();
        var back = scene.Run(s, 3.04, 6);
        Assert.Contains(back.SelectMany(r => r.Events), e => e.Kind == "radarRestored");
        Assert.True(s.Transmitting);
    }

    [Fact]
    public void SBandDegradedReducesOnlySBandEchoes()
    {
        double Sum(RadarUnit r, LinearScene sc) => Scan(r, sc).Sum(sp => sp.Sum(v => (double)v));
        LinearScene Scene(bool fault)
        {
            var sc = new LinearScene();
            sc.Targets.Add(new LinearTarget("T", 4000, 4000, 0, 0, 25, 6, "fishing"));
            if (fault) sc.Faults.Add("radarDegradedS");
            return sc;
        }
        var sNominal = Sum(RadarFactory.S(), Scene(false));
        var sDegradedUnit = RadarFactory.S();
        var sDegraded = Sum(sDegradedUnit, Scene(true));
        var xNominal = Sum(RadarFactory.X(), Scene(false));
        var xUnit = RadarFactory.X();
        var xWithSFault = Sum(xUnit, Scene(true));
        Assert.True(sDegraded < sNominal * 0.8, $"S 頻劣化 {sDegraded} vs {sNominal}");
        Assert.True(sDegradedUnit.Degraded && sDegradedUnit.Status().Degraded);
        Assert.False(xUnit.Degraded);
        Assert.Equal(xNominal, xWithSFault);   // X 頻不受 radarDegradedS 影響(同種子逐值相同)
    }

    [Fact]
    public void ScenarioRainPolygonAndCircleProduceRainClutterReducedByFtcAndWeakerOnS()
    {
        var circle = new LinearScene { RadarEnvironment = Rain(polygon: false) };
        var square = new LinearScene { RadarEnvironment = Rain(polygon: true) };
        var dry = new LinearScene();
        int xc = CountAbove(Scan(Make(false), circle), 200), xp = CountAbove(Scan(Make(false), square), 200), x0 = CountAbove(Scan(Make(false), dry), 200);
        int xpFtc = CountAbove(Scan(Make(false, 80), square), 200), sp = CountAbove(Scan(Make(true), square), 200);
        Assert.Equal(0, x0);
        Assert.True(xc > 500, $"情境圓形雨區 X 頻 {xc}");
        Assert.True(xp > xc, $"方形雨區(面積較大)回波 {xp} 應多於內切圓 {xc}");
        Assert.True(xp < xc * 1.6, $"方形 {xp} vs 圓 {xc}(面積比 4/π)");
        Assert.True(xpFtc < xp / 3, $"FTC 應抑制雨回波:{xpFtc} vs {xp}");
        Assert.True(sp < xp / 3, $"S 頻雨回波應較弱:{sp} vs {xp}");
        // 雨回波只在雨區方位(正北 ±27°)
        var img = Scan(Make(false), square);
        Assert.Equal(0, CountAbove(img, 200, 512 - 100, 512 + 100) + CountAbove(img, 200, 1536 - 100, 1536 + 100));
        // 強度 0 = 無雨;強度越大回波越多
        Assert.Equal(0, CountAbove(Scan(Make(false), new LinearScene { RadarEnvironment = Rain(true, 0.0) }), 200));
        Assert.True(CountAbove(Scan(Make(false), new LinearScene { RadarEnvironment = Rain(true, 0.25) }), 100) < CountAbove(Scan(Make(false), square), 100));
    }

    [Fact]
    public void AntennaInsideRainPolygonGetsRainFromZeroRange()
    {
        var env = new RadarEnvironmentState
        {
            RainAreas = new[] { new RadarRainAreaState { Id = "R", Intensity = 1, RateMmH = 16, Polygon = new[] { P(-2000, -2000), P(2000, -2000), P(2000, 2000), P(-2000, 2000) } } },
        };
        var r = Make(false);
        r.Apply("{\"cmd\":\"range\",\"value\":1.5}");
        var (img, last) = EchoTests.OneScan(r, new LinearScene { RadarEnvironment = env });
        var cell = last.RangeM / last.Cells;
        // 每個方位近距(0.3–1 km)都有雨回波
        var spokesWithNearRain = img.Count(sp => sp.Skip((int)(300 / cell)).Take((int)(700 / cell)).Any(v => v >= 100));
        Assert.True(spokesWithNearRain > img.Length * 0.9, $"{spokesWithNearRain}/{img.Length}");
        Assert.Equal(1, r.Status().RainCells);
    }

    [Fact]
    public void ScenarioSeaStateOverridesWindButInstructorCommandTakesPrecedence()
    {
        var calm = new LinearScene { WindKn = 2 };
        var rough = new LinearScene { WindKn = 2, RadarEnvironment = new RadarEnvironmentState { RainAreas = Array.Empty<RadarRainAreaState>(), SeaState = 6 } };
        RadarUnit X() { var r = RadarFactory.X(); r.Apply("{\"cmd\":\"range\",\"value\":1.5}"); r.Apply("{\"cmd\":\"sea\",\"value\":0}"); return r; }
        var cCalm = CountAbove(Scan(X(), calm), 40);
        var roughUnit = X();
        var cRough = CountAbove(Scan(roughUnit, rough), 40);
        Assert.True(cRough > cCalm * 3 + 100, $"情境海況 6:{cRough} vs 依風速 {cCalm}");
        Assert.Equal(6.0, roughUnit.SeaState);
        var instr = X();
        Assert.True(instr.Apply("{\"cmd\":\"environment\",\"seaState\":1}").Ok);
        Scan(instr, rough);
        Assert.Equal(1.0, instr.SeaState);
        // 情境移除 radarEnvironment(另一情境)→ 情境雨區與海況清除
        var env = roughUnit.Environment;
        env.ApplyScenario(Rain(true));
        Assert.Single(env.ScenarioRainPolygons);
        env.ApplyScenario(null);
        Assert.Empty(env.ScenarioRainPolygons);
        Assert.Null(env.ScenarioSeaState);
    }

    /// <summary>
    /// 核心只在關鍵幀(載入/重設/倒帶後第一幀與每秒一次)帶出 state.radarEnvironment:其他幀省略 = 不變(雨區與海況保留、版本不變);
    /// 模擬時間倒退或情境切換時先清除,直到下一個關鍵幀重新套用。
    /// </summary>
    [Fact]
    public void ScenarioEnvironmentPersistsBetweenKeyframesAndClearsOnReset()
    {
        var scene = new LinearScene { RadarEnvironment = Rain(true, 0.5, 3) };
        var r = Make(false);
        r.Process(scene.StateAt(0));
        Assert.Single(r.Environment.ScenarioRainPolygons);
        var version = r.Environment.Version;
        scene.RadarEnvironment = null;                                  // 非關鍵幀
        for (var i = 1; i < 25; i++) r.Process(scene.StateAt(i * 0.04));
        Assert.Single(r.Environment.ScenarioRainPolygons);
        Assert.Equal(3.0, r.Environment.ScenarioSeaState);
        Assert.Equal(version, r.Environment.Version);
        // 時間倒退(重設/倒帶)且該幀無 radarEnvironment → 清除
        r.Process(scene.StateAt(0.2));
        Assert.Empty(r.Environment.ScenarioRainPolygons);
        Assert.Null(r.Environment.ScenarioSeaState);
        // 下一個關鍵幀重新套用
        scene.RadarEnvironment = Rain(false);
        r.Process(scene.StateAt(0.24));
        Assert.Single(r.Environment.ScenarioRainCells);
        // 情境切換(另一情境無 environment.radar)→ 清除
        r.Process(new LinearScene { ScenarioId = "T_other" }.StateAt(0.28));
        Assert.Empty(r.Environment.ScenarioRainCells);
    }

    [Fact]
    public void SameScenarioContentDoesNotBumpVersion()
    {
        var env = RadarEnvironment.Empty();
        env.ApplyScenario(Rain(true));
        var v = env.Version;
        env.ApplyScenario(Rain(true));   // 每幀反序列化的新物件、內容相同
        Assert.Equal(v, env.Version);
        env.ApplyScenario(Rain(true, 0.5));
        Assert.Equal(v + 1, env.Version);
        Assert.Equal(8.0, env.ScenarioRainPolygons.Single().RateMmH);
    }

    [Fact]
    public void StateJsonCarriesRadarEnvironmentToRadarScene()
    {
        var state = new LinearScene { RadarEnvironment = Rain(false, 0.5, 3) }.StateAt(0);
        var json = ContractJson.Serialize(state);
        Assert.Contains("\"radarEnvironment\":{\"rainAreas\":[{\"id\":\"R1\",\"intensity\":0.5,\"rate_mmh\":8,", json);
        var back = ContractJson.Deserialize<OwnShipState>(json)!;
        var scene = RadarScene.FromState(back, RadarBand.X);
        Assert.Equal(1500, scene.RadarEnvironment!.RainAreas[0].RadiusM);
        Assert.Equal(3, scene.RadarEnvironment.SeaState);
        Assert.DoesNotContain("radarEnvironment", ContractJson.Serialize(new LinearScene().StateAt(0)));
    }
}
