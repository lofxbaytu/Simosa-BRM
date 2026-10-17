using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using static SimosaBRM.SimCore.Tests.ExternalForceTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 外力模組審查發現的回歸測試(設計文件 physics-tugs-anchors-mooring.md 第 2.3、3.3、3.4、6 節):
/// 重複 letGo、絞盤停轉握持上限、standby 繞過換位、情境 initial.mode 與 modes、斷纜拖船的 status、tugLineBreak 未定義 id。
/// </summary>
public class ExternalForceReviewTests
{
    private const double Dt = 0.02;
    private static AnchorParticulars Fsb1Spec => AnchorParticulars.FromShip(TestData.Fsb1())!;

    // ------------------------------------------------------------------ 錨:重複 letGo

    [Fact]
    public void SecondLetGoWhileRunningIsRejectedAndChainNeverShortens()
    {
        var u = new AnchorUnit("port", Fsb1Spec, 5.7);
        var ship = new StateVector(0, 0, 0, 0, 0, 0);
        var ev = new List<ExternalEvent>();
        Assert.Null(u.Command("letGo", 110.0, ship, 30.0));
        for (var i = 0; i < 400; i++) u.PostStep(ship, 30.0, Dt, Seabed.Sand, 1, 0.5, 0, ev); // 8 s → 16 m,懸吊中(h ≈ 31.8 m)
        Assert.Equal(AnchorMode.Running, u.Mode);
        Assert.False(u.OnBottom);
        var l0 = u.Length;
        Assert.Equal("anchorAlreadyOut", u.Command("letGo", 2.0, ship, 30.0));
        Assert.Equal("anchorAlreadyOut", u.Command("letGo", null, ship, 30.0));
        u.PostStep(ship, 30.0, Dt, Seabed.Sand, 1, 0.5, 0, ev);
        Assert.True(u.Length >= l0, "送出中鏈長不可減少");

        // 送出分支的保底:即使目標比目前鏈長短(例如舊快照),鏈長也不會在一步內縮短
        u.TargetLength = 3.0;
        var l1 = u.Length;
        u.PostStep(ship, 30.0, Dt, Seabed.Sand, 1, 0.5, 0, ev);
        Assert.Equal(l1, u.Length);
        Assert.Equal(AnchorMode.Braked, u.Mode);
    }

    [Fact]
    public void LetGoAfterAweighOnlyPaysOutAndRejectsShorterTarget()
    {
        // 起錨到離底(懸吊、剎車):可再拋,但目標不可短於目前鏈長
        var u = new AnchorUnit("port", Fsb1Spec, 5.7);
        var ship = new StateVector(0, 0, 0, 0, 0, 0);
        u.Mode = AnchorMode.Braked;
        u.Length = 20.0;
        u.OnBottom = false;
        Assert.Equal("invalidTarget", u.Command("letGo", 5.0, ship, 30.0));
        Assert.Equal(20.0, u.Length);
        Assert.Null(u.Command("letGo", 60.0, ship, 30.0));
        var ev = new List<ExternalEvent>();
        var prev = u.Length;
        for (var i = 0; i < 1000; i++)
        {
            u.PostStep(ship, 30.0, Dt, Seabed.Sand, 1, 0.5, 0, ev);
            Assert.True(u.Length >= prev);
            prev = u.Length;
        }
        Assert.Equal(60.0, u.Length);
    }

    [Fact]
    public void EngineRepeatedLetGoEmitsSingleLetGoEvent()
    {
        var e = Mmg(Sc(depth: 30));
        var ev = TestData.Capture(e);
        Apply(e, SimCommand.Anchor("port", "letGo", targetShackles: 4));
        RunTo(e, 8);
        var a = e.External.Anchor("port")!;
        var l0 = a.Length;
        Apply(e, SimCommand.Anchor("port", "letGo", lengthM: 3));
        e.Step();
        Assert.True(a.Length >= l0);
        Assert.Equal("running", a.Status);
        Assert.Single(ev, x => x.Kind == SimEventKinds.AnchorLetGo);
        var rej = Assert.Single(ev, x => x.Kind == SimEventKinds.CommandRejected);
        Assert.Equal(CommandRejectReasons.AnchorAlreadyOut, rej.DataString("reason"));
    }

    // ------------------------------------------------------------------ 錨:絞盤停轉握持上限

