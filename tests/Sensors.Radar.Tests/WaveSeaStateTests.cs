using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.Sensors.Radar.Video;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 海雜波與核心浪場連動(docs/design/radar-arpa.md 第 2.1 節;physics-waves.md「練習中變更」):狀態有 state.waves 且情境/教官未明示海況時,
/// 海況由 Hs 依 Douglas 海況表推估(0.1/0.5/1.25/2.5/4/6/9 m 分界);情境 environment.radar.seaState 或教官 environment 指令明示時維持;
/// 無 waves 的狀態仍依真風速推估(舊行為不變)。
/// </summary>
public class WaveSeaStateTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.05, 1)]
    [InlineData(0.1, 2)]
    [InlineData(0.49, 2)]
    [InlineData(0.5, 3)]
    [InlineData(1.25, 4)]
    [InlineData(2.0, 4)]
    [InlineData(2.5, 5)]
    [InlineData(4.0, 6)]
    [InlineData(6.0, 7)]
    [InlineData(8.9, 7)]
    [InlineData(9.0, 8)]
    [InlineData(14.0, 8)]
    [InlineData(-1.0, 0)]
    public void DouglasSeaStateFromSignificantWaveHeight(double hs, double expected)
        => Assert.Equal(expected, VideoGrid.SeaStateFromWaveHeight(hs));

    private static RadarUnit X()
    {
        var r = RadarFactory.X();
        r.Apply("{\"cmd\":\"range\",\"value\":1.5}");
        r.Apply("{\"cmd\":\"sea\",\"value\":0}");
        return r;
    }

    private static int CountAbove(byte[][] img, int thr)
    {
        var n = 0;
        foreach (var sp in img)
            foreach (var v in sp)
                if (v >= thr) n++;
        return n;
    }

    [Fact]
    public void SceneCarriesWaveHeightFromStateWaves()
    {
        var with = new LinearScene { WindKn = 5, WaveHeightM = 3.0 }.StateAt(0);
        Assert.Equal(3.0, RadarScene.FromState(with, RadarBand.X).WaveHeightM);
        var without = new LinearScene { WindKn = 5 }.StateAt(0);
        Assert.Null(without.Waves);
        Assert.Null(RadarScene.FromState(without, RadarBand.X).WaveHeightM);
    }

    [Fact]
    public void WavesDriveSeaClutterWhenSeaStateNotExplicit()
    {
        // 微風(依風速為海況 1)但核心浪場 Hs 3 m → 海況 5:海雜波遠多於只有風的狀態
        var windOnly = new LinearScene { WindKn = 5 };
        var swell = new LinearScene { WindKn = 5, WaveHeightM = 3.0 };
        var calmUnit = X();
        var cWind = CountAbove(EchoTests.OneScan(calmUnit, windOnly).Image, 40);
        Assert.Equal(VideoGrid.SeaStateFromWind(5), calmUnit.SeaState);
        var swellUnit = X();
        var cSwell = CountAbove(EchoTests.OneScan(swellUnit, swell).Image, 40);
        Assert.Equal(5.0, swellUnit.SeaState);
        Assert.True(cSwell > cWind * 3 + 100, $"浪 Hs 3 m:{cSwell} vs 依風速 {cWind}");
        // 練習中浪關閉(state.waves 省略)→ 回到依風速推估
        EchoTests.OneScan(swellUnit, windOnly);
        Assert.Equal(VideoGrid.SeaStateFromWind(5), swellUnit.SeaState);
        // 練習中 Hs 變更(setEnvironment waves)→ 下一圈海況隨之改變
        EchoTests.OneScan(swellUnit, new LinearScene { WindKn = 5, WaveHeightM = 0.3 });
        Assert.Equal(2.0, swellUnit.SeaState);
    }

    [Fact]
    public void ExplicitScenarioOrInstructorSeaStateStillWins()
    {
        var explicitScenario = new LinearScene { WindKn = 5, WaveHeightM = 3.0, RadarEnvironment = new RadarEnvironmentState { RainAreas = Array.Empty<RadarRainAreaState>(), SeaState = 2 } };
        var unit = X();
        EchoTests.OneScan(unit, explicitScenario);
        Assert.Equal(2.0, unit.SeaState);
        var instr = X();
        Assert.True(instr.Apply("{\"cmd\":\"environment\",\"seaState\":1}").Ok);
        EchoTests.OneScan(instr, new LinearScene { WindKn = 5, WaveHeightM = 3.0 });
        Assert.Equal(1.0, instr.SeaState);
        // 情境海況只有雨區沒有 seaState(null)→ 仍由浪推估
        var rainOnly = new LinearScene { WindKn = 5, WaveHeightM = 3.0, RadarEnvironment = new RadarEnvironmentState { RainAreas = Array.Empty<RadarRainAreaState>(), SeaState = null } };
        var unit2 = X();
        EchoTests.OneScan(unit2, rainOnly);
        Assert.Equal(5.0, unit2.SeaState);
    }
}
