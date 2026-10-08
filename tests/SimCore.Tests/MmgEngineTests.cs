using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using Xunit;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// MMG 模型與引擎整合:確定性、快照/還原(含主機換向狀態機)、紀錄→重播、致動器狀態機行為,
/// 以及移植自 Python tests/test_physics_review.py 的力模型檢查(倒退阻力方向、純橫移、零速艏搖阻尼、x_G 運動方程式、
/// 倒車橫向力、倒退中正車的舵流入、Blendermann 風)。
/// </summary>
public class MmgEngineTests
{
    private static SimulationEngine BaselineMmgEngine(int? seed = null)
    {
        var sc = TestData.Baseline();
        if (seed is { } s) sc.Seed = s;
        return new SimulationEngine(TestData.Fsb1(), sc, (ship, loading) => MmgDynamics.Load(TestData.Root, ship, loading));
    }

    [Fact]
    public void BaselineScenarioRunsWithMmgAndIsDeterministic()
    {
        var a = BaselineMmgEngine();
        var b = BaselineMmgEngine();
        Assert.Equal("mmg/1", a.Dynamics.ModelName);
        Assert.Equal(LoadingCondition.Ballast, a.Loading);
        EngineDeterminismTests.RunScript(a, 2000);
        EngineDeterminismTests.RunScript(b, 2000);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(a.Motion, b.Motion);
        var s = a.BuildState();
        Assert.True(double.IsFinite(s.Heading) && double.IsFinite(s.Stw) && s.Stw > 5.0, $"MMG 壓載 HAH 應持續前進:{s.Stw:F2} kn");
        Assert.True(s.Squat > 0.0 && s.Squat < 0.5, $"ICORELS squat {s.Squat:F3} m");
    }

    [Fact]
    public void SnapshotDuringReversalRestoresEngineStateMachine()
    {
        // 參考:14.5 kn → EFAS,連續跑 400 s(換向在約 110 s 後)
        var reference = MmgEngine(14.5);
        reference.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        reference.Run((int)(400.0 / Dt));

        // 60 s 時快照(換向中:燃油切斷、軸轉速衰減),JSON 往返後還原到新引擎並接續
        var a = MmgEngine(14.5);
        a.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        a.Run((int)(60.0 / Dt));
        Assert.Equal(EngineMode.Reversing, a.Actuators.EngineMode);
        Assert.True(a.Actuators.Rpm > 0 && a.Actuators.Rpm < 100, $"換向中軸轉速衰減:{a.Actuators.Rpm:F1}");
        Assert.Equal(EngineRunState.Starting, a.BuildState().Engine!.State);
        var snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
        Assert.Equal(EngineMode.Reversing, snap.Actuators.EngineMode);
        Assert.InRange(snap.Actuators.EngineTimerSec, 59.9, 60.1);

        var b = MmgEngine(14.5);
        b.Restore(snap);
        Assert.Equal(a.StateHash, b.StateHash);
        b.Run((int)(340.0 / Dt));
        Assert.Equal(reference.StateHash, b.StateHash);
        Assert.Equal(reference.Motion, b.Motion);
        Assert.True(reference.Actuators.Rpm < -100, $"400 s 時應已倒車:{reference.Actuators.Rpm:F1} rpm");
        Assert.Equal(EngineMode.Run, reference.Actuators.EngineMode);
    }

