using SimosaBRM.SimCore.Alerts;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 感測器層(設計文件 docs/design/sensors-alerts.md 第 2 節):各故障的指示值 vs 真值、自動舵跟隨電羅經漂移(E08 的關鍵)、
/// 自動舵脫開與拒絕、操舵類物理故障、快照/還原在漂移途中位元一致。
/// </summary>
public class SensorFaultTests
{
    private static SimulationEngine Engine(string extra = "", double speedKn = 8.0) => TestData.MinimalEngine(extra, speedKn: speedKn);

    private static void Do(SimulationEngine e, SimCommand cmd)
    {
        e.Enqueue(cmd);
        e.ProcessPendingCommands();
    }

    private static void RunS(SimulationEngine e, double seconds) => TestData.RunTo(e, e.Time + seconds);

    [Fact]
    public void NominalStateHasValidSensorsAndTruthEqualsIndicated()
    {
        var e = Engine();
        e.Run(100);
        var s = e.BuildState();
        Assert.NotNull(s.Truth);
        Assert.NotNull(s.Sensors);
        Assert.True(s.Sensors!.HeadingValid && s.Sensors.PositionValid && s.Sensors.DepthValid && s.Sensors.StwValid);
        Assert.Equal(s.Truth!.Heading, s.Heading);
        Assert.Equal(s.Truth.Pos, s.Pos);
        Assert.Equal(s.Truth.Stw, s.Stw);
        Assert.Equal(s.Truth.DepthBelowKeel, s.DepthBelowKeel);
        Assert.Equal(0.0, s.Truth.HeadingErrorDeg);
        Assert.Equal(0.0, s.Truth.PositionErrorM);
        Assert.Equal(s.Heading, s.Sensors.MagneticHeading, 9); // 磁羅經誤差預設 0
        Assert.Null(s.Alerts);
        Assert.Equal(Scenario.BnwasMode.Off, s.Bnwas!.Mode);
        Assert.False(s.Bnwas.Active);
        Assert.True(e.Sensors.State.IsNominal);
    }

    [Fact]
    public void GyroDriftAccumulatesWithoutAlertAndAutopilotFollowsIndicatedHeading()
    {
        var e = Engine();
        var events = TestData.Capture(e);
        Do(e, SimCommand.Autopilot(true, heading: 0));
        RunS(e, 60);
        Do(e, SimCommand.InjectFault("gyroDrift", new { rate_degPerMin = 1.0 }));
        RunS(e, 300);
        Assert.Equal(5.0, e.Sensors.GyroDriftDeg(e.Tick), 9); // 1°/分 × 5 分(封閉式,無累加誤差)
        var s = e.BuildState();
        // 自動舵把「指示航向」保持在 000°(P 控制追斜坡約落後 1–2°),真航向因此偏左約 5°(指示 = 真值 + 漂移)
        Assert.InRange(Units.WrapDeg180(s.Heading), -2.0, 2.0);
        Assert.InRange(Units.WrapDeg180(s.Truth!.Heading), -7.0, -3.0);
        Assert.Equal(Units.WrapDeg180(s.Heading - s.Truth.Heading), s.Truth.HeadingErrorDeg, 9);
        Assert.InRange(s.Truth.HeadingErrorDeg, 4.0, 6.0);
        Assert.True(s.Sensors!.HeadingValid);
        Assert.True(e.Autopilot.Enabled);
        // 無警報(規劃書 E08:漂移無警報;航向監視預設關閉)
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.AlertRaised);
        Assert.Null(s.Alerts);

        // 對照:無故障時真航向維持 000°
        var control = Engine();
        Do(control, SimCommand.Autopilot(true, heading: 0));
        TestData.RunTo(control, e.Time);
        Assert.InRange(Units.WrapDeg180(control.BuildState().Truth!.Heading), -1.0, 1.0);

