using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using static SimosaBRM.SimCore.Tests.ExternalForceTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>泊位(設計文件第 4 節):碰墊只推不拉、能量積分 vs 質量–彈簧解析值、過載、碼頭硬接觸、繫纜張力/絞纜/斷裂、麥寮示意泊位。</summary>
public class BerthTests
{
    // ------------------------------------------------------------------ 碰墊曲線

    [Fact]
    public void FenderCurveReactionAndEnergy()
    {
        var lin = new FenderType(new FenderTypeDefinition { HeightM = 1.0, RatedReactionKN = 1000, RatedDeflectionPct = 72, Curve = new() { new[] { 0.0, 0.0 }, new[] { 72.0, 100.0 } } });
        var k = 1000e3 / 0.72;
        Assert.Equal(0.0, lin.Reaction(-0.1));
        Assert.Equal(0.0, lin.Reaction(0.0));
        Assert.Equal(k * 0.3, lin.Reaction(0.3), 1e-6);
        Assert.Equal(0.5 * k * 0.3 * 0.3, lin.Energy(0.3), 1e-6);
        Assert.Equal(0.5 * 1000e3 * 0.72, lin.RatedEnergyJ, 1e-6);
        // 超過最後一點:觸底剛度 20·R/H 外插(仍只推)
        Assert.Equal(1000e3 + 20.0 * 1000e3 * 0.08, lin.Reaction(0.80), 1e-6);

        // 挫屈型(麥寮示意 cone-1000):反力非單調時最大反力取 [0, δ] 上的最大值;能量 = 分段梯形積分
        var def = ScenarioExternalForces.LoadBerth(Path.Combine(TestData.Root, "data", "areas", "mailiao-berth.json"), "MLO-OIL-3");
        var cone = new FenderType(def.FenderTypes["cone-1000"]);
        Assert.Equal(551.4e3, cone.RatedEnergyJ, 1.0);
        var pts = def.FenderTypes["cone-1000"].Curve!;
        var e = 0.0;
        for (var i = 1; i < pts.Count; i++) e += 0.5 * (pts[i][1] + pts[i - 1][1]) / 100.0 * 1000e3 * (pts[i][0] - pts[i - 1][0]) / 100.0;
        Assert.Equal(e, cone.Energy(0.72), 1e-6);
        Assert.True(cone.MaxReaction(0.72) >= cone.Reaction(0.55));
    }

    // ------------------------------------------------------------------ 質點 + 線性碰墊:質量–彈簧解析值

    /// <summary>航向 090、左舷朝北側碼頭;以北向均勻流 v0 使船(對水靜止)以 v0 接近碼頭。</summary>
    private static SimulationEngine Alongside(double v0, bool fenders = true, string lines = "", double y = 19.5)
        => PointMass(Sc(LinearBerthYaml(fenders, lines), x: 0, y: y, headingDeg: 90, currentSet: 0, currentDriftKn: Units.MpsToKn(v0)));

