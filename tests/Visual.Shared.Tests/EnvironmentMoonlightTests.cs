using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.Visual.Shared.Env;
using SimosaBRM.Visual.Shared.Quality;
using SimosaBRM.Visual.Shared.Smoke;
using SimosaBRM.Visual.Shared.View;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

/// <summary>夜間月光與環境光(docs/design/visual-client.md 第 4 節「月光」、第 6 節品質分級):EnvironmentMapper 的 Moon 與 NightSkyBrightness。</summary>
public class EnvironmentMoonlightTests
{
    private static OwnShipState At(string utc) => SmokeSequence.Frame(0) with { Utc = utc };

    [Fact]
    public void FullMoonNightIsBrighterThanNewMoonNight()
    {
        // 2026-01-03 16:00Z(當地午夜,滿月近天頂)vs 2026-01-18 16:00Z(新月)
        var full = EnvironmentMapper.Build(At("2026-01-03T16:00:00.000Z"), null, DayNightMode.Auto, null);
        var newMoon = EnvironmentMapper.Build(At("2026-01-18T16:00:00.000Z"), null, DayNightMode.Auto, null);
        Assert.True(full.SunElevationDeg < -6 && newMoon.SunElevationDeg < -6);
        Assert.True(full.Moon.AboveHorizon);
        Assert.True(full.Moon.Phase > 0.98);
        Assert.InRange(full.Moon.Intensity, 0.8, 1.0);
        Assert.Equal(full.Moon.Intensity, full.Moon.Light, 6); // 10 nm 晴空無衰減
        Assert.True(newMoon.Moon.Light < 0.01, $"新月月光 {newMoon.Moon.Light:F3}");
        Assert.True(full.NightSkyBrightness > 0.8, $"滿月夜空亮度 {full.NightSkyBrightness:F2}");
        Assert.InRange(newMoon.NightSkyBrightness, EnvironmentMapper.StarlightSkyBrightness, EnvironmentMapper.StarlightSkyBrightness + 0.01);
    }

    [Fact]
    public void MoonBelowHorizonGivesNoMoonlight()
    {
        // 2026-01-03 04:00Z 當地正午:太陽在上、月球在地平線下
        var noon = EnvironmentMapper.Build(At("2026-01-03T04:00:00.000Z"), null, DayNightMode.Auto, null);
        Assert.False(noon.Moon.AboveHorizon);
        Assert.Equal(0.0, noon.Moon.Intensity);
        Assert.Equal(0.0, noon.Moon.Light);
        Assert.Equal(0.0, noon.NightSkyBrightness); // 白天不計夜空亮度
    }

    [Fact]
    public void FogAndRainAttenuateMoonlight()
    {
        var clear = At("2026-01-03T16:00:00.000Z") with { VisibilityNm = 10 };
        var fog = clear with { VisibilityNm = 1.0 };
        var lookClear = EnvironmentMapper.Build(clear, null, DayNightMode.Auto, null);
        var lookFog = EnvironmentMapper.Build(fog, null, DayNightMode.Auto, null);
        Assert.Equal(lookClear.Moon.Intensity, lookFog.Moon.Intensity, 6); // 晴空強度不變
        Assert.True(lookFog.Moon.Light < 0.5 * lookClear.Moon.Light, $"霧中月光 {lookFog.Moon.Light:F3} vs {lookClear.Moon.Light:F3}");
        Assert.True(lookFog.NightSkyBrightness < lookClear.NightSkyBrightness);
        // 雨區:自船位置落在圓形雨區內
        var radar = new RadarEnvironmentState
        {
            RainAreas = new[] { new RadarRainAreaState { Id = "r", Intensity = 1.0, RateMmH = 20, Center = new GeoPosition { Lat = 0, Lon = 0, X = clear.Pos.X, Y = clear.Pos.Y }, RadiusM = 500 } },
        };
        var lookRain = EnvironmentMapper.Build(clear, radar, DayNightMode.Auto, null);
        Assert.Equal(1.0, lookRain.Rain);
        Assert.InRange(lookRain.Moon.Light / lookClear.Moon.Light, 0.35, 0.45);
        Assert.Equal(1.0, EnvironmentMapper.MoonlightWeatherFactor(10, 0));
        Assert.InRange(EnvironmentMapper.MoonlightWeatherFactor(0.2, 0), 0.099, 0.101);
        Assert.True(EnvironmentMapper.MoonlightWeatherFactor(3, 0.5) < EnvironmentMapper.MoonlightWeatherFactor(3, 0));
    }

