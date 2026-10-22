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
