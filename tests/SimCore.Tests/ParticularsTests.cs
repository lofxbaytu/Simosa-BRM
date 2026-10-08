using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Physics;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

public class ParticularsTests
{
    [Fact]
    public void LoadsFsb1()
    {
        var p = TestData.Fsb1();
        Assert.Equal("FSB1", p.Id);
        Assert.Equal(99.0, p.Hull.LengthBetweenPerpendiculars_m);
        Assert.Equal(100.0, p.TelegraphRpm(TelegraphOrder.HAH));
        Assert.Equal(-150.0, p.TelegraphRpm(TelegraphOrder.EFAS));
        Assert.Equal(0.0, p.TelegraphRpm(TelegraphOrder.STOP));
        Assert.Equal(7.8, p.TelegraphSpeedKn(TelegraphOrder.HAH, LoadingCondition.Ballast));
        Assert.Equal(7.5, p.TelegraphSpeedKn(TelegraphOrder.HAH, LoadingCondition.Full));
        Assert.Equal(19.2, p.Rudder.HardOverTime35Seconds);
        Assert.Equal(35.0, p.Rudder.NormalMaxAngleDeg);
        Assert.Equal(58.0, p.Engine.MinimumRpm);
        Assert.Equal(20.0, p.BowThruster!.TurningRateAtZeroSpeed_degPerMin);
        Assert.Equal(15.75, p.Hull.BilgeKeelArea_m2);
        Assert.NotNull(p.Extra); // squatTable 等保留
        Assert.True(p.Extra!.ContainsKey("squatTable"));
    }

    [Fact]
    public void ToleratesNullsInFsb2()
    {
        var p = TestData.Fsb2();
        Assert.Equal("FSB2", p.Id);
        Assert.Null(p.Hull.BilgeKeelArea_m2);
        Assert.Null(p.Engine.MinimumRpm);
        Assert.Null(p.Engine.CriticalRpmRange);
        Assert.Null(p.LoadingConditions["ballast"].Displacement_t);
        Assert.Null(p.Rudder.Area_m2);
        // EFAS 未定義 → 退回 fullAstern
        Assert.Equal(-103.0, p.TelegraphRpm(TelegraphOrder.EFAS));
        // hardOverTime35_s 未定義 → 取雙泵 14 s
        Assert.Equal(14.0, p.Rudder.HardOverTime35Seconds);
        Assert.Equal(5.0, p.BowThruster!.NotEffectiveAbove_kn);
        var act = ActuatorParameters.FromParticulars(p);
        Assert.Equal(2.5, act.RudderRateDegPerSec, 9);
        Assert.Null(act.MinimumRpm);
    }

    [Fact]
    public void IntermediateLoadingIsEstimated()
    {
        var p = TestData.Fsb1();
        var mid = p.GetLoading(LoadingCondition.Intermediate);
        Assert.Equal((7724.77 + 4472.8) / 2, mid.Displacement_t!.Value, 6);
        Assert.Contains("estimated", mid.Source);
    }

    [Fact]
    public void AheadSpeedCurveIsMonotonic()
    {
        var curve = TestData.Fsb1().AheadSpeedCurve(LoadingCondition.Ballast);
        Assert.Equal(6, curve.Count); // 原點 + 5 個前進車令
        for (var i = 1; i < curve.Count; i++)
        {
            Assert.True(curve[i].Rpm > curve[i - 1].Rpm);
            Assert.True(curve[i].SpeedKn > curve[i - 1].SpeedKn);
        }
    }
}
