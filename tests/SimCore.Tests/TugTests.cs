using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using static SimosaBRM.SimCore.Tests.ExternalForceTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>拖船(設計文件 physics-tugs-anchors-mooring.md 第 2 節):出力一階上升、方向迴轉速率、速度效能遞減、推拉換位、斷纜。</summary>
public class TugTests
{
    private const double Dt = 0.02;

    private static TugUnit Unit(Action<ScenarioTug>? edit = null)
    {
        var t = new ScenarioTug { Id = "TG1", BollardPullT = 40, RiseTimeS = 30, TurnRateDegPerSec = 3, ModeChangeS = 60, ConnectS = 90 };
        edit?.Invoke(t);
        return new TugUnit(t, TestData.Fsb1(), Dt);
    }

    private static void Run(TugUnit t, double seconds, double speedMps = 0.0)
    {
        for (var i = 0; i < (int)Math.Round(seconds / Dt); i++) t.PreStep(Dt, speedMps);
    }

    [Fact]
    public void ForceRisesFirstOrderWithTimeConstant()
    {
        var t = Unit();
        Assert.Null(t.Command(TugMode.Push, 90, 100));
        Assert.Equal(TugStatus.Working, t.Status);
        Run(t, 30.0);
        // 離散一階:F_n = F_t (1 − (1 − α)^n),α = 1 − e^(−dt/τ) → 30 s 時 = F_t (1 − e^(−1))
        Assert.Equal(40.0 * (1.0 - Math.Exp(-1.0)), t.ForceN / TugUnit.TonneForce, 9);
        Run(t, 270.0);
        Assert.Equal(40.0 * (1.0 - Math.Exp(-10.0)), t.ForceN / TugUnit.TonneForce, 9);
        // 降到 50 %:同樣一階
        t.Command(TugMode.Push, null, 50);
        Run(t, 30.0);
        Assert.Equal(20.0 + 20.0 * Math.Exp(-1.0), t.ForceN / TugUnit.TonneForce, 2); // 起點 ≈ 40 t
    }

    [Fact]
    public void DirectionChangesAtLimitedRate()
    {
        var t = Unit();
        t.Command(TugMode.Pull, 90, 100);
        t.Command(TugMode.Pull, 180, null);
        Run(t, 10.0);
        Assert.Equal(120.0, Units.RadToDeg(t.DirectionRad), 6);
        Run(t, 20.0);
        Assert.Equal(180.0, Math.Abs(Units.RadToDeg(t.DirectionRad)), 6);
        // 最短弧:180 → 270(= −90)走 90° 經 225°
        t.Command(TugMode.Pull, 270, null);
        Run(t, 15.0);
        Assert.Equal(225.0, Units.NormalizeHeadingDeg(Units.RadToDeg(t.DirectionRad)), 6);

        // 未給 turnRate:= 繞行速度 / 纜長(5 kn / 40 m ≈ 3.68 °/s)
        var d = Unit(x => { x.TurnRateDegPerSec = null; x.LineLengthM = 40; x.RepositionSpeedKn = 5; });
        Assert.Equal(Units.RadToDeg(Units.KnToMps(5) / 40.0), Units.RadToDeg(d.TurnRateRadPerSec), 9);
    }

    [Fact]
    public void EfficiencyDropsWithShipSpeed()
    {
        var t = Unit();
        Assert.Equal(1.0, t.SpeedEfficiency(TugMode.Push, Units.KnToMps(2.9)));
        Assert.Equal(0.5, t.SpeedEfficiency(TugMode.Push, Units.KnToMps(5.0)), 9);
        Assert.Equal(0.2, t.SpeedEfficiency(TugMode.Push, Units.KnToMps(7.0)), 9);
        Assert.Equal(1.0, t.SpeedEfficiency(TugMode.Pull, Units.KnToMps(4.0)));
        Assert.Equal(0.5, t.SpeedEfficiency(TugMode.Pull, Units.KnToMps(7.0)), 9);
        // 穩態出力 = 拖力 × 效能
        t.Command(TugMode.Push, 90, 100);
        Run(t, 600.0, Units.KnToMps(5.0));
        Assert.Equal(20.0, t.ForceN / TugUnit.TonneForce, 3);
    }