    [Fact]
    public void FixedNightModeUsesPhaseFromUtcOrDefault()
    {
        var s = SmokeSequence.Frame(0);
        // 有 state.utc(滿月日)→ 月相由 UTC 推得;位置固定 45°/135°
        var full = EnvironmentMapper.Build(s with { Utc = "2026-01-03T10:00:00.000Z" }, null, DayNightMode.Night, null);
        Assert.Equal(EnvironmentMapper.FixedNightMoonElevationDeg, full.Moon.ElevationDeg);
        Assert.Equal(EnvironmentMapper.FixedNightMoonAzimuthDeg, full.Moon.AzimuthDeg);
        Assert.True(full.Moon.Phase > 0.98);
        Assert.True(full.Moon.Light > 0.5);
        // 無 state.utc 但有 --start-utc(新月日)+ t
        var newMoon = EnvironmentMapper.Build(s with { Utc = null, T = 600 }, null, DayNightMode.Night, new DateTimeOffset(2026, 1, 18, 19, 0, 0, TimeSpan.Zero));
        Assert.True(newMoon.Moon.Phase < 0.02);
        Assert.True(newMoon.Moon.Light < 0.01);
        // 皆無 → 0.5
        var none = EnvironmentMapper.Build(s with { Utc = null }, null, DayNightMode.Night, null);
        Assert.Equal(EnvironmentMapper.DefaultMoonPhase, none.Moon.Phase);
        Assert.True(none.Moon.Light > 0.0);
        Assert.Equal(0.0, none.Daylight);
        Assert.True(none.NightSkyBrightness > EnvironmentMapper.StarlightSkyBrightness);
    }

    [Fact]
    public void FixedDayAndDuskModesHaveNoMoonlight()
    {
        var s = SmokeSequence.Frame(0) with { Utc = "2026-01-03T16:00:00.000Z" };
        var day = EnvironmentMapper.Build(s, null, DayNightMode.Day, null);
        Assert.False(day.Moon.AboveHorizon);
        Assert.Equal(0.0, day.Moon.Light);
        Assert.Equal(0.0, day.NightSkyBrightness);
        var dusk = EnvironmentMapper.Build(s, null, DayNightMode.Dusk, null);
        Assert.Equal(0.0, dusk.Moon.Light);
        Assert.True(dusk.NightSkyBrightness > 0 && dusk.NightSkyBrightness < EnvironmentMapper.StarlightSkyBrightness); // 曙暮光:(1 − Daylight) × 星光底值
        // auto 無任何 UTC → 白天,無月光
        var none = EnvironmentMapper.Build(s with { Utc = null }, null, DayNightMode.Auto, null);
        Assert.Equal(0.0, none.Moon.Light);
    }

    [Fact]
    public void NightSkyBrightnessBlendsStarlightAndMoonlight()
    {
        Assert.Equal(0.0, EnvironmentMapper.NightSkyBrightnessFor(1.0, 1.0));
        Assert.Equal(EnvironmentMapper.StarlightSkyBrightness, EnvironmentMapper.NightSkyBrightnessFor(0.0, 0.0), 9);
        Assert.Equal(1.0, EnvironmentMapper.NightSkyBrightnessFor(0.0, 1.0), 9);
        Assert.InRange(EnvironmentMapper.NightSkyBrightnessFor(0.5, 1.0), 0.49, 0.51);
    }

    [Fact]
    public void LowQualityDisablesMoonShadows()
    {
        Assert.False(QualitySettings.For(QualityTier.Low).MoonShadows);
        Assert.True(QualitySettings.For(QualityTier.Mid).MoonShadows);
        Assert.True(QualitySettings.For(QualityTier.High).MoonShadows);
    }
}
