using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>操縱控制指標(情境引擎設計第 4.5 節):舵角分箱、滿舵、車令段落、凍結不取樣、速限來源優先序。</summary>
public class MetricsCollectorTests
{
    [Fact]
    public void RudderBinsSumToSamplesAndHardOverCountsFsb1At70()
    {
        var e = TestData.MinimalEngine("assessment:\n  manoeuvring: { rotLimit_degPerMin: 10 }");
        TestData.RunTo(e, 20);
        e.Enqueue(SimCommand.Rudder(70)); e.ProcessPendingCommands();
        TestData.RunTo(e, 80);
        e.Enqueue(SimCommand.Rudder(0)); e.ProcessPendingCommands();
        TestData.RunTo(e, 120);
        e.Enqueue(SimCommand.Rudder(-70)); e.ProcessPendingCommands();
        TestData.RunTo(e, 160);
        var m = e.LatestSummary!.Manoeuvring;
        Assert.Equal(160.0, m.RudderDistributionS.Values.Sum(), 9);
        // 暫代模型的舵角上限為 normalMaxAngle 35°(MMG 為 70°),滿舵門檻取目前模型的上限
        Assert.Equal(35.0, e.Actuators.Parameters.RudderMaxDeg);
        Assert.Equal(0.0, m.RudderDistributionS[">35"]);
        Assert.True(m.RudderDistributionS["20-35"] > 40, $"20-35 = {m.RudderDistributionS["20-35"]}");
        Assert.Equal(2, m.HardOverCount);
        Assert.True(m.HardOverTimeS > 30);
        Assert.Equal(3, m.RudderOrders);
        Assert.True(m.MaxRotDegPerMin > 10);
        Assert.True(m.TimeRotOverLimitS > 0);
        Assert.True(m.RotExcursions >= 1);
        Assert.Equal(160.0, e.LatestSummary.General.DurationS);
        Assert.True(e.LatestSummary.General.DistanceNm > 0.2);
    }