        // 清除後指示值回到真值
        Do(e, SimCommand.ClearFault("gyroDrift"));
        var after = e.BuildState();
        Assert.Equal(after.Truth!.Heading, after.Heading);
        Assert.True(e.Sensors.State.IsNominal);
    }

    [Fact]
    public void GyroDriftReinjectionContinuesFromCurrentError()
    {
        var e = Engine();
        Do(e, SimCommand.InjectFault("gyroDrift", new { rate_degPerMin = 2.0 }));
        RunS(e, 60);
        Assert.Equal(2.0, e.Sensors.GyroDriftDeg(e.Tick), 9);
        Do(e, SimCommand.InjectFault("gyroDrift", new { rate_degPerMin = -1.0 }));
        RunS(e, 60);
        Assert.Equal(1.0, e.Sensors.GyroDriftDeg(e.Tick), 9);
        Do(e, SimCommand.InjectFault("gyroDrift")); // 預設 1°/分
        RunS(e, 30);
        Assert.Equal(1.5, e.Sensors.GyroDriftDeg(e.Tick), 9);
    }

    [Fact]
    public void GyroFreezeHoldsIndicatedHeadingWhileShipTurns()
    {
        var e = Engine();
        RunS(e, 10);
        var h0 = e.BuildState().Heading;
        Do(e, SimCommand.InjectFault("gyroFreeze"));
        Do(e, SimCommand.Rudder(20));
        RunS(e, 60);
        var s = e.BuildState();
        Assert.Equal(h0, s.Heading);
        Assert.True(s.Sensors!.HeadingValid); // 凍結不會自行標示無效
        Assert.True(Math.Abs(Units.WrapDeg180(s.Truth!.Heading - h0)) > 10);
        Assert.Null(s.Alerts);
    }

    [Fact]
    public void GyroFailureInvalidatesHeadingTripsAutopilotAndRejectsReEnable()
    {
        var e = Engine();
        var events = TestData.Capture(e);
        Do(e, SimCommand.Autopilot(true, heading: 10));
        RunS(e, 30);
        var held = e.BuildState().Heading;
        Do(e, SimCommand.InjectFault("gyroFailure"));
        Assert.False(e.Autopilot.Enabled);
        Assert.Equal(SteeringMode.Fu, e.SteeringMode);
        var trip = events.Single(x => x.Kind == SimEventKinds.AutopilotTripped);
        Assert.Equal("headingInvalid", trip.DataString("reason"));
        Assert.Equal(SimEventSources.Engine, trip.Source);
        Assert.Equal(e.Actuators.RudderDeg, e.Actuators.RudderOrderDeg); // 舵停在原處
        e.Step();
        var s = e.BuildState();
        Assert.False(s.Sensors!.HeadingValid);
        Assert.Equal(held, s.Heading);
        Assert.False(s.Autopilot!.Available);
        var alert = Assert.Single(s.Alerts!);
        Assert.Equal("gyroFailure", alert.Id);
        Assert.Equal(AlertPriority.Alarm, alert.Priority);
        Assert.Equal(AlertState.ActiveUnacknowledged, alert.State);
        Assert.True(alert.Audible);

        // 失效中不能再啟用自動舵(autopilot 與 steeringMode autopilot)
        Do(e, SimCommand.Autopilot(true));
        Do(e, SimCommand.SteeringMode(SteeringMode.Autopilot));
        Assert.False(e.Autopilot.Enabled);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.CommandRejected && x.DataString("reason") == CommandRejectReasons.AutopilotUnavailable));

        Do(e, SimCommand.ClearFault("gyroFailure"));
        e.Step();
        s = e.BuildState();
        Assert.True(s.Sensors!.HeadingValid);
        Assert.Equal(s.Truth!.Heading, s.Heading);
        Assert.True(s.Autopilot!.Available);
        Assert.Equal(AlertState.RectifiedUnacknowledged, Assert.Single(s.Alerts!).State);
    }

    [Fact]
    public void AutopilotFailureTripsAndRaisesAlarm()
    {
        var e = Engine();
        var events = TestData.Capture(e);
        Do(e, SimCommand.Autopilot(true, heading: 0));
        RunS(e, 5);
        Do(e, SimCommand.InjectFault("autopilotFailure"));
        e.Step();
        Assert.False(e.Autopilot.Enabled);
        Assert.Equal("autopilotFailure", events.Single(x => x.Kind == SimEventKinds.AutopilotTripped).DataString("reason"));
        Assert.Contains(events, x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "autopilotFailure");
        Assert.True(e.BuildState().Sensors!.HeadingValid);
        Do(e, SimCommand.Autopilot(true));
        Assert.False(e.Autopilot.Enabled);
    }

    [Fact]
    public void GpsJumpOffsetAndFailure()
    {
        var e = Engine();
        RunS(e, 5);
        Do(e, SimCommand.InjectFault("gpsJump", new { distance_m = 500, bearing_deg = 90 }));
        var s = e.BuildState();
        Assert.Equal(500.0, s.Pos.X - s.Truth!.Pos.X, 6);
        Assert.Equal(0.0, s.Pos.Y - s.Truth.Pos.Y, 6);
        Assert.Equal(500.0, s.Truth.PositionErrorM, 6);
        Assert.True(s.Pos.Lon > s.Truth.Pos.Lon);
        Assert.True(s.Sensors!.PositionValid);
        Do(e, SimCommand.ClearFault("gpsJump"));

        Do(e, SimCommand.InjectFault("gpsOffset", new { distance_m = 200, bearing_deg = 0, ramp_s = 60 }));
        RunS(e, 30);
        s = e.BuildState();
        Assert.Equal(100.0, s.Pos.Y - s.Truth!.Pos.Y, 6); // 漸進一半
        RunS(e, 40);
        s = e.BuildState();
        Assert.Equal(200.0, s.Pos.Y - s.Truth!.Pos.Y, 6);
        Do(e, SimCommand.ClearFault("gpsOffset"));

        var events = TestData.Capture(e);
        var before = e.BuildState();
        Do(e, SimCommand.InjectFault("gpsFailure"));
        RunS(e, 20);
        s = e.BuildState();
        Assert.False(s.Sensors!.PositionValid);
        Assert.Equal(before.Pos, s.Pos);      // 保持最後有效位置
        Assert.Equal(before.Sog, s.Sog);
        Assert.NotEqual(before.Truth!.Pos, s.Truth!.Pos);
        Assert.Contains(events, x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "gpsFailure" && x.DataString("priority") == "alarm");
    }

    [Fact]
    public void EchoSounderAndLogFailures()
    {
        var e = Engine();
        var events = TestData.Capture(e);
        RunS(e, 5);
        var d0 = e.BuildState().DepthBelowKeel;
        Do(e, SimCommand.InjectFault("echoSounderFailure"));
        Do(e, SimCommand.SetEnvironment(waterDepth: 20));
        RunS(e, 2);
        var s = e.BuildState();
        Assert.False(s.Sensors!.DepthValid);
        Assert.Equal(d0, s.DepthBelowKeel);
        Assert.True(s.Truth!.DepthBelowKeel < d0 - 5);
        Assert.Contains(events, x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "echoSounderFailure" && x.DataString("priority") == "warning");

        // 計程儀偏差模式:STW = 真值 × factor + bias(有效、無警報)
        Do(e, SimCommand.InjectFault("logFailure", new { bias_kn = 2.0 }));
        e.Step();
        s = e.BuildState();
        Assert.True(s.Sensors!.StwValid);
        Assert.Equal(s.Truth!.Stw + 2.0, s.Stw, 9);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "logFailure");
        // 改為無效模式(重複注入、無偏差參數):STW 保持、警報 warning
        Do(e, SimCommand.InjectFault("logFailure"));
        var held = e.BuildState().Stw;
        Do(e, SimCommand.Telegraph(TelegraphOrder.STOP));
        RunS(e, 30);
        s = e.BuildState();
        Assert.False(s.Sensors!.StwValid);
        Assert.Equal(held, s.Stw);
        Assert.True(s.Truth!.Stw < held - 0.3);
        Assert.Contains(events, x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "logFailure");
    }

    [Fact]
    public void SteeringFuOnlyJamsFollowUpButNfuStillWorks()
    {
        var e = Engine();
        var events = TestData.Capture(e);
        Do(e, SimCommand.InjectFault("steeringFuOnly"));
        Do(e, SimCommand.Rudder(20));
        RunS(e, 5);
        Assert.Equal(0.0, e.Actuators.RudderDeg, 9);
        Do(e, SimCommand.SteeringMode(SteeringMode.Nfu, direction: "starboard"));
        RunS(e, 3);
        Assert.True(e.Actuators.RudderDeg > 3.0);
        Assert.Contains(events, x => x.Kind == SimEventKinds.AlertRaised && x.ItemId == "steeringFuOnly");
    }

    [Fact]
    public void SteeringPumpFailureHalvesRudderRate()
    {
        var a = Engine();
        var b = Engine();
        Do(b, SimCommand.InjectFault("steeringPumpFailure"));
        Do(a, SimCommand.Rudder(35));
        Do(b, SimCommand.Rudder(35));
        a.Run(100);
        b.Run(100);
        Assert.True(a.Actuators.RudderDeg < 34.0, "2 s 內不應已到滿舵,否則比例無意義");
        Assert.Equal(a.Actuators.RudderDeg / 2.0, b.Actuators.RudderDeg, 6);
    }

    [Fact]
    public void SnapshotMidDriftRestoresBitExactly()
    {
        SimulationEngine Make()
        {
            var e = Engine();
            Do(e, SimCommand.Autopilot(true, heading: 5));
            Do(e, SimCommand.InjectFault("gyroDrift", new { rate_degPerMin = -1.5 }));
            Do(e, SimCommand.InjectFault("gpsOffset", new { distance_m = 300, bearing_deg = 200, ramp_s = 100 }));
            return e;
        }
        var reference = Make();
        TestData.RunTo(reference, 200);

        var a = Make();
        TestData.RunTo(a, 77.3);
        var snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
        Assert.Equal(3, snap.Version);
        Assert.NotNull(snap.Sensors);
        Assert.Equal(-1.5, snap.Sensors!.GyroDriftRateDegPerMin);
        var b = Engine();
        b.Restore(snap);
        TestData.RunTo(b, 200);
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal(ContractJson.Serialize(reference.BuildState()), ContractJson.Serialize(b.BuildState()));
    }

    [Fact]
    public void SensorBytesEnterHashOnlyWhenNotNominal()
    {
        var plain = Engine();
        var drifted = Engine();
        Do(drifted, SimCommand.InjectFault("gyroDrift"));
        plain.Run(50);
        drifted.Run(50);
        Assert.Equal(plain.Motion, drifted.Motion); // 未接自動舵:物理相同
        Assert.NotEqual(plain.StateHash, drifted.StateHash);
    }
}
