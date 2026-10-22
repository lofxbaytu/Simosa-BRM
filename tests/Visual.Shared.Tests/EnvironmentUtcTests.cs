using SimosaBRM.Visual.Shared.Env;
using SimosaBRM.Visual.Shared.Smoke;
using SimosaBRM.Visual.Shared.View;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

/// <summary>日夜自動模式依 state.utc(docs/design/visual-client.md 第 4 節);無 utc 時退回 --start-utc + t;兩者皆無為白天。</summary>
public class EnvironmentUtcTests
{
    [Fact]
    public void StateUtcDrivesDayNightInAutoMode()
    {
        // 高雄 22.53N 120.24E:UTC 04:00 = 當地 12:00(白天);UTC 16:00 = 當地 00:00(夜)
        var s = SmokeSequence.Frame(0) with { Utc = "2026-05-05T04:00:00.000Z" };
        var day = EnvironmentMapper.Build(s, null, DayNightMode.Auto, null);
        Assert.True(day.SunElevationDeg > 60, $"中午高度角 {day.SunElevationDeg:F1}");
        Assert.False(day.NavigationLightsOn);
        var night = EnvironmentMapper.Build(s with { Utc = "2026-05-05T16:00:00.000Z" }, null, DayNightMode.Auto, null);
        Assert.True(night.SunElevationDeg < -30);
        Assert.True(night.NavigationLightsOn);
        Assert.Equal(0.0, night.Daylight);
    }

    [Fact]
    public void StateUtcTakesPrecedenceOverStartUtcAndSimTime()
    {
        var s = SmokeSequence.Frame(0) with { Utc = "2026-05-05T16:00:00.000Z", T = 3600 * 8 };
        var startUtcNoon = new DateTimeOffset(2026, 5, 5, 4, 0, 0, TimeSpan.Zero);
        var look = EnvironmentMapper.Build(s, null, DayNightMode.Auto, startUtcNoon);
        Assert.True(look.NavigationLightsOn); // 以 state.utc(夜)為準,不是 --start-utc + t
    }

    [Fact]
    public void FallsBackToStartUtcPlusSimTimeWithoutStateUtc()
    {
        var s = SmokeSequence.Frame(0) with { Utc = null, T = 3600 * 8 };
        var start = new DateTimeOffset(2026, 5, 5, 4, 0, 0, TimeSpan.Zero); // + 8 h = 12:00 UTC = 當地 20:00(夜)
        Assert.True(EnvironmentMapper.Build(s, null, DayNightMode.Auto, start).NavigationLightsOn);
        Assert.False(EnvironmentMapper.Build(s with { T = 0 }, null, DayNightMode.Auto, start).NavigationLightsOn);
        // 不可解析的 utc 也退回
        Assert.True(EnvironmentMapper.Build(s with { Utc = "??" }, null, DayNightMode.Auto, start).NavigationLightsOn);
        // 皆無 → 白天
        var none = EnvironmentMapper.Build(s with { Utc = null }, null, DayNightMode.Auto, null);
        Assert.InRange(none.SunElevationDeg, 44.9, 45.1);
    }

    [Fact]
    public void ManualModesIgnoreStateUtc()
    {
        var s = SmokeSequence.Frame(0) with { Utc = "2026-05-05T16:00:00.000Z" };
        Assert.False(EnvironmentMapper.Build(s, null, DayNightMode.Day, null).NavigationLightsOn);
        Assert.True(EnvironmentMapper.Build(s, null, DayNightMode.Night, null).NavigationLightsOn);
    }

    [Fact]
    public void ResolveUtcParsesIsoVariants()
    {
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero), EnvironmentMapper.ResolveUtc("2026-01-01T10:00:00+08:00", null, 0));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 2, 0, 5, 40, TimeSpan.Zero), EnvironmentMapper.ResolveUtc("2026-01-01T02:00:05.040Z", null, 0));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero), EnvironmentMapper.ResolveUtc("2026-01-01T02:00:00", null, 0)); // 無時區視為 UTC
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 2, 0, 10, TimeSpan.Zero), EnvironmentMapper.ResolveUtc(null, new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero), 10));
        Assert.Null(EnvironmentMapper.ResolveUtc(null, null, 10));
    }

    [Fact]
    public void SmokeFramesCarryUtc()
    {
        Assert.Equal("2026-05-05T22:30:00.000Z", SmokeSequence.Frame(0).Utc);
        Assert.Equal("2026-05-05T22:30:02.000Z", SmokeSequence.Frame(50).Utc);
    }
}