    [Fact]
    public void StalledWindlassRendersChainAboveBrakeHolding()
    {
        var spec = Fsb1Spec;
        foreach (var mode in new[] { "brake", "heave" })
        {
            var u = new AnchorUnit("starboard", spec, 5.7);
            var ship = new StateVector(0, 0, 0, 0, 0, 0);
            u.Length = 137.5; u.Mode = AnchorMode.Braked; u.OnBottom = true;
            var hv = u.VerticalSpan(20.0);
            var chord = Math.Sqrt(u.Length * u.Length - hv * hv);
            var (he, hn) = BodyFrame.ToEnu(ship, u.HawseXb, u.HawseYb);
            (u.AnchorX, u.AnchorY) = (he, hn + chord + 0.5); // 拉直
            Assert.Null(u.Command(mode, null, ship, 20.0));
            var ev = new List<ExternalEvent>();
            var l0 = u.Length;
            for (var i = 0; i < 50; i++) u.PostStep(ship, 20.0, Dt, Seabed.Sand, 100.0, 0.5, 0.0, ev);
            var r = u.Solve(ship, 20.0, out _, out _);
            Assert.True(u.Length > l0, $"{mode}:張力超過剎車握持力時鏈應被拉出");
            Assert.True(u.Length - l0 <= 50 * AnchorUnit.SlipRateMps * Dt + 1e-9);
            Assert.True(r.TopTension <= 1.0001 * spec.BrakeHoldingN, $"{mode}:錨鏈孔張力 {r.TopTension / 1e3:F0} kN 不可高於剎車 {spec.BrakeHoldingN / 1e3:F0} kN");
            Assert.Single(ev, x => x.Kind == "anchorBrakeSlipping");
            if (mode == "heave")
            {
                Assert.Equal(AnchorMode.Heaving, u.Mode);
                Assert.True(u.Stalled);
                Assert.Single(ev, x => x.Kind == "anchorWindlassStalled");
            }
        }
    }

    [Fact]
    public void EngineStalledWindlassDoesNotHoldMoreThanBrake()
    {
        // 兩艘 50 t 拖船在艉向後拉、錨抓力倍率 20(錨不走):剎車與停轉絞盤的握持應相同(鏈同樣被拉出,張力不高於剎車情形)
        (double Length0, double Length, double MaxTopT, List<SimEvent> Events, AnchorUnit Anchor) Run(bool heave)
        {
            var e = Mmg(Sc("""
                tugs: [ { id: TG1, attach: aft, bollardPull_t: 50, riseTime_s: 5, initial: { mode: pull, direction_deg: 180, power_pct: 100 } },
                        { id: TG2, attach: aft, bollardPull_t: 50, riseTime_s: 5, initial: { mode: pull, direction_deg: 180, power_pct: 100 } } ]
                anchors: { starboard: { length_shackles: 5 }, holdingFactor: 20 }
                """, depth: 20));
            var ev = TestData.Capture(e);
            var a = e.External.Anchor("starboard")!;
            if (heave) Apply(e, SimCommand.Anchor("starboard", "heave", targetShackles: 4));
            var l0 = a.Length;
            double maxTop = 0;
            for (var i = 0; i < 50 * 90; i++)
            {
                e.Step();
                maxTop = Math.Max(maxTop, e.BuildState().Anchors!.First(x => x.Side == "starboard").TensionT);
            }
            return (l0, a.Length, maxTop, ev, a);
        }
        var brake = Run(heave: false);
        var heave = Run(heave: true);
        Assert.False(brake.Anchor.FullyOut);
        Assert.Contains(heave.Events, x => x.Kind == SimEventKinds.AnchorWindlassStalled);
        Assert.Contains(heave.Events, x => x.Kind == SimEventKinds.AnchorBrakeSlipping);
        Assert.Equal(AnchorMode.Heaving, heave.Anchor.Mode);
        Assert.True(heave.Length > heave.Length0 + 50.0, $"停轉的絞盤應被拉出鏈:{heave.Length0:F1} → {heave.Length:F1} m");
        Assert.InRange(heave.Length, brake.Length - 2.0, brake.Length + 2.0);
        Assert.True(heave.MaxTopT <= brake.MaxTopT + 0.5, $"停轉絞盤最高張力 {heave.MaxTopT:F1} t 不可高於剎車情形 {brake.MaxTopT:F1} t");
    }

    // ------------------------------------------------------------------ 拖船:standby 繞過換位

