using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Geo;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class EnvironmentTests
{
    [Fact]
    public void RelativeWindHeadOnAndBeam()
    {
        // 真風自北 10 m/s,船向北 5 m/s → 相對風自艏 15 m/s
        var env = new EnvironmentConditions { WindTrueSpeedMps = 10, WindTrueDirFromRad = 0 };
        var s = env.SampleAt(0, 0);
        var (spd, dir) = EnvironmentMath.RelativeWind(s.WindEastMps, s.WindNorthMps, 0, 5, 0);
        Assert.Equal(15.0, spd, 9);
        Assert.Equal(0.0, dir, 9);

        // 船向東 5 m/s,真風自北 → 相對風自左艏
        var (spd2, dir2) = EnvironmentMath.RelativeWind(s.WindEastMps, s.WindNorthMps, 5, 0, Math.PI / 2);
        Assert.Equal(Math.Sqrt(125), spd2, 9);
        Assert.InRange(Units.RadToDeg(dir2), 270.0, 360.0);
        Assert.Equal(360 - Units.RadToDeg(Math.Atan2(10, 5)), Units.RadToDeg(dir2), 6);
    }

    [Fact]
    public void CogSogIncludesCurrent()
    {
        var (vE, vN) = EnvironmentMath.GroundVelocity(u: 5, v: 0, headingRad: 0, currentEast: 1, currentNorth: 0);
        var (sog, cog) = EnvironmentMath.CogSog(vE, vN, 0);
        Assert.Equal(Math.Sqrt(26), sog, 9);
        Assert.Equal(Math.Atan2(1, 5), cog, 9);
        // 船體橫向速度 v 右正:航向北、v = 1 → 向東
        var (vE2, vN2) = EnvironmentMath.GroundVelocity(0, 1, 0, 0, 0);
        Assert.Equal(1.0, vE2, 12);
        Assert.Equal(0.0, vN2, 12);
    }

    [Fact]
    public void SquatAndUkc()
    {
        // FSB1 海報:滿載 Cb 0.738,10 kn → Barrass 約 0.74 m(海報 0.27 m,UKC 6 m 開闊水域;簡式偏保守)
        var squat = EnvironmentMath.SquatBarrass(0.738, 10);
        Assert.Equal(0.738, squat, 6);
        Assert.Equal(30 - 5.41 - squat, EnvironmentMath.UnderKeelClearance(30, 5.41, squat), 9);
    }

    [Fact]
    public void LocalTangentPlaneRoundTrips()
    {
        var ltp = new LocalTangentPlane(23.8, 120.05);
        var (lat, lon) = ltp.ToGeodetic(1000, 2000);
        Assert.True(lat > 23.8 && lon > 120.05);
        var (x, y) = ltp.ToLocal(lat, lon);
        Assert.Equal(1000, x, 6);
        Assert.Equal(2000, y, 6);
        // 1 浬向北 ≈ 1 分緯度
        var (lat2, _) = ltp.ToGeodetic(0, 1852);
        Assert.Equal(1.0 / 60.0, lat2 - 23.8, 4);
    }

    [Fact]
    public void CallbackDepthIsUsed()
    {
        var env = new EnvironmentConditions { Depth = new CallbackDepth((x, y) => 10 + x * 0.01) };
        Assert.Equal(20.0, env.SampleAt(1000, 0).WaterDepthM, 9);
    }
}
