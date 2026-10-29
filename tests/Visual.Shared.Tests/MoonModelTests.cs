using SimosaBRM.Visual.Shared.Env;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

/// <summary>
/// 月球位置與月相(docs/design/visual-client.md 第 4 節「月光」)。
/// 期望值來源:演算法本身(Schlyter 軌道根數 + 攝動)的日月黃經差所得月齡/照明比例,不是外部星曆資料;
/// 作為交叉檢查,選用的日期是日食/月食日(日食必在新月、月食必在滿月:2026-02-17 環食、2026-08-12 全食、2026-03-03 月全食)
/// 與 2026-01-03 滿月、2026-01-18 新月。位置斷言用天文常識的寬範圍:滿月在當地午夜近中天、正午在地平線下、傍晚自東方升起。
/// </summary>
public class MoonModelTests
{
    private const double KaohsiungLat = 22.53;
    private const double KaohsiungLon = 120.24;

    [Theory]
    [InlineData(2026, 1, 3, 10, 3)]   // 滿月
    [InlineData(2026, 3, 3, 11, 38)]  // 月全食(必為滿月)
    [InlineData(2026, 8, 28, 4, 18)]  // 月偏食(必為滿月)
    public void FullMoonDatesGiveIlluminatedFractionNearOne(int y, int mo, int d, int h, int mi)
    {
        var m = MoonModel.Compute(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero), KaohsiungLat, KaohsiungLon);
        Assert.True(m.IlluminatedFraction > 0.98, $"{y}-{mo}-{d} 照明比例 {m.IlluminatedFraction:F3}");
        Assert.InRange(m.PhaseAngleDeg, 0, 16);
        Assert.InRange(m.AgeDays, 13.5, 16.0);
    }

    [Theory]
    [InlineData(2026, 1, 18, 19, 52)]  // 新月
    [InlineData(2026, 2, 17, 12, 12)]  // 日環食(必為新月)
    [InlineData(2026, 8, 12, 17, 46)]  // 日全食(必為新月)
    public void NewMoonDatesGiveIlluminatedFractionNearZero(int y, int mo, int d, int h, int mi)
    {
        var m = MoonModel.Compute(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero), KaohsiungLat, KaohsiungLon);
        Assert.True(m.IlluminatedFraction < 0.02, $"{y}-{mo}-{d} 照明比例 {m.IlluminatedFraction:F3}");
        Assert.True(m.AgeDays < 1.0 || m.AgeDays > 28.5, $"月齡 {m.AgeDays:F2}");
    }

    [Fact]
    public void QuarterMoonsAreHalfLitWithWaxingFlag()
    {
        // 2025-12-27 上弦(漸盈)、2026-01-10 下弦(漸虧):新月 2026-01-18 往前推 1/4 與 1/2 朔望月
        var first = MoonModel.Compute(new DateTimeOffset(2025, 12, 27, 19, 10, 0, TimeSpan.Zero), KaohsiungLat, KaohsiungLon);
        Assert.InRange(first.IlluminatedFraction, 0.45, 0.55);
        Assert.True(first.Waxing);
        var last = MoonModel.Compute(new DateTimeOffset(2026, 1, 10, 15, 48, 0, TimeSpan.Zero), KaohsiungLat, KaohsiungLon);
        Assert.InRange(last.IlluminatedFraction, 0.45, 0.55);
        Assert.False(last.Waxing);
    }

    [Fact]
    public void FullMoonCulminatesNearLocalMidnightAndIsBelowHorizonAtNoon()
    {
        // 高雄 UTC+8:2026-01-03 16:00Z = 當地 00:00;滿月與太陽相對,一月太陽赤緯 −23° → 月球赤緯約 +23°,緯度 22.5° 的中天高度接近天頂
        var midnight = MoonModel.Compute(new DateTimeOffset(2026, 1, 3, 16, 0, 0, TimeSpan.Zero), KaohsiungLat, KaohsiungLon);
        Assert.InRange(midnight.ElevationDeg, 65, 90);
        // 當地正午(04:00Z)滿月在地平線下
        var noon = MoonModel.Compute(new DateTimeOffset(2026, 1, 3, 4, 0, 0, TimeSpan.Zero), KaohsiungLat, KaohsiungLon);
        Assert.True(noon.ElevationDeg < -25, $"正午月球高度 {noon.ElevationDeg:F1}");
    }

    [Fact]
    public void FullMoonRisesInTheEastInTheEveningAndSetsInTheWestBeforeDawn()
    {
        // 11:00Z = 當地 19:00:滿月已自東北東升起、高度低
        var evening = MoonModel.Compute(new DateTimeOffset(2026, 1, 3, 11, 0, 0, TimeSpan.Zero), KaohsiungLat, KaohsiungLon);
        Assert.InRange(evening.ElevationDeg, 5, 40);
        Assert.InRange(evening.AzimuthDeg, 45, 110);
        // 21:00Z = 當地 05:00:月球在西北西、尚未落下
        var dawn = MoonModel.Compute(new DateTimeOffset(2026, 1, 3, 21, 0, 0, TimeSpan.Zero), KaohsiungLat, KaohsiungLon);
        Assert.InRange(dawn.ElevationDeg, 5, 45);
        Assert.InRange(dawn.AzimuthDeg, 250, 315);
    }

    [Fact]
    public void ComputeIsDeterministicAndIndependentOfOffset()
    {
        var a = MoonModel.Compute(new DateTimeOffset(2026, 5, 5, 16, 0, 0, TimeSpan.Zero), KaohsiungLat, KaohsiungLon);
        var b = MoonModel.Compute(new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.FromHours(8)), KaohsiungLat, KaohsiungLon);
        Assert.Equal(a, b);
        Assert.InRange(a.DistanceEarthRadii, 55, 64);
        Assert.InRange(a.AzimuthDeg, 0, 360);
    }

    [Fact]
    public void RelativeIlluminanceFollowsElevationAndPhase()
    {
        Assert.Equal(0.0, MoonModel.RelativeIlluminance(-5, 1.0));
        Assert.Equal(0.0, MoonModel.RelativeIlluminance(0, 1.0));
        Assert.InRange(MoonModel.RelativeIlluminance(90, 1.0), 0.99, 1.0); // 滿月天頂 = 1(0.25 lux)
        Assert.InRange(MoonModel.FullMoonZenithLux, 0.2, 0.3);
        var high = MoonModel.RelativeIlluminance(60, 1.0);
        var low = MoonModel.RelativeIlluminance(10, 1.0);
        Assert.True(high > low && low > 0, $"60° {high:F3} > 10° {low:F3}");
        Assert.True(low < 0.15, $"低仰角 10° 經投影與消光應小於 0.15:{low:F3}");
        // 相位:上弦約 1/10 滿月、新月近 0
        Assert.InRange(MoonModel.PhaseFactor(1.0), 0.999, 1.0);
        Assert.InRange(MoonModel.PhaseFactor(0.5), 0.07, 0.12);
        Assert.True(MoonModel.PhaseFactor(0.0) < 0.001);
        Assert.True(MoonModel.RelativeIlluminance(60, 0.5) < MoonModel.RelativeIlluminance(60, 1.0));
    }
}