    private static TugUnit Tug(Action<ScenarioTug>? edit = null)
    {
        var t = new ScenarioTug { Id = "TG1", BollardPullT = 40, RiseTimeS = 30, TurnRateDegPerSec = 3, ModeChangeS = 60, ConnectS = 90 };
        edit?.Invoke(t);
        return new TugUnit(t, TestData.Fsb1(), Dt);
    }

    private static void Run(TugUnit t, double seconds)
    {
        for (var i = 0; i < (int)Math.Round(seconds / Dt); i++) t.PreStep(Dt, 0.0);
    }

    [Fact]
    public void StandbyBetweenPushAndPullStillRequiresModeChange()
    {
        var t = Tug();
        Assert.Null(t.Command(TugMode.Push, 270, 50));
        Run(t, 5);
        Assert.Equal(TugStatus.Working, t.Status);
        Assert.Null(t.Command(TugMode.Standby, null, null));
        Assert.Equal(TugStatus.Standby, t.Status);
        Assert.Null(t.Command(TugMode.Pull, 90, 50));
        Assert.Equal(TugStatus.Repositioning, t.Status);
        Assert.Equal(3000, t.TimerTicks);
        Run(t, 60.0);
        Assert.Equal(TugStatus.Working, t.Status);
        Assert.Equal(TugMode.Pull, t.LastWorkingMode);

        // 換位中下 standby 不可縮短換位時間
        Assert.Null(t.Command(TugMode.Push, 270, 50));
        Assert.Equal(TugStatus.Repositioning, t.Status);
        Run(t, 10.0);
        Assert.Null(t.Command(TugMode.Standby, null, null));
        Assert.Equal(TugStatus.Repositioning, t.Status);
        Assert.Null(t.Command(TugMode.Push, 270, 50));
        Assert.Equal(TugStatus.Repositioning, t.Status);
        Run(t, 49.0);
        Assert.Equal(TugStatus.Repositioning, t.Status);
        Run(t, 1.1);
        Assert.Equal(TugStatus.Working, t.Status);

        // 同側待命後恢復:不需換位
        Assert.Null(t.Command(TugMode.Standby, null, null));
        Assert.Null(t.Command(TugMode.Push, 270, 50));
        Assert.Equal(TugStatus.Working, t.Status);
    }

    [Fact]
    public void EngineStandbyBypassIsClosedAndSideIsInSnapshotAndHash()
    {
        var sc = Sc("tugs: [ { id: TG1, attach: fwdStbd, modeChange_s: 60, initial: { mode: push, direction_deg: 270, power_pct: 50 } } ]");
        var e = Mmg(sc);
        RunTo(e, 5);
        Apply(e, SimCommand.TugControl("TG1", "standby"));
        Apply(e, SimCommand.TugControl("TG1", "pull", 90, 50));
        var t = e.External.Tug("TG1")!;
        Assert.Equal(TugStatus.Repositioning, t.Status);
        Assert.Equal(3000, t.TimerTicks);

        // 最後工作側進快照(JSON 往返)與雜湊
        var snap = EngineSnapshot.FromJson(e.CreateSnapshot().ToJson());
        Assert.Equal(TugMode.Pull, snap.External!.Tugs![0].LastWorkingMode);
        var a = Mmg(sc);
        RunTo(a, 5);
        Apply(a, SimCommand.TugControl("TG1", "standby"));
        var b = Mmg(sc);
        b.Restore(a.CreateSnapshot());
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(TugMode.Push, b.External.Tug("TG1")!.LastWorkingMode);
        b.External.Tug("TG1")!.LastWorkingMode = TugMode.Standby;
        a.Step(); b.Step();
        Assert.NotEqual(a.StateHash, b.StateHash);
    }

    // ------------------------------------------------------------------ 情境:initial.mode 不在 modes 內

    [Fact]
    public void ScenarioInitialModeMustBeAllowedByModes()
    {
        Assert.Throws<InvalidDataException>(() => Sc("tugs: [ { id: TG1, modes: [push], initial: { mode: pull, direction_deg: 180, power_pct: 100 } } ]"));
        Assert.Throws<InvalidDataException>(() => Sc("tugs: [ { id: TG1, modes: [pull], initial: { mode: push, direction_deg: 90, power_pct: 50 } } ]"));
        // standby / free 不受 modes 限制;modes 省略 = 兩者皆可
        Sc("tugs: [ { id: TG1, modes: [push], initial: { mode: standby } } ]");
        Sc("tugs: [ { id: TG1, modes: [push], initial: { mode: free } } ]");
        Sc("tugs: [ { id: TG1, modes: [push], initial: { mode: push, power_pct: 30 } } ]");
        Sc("tugs: [ { id: TG1, initial: { mode: pull, power_pct: 30 } } ]");
    }