    [Fact]
    public void ModeChangeAndConnectTakeTime()
    {
        var t = Unit();
        t.Command(TugMode.Push, 90, 100);
        Run(t, 300.0);
        t.Command(TugMode.Pull, 270, null);
        Assert.Equal(TugStatus.Repositioning, t.Status);
        Run(t, 59.0);
        Assert.Equal(TugStatus.Repositioning, t.Status);
        Assert.True(t.ForceN / TugUnit.TonneForce < 40.0 * Math.Exp(-59.0 / 30.0) + 0.01); // 換位中無出力(衰減)
        Run(t, 1.1);
        Assert.Equal(TugStatus.Working, t.Status);
        t.Command(TugMode.Free, null, null);
        Assert.Equal(0.0, t.ForceN);
        t.Command(TugMode.Pull, null, null);
        Assert.Equal(TugStatus.Connecting, t.Status);
        Run(t, 90.0);
        Assert.Equal(TugStatus.Working, t.Status);
        // 不具此模式
        var pushOnly = Unit(x => x.Modes = new List<string> { "push" });
        Assert.Equal("tugModeUnavailable", pushOnly.Command(TugMode.Pull, 0, 50));
    }

    private const string TwoTugs = """
        tugs:
          - { id: TG1, name: 麥寮1號, bollardPull_t: 40, attach: fwd, riseTime_s: 30, turnRate_degPerSec: 3, initial: { mode: standby, direction_deg: 90 } }
          - { id: TG2, bollardPull_t: 40, attach: aft, riseTime_s: 30, initial: { mode: standby, direction_deg: 90 } }
        """;

    [Fact]
    public void BowTugPushTurnsBowAndPushesShipSideways()
    {
        var e = Mmg(Sc(TwoTugs));
        Assert.True(e.External.Active);
        var events = TestData.Capture(e);
        Apply(e, SimCommand.TugControl("TG1", "push", 90, 100));
        RunTo(e, 60);
        Assert.True(e.Motion.R > 0.0, "艏拖船向右推 → 艏向右(r > 0)");
        Assert.True(e.Motion.V > 0.0, "向右舷平移(v > 0)");
        var st = e.BuildState();
        var tg1 = st.Tugs!.Single(t => t.Id == "TG1");
        Assert.Equal("push", tg1.Mode);
        Assert.Equal("working", tg1.Status);
        Assert.InRange(tg1.ForceT, 40 * (1 - Math.Exp(-2.0)) * 0.98, 40.0);
        Assert.Equal(90.0, tg1.DirectionDeg, 6);
        Assert.Equal("standby", st.Tugs!.Single(t => t.Id == "TG2").Status);
        var cmd = Assert.Single(events, x => x.Kind == SimEventKinds.TugCommand);
        Assert.Equal("TG1", cmd.ItemId);
        Assert.Equal("push", cmd.DataString("mode"));

        // 兩艘同向推 → 純平移(艏搖小)
        var b = Mmg(Sc(TwoTugs));
        Apply(b, SimCommand.TugControl("TG1", "push", 90, 100));
        Apply(b, SimCommand.TugControl("TG2", "push", 90, 100));
        RunTo(b, 60);
        Assert.True(b.Motion.V > e.Motion.V);
        Assert.True(Math.Abs(b.Motion.R) < Math.Abs(e.Motion.R) * 0.5);
    }

    [Fact]
    public void FastShipReducesPushForce()
    {
        var e = Mmg(Sc(TwoTugs, speedKn: 8.0, telegraph: "HAH"));
        Apply(e, SimCommand.TugControl("TG1", "push", 90, 100));
        RunTo(e, 200);
        var tg1 = e.BuildState().Tugs!.Single(t => t.Id == "TG1");
        Assert.True(tg1.Efficiency < 0.35, $"8 kn 推頂效能應大幅下降,實際 {tg1.Efficiency}");
        Assert.True(tg1.ForceT < 15.0);
    }

