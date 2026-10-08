using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class PlaceholderDynamicsTests
{
    [Fact]
    public void StarboardRudderIncreasesHeadingWithPositiveRot()
    {
        var e = TestData.Engine(speedKn: 10, telegraph: "FAH");
        e.Enqueue(SimCommand.Rudder(20));
        e.Run(50 * 60); // 60 s
        var s = e.BuildState();
        Assert.True(s.Rot > 5, $"ROT 應為正且明顯:{s.Rot}");
        Assert.InRange(s.Heading, 5.0, 180.0);
        Assert.Equal(20.0, s.Rudder, 6); // 舵機 1.82°/s,11 s 內到位
        Assert.True(s.Drift > 0, "右轉時船舯向外漂,漂角為正");
        Assert.True(s.Stw < 10.0, "轉舵時航速下降");
    }

    [Fact]
    public void PortRudderDecreasesHeading()
    {
        var e = TestData.Engine(speedKn: 10, telegraph: "FAH");
        e.Enqueue(SimCommand.Rudder(-20));
        e.Run(50 * 60);
        var s = e.BuildState();
        Assert.True(s.Rot < -5);
        Assert.InRange(s.Heading, 180.0, 355.0);
    }

    [Fact]
    public void HalfAheadConvergesToParticularsSpeed()
    {
        var e = TestData.Engine(speedKn: 0, telegraph: "STOP");
        Assert.Equal(TelegraphOrder.STOP, e.Telegraph);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.HAH));
        e.Run(50 * 1500); // 25 分鐘
        var s = e.BuildState();
        var expected = TestData.Fsb1().TelegraphSpeedKn(TelegraphOrder.HAH, LoadingCondition.Ballast)!.Value; // 7.8 kn
        Assert.Equal(TelegraphOrder.HAH, s.Telegraph);
        Assert.Equal(100.0, s.Rpm, 1);
        Assert.InRange(s.Stw, expected - 0.2, expected + 0.2);
        Assert.Equal(EngineRunState.Running, s.Engine!.State);
    }

    [Fact]
    public void AsternTelegraphGivesNegativeRpmAndSpeed()
    {
        var e = TestData.Engine(speedKn: 0, telegraph: "STOP");
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.HAS));
        e.Run(50 * 600);
        var s = e.BuildState();
        Assert.True(s.Rpm < -90);
        Assert.True(s.Stw < -2.0);
    }

    [Fact]
    public void RudderIsRateLimitedByHardOverTime()
    {
        var e = TestData.Engine();
        e.Enqueue(SimCommand.Rudder(35));
        e.Run(50 * 5); // 5 s × (35/19.2 °/s) ≈ 9.11°
        Assert.Equal(5.0 * 35.0 / 19.2, e.Actuators.RudderDeg, 2);
        e.Enqueue(SimCommand.Rudder(70)); // 超過一般最大舵角 → 夾到 35
        e.Run(50 * 30);
        Assert.Equal(35.0, e.Actuators.RudderDeg, 6);
        Assert.Equal(35.0, e.Actuators.RudderOrderDeg, 6);
    }

    [Fact]
    public void BowThrusterTurnsShipAtLowSpeed()
    {
        var e = TestData.Engine(speedKn: 0, telegraph: "STOP");
        e.Enqueue(SimCommand.Thruster(1.0));
        e.Run(50 * 120);
        var s = e.BuildState();
        Assert.True(s.Thruster!.Actual > 0.99);
        Assert.InRange(s.Rot, 10.0, 21.0); // 零速迴轉率 20 °/min(側推尚未完全推上時略低)
        Assert.True(s.Heading > 5);

        // 高速時幾乎無效
        var fast = TestData.Engine(speedKn: 14, telegraph: "NAVF");
        fast.Enqueue(SimCommand.Thruster(1.0));
        fast.Run(50 * 120);
        Assert.True(fast.BuildState().Rot < 2.0);
    }

    [Fact]
    public void AutopilotSettlesOnHeading()
    {
        var e = TestData.Engine(speedKn: 10, telegraph: "FAH");
        e.Enqueue(SimCommand.Autopilot(true, heading: 30, rotLimit: 15));
        var maxRot = 0.0;
        e.Stepped += x => maxRot = Math.Max(maxRot, Math.Abs(Units.RadPerSecToDegPerMin(x.Motion.R)));
        e.Run(50 * 600);
        var s = e.BuildState();
        Assert.InRange(Units.WrapDeg180(s.Heading - 30), -2.0, 2.0);
        Assert.True(maxRot < 25.0, $"ROT 限制 15 °/min,最大 {maxRot:F1}");
        // 手動舵令解除自動舵
        e.Enqueue(SimCommand.Rudder(0));
        e.ProcessPendingCommands();
        Assert.False(e.Autopilot.Enabled);
    }

    [Fact]
    public void FaultsAffectActuators()
    {
        var e = TestData.Engine();
        e.Enqueue(SimCommand.Rudder(10));
        e.Run(50 * 10);
        Assert.Equal(10.0, e.Actuators.RudderDeg, 6);
        e.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear));
        e.Enqueue(SimCommand.Rudder(-10));
        e.Run(50 * 10);
        Assert.Equal(10.0, e.Actuators.RudderDeg, 6); // 卡舵
        Assert.Contains(FaultNames.SteeringGear, e.BuildState().Faults!);

        e.Enqueue(SimCommand.InjectFault(FaultNames.MainEngine));
        e.Run(50 * 120);
        var s = e.BuildState();
        Assert.Equal(EngineRunState.Failed, s.Engine!.State);
        Assert.True(Math.Abs(s.Rpm) < 1.0);
        Assert.True(s.Stw < 7.8);
    }

    [Fact]
    public void CurrentAffectsGroundTrackButNotWaterSpeed()
    {
        var e = TestData.Engine(speedKn: 0, telegraph: "STOP");
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 0, currentSetDeg: 90, currentDriftKn: 2.0));
        e.Run(50 * 60);
        var s = e.BuildState();
        Assert.InRange(s.Sog, 1.9, 2.1);
        Assert.InRange(s.Cog, 85.0, 95.0);
        Assert.True(Math.Abs(s.Stw) < 0.2);
        Assert.True(s.Pos.X > 50.0, "1 分鐘內向東漂約 62 m");
    }

    [Fact]
    public void GroundingSetsFlag()
    {
        var e = TestData.Engine(speedKn: 0, telegraph: "STOP");
        var grounded = false;
        e.Grounded += _ => grounded = true;
        e.Enqueue(SimCommand.SetEnvironment(waterDepth: 5.0)); // 壓載最大吃水 5.41 m
        e.Run(2);
        Assert.True(grounded);
        Assert.True(e.BuildState().Flags!.Aground);
        Assert.True(e.BuildState().DepthBelowKeel < 0);
    }
}