    [Fact]
    public void RecordAndReplayWithMmg()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"mmg-replay-{Guid.NewGuid():N}.jsonl");
        var engine = BaselineMmgEngine();
        string hash;
        using (var writer = new RecordWriter(path, engine))
        {
            EngineDeterminismTests.RunScript(engine, 1500);
            hash = writer.Close().StateHash;
        }
        var result = Replayer.Replay(path, TestData.Ship, (s, l) => MmgDynamics.Load(TestData.Root, s, l));
        Assert.True(result.IntegrityOk);
        Assert.Equal("mmg/1", result.DynamicsRecorded);
        Assert.Null(result.Warning);
        Assert.Equal(hash, result.ActualHash);
        Assert.True(result.HashMatches);
        File.Delete(path);
    }

    [Fact]
    public void EngineStateMachine_CrashStopSequence()
    {
        var e = MmgEngine(14.5);
        var p = e.Actuators.Parameters;
        Assert.Equal(EngineMode.Run, e.Actuators.EngineMode);
        Assert.InRange(e.Actuators.Rpm, 182.0, 182.5); // 14.5 kn 的直航平衡轉速(Python 182.197)
        Assert.Equal(TelegraphOrder.NAVF, e.Telegraph);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.EFAS));
        e.Run(50); // 1 s
        Assert.Equal(EngineMode.Reversing, e.Actuators.EngineMode);
        Assert.Equal(0.0, e.Actuators.RpmTarget);
        Assert.Equal(p.ShaftStopTimeConstantSec, e.Actuators.RpmTau);
        // 速率限制 3 rpm/s:1 s 後約 179 rpm
        Assert.InRange(e.Actuators.Rpm, 182.2 - 3.1, 182.2 - 2.9);
        // reversalDelay 110 s 前不得反向點火
        e.Run((int)(100.0 / Dt));
        Assert.Equal(EngineMode.Reversing, e.Actuators.EngineMode);
        Assert.True(e.Actuators.Rpm >= 0.0);
        // 逾時且軸轉速低於 5 % MCR 後反向;Python 參考 astern start ≈ 110.6 s
        var asternStart = double.NaN;
        while (e.Time < 200.0)
        {
            e.Step();
            if (double.IsNaN(asternStart) && e.Actuators.Rpm < 0.0) asternStart = e.Time;
        }
        Assert.InRange(asternStart, 110.0, 112.0);
        Assert.Equal(EngineMode.Run, e.Actuators.EngineMode);
        Assert.InRange(e.Actuators.Rpm, -150.5, -140.0);
        Assert.Equal(EngineRunState.Running, e.BuildState().Engine!.State);
    }

    [Fact]
    public void EngineStateMachine_StopThenStartDelay()
    {
        var e = MmgEngine(14.5);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
        e.Run((int)(1.0 / Dt));
        Assert.Equal(EngineMode.Stopping, e.Actuators.EngineMode);
        e.Run((int)(300.0 / Dt));
        Assert.Equal(EngineMode.Stopped, e.Actuators.EngineMode);
        Assert.True(Math.Abs(e.Actuators.Rpm) < 0.02 * 183.0);
        Assert.Equal(EngineRunState.Stopped, e.BuildState().Engine!.State);
        // 重新起動:startDelay 5 s 內轉速目標為 0
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH));
        e.Run((int)(3.0 / Dt));
        Assert.Equal(EngineMode.Starting, e.Actuators.EngineMode);
        Assert.Equal(EngineRunState.Starting, e.BuildState().Engine!.State);
        e.Run((int)(30.0 / Dt));
        Assert.Equal(EngineMode.Run, e.Actuators.EngineMode);
        Assert.InRange(e.Actuators.Rpm, 60.0, 70.5);
    }

    [Fact]
    public void RudderUsesCoefficientRateAndSchillingLimit()
    {
        var e = MmgEngine(10.0);
        e.Enqueue(SimCommand.Rudder(70.0)); // Schilling 舵 70°
        e.Run((int)(5.0 / Dt));
        // 3.646 °/s(70° 行程 / 19.2 s),一階項 τ = 1 s 在遠離舵令時被速率限制飽和
        Assert.InRange(e.Actuators.RudderDeg, 5.0 * 3.6458 - 0.05, 5.0 * 3.6458 + 0.05);
        e.Run((int)(30.0 / Dt));
        Assert.InRange(e.Actuators.RudderDeg, 69.9, 70.0);
        e.Enqueue(SimCommand.Rudder(90.0));
        e.ProcessPendingCommands();
        Assert.Equal(70.0, e.Actuators.RudderOrderDeg);
    }

    [Fact]
    public void FaultsStillAffectMmgActuators()
    {
        var e = MmgEngine(10.0);
        e.Enqueue(SimCommand.Rudder(10));
        e.Run((int)(10.0 / Dt));
        Assert.InRange(e.Actuators.RudderDeg, 9.9, 10.0);
        e.Enqueue(SimCommand.InjectFault(FaultNames.SteeringGear));
        e.Enqueue(SimCommand.Rudder(-10));
        e.Run((int)(10.0 / Dt));
        Assert.InRange(e.Actuators.RudderDeg, 9.9, 10.0); // 卡舵
        e.Enqueue(SimCommand.InjectFault(FaultNames.MainEngine));
        e.Run((int)(200.0 / Dt));
        var s = e.BuildState();
        Assert.Equal(EngineRunState.Failed, s.Engine!.State);
        Assert.True(Math.Abs(s.Rpm) < 1.0);
        Assert.True(s.Stw < 10.0);
    }

    // ------------------------------------------------------------------ 力模型(test_physics_review 移植)

    private static MmgShallowFactors Deep(MmgDynamics d) => d.ShallowFactors(double.PositiveInfinity);

    [Fact]
    public void ResistanceOpposesAsternMotion()
    {
        var d = Fsb1FullDynamics();
        var f = Deep(d);
        var ahead = d.Forces(3.0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, f);
        var astern = d.Forces(-3.0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, f);
        Assert.True(ahead.XH < 0.0);
        Assert.True(astern.XH > 0.0, "倒退時船體阻力應向前");
        Assert.Equal(-ahead.XH, astern.XH, 6);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void PureSwayGivesNoSurgeForceAndNoYawMoment(double v)
    {
        var d = Fsb1FullDynamics();
        var r = d.Forces(0.0, v, 0, 0, 0, 0, 0, 0, 0, 0, 0, Deep(d));
        Assert.True(Math.Abs(r.XH) < 1e-6 * Math.Abs(r.YH));
        Assert.True(r.YH < 0.0);
        var expected = -0.5 * d.Rho * d.Draft * d.L * d.Coefficients.Hull.CrossFlow.Cd * v * v; // 純橫移 = 截面橫流阻力
        Assert.Equal(expected, r.YH, 6);
        Assert.True(Math.Abs(r.NH) < 1e-6 * Math.Abs(r.YH) * d.L);
    }

    [Fact]
    public void PureYawAtZeroSpeedHasDamping()
    {
        var d = Fsb1FullDynamics();
        var f = Deep(d);
        var prev = 0.0;
        foreach (var r in new[] { 0.003, 0.006, 0.012 })
        {
            var n = d.Forces(0, 0, r, 0, 0, 0, 0, 0, 0, 0, 0, f).N;
            Assert.True(n < prev, "零速純艏搖必須有阻尼力矩");
            prev = n;
        }
        var nNeg = d.Forces(0, 0, -0.006, 0, 0, 0, 0, 0, 0, 0, 0, f).N;
        var nPos = d.Forces(0, 0, 0.006, 0, 0, 0, 0, 0, 0, 0, 0, f).N;
        Assert.Equal(-nPos, nNeg, 6);
        var expected = -0.5 * d.Rho * d.Draft * d.Coefficients.Hull.CrossFlow.Cd * 0.006 * 0.006 * 0.030625 * Math.Pow(d.L, 4); // 10 條帶 ∫x|x|x dx
        Assert.Equal(expected, nPos, 3);
    }

    [Fact]
    public void EquationsOfMotionWithXgMatchPaper()
    {
        var c = MmgCoefficients.FromJson(Fsb1Full.ToJson());
        c.Reference.XG_m = 4.0;
        var d = new MmgDynamics(TestData.Fsb1(), LoadingCondition.Full, c);
        var f = Deep(d);
        double u = 5.0, v = -0.6, r = 0.012;
        var delta = Units.DegToRad(15.0);
        var fo = d.Forces(u, v, r, delta, 2.5, 0, 0, 0, 0, 0, 0, f);
        var (du, dv, dr) = d.Accelerations(u, v, r, fo.X, fo.Y, fo.N, f);
        var (m, mx, my, xg) = (d.M, f.Mx, f.My, d.XG);
        Assert.Equal(fo.X, (m + mx) * du - (m + my) * v * r - xg * m * r * r, 3);
        Assert.Equal(fo.Y, (m + my) * dv + (m + mx) * u * r + xg * m * dr, 3);
        Assert.Equal(fo.N, (d.Izz + xg * xg * m + f.Jzz) * dr + xg * m * (dv + u * r), 2);
    }

    [Fact]
    public void AsternPropellerSideForceBowToStarboard()
    {
        var d = Fsb1FullDynamics();
        var r = d.Forces(3.0, 0, 0, 0, -2.0, 0, 0, 0, 0, 0, 0, Deep(d));
        Assert.True(r.YP < 0.0 && r.NP > 0.0, "右旋槳倒車:艉向左、艏向右");
    }

    [Fact]
    public void RudderInflowPositiveWhenMovingAsternWithAheadPropeller()
    {
        var d = Fsb1FullDynamics();
        var r = d.Forces(-2.0, 0, 0, Units.DegToRad(20.0), 2.0, 0, 0, 0, 0, 0, 0, Deep(d));
        Assert.True(r.Thrust > 0.0);
        Assert.True(r.UR > 0.0, "倒退中正車:螺槳滑流仍由前向後流過舵,u_R 應為正");
    }

    [Fact]
    public void PropellerThrustDeductionAndWakeDrift()
    {
        var d = Fsb1FullDynamics();
        var f = Deep(d);
        double u = 6.0, n = 2.5;
        var bd = d.Forces(u, 0, 0, 0, n, 0, 0, 0, 0, 0, 0, f);
        Assert.Equal((1.0 - d.Coefficients.Propeller.TP) * bd.Thrust, bd.XP, 6);
        var va = u * (1.0 - d.Coefficients.Propeller.WP0);
        var j = va / (n * d.Propeller.Diameter);
        Assert.Equal(j, bd.J, 9);
        Assert.Equal(d.Propeller.KtAt(j), bd.Kt, 9);
        var beta = Units.DegToRad(15.0);
        var bd2 = d.Forces(u * Math.Cos(beta), -u * Math.Sin(beta), 0, 0, n, 0, 0, 0, 0, 0, 0, f);
        Assert.True(bd2.J > bd.J && bd2.Kt < bd.Kt, "伴流隨螺槳處漂角減小 → J 變大、K_T 變小");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(135.0)]
    [InlineData(180.0)]
    [InlineData(225.0)]
    [InlineData(300.0)]
    public void WindBlendermannMatchesReference(double windFromDeg)
    {
        var d = Fsb1FullDynamics();
        var w = d.Coefficients.Wind;
        var V = Units.KnToMps(30.0);
        var wd = Units.DegToRad(windFromDeg);
        var (windE, windN) = (-V * Math.Sin(wd), -V * Math.Cos(wd));
        var bd = d.Forces(0, 0, 0, 0, 0, 0, 0, 0, 0, windE, windN, Deep(d)); // 靜止、航向北 → 相對風 = 真風
        var gamma = Math.Abs(Math.Atan2(Math.Sin(wd), Math.Cos(wd)));
        var cdlAf = gamma <= Math.PI / 2 ? w.CDlHead : w.CDlTail;
        var cdl = cdlAf * w.FrontalArea_m2 / w.LateralArea_m2;
        var den = 1.0 - 0.5 * w.Delta * (1.0 - cdl / w.CDt) * Math.Pow(Math.Sin(2.0 * gamma), 2);
        var cx = -cdl * (w.LateralArea_m2 / w.FrontalArea_m2) * Math.Cos(gamma) / den;
        var cy = w.CDt * Math.Sin(gamma) / den;
        var cn = (w.LateralCentroid_m / d.Loa - 0.18 * (gamma - Math.PI / 2)) * cy;
        var qa = 0.5 * w.AirDensity_kgm3 * V * V;
        var sgn = Math.Sin(wd) >= 0 ? 1.0 : -1.0;
        Assert.Equal(qa * cx * w.FrontalArea_m2, bd.XW, 6);
        Assert.Equal(-sgn * qa * cy * w.LateralArea_m2, bd.YW, 6); // 風自右舷 → 向左的橫向力
        Assert.Equal(-sgn * qa * cn * w.LateralArea_m2 * d.Loa, bd.NW, 4);
    }

    [Fact]
    public void ShallowWaterIncreasesTurningDiameterAndSquat()
    {
        // h/T = 1.5(水深 8.55 m):迴旋圈增大;squat 隨速度增加(規劃書第 14 章趨勢項)
        var deep = TurningCircle(35.0, "port", 10.0, maxHeadingChangeDeg: 200.0);
        var ship = TestData.Fsb1();
        var sc = MmgScenario(10.0, waterDepth: 1.5 * Fsb1Full.Reference.Draft_m);
        var e = MmgEngine(sc, ship: ship);
        e.Enqueue(SimCommand.Rudder(-35.0));
        var psi0 = e.Motion.Psi;
        var total = 0.0;
        var prev = psi0;
        var td = double.NaN;
        while (e.Time < 1800.0 && double.IsNaN(td))
        {
            e.Step();
            total += Units.WrapRadPi(e.Motion.Psi - prev);
            prev = e.Motion.Psi;
            if (-Units.RadToDeg(total) >= 180.0) td = -(e.Motion.X * Math.Cos(psi0) - e.Motion.Y * Math.Sin(psi0));
        }
        Assert.True(td > deep.TacticalDiameterM * 1.05, $"淺水戰術直徑 {td:F0} m 應大於深水 {deep.TacticalDiameterM:F0} m");
        var d = Fsb1FullDynamics();
        var s1 = d.Squat(new StateVector(Units.KnToMps(8.0), 0, 0, 0, 0, 0), 11.7);
        var s2 = d.Squat(new StateVector(Units.KnToMps(14.0), 0, 0, 0, 0, 0), 11.7);
        Assert.True(0.0 < s1 && s1 < s2 && s2 < 1.0, $"squat 8 kn {s1:F2} m、14 kn {s2:F2} m(海報 0.13 / 0.42)");
    }
}