    [Fact]
    public void TelegraphSpansCoverDurationAndFreezeIsNotSampled()
    {
        var e = TestData.MinimalEngine();
        TestData.RunTo(e, 30);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH)); e.ProcessPendingCommands();
        TestData.RunTo(e, 50);
        e.Enqueue(SimCommand.Freeze()); e.ProcessPendingCommands();
        e.Run(500); // 凍結:時間不前進
        e.Enqueue(SimCommand.Resume()); e.ProcessPendingCommands();
        TestData.RunTo(e, 90);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH)); // 相同車令不算變更
        e.Enqueue(SimCommand.Rpm(40));
        e.Enqueue(SimCommand.Thruster(0.3));
        e.Enqueue(SimCommand.Autopilot(true, heading: 10));
        e.ProcessPendingCommands();
        TestData.RunTo(e, 100);
        var s = e.LatestSummary!;
        var m = s.Manoeuvring;
        Assert.Equal(1, m.TelegraphChanges);
        Assert.Equal(2, m.TelegraphOrders.Count);
        Assert.Equal("HAH", m.TelegraphOrders[0].Order);
        Assert.Equal(30.0, m.TelegraphOrders[0].DurationS);
        Assert.Equal(100.0, m.TelegraphOrders.Sum(t => t.DurationS), 9);
        Assert.Equal(1, s.General.Freezes);
        Assert.Equal(100.0, s.General.DurationS);
        Assert.Equal(100.0, m.RudderDistributionS.Values.Sum(), 9);
        Assert.Equal(1, m.RpmOrders);
        Assert.Equal(1, m.ThrusterOrders);
        Assert.Equal(10.0, m.ThrusterTimeS);
        Assert.Equal(1, m.AutopilotSwitches);
        Assert.Equal(10.0, m.AutopilotTimeS);
    }

    [Fact]
    public void SpeedLimitTakesStricterOfLegAndZone()
    {
        var e = TestData.MinimalEngine("""
            route:
              waypoints: [ { name: A, x: 0, y: -100 }, { name: B, x: 0, y: 20000, speedLimit_kn: 5 } ]
            zones:
              - id: inner
                type: speedLimit
                speedLimit_kn: 3
                polygon: [ { x: -500, y: 300 }, { x: 500, y: 300 }, { x: 500, y: 5000 }, { x: -500, y: 5000 } ]
            """, speedKn: 8);
        var a = e.Assessment!;
        TestData.RunTo(e, 5);
        Assert.Equal((5.0, "leg:B"), a.SpeedLimit());
        TestData.RunTo(e, 150);
        Assert.True(a.Zones.IsInside("inner"));
        Assert.Equal((3.0, "zone:inner"), a.SpeedLimit());
        Assert.Equal(5.0, e.BuildState().Route!.SpeedLimitKn);
        var m = a.Metrics.State;
        Assert.Equal(150, (int)m.TimeOverSpeedLimitS);
        Assert.Equal("zone:inner", m.SpeedLimitSource);
    }


    /// <summary>舵令計數以 1 s 視窗合併,視窗不隨後續舵令延長:每 0.5 s 一道、共 60 道(30 s)→ 30 次。</summary>
    [Fact]
    public void RudderOrdersCountOncePerSecondWindowDuringContinuousSteering()
    {
        var e = TestData.MinimalEngine();
        TestData.RunTo(e, 5);
        for (var i = 0; i < 60; i++)
        {
            e.Enqueue(SimCommand.Rudder(i % 2 == 0 ? 5 : 0)); e.ProcessPendingCommands();
            TestData.RunTo(e, 5 + 0.5 * (i + 1));
        }
        Assert.Equal(30, e.LatestSummary!.Manoeuvring.RudderOrders);
        // 間隔 ≥ 1 s 的舵令各計一次
        TestData.RunTo(e, 40);
        e.Enqueue(SimCommand.Rudder(10)); e.ProcessPendingCommands();
        TestData.RunTo(e, 41.5);
        e.Enqueue(SimCommand.Rudder(0)); e.ProcessPendingCommands();
        TestData.RunTo(e, 42);
        Assert.Equal(32, e.LatestSummary!.Manoeuvring.RudderOrders);
    }

    /// <summary>timeScale ≤ 0 與 freeze 同路徑:發 frozen 事件、計入 freezes;resume 後 resumed 有對應。</summary>
    [Fact]
    public void TimeScaleZeroCountsAsFreeze()
    {
        var e = TestData.MinimalEngine();
        var events = TestData.Capture(e);
        TestData.RunTo(e, 5);
        e.Enqueue(SimCommand.TimeScale(0)); e.ProcessPendingCommands();
        Assert.True(e.Frozen);
        Assert.Equal(new[] { SimEventKinds.Frozen, SimEventKinds.TimeScaleChanged }, events.Select(x => x.Kind));
        Assert.Equal(1, e.Assessment!.Metrics.State.Freezes);
        e.Enqueue(SimCommand.Resume()); e.ProcessPendingCommands();
        Assert.False(e.Frozen);
        Assert.Equal(1, events.Count(x => x.Kind == SimEventKinds.Resumed));
        TestData.RunTo(e, 6);
        Assert.Equal(1, e.LatestSummary!.General.Freezes);
    }

    /// <summary>自動舵開啟時的手動舵令視為一次 autopilotOff(切換次數與必要事件 expect autopilotOff);時間表 ownShip 覆寫不計。</summary>
    [Fact]
    public void ManualRudderWhileAutopilotCountsAsAutopilotOff()
    {
        var e = TestData.MinimalEngine("""
            timeline:
              - id: fail
                when: { type: time, atLeast: 2 }
                actions: [ { injectFault: steeringGear } ]
              - id: override
                when: { type: time, atLeast: 20 }
                actions: [ { autopilot: { enabled: true, heading: 10 } }, { ownShip: { rudder: 5 } } ]
            assessment:
              required:
                - id: firstAction
                  after: { event: faultInjected, itemId: fail }
                  expect: { any: [ telegraphChange, autopilotOff ] }
                  within_s: 60
                  penalty: 20
            """);
        var events = TestData.Capture(e);
        e.Enqueue(SimCommand.Autopilot(true, heading: 0)); e.ProcessPendingCommands();
        TestData.RunTo(e, 5);
        Assert.Equal(1, e.LatestSummary!.Manoeuvring.AutopilotSwitches);
        e.Enqueue(SimCommand.Rudder(-20)); e.ProcessPendingCommands();
        Assert.False(e.Autopilot.Enabled);
        TestData.RunTo(e, 6);
        Assert.Equal(2, e.LatestSummary!.Manoeuvring.AutopilotSwitches);
        var req = e.LatestSummary.Assessment.Required.Single();
        Assert.Equal("met", req.State);
        Assert.Equal("autopilotOff", req.By);
        Assert.Contains(events, x => x.Kind == SimEventKinds.RequiredEventMet && x.ItemId == "firstAction");
        // 再下一道舵令(自動舵已關)不再計;時間表的 autopilot + ownShip 覆寫不觀察
        e.Enqueue(SimCommand.Rudder(0)); e.ProcessPendingCommands();
        TestData.RunTo(e, 25);
        Assert.False(e.Autopilot.Enabled);
        Assert.Equal(2, e.LatestSummary!.Manoeuvring.AutopilotSwitches);
    }
}
