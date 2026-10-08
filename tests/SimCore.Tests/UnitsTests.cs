using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class UnitsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(180, Math.PI)]
    [InlineData(-90, -Math.PI / 2)]
    public void DegRadRoundTrip(double deg, double rad)
    {
        Assert.Equal(rad, Units.DegToRad(deg), 12);
        Assert.Equal(deg, Units.RadToDeg(rad), 12);
    }

    [Fact]
    public void KnotsAndMetresPerSecond()
    {
        Assert.Equal(1852.0 / 3600.0, Units.KnToMps(1.0), 12);
        Assert.Equal(10.0, Units.MpsToKn(Units.KnToMps(10.0)), 12);
        Assert.Equal(7.8, Units.MpsToKn(Units.KnToMps(7.8)), 12);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-10, 350)]
    [InlineData(725, 5)]
    [InlineData(-720, 0)]
    [InlineData(359.999, 359.999)]
    [InlineData(-1e-15, 0)]
    public void NormalizeHeadingDeg(double input, double expected)
    {
        var h = Units.NormalizeHeadingDeg(input);
        Assert.Equal(expected, h, 9);
        Assert.InRange(h, 0.0, 360.0 - 1e-12);
    }

    [Fact]
    public void NormalizeHeadingRadStaysInRange()
    {
        for (var a = -20.0; a <= 20.0; a += 0.37)
        {
            var r = Units.NormalizeHeadingRad(a);
            Assert.InRange(r, 0.0, Units.TwoPi);
            Assert.True(r < Units.TwoPi);
            Assert.Equal(Math.Sin(a), Math.Sin(r), 9);
        }
    }

    [Theory]
    [InlineData(10, 350, -20)]
    [InlineData(350, 10, 20)]
    [InlineData(0, 180, 180)]
    [InlineData(90, 270, 180)]
    [InlineData(270, 90, -180 + 360)] // (−180, 180]:−180 折成 180
    public void WrapDeg180(double from, double to, double expected)
    {
        Assert.Equal(expected, Units.WrapDeg180(to - from), 9);
    }

    [Fact]
    public void RotConversion()
    {
        // 1 度/秒 = 60 度/分
        Assert.Equal(60.0, Units.RadPerSecToDegPerMin(Units.DegToRad(1.0)), 9);
        Assert.Equal(Units.DegToRad(0.5), Units.DegPerMinToRadPerSec(30.0), 12);
        Assert.Equal(-12.3, Units.RadPerSecToDegPerMin(Units.DegPerMinToRadPerSec(-12.3)), 9);
    }
}
