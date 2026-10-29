using SimosaBRM.Visual.Shared.Scene;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

public class SceneCoordinatesTests
{
    [Fact]
    public void EnuEastNorthUpMapsToGodotXYNegZ()
    {
        var p = SceneCoordinates.FromEnu(100, 200, 5);
        Assert.Equal(new ScenePoint(100, 5, -200), p);
        var (e, n, u) = SceneCoordinates.ToEnu(p);
        Assert.Equal((100.0, 200.0, 5.0), (e, n, u));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, -Math.PI / 2)]
    [InlineData(180, -Math.PI)]
    [InlineData(270, -3 * Math.PI / 2)]
    public void HeadingToYawIsNegativeRadians(double heading, double yaw)
    {
        Assert.InRange(SceneCoordinates.HeadingToYawRad(heading), yaw - 1e-12, yaw + 1e-12);
        Assert.InRange(SceneCoordinates.YawRadToHeading(yaw), heading - 1e-9, heading + 1e-9);
    }

    [Fact]
    public void HeadingEastRotatesNodeForwardToPlusX()
    {
        // 節點前方 −z 經 yaw 旋轉後應指向東(+x):rotation about y by θ: (x,z) → (x cos θ + z sin θ, −x sin θ + z cos θ)
        var yaw = SceneCoordinates.HeadingToYawRad(90);
        var fx = 0 * Math.Cos(yaw) + (-1) * Math.Sin(yaw);
        var fz = -0 * Math.Sin(yaw) + (-1) * Math.Cos(yaw);
        Assert.InRange(fx, 0.999, 1.001);
        Assert.InRange(fz, -0.001, 0.001);
    }

    [Fact]
    public void BodyFrameMapsBowToMinusZAndStarboardToPlusX()
    {
        Assert.Equal(new ScenePoint(0, 0, -50), SceneCoordinates.FromBody(50, 0, 0));
        Assert.Equal(new ScenePoint(9, 0, 0), SceneCoordinates.FromBody(0, 9, 0));
        Assert.Equal(new ScenePoint(0, 12, 0), SceneCoordinates.FromBody(0, 0, 12));
    }

    [Fact]
    public void BodyOffsetRotatedByHeadingGivesWorldOffset()
    {
        // 航向 090,艏前 10 m → 東 10 m;右舷 5 m → 南 5 m(場景 z = +5)
        var bow = SceneCoordinates.BodyToWorldOffset(10, 0, 0, 90);
        Assert.InRange(bow.X, 9.999, 10.001);
        Assert.InRange(bow.Z, -0.001, 0.001);
        var stbd = SceneCoordinates.BodyToWorldOffset(0, 5, 0, 90);
        Assert.InRange(stbd.X, -0.001, 0.001);
        Assert.InRange(stbd.Z, 4.999, 5.001);
    }

    [Fact]
    public void BearingAndRangeToEnu()
    {
        var (e, n) = SceneCoordinates.FromBearing(45, 1414.2135);
        Assert.InRange(e, 999.9, 1000.1);
        Assert.InRange(n, 999.9, 1000.1);
    }
}

/// <summary>橫搖的場景幾何(docs/design/visual-client.md 第 4 節「橫搖視角」):右傾正、右舷下沉、高處往右舷移;0 時與無橫搖相同。</summary>
public class SceneRollTests
{
    [Fact]
    public void HeelOffsetMovesHighPointsToStarboardAndLowersStarboardSide()
    {
        // 桅頂(中心線、高 20 m)右傾 10°:往右舷 20·sin10° ≈ 3.47 m、高度 20·cos10° ≈ 19.70 m
        var (s, u) = SceneCoordinates.HeelOffset(0, 20, 10);
        Assert.InRange(s, 3.47, 3.48);
        Assert.InRange(u, 19.69, 19.70);
        // 右舷舷側(stbd 9 m、水線高 0)右傾 10°:下沉 9·sin10° ≈ 1.56 m
        var (s2, u2) = SceneCoordinates.HeelOffset(9, 0, 10);
        Assert.InRange(s2, 8.86, 8.87);
        Assert.InRange(u2, -1.57, -1.56);
        // 左傾(負)對稱
        var (s3, u3) = SceneCoordinates.HeelOffset(0, 20, -10);
        Assert.InRange(s3, -3.48, -3.47);
        Assert.Equal(u, u3, 9);
    }

    [Fact]
    public void ZeroRollKeepsBodyOffsetBitIdentical()
    {
        var a = SceneCoordinates.BodyToWorldOffset(30, -9, 18, 137);
        var b = SceneCoordinates.BodyToWorldOffset(30, -9, 18, 137, 0.0);
        Assert.Equal(a, b);
        Assert.Equal((-9.0, 18.0), SceneCoordinates.HeelOffset(-9, 18, 0));
    }

    [Fact]
    public void BodyOffsetWithRollRotatesEyeTowardsStarboardInWorldFrame()
    {
        // 航向 000:右舷 = 東;駕駛台眼位(中心線、高 18 m)右傾 5° → 東向 +18·sin5°、高度 18·cos5°
        var p = SceneCoordinates.BodyToWorldOffset(0, 0, 18, 0, 5);
        var (east, north, up) = SceneCoordinates.ToEnu(p);
        Assert.InRange(east, 1.568, 1.570);
        Assert.InRange(north, -1e-9, 1e-9);
        Assert.InRange(up, 17.93, 17.94);
        // 航向 090:右舷 = 南 → 北向為負
        var q = SceneCoordinates.BodyToWorldOffset(0, 0, 18, 90, 5);
        var (e2, n2, _) = SceneCoordinates.ToEnu(q);
        Assert.InRange(n2, -1.570, -1.568);
        Assert.InRange(e2, -1e-9, 1e-9);
    }

    [Fact]
    public void ObserverHeelComponentsFlipForAftViewAndBecomePitchAbeam()
    {
        var (r0, p0) = SceneCoordinates.ObserverHeelComponents(6, 0);     // 駕駛台前視:全為橫滾
        Assert.Equal(6.0, r0, 9);
        Assert.Equal(0.0, p0, 9);
        var (r180, p180) = SceneCoordinates.ObserverHeelComponents(6, 180); // 艉視:橫滾反號
        Assert.Equal(-6.0, r180, 9);
        Assert.InRange(p180, -1e-9, 1e-9);
        var (r90, p90) = SceneCoordinates.ObserverHeelComponents(6, 90);   // 正右舷看:右傾全化為下俯
        Assert.InRange(r90, -1e-9, 1e-9);
        Assert.Equal(-6.0, p90, 9);
        var (r60, p60) = SceneCoordinates.ObserverHeelComponents(6, 60);   // 右翼橋 60°:橫滾 3°、俯仰 −5.2°
        Assert.InRange(r60, 2.99, 3.01);
        Assert.InRange(p60, -5.20, -5.19);
    }
}
