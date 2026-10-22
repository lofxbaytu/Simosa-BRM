using SimosaBRM.Visual.Shared.Scene;
using SimosaBRM.Visual.Shared.Ship;
using SimosaBRM.Visual.Shared.Smoke;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

/// <summary>
/// 審查發現(visual):推頂拖船的艏必須指向自船。契約 direction_deg 為力方向(相對船艏),
/// SimCore ExternalForceModel.TugStates 把 push 拖船放在力方向的反側,所以艏向 = 自船艏向 + 力方向(與 pull 相同;位置由核心決定)。
/// </summary>
public class TugPresentationTests
{
    [Fact]
    public void PushTugBowPointsTowardsOwnShipInSmokeFrame()
    {
        var s = SmokeSequence.Frame(0); // 自船 045;拖船 push、direction 90、位於左舷後方
        var tug = s.Tugs![0];
        Assert.Equal("push", tug.Mode);
        var heading = TugPresentation.HeadingDeg(s.Heading, tug.Mode, tug.DirectionDeg);
        var bow = SceneCoordinates.BodyToWorldOffset(TugPresentation.LineAttachForwardM(tug.Mode), 0, 0, heading);
        var (be, bn, _) = SceneCoordinates.ToEnu(bow);
        var centre = Dist(tug.Pos!.X, tug.Pos.Y, s.Pos.X, s.Pos.Y);
        var bowDist = Dist(tug.Pos.X + be, tug.Pos.Y + bn, s.Pos.X, s.Pos.Y);
        Assert.True(bowDist < centre, $"艏距 {bowDist:F1} m 應小於中心距 {centre:F1} m(艏向 {heading:F0})");
        Assert.InRange(heading, 134.9, 135.1);
    }

    [Theory]
    [InlineData("push", 45.0, 90.0, 135.0)]
    [InlineData("pull", 45.0, 90.0, 135.0)]
    [InlineData("push", 350.0, 270.0, 260.0)]
    [InlineData("standby", 45.0, 90.0, 45.0)]
    [InlineData(null, 45.0, 90.0, 45.0)]
    public void HeadingFollowsForceDirectionForPushAndPull(string? mode, double own, double dir, double expected)
    {
        Assert.InRange(TugPresentation.HeadingDeg(own, mode, dir), expected - 1e-9, expected + 1e-9);
    }

    [Fact]
    public void PullTugAttachesLineAtStern()
    {
        // 拖拉:艏背向自船、以艉纜拖;推頂:艏頂自船
        Assert.True(TugPresentation.LineAttachForwardM("push") > 0);
        Assert.True(TugPresentation.LineAttachForwardM("pull") < 0);
        Assert.Equal(0, TugPresentation.LineAttachForwardM("standby"));
    }

    private static double Dist(double x0, double y0, double x1, double y1) => Math.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1));
}