    [Fact]
    public void FenderEnergyMatchesMassSpringAnalytic()
    {
        const double v0 = 0.15;
        var e = Alongside(v0);
        Assert.True(e.External.Active);
        var events = TestData.Capture(e);
        var maxDef = 0.0;
        e.Stepped += eng =>
        {
            var b = eng.External.Berth!;
            maxDef = Math.Max(maxDef, b.Fenders.Max(f => f.LastDeflection));
        };
        RunTo(e, 30);
        var hits = events.Where(x => x.Kind == SimEventKinds.FenderContact).ToList();
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.False(h.DataBool("overload")));
        Assert.All(hits, h => Assert.Equal(v0, h.DataNumber("normalSpeed_mps")!.Value, 3));
        var energy = hits.Sum(h => h.DataNumber("energy_kNm")!.Value) * 1000.0;
        var analytic = 0.5 * PointMassKg * v0 * v0;
        Assert.Equal(analytic, energy, analytic * 0.005);
        // δ_max = v0·√(m / Σk);最大反力 = k·δ_max
        var k = 1000e3 / 0.72;
        var dmax = v0 * Math.Sqrt(PointMassKg / (2.0 * k));
        Assert.Equal(dmax, hits[0].DataNumber("maxDeflection_m")!.Value, dmax * 0.005);
        Assert.Equal(k * dmax / 1000.0, hits[0].DataNumber("maxReaction_kN")!.Value, k * dmax / 1000.0 * 0.005);
        Assert.Equal(dmax, maxDef, dmax * 0.01);
        // 彈性回彈後離開碼頭(只推不拉:分離後不再受力,船以約 v0 遠離)
        RunTo(e, 60);
        Assert.All(e.External.Berth!.Fenders, f => Assert.False(f.InContact));
        var groundNorth = -e.Motion.V + v0; // 航向 090:北向對水速度 = −v;對地 = 對水 + 北向流
        Assert.Equal(-v0, groundNorth, 0.003);
        Assert.Equal(2, events.Count(x => x.Kind == SimEventKinds.FenderContact)); // 一次接觸一個事件
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.QuayContact);
    }

    [Fact]
    public void FenderOnlyPushesNeverPulls()
    {
        var sc = Sc(LinearBerthYaml(), x: 0, y: 19.5, headingDeg: 90);
        var model = new ExternalForceModel(TestData.Fsb1(), LoadingCondition.Full, sc, new Geo.LocalTangentPlane(23.80, 120.15), 0.02, true,
            new Physics.StateVector(0, 0, 0, 0, 19.5, Math.PI / 2));
        var env = new Environment.EnvironmentSample(0, 0, 0, 0, 20);
        // 左舷距碰墊面 0.5 m:無力
        Assert.Equal(BodyForce.Zero, model.Evaluate(new Physics.StateVector(0, 0, 0, 0, 19.5, Math.PI / 2), env));
        // 壓縮 0.2 m:力推向右舷(+y,離開碼頭)、無艏搖(對稱)
        var f = model.Evaluate(new Physics.StateVector(0, 0, 0, 0, 20.2, Math.PI / 2), env);
        Assert.Equal(2 * 1000e3 / 0.72 * 0.2, f.Y, 1e-3);
        Assert.Equal(0.0, f.N, 1e-3);
        // 壓縮中但快速離開(阻尼為 0 → 仍只有彈力;阻尼型碰墊合力夾在 ≥ 0)
        var damped = new ExternalForceModel(TestData.Fsb1(), LoadingCondition.Full, Sc(LinearBerthYaml(damping: 5e5), x: 0, y: 19.5, headingDeg: 90),
            new Geo.LocalTangentPlane(23.80, 120.15), 0.02, true, new Physics.StateVector(0, 0, 0, 0, 19.5, Math.PI / 2));
        var leaving = damped.Evaluate(new Physics.StateVector(0, 2.0, 0, 0, 20.05, Math.PI / 2), env); // v = 2 m/s 向右舷 = 向南離開
        Assert.Equal(0.0, leaving.Y);
        Assert.True(damped.Evaluate(new Physics.StateVector(0, -0.1, 0, 0, 20.05, Math.PI / 2), env).Y > 2 * 1000e3 / 0.72 * 0.55); // 壓縮中再加阻尼
    }

    [Fact]
    public void FastApproachOverloadsFenders()
    {
        const double v0 = 0.5;
        var e = Alongside(v0);
        var events = TestData.Capture(e);
        RunTo(e, 20);
        var hits = events.Where(x => x.Kind == SimEventKinds.FenderContact).ToList();
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.DataBool("overload")));
        Assert.All(hits, h => Assert.True(h.DataNumber("deflection_pct") > 72.0));
        var energy = hits.Sum(h => h.DataNumber("energy_kNm")!.Value) * 1000.0;
        Assert.Equal(0.5 * PointMassKg * v0 * v0, energy, 0.5 * PointMassKg * v0 * v0 * 0.01);
        Assert.All(hits, h => Assert.True(h.DataNumber("energy_kNm") > h.DataNumber("ratedEnergy_kNm")));
    }

    [Fact]
    public void HullQuayHardContactWithoutFenders()
    {
        var e = Alongside(0.1, fenders: false, y: 20.8); // 左舷距碼頭面 0.2 m
        var events = TestData.Capture(e);
        var maxPen = 0.0;
        e.Stepped += eng => maxPen = Math.Max(maxPen, eng.External.Berth!.QuayMaxPenetration);
        RunTo(e, 20);
        var q = Assert.Single(events, x => x.Kind == SimEventKinds.QuayContact);
        Assert.Equal(0.1, q.DataNumber("normalSpeed_mps")!.Value, 2);
        Assert.InRange(maxPen, 0.0, 0.08); // 懲罰剛度 2e7 N/m:穿入量為公分級
        Assert.True(e.Motion.Y < 20.8 + 0.1, "船被碼頭擋住");
    }

    // ------------------------------------------------------------------ 繫纜

    private const string MidLine = "{ id: L1, fairlead: midPort, bollard: B1, mbl_t: 50, breakStrain: 0.18, exponent: 1.5, pretension_t: 2, winchPull_t: 10, heaveRate_mPerMin: 15 }";

    [Fact]
    public void MooringLineTensionHeaveAndParting()
    {
        // 纜由左舷船舯(0, −9)至繫船柱 B1(x 0、y 33):距離 4.5 m,與碼頭垂直
        var sc = Sc(LinearBerthYaml(lines: MidLine) + "\ntugs: [ { id: TG, bollardPull_t: 20, attach: { x: 0, y: 0 }, riseTime_s: 20 } ]", x: 0, y: 19.5, headingDeg: 90);
        var e = PointMass(sc);
        var events = TestData.Capture(e);
        Apply(e, SimCommand.Mooring("L1", "makeFast"));
        e.Step();
        var line = e.BuildState().Berth!.Lines.Single();
        Assert.Equal("fast", line.Status);
        Assert.Equal("B1", line.BollardId);
        Assert.Equal(2.0, line.TensionT, 0.01);
        Assert.Single(events, x => x.Kind == SimEventKinds.MooringCommand);

        // 拖船向南(右舷,90°)拉 20 t → 靜平衡:張力 = 20 t,應變 ε = ε_b (T/MBL)^(1/n)
        Apply(e, SimCommand.TugControl("TG", "pull", 90, 100));
        RunTo(e, 400);
        var t = e.BuildState().Berth!.Lines.Single().TensionT;
        Assert.Equal(20.0, t, 0.3);
        var ml = e.External.Berth!.Lines.Single();
        var (dist, _) = e.External.Berth.LineGeometry(ml, e.Motion, new Environment.EnvironmentSample(0, 0, 0, 0, 20));
        Assert.Equal(ml.RestLength * (1.0 + 0.18 * Math.Pow(20.0 / 50.0, 1.0 / 1.5)), dist, 0.01);

        // 只拉不推:船推近繫船柱(纜鬆)時張力 0
        Assert.Equal(0.0, ml.StaticTension(ml.RestLength * 0.9));

        // 絞纜到 8 t(拖船停)
        Apply(e, SimCommand.TugControl("TG", "standby"));
        Apply(e, SimCommand.Mooring("L1", "heave", tensionT: 8));
        Assert.Equal("heaving", e.BuildState().Berth!.Lines.Single().Status);
        RunTo(e, e.Time + 300);
        Assert.Equal("fast", e.BuildState().Berth!.Lines.Single().Status);

        // 拖船 100 % 加大到超過 MBL(以 60 t 拖船)→ 斷裂
        var e2 = PointMass(Sc(LinearBerthYaml(lines: MidLine) + "\ntugs: [ { id: TG, bollardPull_t: 60, attach: { x: 0, y: 0 }, riseTime_s: 20 } ]", x: 0, y: 19.5, headingDeg: 90));
        var ev2 = TestData.Capture(e2);
        Apply(e2, SimCommand.Mooring("L1", "makeFast"));
        Apply(e2, SimCommand.TugControl("TG", "pull", 90, 100));
        RunTo(e2, 200);
        var parted = Assert.Single(ev2, x => x.Kind == SimEventKinds.MooringLineParted);
        Assert.Equal("L1", parted.ItemId);
        Assert.True(parted.DataNumber("tension_t") > 50.0);
        var st = e2.BuildState().Berth!.Lines.Single();
        Assert.Equal("parted", st.Status);
        Assert.Equal(0.0, st.TensionT);
        Apply(e2, SimCommand.Mooring("L1", "makeFast"));
        Assert.Equal(CommandRejectReasons.MooringLineParted, ev2.Last(x => x.Kind == SimEventKinds.CommandRejected).DataString("reason"));
    }

    [Fact]
    public void MooringCommandRejections()
    {
        var e = PointMass(Sc(LinearBerthYaml(lines: MidLine), x: 0, y: 19.5, headingDeg: 90));
        var events = TestData.Capture(e);
        Apply(e, SimCommand.Mooring("LX", "makeFast"));
        Apply(e, SimCommand.Mooring("L1", "heave"));
        Apply(e, SimCommand.Mooring("L1", "makeFast", bollard: "B9"));
        Assert.Equal(new[] { CommandRejectReasons.UnknownMooringLine, CommandRejectReasons.MooringLineNotFast, CommandRejectReasons.UnknownBollard },
            events.Where(x => x.Kind == SimEventKinds.CommandRejected).Select(x => x.DataString("reason")));
        // 無泊位
        var nb = Mmg(Sc());
        var ev2 = TestData.Capture(nb);
        Apply(nb, SimCommand.Mooring("L1", "makeFast"));
        Assert.Equal(CommandRejectReasons.NoBerth, ev2.Single().DataString("reason"));
    }

    // ------------------------------------------------------------------ 麥寮示意泊位(MMG)

    [Fact]
    public void MailiaoBerthFileResolvesAndTugPushesShipOntoFenders()
    {
        var def = ScenarioExternalForces.LoadBerth(Path.Combine(TestData.Root, "data", "areas", "mailiao-berth.json"), null);
        Assert.Equal("MLO-OIL-3", def.Id);
        Assert.Equal(12, def.Fenders.Count);
        Assert.Contains("示意", def.Source);
        // 碼頭線 085°,水域在南:船舯放在 chainage 130 m 的碼頭法線上、左舷距碰墊面 3 m,航向 085
        var proj = new Geo.LocalTangentPlane(23.80, 120.15);
        var (ax, ay) = proj.ToLocal(def.Quay[0].Lat!.Value, def.Quay[0].Lon!.Value);
        var (bx, by) = proj.ToLocal(def.Quay[1].Lat!.Value, def.Quay[1].Lon!.Value);
        var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        var (tx, ty) = ((bx - ax) / len, (by - ay) / len);
        var (nx, ny) = (ty, -tx);
        var off = 1.0 + 9.0 + 3.0;
        var (sx, sy) = (ax + 130 * tx + off * nx, ay + 130 * ty + off * ny);
        var hdg = Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(tx, ty)));
        var sc = Sc($$"""
            tugs:
              - { id: TG1, bollardPull_t: 40, attach: fwd, initial: { mode: standby, direction_deg: 270 } }
              - { id: TG2, bollardPull_t: 40, attach: aft, initial: { mode: standby, direction_deg: 270 } }
            berth:
              file: data/areas/mailiao-berth.json
              id: MLO-OIL-3
              lines:
                - { id: HEAD, fairlead: fwdPort, bollard: B6, mbl_t: 50 }
                - { id: STERN, fairlead: aftPort, bollard: B2, mbl_t: 50 }
            """, x: sx, y: sy, headingDeg: hdg, depth: 14);
        Assert.NotNull(sc.Berth!.Definition);
        Assert.Equal(12, sc.Berth.Definition!.Fenders.Count);
        // 紀錄 header 往返:definition 內嵌,不需讀檔
        var back = Scenario.Scenario.FromJson(sc.ToJson());
        Assert.Equal(12, back.Berth!.Definition!.Fenders.Count);

        var e = Mmg(sc);
        var events = TestData.Capture(e);
        Apply(e, SimCommand.TugControl("TG1", "push", 270, 25)); // 向左舷(碼頭)推
        Apply(e, SimCommand.TugControl("TG2", "push", 270, 25));
        RunTo(e, 240);
        var hits = events.Where(x => x.Kind == SimEventKinds.FenderContact).ToList();
        Assert.NotEmpty(hits);
        Assert.All(hits, h =>
        {
            Assert.InRange(h.DataNumber("normalSpeed_mps")!.Value, 0.0, 0.5);
            Assert.True(h.DataNumber("energy_kNm") >= 0.0);
        });
        var st = e.BuildState();
        Assert.NotEmpty(st.Berth!.Contacts);
        Assert.False(st.Berth.QuayContact);
        Assert.True(double.IsFinite(e.Motion.X) && double.IsFinite(e.Motion.Y));
        // 繫纜後停推:船靠在碰墊上
        Apply(e, SimCommand.Mooring("HEAD", "makeFast"));
        Apply(e, SimCommand.Mooring("STERN", "makeFast"));
        Apply(e, SimCommand.TugControl("TG1", "free"));
        Apply(e, SimCommand.TugControl("TG2", "free"));
        RunTo(e, 600);
        Assert.All(e.BuildState().Berth!.Lines, l => Assert.Equal("fast", l.Status));
        Assert.True(e.BuildState().Truth!.Sog < 0.3);
    }

    [Fact]
    public void BerthStructuralValidation()
    {
        Assert.Throws<InvalidDataException>(() => Sc("berth: { lines: [] }"));
        Assert.Throws<InvalidDataException>(() => Sc("berth: { file: data/areas/no-such-file.json }"));
        Assert.Throws<InvalidDataException>(() => Sc(LinearBerthYaml(lines: "{ id: L1, fairlead: midPort, bollard: B9 }")));
        Assert.Throws<InvalidDataException>(() => Sc(LinearBerthYaml().Replace("type: lin, chainage_m: 180", "type: nope, chainage_m: 180")));
        Assert.Throws<InvalidDataException>(() => Sc("", seabed: "gravel"));
    }

    /// <summary>
    /// berth.departedDistance_m 須為有限正數(scenario.schema.json exclusiveMinimum 0):0 或負值會讓離泊過程中的艉擺碰墊被當成
    /// 「已離開泊位」後再碰回而改變 E14 評分,故載入時即以結構錯誤拒絕;省略時用預設值、正值照用
    /// (.nan/.inf 在 YAML → JSON 轉換時即被 ContractJson 拒絕,結構檢查另以 double.IsFinite 防護)。
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void BerthDepartedDistanceMustBeFinitePositive(string value)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Sc(LinearBerthYaml() + $"\n  departedDistance_m: {value}"));
        Assert.Contains("berth.departedDistance_m", ex.Message);
        Assert.Equal(15.0, Sc(LinearBerthYaml() + "\n  departedDistance_m: 15").Berth!.DepartedDistanceM);
        Assert.Null(Sc(LinearBerthYaml()).Berth!.DepartedDistanceM);
    }
}