    // ------------------------------------------------------------------ 拖船:斷纜後只調出力

    [Fact]
    public void PowerOnlyCommandKeepsPartedStatus()
    {
        var e = Mmg(Sc("tugs: [ { id: TG1, attach: aft, initial: { mode: pull, direction_deg: 180, power_pct: 50 } } ]"));
        var ev = TestData.Capture(e);
        RunTo(e, 2);
        Apply(e, SimCommand.InjectFault(FaultNames.TugLineBreak));
        var t = e.External.Tug("TG1")!;
        Assert.Equal(TugStatus.Parted, t.Status);
        Apply(e, SimCommand.TugControl("TG1", powerPct: 80));
        Apply(e, SimCommand.TugControl("TG1", directionDeg: 200));
        Apply(e, SimCommand.TugControl("TG1", "free"));
        Assert.Equal(TugStatus.Parted, t.Status);
        Assert.True(t.LineParted);
        e.Step();
        Assert.Equal("parted", e.BuildState().Tugs![0].Status);
        Assert.Equal(0.0, e.BuildState().Tugs![0].ForceT);
        Assert.All(ev.Where(x => x.Kind == SimEventKinds.TugCommand), x => Assert.Equal("parted", x.DataString("status")));
        // 清除後 parted → free
        Apply(e, SimCommand.ClearFault(FaultNames.TugLineBreak));
        Assert.Equal(TugStatus.Free, t.Status);
    }

    // ------------------------------------------------------------------ 故障:tugLineBreak 未定義 id

    [Fact]
    public void TugLineBreakWithUnknownIdIsRejected()
    {
        var e = Mmg(Sc("tugs: [ { id: TG1, attach: aft, initial: { mode: pull, direction_deg: 180, power_pct: 50 } } ]"));
        var ev = TestData.Capture(e);
        RunTo(e, 2);
        Apply(e, SimCommand.InjectFault(FaultNames.TugLineBreak, new { id = "TGX" }));
        Assert.DoesNotContain(FaultNames.TugLineBreak, e.Faults);
        Assert.DoesNotContain(ev, x => x.Kind == SimEventKinds.FaultInjected);
        var rej = Assert.Single(ev, x => x.Kind == SimEventKinds.CommandRejected);
        Assert.Equal(CommandRejectReasons.UnknownTug, rej.DataString("reason"));
        Assert.Equal("injectFault", rej.DataString("command"));
        Assert.Equal(TugStatus.Working, e.External.Tug("TG1")!.Status);

        // 之後以正確 id 注入:alreadyActive = false
        Apply(e, SimCommand.InjectFault(FaultNames.TugLineBreak, new { id = "tg1" }));
        var inj = Assert.Single(ev, x => x.Kind == SimEventKinds.FaultInjected);
        Assert.False(inj.DataBool("alreadyActive"));
        Assert.Single(ev, x => x.Kind == SimEventKinds.TugLineParted);
    }

    [Fact]
    public void TugLineBreakWithIdOnUnsupportedModelIsRejected()
    {
        var sc = Sc("tugs: [ { id: TG1, attach: aft } ]");
        var n = new SimulationEngine(TestData.Fsb1(), sc, (s, l) => new NoExternalDynamics(new PlaceholderDynamics(s, l)));
        var ev = TestData.Capture(n);
        Apply(n, SimCommand.InjectFault(FaultNames.TugLineBreak, new { id = "TG1" }));
        Assert.DoesNotContain(FaultNames.TugLineBreak, n.Faults);
        Assert.Equal(CommandRejectReasons.ExternalForcesUnsupported, Assert.Single(ev, x => x.Kind == SimEventKinds.CommandRejected).DataString("reason"));
        // 未指定 id 時維持原行為(故障列出,無拖船可斷)
        Apply(n, SimCommand.InjectFault(FaultNames.TugLineBreak));
        Assert.Contains(FaultNames.TugLineBreak, n.Faults);
    }
}