    [Fact]
    public void LineBreakFaultPartsPullingTug()
    {
        var e = Mmg(Sc(TwoTugs));
        var events = TestData.Capture(e);
        Apply(e, SimCommand.TugControl("TG2", "pull", 180, 100));
        Apply(e, SimCommand.TugControl("TG1", "push", 90, 50));
        RunTo(e, 120);
        Assert.True(e.BuildState().Tugs!.Single(t => t.Id == "TG2").ForceT > 35.0);
        Apply(e, SimCommand.InjectFault(FaultNames.TugLineBreak, new { id = "TG2" }));
        var parted = Assert.Single(events, x => x.Kind == SimEventKinds.TugLineParted);
        Assert.Equal("TG2", parted.ItemId);
        Assert.True(parted.DataNumber("force_t") > 35.0);
        e.Step();
        var st = e.BuildState().Tugs!;
        var tg2 = st.Single(t => t.Id == "TG2");
        Assert.Equal(0.0, tg2.ForceT);
        Assert.Equal("parted", tg2.Status);
        Assert.True(tg2.LineParted);
        Assert.Equal("working", st.Single(t => t.Id == "TG1").Status); // 推頂者不受影響
        Assert.Contains(FaultNames.TugLineBreak, e.Faults);

        // 故障中不能拉
        Apply(e, SimCommand.TugControl("TG2", "pull", 180, 100));
        var rej = Assert.Single(events, x => x.Kind == SimEventKinds.CommandRejected);
        Assert.Equal(CommandRejectReasons.TugLineParted, rej.DataString("reason"));

        // 清除後重新帶纜:connect_s(預設 90 s)後 working
        Apply(e, SimCommand.ClearFault(FaultNames.TugLineBreak));
        Assert.Equal("free", e.BuildState().Tugs!.Single(t => t.Id == "TG2").Status);
        Apply(e, SimCommand.TugControl("TG2", "pull", 180, 100));
        Assert.Equal("connecting", e.BuildState().Tugs!.Single(t => t.Id == "TG2").Status);
        RunTo(e, e.Time + 91);
        Assert.Equal("working", e.BuildState().Tugs!.Single(t => t.Id == "TG2").Status);

        // 未指定 id:斷第一艘拉中者
        Apply(e, SimCommand.InjectFault(FaultNames.TugLineBreak));
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.TugLineParted));
        Assert.Equal("TG2", events.Last(x => x.Kind == SimEventKinds.TugLineParted).ItemId);
    }

    [Fact]
    public void UnknownTugAndModeAreRejected()
    {
        var e = Mmg(Sc(TwoTugs));
        var events = TestData.Capture(e);
        Apply(e, SimCommand.TugControl("TGX", "push", 90, 100));
        Apply(e, SimCommand.TugControl("TG1", "tow", 90, 100));
        Apply(e, SimCommand.TugControl("TG1", "push", 90, 150));
        Assert.Equal(new[] { CommandRejectReasons.UnknownTug, CommandRejectReasons.UnknownTugMode, CommandRejectReasons.InvalidPower },
            events.Where(x => x.Kind == SimEventKinds.CommandRejected).Select(x => x.DataString("reason")));
    }

    [Fact]
    public void ScenarioTugValidation()
    {
        Assert.Throws<InvalidDataException>(() => Sc("tugs: [ { id: A, bollardPull_t: 0 } ]"));
        Assert.Throws<InvalidDataException>(() => Sc("tugs: [ { id: A }, { id: a } ]"));
        Assert.Throws<InvalidDataException>(() => Sc("tugs: [ { id: A, attach: mast } ]"));
        Assert.Throws<InvalidDataException>(() => Sc("tugs: [ { id: A, modes: [tow] } ]"));
        var ok = Sc("tugs: [ { id: A, attach: { x: 30, y: -5 }, initial: { mode: pull, direction_deg: 200, power_pct: 40 } } ]");
        var tug = new TugUnit(ok.Tugs![0], TestData.Fsb1(), Dt);
        Assert.Equal((30.0, -5.0), (tug.AttachX, tug.AttachY));
        Assert.Equal(TugStatus.Working, tug.Status);
        Assert.Equal(16.0, tug.ForceN / TugUnit.TonneForce, 9); // 初始已在出力中:40 t × 40 %
        // 情境 JSON 往返(紀錄 header)
        var back = Scenario.Scenario.FromJson(ok.ToJson());
        Assert.Equal(30.0, back.Tugs![0].Attach!.X);
        Assert.Equal("pull", back.Tugs![0].Initial!.Mode);
    }
}
