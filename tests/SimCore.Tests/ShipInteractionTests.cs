using System.Globalization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;
using Xunit.Abstractions;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 船間交互作用(情境 environment.shipInteraction;設計文件 docs/design/physics-ship-interaction.md):對稱性(左右舷鏡像)、追越各階段的力與力矩方向
/// (並行吸引、對方艏到自船艉時艉被推離/艏轉向對方、對方艏越過自船艏時艏被推離)、對遇、距離衰減與作用範圍(漸減帶、範圍外零力)、
/// 量級(FSB1 8 kn 被 2 倍排水量船以 12 kn 近距 50 m 追越:深水約十餘 t、淺水放大)、靜止目標零力、目標船體預設、核函數鏡像級數收斂、
/// 未啟用時輸出/運動/雜湊不變、引擎施力與 state.shipInteraction、事件進入/離開各一次、快照還原與紀錄/重播一致、結構檢查,
/// 以及 C#↔Python 跨語言夾具(Golden/interaction_force_table.csv 相對 1e-12、interaction_overtaking_run.csv 軌跡 &lt; 1% 與事件 tick 相同;
/// export_interaction_reference.py 產生)。
/// </summary>
public class ShipInteractionTests
{
    private readonly ITestOutputHelper _out;
    public ShipInteractionTests(ITestOutputHelper output) => _out = output;

    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private const double TonneForce = ShipInteractionModel.TonneForce;
    private const double Deep = 1000.0;

    private static ScenarioShipInteraction Config(double? scale = null, double? threshold = null)
        => new() { Enabled = true, CoefficientScale = scale, EventThresholdT = threshold };

    private static ShipInteractionModel Model(double? scale = null, double? threshold = null, LoadingCondition loading = LoadingCondition.Full, string ship = "FSB1")
        => new(TestData.Ship(ship), loading, Config(scale, threshold), supported: true);

    private static StateVector At(double psiDeg, double u = 0.0, double v = 0.0, double x = 0.0, double y = 0.0) => new(u, v, 0.0, x, y, Units.DegToRad(psiDeg));

    /// <summary>自船船體座標 (dx 前正, dy 右正) 的目標(相對航向 relDeg、航速 kn);2 倍 FSB1 排水量的貨船為預設尺寸。</summary>
    private static InteractionTarget Target(in StateVector own, double dx, double dy, double relDeg = 0.0, double speedKn = 12.0,
        double l = 130.0, double b = 22.0, double t = 7.5, double cb = 0.75, string id = "T")
    {
        var (e, n) = BodyFrame.ToEnu(own, dx, dy);
        return new InteractionTarget(id, e, n, Units.NormalizeHeadingRad(own.Psi + Units.DegToRad(relDeg)), Units.KnToMps(speedKn), l, b, t, cb);
    }

    private static ShipInteractionResult Overtaking(ShipInteractionModel m, double xi, double dy = -50.0, double depth = Deep, double ownKn = 8.0, double targetKn = 12.0)
    {
        var own = At(0.0, Units.KnToMps(ownKn));
        return m.Compute(own, depth, new[] { Target(own, xi * m.L, dy, speedKn: targetKn) });
    }

    /// <summary>內嵌情境:<paramref name="interaction"/> 為 environment 之下的一行(兩格縮排);<paramref name="targets"/> 為頂層 targets 區段。</summary>
    private static Scenario.Scenario Yaml(string interaction, string targets = "", double speedKn = 8.0, string telegraph = "HAH", double waterDepth = 1000.0, string extra = "")
        => ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_interaction",
            "seed: 7",
            "ship: { id: FSB1, loading: full }",
            "origin: { lat: 23.80, lon: 120.15 }",
            $"initial: {{ position: {{ x: 0, y: 0 }}, heading: 0, speed: {speedKn.ToString(CultureInfo.InvariantCulture)}, telegraph: {telegraph} }}",
            "environment:",
            "  wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }",
            "  current: { set: 0, drift: 0 }",
            $"  waterDepth: {waterDepth.ToString(CultureInfo.InvariantCulture)}",
            interaction,
            targets,
            extra,
        }) + "\n");

    private const string Enabled = "  shipInteraction: { enabled: true }";
    private const string Overtaker = """
        targets:
          - id: OVT
            shipType: cargo
            loa: 130
            beam: 22
            draft: 7.5
            displacement_t: 16490
            initial: { position: { x: -50, y: -200 }, heading: 0, speed: 12 }
            behaviour: { mode: hold }
        """;

    private static void AssertRel(double expected, double actual, double rel, string what)
    {
        var scale = Math.Max(1.0, Math.Abs(expected));
        Assert.True(Math.Abs(expected - actual) <= rel * scale, $"{what}: expected {expected:R} actual {actual:R}");
    }

    // ------------------------------------------------------------------ 純函數

    [Theory]
    [InlineData(0.0, 4.0, 0.0, -0.8)]
    [InlineData(30.0, 3.0, 0.2, 0.0)]
    [InlineData(200.0, -1.5, 0.0, 0.6)]
    [InlineData(120.0, 5.0, -0.4, -1.2)]
    public void PortStarboardMirrorSymmetry(double psi, double u, double v, double xi)
    {
        // 鏡像:目標在另一舷同距、自船側向速度反號時 X 相同,Y 與 N 反號(ξ 相同、η 反號)
        var m = Model();
        var a = m.Compute(At(psi, u, v, 100.0, -50.0), 20.0, new[] { Target(At(psi, u, v, 100.0, -50.0), xi * m.L, -45.0) });
        var b = m.Compute(At(psi, u, -v, 100.0, -50.0), 20.0, new[] { Target(At(psi, u, -v, 100.0, -50.0), xi * m.L, 45.0) });
        AssertRel(a.X, b.X, 1e-9, "X");
        AssertRel(-a.Y, b.Y, 1e-9, "Y");
        AssertRel(-a.N, b.N, 1e-9, "N");
        AssertRel(a.Xi, b.Xi, 1e-12, "xi");
        AssertRel(-a.Eta, b.Eta, 1e-12, "eta");
        Assert.True(a.Active && b.Active);
    }

    [Fact]
    public void OvertakingStagesHaveClassicSignsAndMagnitude()
    {
        // FSB1 滿載 8 kn,2.1 倍排水量貨船(L 130 m)以 12 kn 自左舷 50 m 追越(設計文件第 4.2 節量級檢查)
        var m = Model();
        var abreast = Overtaking(m, 0.0);
        var sternApproach = Overtaking(m, -1.0);  // 對方艏接近自船艉
        var bowPassed = Overtaking(m, 1.0);       // 對方艏越過自船艏
        _out.WriteLine($"深水 ξ 0:X {abreast.X / TonneForce:F2} t Y {abreast.Y / TonneForce:F2} t N {abreast.N / 1000:F0} kN·m;ξ −1:Y {sternApproach.Y / TonneForce:F2} t N {sternApproach.N / 1000:F0} kN·m;ξ +1:Y {bowPassed.Y / TonneForce:F2} t N {bowPassed.N / 1000:F0} kN·m");
        // 並行:吸引(目標在左舷 → Y 負),無艏搖(對稱)
        Assert.InRange(abreast.Y / TonneForce, -20.0, -8.0);
        Assert.True(Math.Abs(abreast.N) < 1e-6 * Math.Abs(abreast.Y) * m.L, $"N {abreast.N}");
        Assert.True(Math.Abs(abreast.X) < 1e-6 * Math.Abs(abreast.Y), $"X {abreast.X}");
        Assert.Equal("T", abreast.TargetId);
        AssertRel(0.0, abreast.Xi, 1e-12, "xi");
        AssertRel(-50.0 / m.L, abreast.Eta, 1e-12, "eta");
        // 對方艏到自船艉:自船艉被推離(Y 正 = 向右舷)、艏轉向對方(N 負 = 向左)
        Assert.True(sternApproach.Y > 0.0 && sternApproach.N < 0.0, $"Y {sternApproach.Y} N {sternApproach.N}");
        Assert.InRange(sternApproach.Y / TonneForce, 2.0, 12.0);
        Assert.InRange(-sternApproach.N / 1e6, 0.3, 3.0);
        // 對方艏越過自船艏:自船艏被推離(N 正 = 向右,遠離左舷的對方)
        Assert.True(bowPassed.Y > 0.0 && bowPassed.N > 0.0, $"Y {bowPassed.Y} N {bowPassed.N}");
        // 淺水放大:12 m(h/T 2.1)並行吸力為深水的 2–6 倍;50 m 介於兩者
        var shallow = Overtaking(m, 0.0, depth: 12.0);
        var mid = Overtaking(m, 0.0, depth: 50.0);
        Assert.InRange(shallow.Y / abreast.Y, 2.0, 6.0);
        Assert.True(Math.Abs(mid.Y) > Math.Abs(abreast.Y) && Math.Abs(mid.Y) < Math.Abs(shallow.Y));
        _out.WriteLine($"並行吸力:深水 {abreast.Y / TonneForce:F2} t、50 m {mid.Y / TonneForce:F2} t、12 m {shallow.Y / TonneForce:F2} t");
        // 與舵力同量級:10° 舵在 8 kn 約 5–8 t;並行吸力在 1–4 倍之間
        Assert.InRange(Math.Abs(abreast.Y) / (6.5 * TonneForce), 1.0, 4.0);
        // 速度:靜止目標零力(單向、定常模型);自船靜止(繫泊)仍受壓力梯度項(∝ U_t²)
        var still = m.Compute(At(0.0, Units.KnToMps(8.0)), Deep, new[] { Target(At(0.0, Units.KnToMps(8.0)), 0.0, -50.0, speedKn: 0.0) });
        Assert.True(still.Active && still.X == 0.0 && still.Y == 0.0 && still.N == 0.0);
        var moored = m.Compute(At(0.0), Deep, new[] { Target(At(0.0), 0.0, -50.0) });
        Assert.True(moored.Y < -3.0 * TonneForce, $"moored Y {moored.Y / TonneForce:F2} t");
        // 倍率:coefficientScale 2 → 力加倍;0 → 零力但 active
        var twice = Model(scale: 2.0);
        AssertRel(2.0 * abreast.Y, Overtaking(twice, 0.0).Y, 1e-12, "scale 2");
        var zero = Overtaking(Model(scale: 0.0), 0.0);
        Assert.True(zero.Active && zero.Y == 0.0 && zero.N == 0.0);
    }

    [Fact]
    public void HeadOnEncounterRepelsApproachingBowsAndAttractsAbreast()
    {
        var m = Model();
        var own = At(0.0, Units.KnToMps(8.0));
        InteractionTarget Oncoming(double xi) => Target(own, xi * m.L, -50.0, relDeg: 180.0, speedKn: 10.0);
        // 對遇船在左舷 50 m 迎面而來(設計文件第 4.3 節):艏對艏接近(ξ +1.3,兩艏尚未重疊)→ 兩船被推開(Y 正 = 遠離左舷的對方;艏墊 bow cushion)
        var approaching = m.Compute(own, Deep, new[] { Oncoming(1.3) });
        Assert.True(approaching.Y > 0.0, $"Y {approaching.Y}");
        // 對方低壓中體到自船艏旁(ξ +0.4):艏被吸向對方(N 負 = 向左)
        var bowAlongside = m.Compute(own, Deep, new[] { Oncoming(0.4) });
        Assert.True(bowAlongside.N < 0.0, $"N {bowAlongside.N}");
        // 並行(ξ 0):吸引(Y 負 = 向左舷的對方)、無艏搖;通過後(ξ −0.4)艉被吸向對方、艏轉離(N 正)
        var abreast = m.Compute(own, Deep, new[] { Oncoming(0.0) });
        Assert.True(abreast.Y < 0.0, $"Y {abreast.Y}");
        Assert.True(Math.Abs(abreast.N) < 1e-6 * Math.Abs(abreast.Y) * m.L);
        var passed = m.Compute(own, Deep, new[] { Oncoming(-0.4) });
        Assert.True(passed.N > 0.0, $"N {passed.N}");
        AssertRel(-bowAlongside.N, passed.N, 1e-9, "head-on antisymmetry");
        _out.WriteLine($"對遇 ξ 1.3:Y {approaching.Y / TonneForce:F2} t N {approaching.N / 1000:F0} kN·m;ξ 0.4:Y {bowAlongside.Y / TonneForce:F2} t N {bowAlongside.N / 1000:F0} kN·m;ξ 0:Y {abreast.Y / TonneForce:F2} t");
        // 30° 交叉:有力且非對稱
        var crossing = m.Compute(own, Deep, new[] { Target(own, -20.0, 60.0, relDeg: 30.0, speedKn: 9.0, l: 80.0, b: 14.0, t: 4.5, cb: 0.65) });
        Assert.True(crossing.Active && crossing.Y != 0.0 && crossing.N != 0.0);
    }

    [Fact]
    public void ForceDecaysWithSeparationAndVanishesOutsideRange()
    {
        var m = Model();
        var own = At(0.0, Units.KnToMps(8.0));
        // 橫向:吸力隨間距單調減小
        var prev = double.MaxValue;
        foreach (var dy in new[] { 30.0, 40.0, 50.0, 60.0, 70.0 })
        {
            var y = Math.Abs(Overtaking(m, 0.0, dy: -dy).Y);
            Assert.True(y < prev, $"dy {dy}: {y} ≥ {prev}");
            prev = y;
        }
        // 範圍:橫向 2(B_o + B_t) = 80 m、縱向 3 L;漸減帶 0.8–1.0 連續、範圍外零力且 active = false
        var yl = 2.0 * (m.B + 22.0);
        var full = Overtaking(m, 0.0, dy: -(0.8 * yl - 0.01));
        var taperIn = Overtaking(m, 0.0, dy: -(0.8 * yl + 0.01));
        AssertRel(full.Y, taperIn.Y, 2e-2, "taper start continuity");
        var nearEdge = Overtaking(m, 0.0, dy: -(yl - 0.01));
        Assert.True(Math.Abs(nearEdge.Y) < 1e-3 * Math.Abs(full.Y), $"edge {nearEdge.Y}");
        var outside = Overtaking(m, 0.0, dy: -(yl + 0.01));
        Assert.False(outside.Active);
        Assert.True(outside.Y == 0.0 && outside.N == 0.0 && outside.X == 0.0 && outside.TargetId is null && outside.Count == 0);
        var farAhead = Overtaking(m, 3.0);
        Assert.False(farAhead.Active);
        var inRange = Overtaking(m, 2.99);
        Assert.True(inRange.Active && Math.Abs(inRange.Y) < 1e-3 * Math.Abs(full.Y));
        Assert.Equal(1.0, ShipInteractionModel.Taper(0.5, 0.8));
        Assert.Equal(0.0, ShipInteractionModel.Taper(1.0, 0.8));
        Assert.Equal(0.5, ShipInteractionModel.Taper(0.9, 0.8), 12);
        // 兩艘目標:合力為各自之和,主要目標為等效側向力最大者
        var near = Target(own, 0.0, -50.0, id: "NEAR");
        var far = Target(own, 0.0, 70.0, id: "FAR");
        var both = m.Compute(own, Deep, new[] { far, near });
        var a = m.Compute(own, Deep, new[] { near });
        var b = m.Compute(own, Deep, new[] { far });
        AssertRel(a.Y + b.Y, both.Y, 1e-12, "sum Y");
        AssertRel(a.N + b.N, both.N, 1e-12, "sum N");
        Assert.Equal("NEAR", both.TargetId);
        Assert.Equal(2, both.Count);
    }

    [Fact]
    public void KernelImageSeriesConvergesAndMatchesDeepWaterLimit()
    {
        // 深水核 r^−3;有限水深鏡像級數(6 個鏡像 + 積分尾項)與 60 個鏡像相對相差 < 1e-3(實測約 2e-4);水深很大時趨近深水核
        ShipInteractionModel.Kernel(2500.0, 12.0, deep: false, images: 6, out var g6, out var dg6);
        ShipInteractionModel.Kernel(2500.0, 12.0, deep: false, images: 60, out var g60, out var dg60);
        Assert.True(Math.Abs(g60 - g6) <= 1e-3 * Math.Abs(g60), $"g {g6:R} vs {g60:R}");
        Assert.True(Math.Abs(dg60 - dg6) <= 1e-3 * Math.Abs(dg60), $"dg {dg6:R} vs {dg60:R}");
        ShipInteractionModel.Kernel(2500.0, 0.0, deep: true, images: 6, out var gd, out var dgd);
        Assert.Equal(Math.Pow(2500.0, -1.5), gd, 15);
        Assert.Equal(-1.5 * gd / 2500.0, dgd, 15);
        ShipInteractionModel.Kernel(2500.0, 5000.0, deep: false, images: 6, out var gBig, out _);
        Assert.True(Math.Abs(gBig - gd) <= 1e-3 * gd, $"near-deep {gBig:R} vs {gd:R}");
        // 淺水 2D 極限:r ≫ h 時 G ≈ 1/(h r²)
        ShipInteractionModel.Kernel(1e6, 5.0, deep: false, images: 6, out var g2d, out _);
        Assert.True(Math.Abs(g2d - 1.0 / (5.0 * 1e6)) <= 1e-2 / (5.0 * 1e6), $"2D limit {g2d:R}");
        // 導數:有限差分
        const double r2 = 3000.0;
        const double eps = 1e-3;
        ShipInteractionModel.Kernel(r2 + eps, 20.0, false, 6, out var gp, out _);
        ShipInteractionModel.Kernel(r2 - eps, 20.0, false, 6, out var gm, out _);
        ShipInteractionModel.Kernel(r2, 20.0, false, 6, out _, out var dg);
        var fd = (gp - gm) / (2.0 * eps);
        Assert.True(Math.Abs(fd - dg) <= 1e-6 * Math.Abs(dg), $"dG/d(r²) {dg:R} vs {fd:R}");
    }

    [Fact]
    public void HullProfileAndTargetDefaults()
    {
        var m = Model();
        // FSB1 滿載:Cp = 0.738/0.98 → L_e ≈ 0.38 L;源強度總和為 0(封閉船體)、剖面積總和 = 排水體積
        Assert.InRange(m.EntranceLengthM / m.L, 0.3, 0.45);
        var own = HullProfile.Build(m.L, m.B, m.T, m.Cb, ShipInteractionCoefficients.Default);
        Assert.True(Math.Abs(own.A.Sum()) < 1e-9 * own.A.Max(), $"ΣA {own.A.Sum()}");
        AssertRel(m.Cb * m.L * m.B * m.T, own.S.Sum(), 2e-2, "volume");
        Assert.True(own.A[^1] > 0.0 && own.A[0] < 0.0); // 艏源、艉匯
        // 目標船預設:displacement_t → Cb 反推並夾限;無 displacement 依船型
        var spec = new ScenarioTarget { Loa = 130, Beam = 22, Draft = 7.5, ShipType = "cargo", DisplacementT = 16490 };
        AssertRel(16490.0 * 1000.0 / (1025.0 * 130.0 * 22.0 * 7.5), TargetHullDefaults.BlockCoefficient(spec, 1025.0), 1e-12, "Cb from Δ");
        Assert.Equal(0.95, TargetHullDefaults.BlockCoefficient(new ScenarioTarget { Loa = 50, Beam = 10, Draft = 3, DisplacementT = 5000 }, 1025.0));
        Assert.Equal(0.35, TargetHullDefaults.BlockCoefficient(new ScenarioTarget { Loa = 100, Beam = 20, Draft = 8, DisplacementT = 10 }, 1025.0));
        Assert.Equal(0.82, TargetHullDefaults.BlockCoefficient(new ScenarioTarget { ShipType = "tanker" }, 1025.0));
        Assert.Equal(0.72, TargetHullDefaults.DefaultCb("CARGO"));
        Assert.Equal(0.65, TargetHullDefaults.DefaultCb("other"));
        Assert.Equal(0.45, TargetHullDefaults.DefaultCb("hsc"));
        Assert.Equal(5.0, TargetHullDefaults.Draft(new ScenarioTarget()));
        Assert.Equal(4.8, TargetHullDefaults.Draft(new ScenarioTarget { Draft = 0, Beam = 16 }), 12);
    }

    // ------------------------------------------------------------------ 引擎

    [Fact]
    public void WithoutInteractionOutputsMotionAndHashAreUnchanged()
    {
        var e = TestData.MinimalEngine();
        Assert.Null(e.ShipInteraction);
        TestData.RunTo(e, 3);
        var s = e.BuildState();
        Assert.Null(s.ShipInteraction);
        Assert.DoesNotContain("\"shipInteraction\"", ContractJson.Serialize(s));
        // 無區段、enabled: false、有區段但目標在範圍外:前兩者與舊版逐位元相同(含雜湊);第三者運動相同但雜湊因追加區塊而不同
        var plain = MmgEngine(Yaml("", Overtaker));
        var disabled = MmgEngine(Yaml("  shipInteraction: { enabled: false, coefficientScale: 2 }", Overtaker));
        var far = MmgEngine(Yaml(Enabled, Overtaker.Replace("x: -50, y: -200", "x: -600, y: -200")));
        Assert.Null(disabled.ShipInteraction);
        Assert.NotNull(far.ShipInteraction);
        Assert.True(far.ShipInteraction!.Active);
        for (var i = 0; i < 250; i++) { plain.Step(); disabled.Step(); far.Step(); }
        Assert.Equal(plain.Motion, disabled.Motion);
        Assert.Equal(plain.StateHash, disabled.StateHash);
        Assert.Equal(plain.Motion, far.Motion);
        Assert.NotEqual(plain.StateHash, far.StateHash);
        var st = far.BuildState();
        Assert.NotNull(st.ShipInteraction);
        Assert.False(st.ShipInteraction!.Active);
        Assert.Null(st.ShipInteraction.TargetId);
        Assert.Equal(0.0, st.ShipInteraction.ForceSwayT);
        Assert.Null(st.ShipInteraction.Xi);
        Assert.Null(disabled.BuildState().ShipInteraction);
    }

    [Fact]
    public void EngineAppliesInteractionForceAndReportsState()
    {
        // 追越船自左舷後方接近:自船先被推離(向右舷)、艏向對方轉,之後被吸向對方;state.shipInteraction 帶出目標與力
        var e = MmgEngine(Yaml(Enabled, Overtaker));
        var calm = MmgEngine(Yaml("", Overtaker));
        Assert.True(e.ShipInteraction is { Active: true });
        var s0 = e.BuildState();
        Assert.NotNull(s0.ShipInteraction);
        Assert.True(s0.ShipInteraction!.Active);
        Assert.Equal("OVT", s0.ShipInteraction.TargetId);
        AssertRel(-200.0 / e.ShipInteraction!.L, s0.ShipInteraction.Xi!.Value, 1e-12, "xi");
        AssertRel(-50.0 / e.ShipInteraction.L, s0.ShipInteraction.Eta!.Value, 1e-12, "eta");
        Assert.Equal(1, s0.ShipInteraction.Targets);
        Assert.False(s0.ShipInteraction.Event);
        TestData.RunTo(e, 60);
        TestData.RunTo(calm, 60);
        var s60 = e.BuildState();
        _out.WriteLine($"60 s:ξ {s60.ShipInteraction!.Xi:F2} Y {s60.ShipInteraction.ForceSwayT:F2} t N {s60.ShipInteraction.MomentYawTm:F0} t·m;ψ {s60.Truth!.Heading:F2}°(無交互作用 {calm.BuildState().Truth!.Heading:F2}°)");
        Assert.True(s60.ShipInteraction.Event);
        Assert.NotEqual(calm.Motion.Psi, e.Motion.Psi);
        Assert.True(Math.Abs(Units.RadToDeg(Units.WrapRadPi(e.Motion.Psi - calm.Motion.Psi))) > 0.5, "交互作用應使航向偏離");
        var json = ContractJson.Serialize(s60);
        Assert.Contains("\"shipInteraction\":{\"active\":true,\"targetId\":\"OVT\"", json);
        Assert.Contains("\"forceSway_t\":", json);
        Assert.Contains("\"momentYaw_tm\":", json);
        var back = ContractJson.Deserialize<OwnShipState>(json)!;
        Assert.Equal(s60.ShipInteraction.ForceSwayT, back.ShipInteraction!.ForceSwayT);
        Assert.Equal(s60.ShipInteraction.Xi, back.ShipInteraction.Xi);
        // 步末 Last 與以目前狀態/目標重算相同
        var r = e.ShipInteraction.Compute(e.Motion, e.Environment.DepthAt(e.Motion.X, e.Motion.Y), e.ShipInteraction.Targets.ToArray());
        Assert.Equal(e.ShipInteraction.Last, r);
    }

    [Fact]
    public void EventIsRaisedOnceOnEnterAndOnceOnExit()
    {
        var e = MmgEngine(Yaml(Enabled, Overtaker));
        var events = TestData.Capture(e);
        TestData.RunTo(e, 210);
        var si = events.Where(ev => ev.Kind == SimEventKinds.ShipInteraction).ToList();
        Assert.Equal(2, si.Count);
        Assert.Equal("enter", si[0].DataString("phase"));
        Assert.Equal("exit", si[1].DataString("phase"));
        Assert.All(si, ev => Assert.Equal("OVT", ev.TargetId));
        Assert.All(si, ev => Assert.Equal(SimEventSources.Engine, ev.Source));
        Assert.All(si, ev => Assert.True(ev.Visible));
        Assert.True(si[0].DataNumber("forceSway_t") is { } f && Math.Abs(f) >= 0.0);
        Assert.Equal(3.0, si[0].DataNumber("threshold_t"));
        Assert.True(si[0].Tick < si[1].Tick);
        Assert.Contains("OVT", si[0].Message);
        _out.WriteLine($"enter tick {si[0].Tick}({si[0].Message});exit tick {si[1].Tick}");
        Assert.False(e.ShipInteraction!.EventActive);
        // 門檻 0:不發事件;倍率 0:零力不發事件
        var none = MmgEngine(Yaml("  shipInteraction: { enabled: true, eventThreshold_t: 0 }", Overtaker));
        var noneEvents = TestData.Capture(none);
        TestData.RunTo(none, 60);
        Assert.DoesNotContain(noneEvents, ev => ev.Kind == SimEventKinds.ShipInteraction);
        var zero = MmgEngine(Yaml("  shipInteraction: { enabled: true, coefficientScale: 0 }", Overtaker));
        var zeroEvents = TestData.Capture(zero);
        TestData.RunTo(zero, 60);
        Assert.DoesNotContain(zeroEvents, ev => ev.Kind == SimEventKinds.ShipInteraction);
        Assert.True(zero.BuildState().ShipInteraction!.Active);
    }

    [Fact]
    public void EscortAndNotYetActivatedTargetsAreExcluded()
    {
        const string escortAndLater = """
            targets:
              - id: PILOT
                shipType: pilot
                loa: 20
                beam: 6
                draft: 2
                initial: { position: { x: 30, y: 0 }, heading: 0, speed: 8 }
                behaviour: { mode: follow, follow: { leader: own, bearing: 90, range: 30 } }
              - id: LATER
                loa: 130
                beam: 22
                draft: 7.5
                initial: { position: { x: -50, y: 0 }, heading: 0, speed: 8 }
                behaviour: { mode: hold }
                trigger: { type: time, time: 30 }
            """;
        var e = MmgEngine(Yaml(Enabled, escortAndLater));
        TestData.RunTo(e, 10);
        var s = e.BuildState();
        Assert.False(s.ShipInteraction!.Active);
        Assert.Equal(0, s.ShipInteraction.Targets);
        TestData.RunTo(e, 40);
        s = e.BuildState();
        Assert.True(s.ShipInteraction!.Active);
        Assert.Equal("LATER", s.ShipInteraction.TargetId);
        Assert.Equal(1, s.ShipInteraction.Targets);
    }

    [Fact]
    public void SnapshotRestoreContinuesBitIdentical()
    {
        var sc = Yaml(Enabled, Overtaker, waterDepth: 20.0);
        var e = MmgEngine(sc);
        var events = TestData.Capture(e);
        TestData.RunTo(e, 5);
        var snap = e.TakeSnapshot();
        Assert.NotNull(snap.ShipInteraction);
        Assert.False(snap.ShipInteraction!.EventActive);
        TestData.RunTo(e, 60);
        var (hash, s60, last, eventsBefore) = (e.StateHash, e.BuildState(), e.ShipInteraction!.Last, events.Count(ev => ev.Kind == SimEventKinds.ShipInteraction));
        Assert.Equal(1, eventsBefore);
        e.Restore(snap);
        Assert.Equal(snap.Tick, e.Tick);
        Assert.False(e.ShipInteraction!.EventActive);
        Assert.Equal(e.ShipInteraction.Compute(e.Motion, e.Environment.DepthAt(e.Motion.X, e.Motion.Y), e.ShipInteraction.Targets.ToArray()), e.ShipInteraction.Last);
        TestData.RunTo(e, 60);
        Assert.Equal(hash, e.StateHash);
        Assert.Equal(last, e.ShipInteraction.Last);
        Assert.Equal(s60.ShipInteraction!.ForceSwayT, e.BuildState().ShipInteraction!.ForceSwayT);
        Assert.Equal(2, events.Count(ev => ev.Kind == SimEventKinds.ShipInteraction)); // 還原後進入事件再發一次(倒帶重跑)
        // 事件旗標進快照:在進入之後拍的快照還原時不再重發
        var snap2 = e.TakeSnapshot();
        Assert.True(snap2.ShipInteraction!.EventActive);
        Assert.Equal("OVT", snap2.ShipInteraction.EventTargetId);
        TestData.RunTo(e, 70);
        e.Restore(snap2);
        Assert.True(e.ShipInteraction.EventActive);
        TestData.RunTo(e, 70);
        Assert.Equal(2, events.Count(ev => ev.Kind == SimEventKinds.ShipInteraction));
        // 同一輸入重跑雜湊相同
        var again = MmgEngine(sc);
        TestData.RunTo(again, 60);
        Assert.Equal(hash, again.StateHash);
        // 快照 JSON 往返
        var back = EngineSnapshot.FromJson(snap2.ToJson());
        Assert.Equal(snap2.ShipInteraction, back.ShipInteraction);
    }

    [Fact]
    public void RecordReplayReproducesHashAndEvents()
    {
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, "interaction_overtaking_run.yaml"));
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"interaction-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        try
        {
            TestData.RunTo(e, 50);
            e.Enqueue(SimCommand.Rudder(-5));
            TestData.RunTo(e, 120);
            w.Close();
            Assert.NotNull(e.BuildState().ShipInteraction);
            var r = Replayer.Replay(path, TestData.Ship, Mmg);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("  shipInteraction: { enabled: true, coefficientScale: -1 }", "environment.shipInteraction.coefficientScale", "≥ 0")]
    [InlineData("  shipInteraction: { enabled: true, eventThreshold_t: -0.5 }", "environment.shipInteraction.eventThreshold_t", "≥ 0")]
    [InlineData("  shipInteraction: { enabled: false, coefficientScale: -1 }", "environment.shipInteraction.coefficientScale", "≥ 0")]
    public void StructuralIssuesRejectBadInteraction(string interaction, string path, string message)
    {
        var sc = Yaml(interaction);
        var issues = sc.StructuralIssues().ToList();
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Error && i.Path == path && i.Message.Contains(message));
        Assert.Throws<InvalidDataException>(() => sc.Validate());
    }

    [Fact]
    public void ValidInteractionHasNoStructuralIssuesAndDefaults()
    {
        Assert.Empty(Yaml(Enabled).StructuralIssues());
        Assert.Empty(Yaml("  shipInteraction: { enabled: true, coefficientScale: 0, eventThreshold_t: 0 }").StructuralIssues());
        Assert.Empty(Yaml("  shipInteraction: { }").StructuralIssues());
        var sc = Yaml("  shipInteraction: { enabled: true, coefficientScale: 1.5 }", Overtaker).Validate();
        var m = ShipInteractionModel.Create(TestData.Fsb1(), LoadingCondition.Full, sc, supported: true)!;
        Assert.Equal(1.5, m.Scale);
        Assert.Equal(3.0 * TonneForce, m.EventThresholdN);
        Assert.True(m.Active);
        Assert.Equal(16490.0, sc.Targets[0].DisplacementT);
        Assert.Null(ShipInteractionModel.Create(TestData.Fsb1(), LoadingCondition.Full, Yaml("").Validate(), supported: true));
        Assert.Null(ShipInteractionModel.Create(TestData.Fsb1(), LoadingCondition.Full, Yaml("  shipInteraction: { enabled: false }").Validate(), supported: true));
        Assert.Null(ShipInteractionModel.Create(TestData.Fsb1(), LoadingCondition.Full, Yaml("  shipInteraction: { coefficientScale: 2 }").Validate(), supported: true));
        // 動力學不接受外力時不啟用(不施力、不輸出、不追加雜湊)
        var unsupported = new ShipInteractionModel(TestData.Fsb1(), LoadingCondition.Full, Config(), supported: false);
        Assert.False(unsupported.Active);
        Assert.Equal(0, unsupported.Targets.Count);
    }

    // ------------------------------------------------------------------ 跨語言夾具

    /// <summary>純函數取樣表:Python export_interaction_reference.py 的每列以同船舶/裝載/倍率建模,同狀態、水深與目標 Compute,相對差 ≤ 1e-12。</summary>
    [Fact]
    public void GoldenForceTableMatchesPythonReference()
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = File.ReadLines(Path.Combine(GoldenDirectory, "interaction_force_table.csv")).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var cols = lines[0].Split(',');
        Assert.Equal(new[] { "ship", "loading", "scale", "depth_m", "psi_deg", "u", "v", "tgt_x", "tgt_y", "tgt_psi_deg", "tgt_speed_kn", "tgt_L", "tgt_B", "tgt_T", "tgt_cb", "active", "xi", "eta", "X_N", "Y_N", "N_Nm" }, cols);
        var rows = lines.Skip(1).Select(l => l.Split(',')).ToList();
        Assert.True(rows.Count >= 400, $"{rows.Count} rows");
        var models = new Dictionary<string, ShipInteractionModel>();
        var worst = 0.0;
        string? worstWhat = null;
        var activeRows = 0;
        foreach (var c in rows)
        {
            var key = string.Join("|", c.Take(3));
            if (!models.TryGetValue(key, out var m))
            {
                m = new ShipInteractionModel(TestData.Ship(c[0]), Enum.Parse<LoadingCondition>(c[1], ignoreCase: true), Config(scale: double.Parse(c[2], inv)), supported: true);
                models[key] = m;
            }
            var own = At(double.Parse(c[4], inv), double.Parse(c[5], inv), double.Parse(c[6], inv), 100.0, -200.0);
            var t = new InteractionTarget("T", double.Parse(c[7], inv), double.Parse(c[8], inv), Units.DegToRad(double.Parse(c[9], inv)), Units.KnToMps(double.Parse(c[10], inv)),
                double.Parse(c[11], inv), double.Parse(c[12], inv), double.Parse(c[13], inv), double.Parse(c[14], inv));
            var r = m.Compute(own, double.Parse(c[3], inv), new[] { t });
            Assert.Equal(c[15] == "1", r.Active);
            if (r.Active) activeRows++;
            foreach (var (name, expected, actual) in new[]
                     {
                         ("xi", double.Parse(c[16], inv), r.Xi), ("eta", double.Parse(c[17], inv), r.Eta),
                         ("X_N", double.Parse(c[18], inv), r.X), ("Y_N", double.Parse(c[19], inv), r.Y), ("N_Nm", double.Parse(c[20], inv), r.N),
                     })
            {
                var rel = Math.Abs(expected - actual) / Math.Max(1.0, Math.Abs(expected));
                var what = $"{key} depth {c[3]} psi {c[4]} u {c[5]} v {c[6]} tgt ({c[7]},{c[8]}) {name}";
                if (rel > worst) { worst = rel; worstWhat = $"{what}: Python {expected:R} C# {actual:R}"; }
                Assert.True(rel <= 1e-12, $"{what}: Python {expected:R} vs C# {actual:R}(相對差 {rel:E2})");
            }
        }
        Assert.True(activeRows > rows.Count / 2);
        _out.WriteLine($"{rows.Count} 列({activeRows} 列有作用)、{models.Count} 個模型;最大相對差 {worst:E2}({worstWhat})");
    }

    /// <summary>積分案例:同一份 YAML 建立 MMG 引擎(含目標船),依標頭指令推進,每 1 s 比對位置/航向/速度/交互作用力/目標位置(規劃書第 14 章 &lt; 1%),事件 tick/phase 相同。</summary>
    [Fact]
    public void GoldenRunMatchesPythonWithinOnePercent()
    {
        var inv = CultureInfo.InvariantCulture;
        string? scenario = null;
        var commands = new List<(long Tick, SimCommand Command)>();
        var expectedEvents = new List<string>();
        string[]? cols = null;
        var rows = new List<double[]>();
        foreach (var raw in File.ReadLines(Path.Combine(GoldenDirectory, "interaction_overtaking_run.csv")))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var body = line.TrimStart('#').Trim();
                if (body.StartsWith("scenario:", StringComparison.Ordinal)) scenario = body["scenario:".Length..].Trim();
                else if (body.StartsWith("cmd: tick=", StringComparison.Ordinal))
                {
                    var rest = body["cmd: tick=".Length..];
                    var sp = rest.IndexOf(' ');
                    commands.Add((long.Parse(rest[..sp], inv), ContractJson.Deserialize<SimCommand>(rest[(sp + 1)..])!));
                }
                else if (body.StartsWith("event: ", StringComparison.Ordinal)) expectedEvents.Add(body["event: ".Length..]);
                continue;
            }
            if (cols is null) { cols = line.Split(','); continue; }
            rows.Add(line.Split(',').Select(v => double.Parse(v, inv)).ToArray());
        }
        Assert.NotNull(cols);
        Assert.Equal("interaction_overtaking_run.yaml", scenario);
        Assert.Equal(2, expectedEvents.Count);
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, scenario!));
        var e = MmgEngine(sc);
        Assert.True(e.ShipInteraction is { Active: true });
        var events = TestData.Capture(e);
        double Value(string col)
        {
            var m = e.Motion;
            var si = e.ShipInteraction!;
            var tg = e.Traffic.Targets[0];
            return col switch
            {
                "tick" => e.Tick, "t" => e.Time, "x" => m.X, "y" => m.Y, "psi_deg" => Units.NormalizeHeadingDeg(Units.RadToDeg(m.Psi)),
                "u" => m.U, "v" => m.V, "r" => m.R, "rpm" => e.Actuators.Rpm,
                "int_X" => si.Last.X / 1000.0, "int_Y" => si.Last.Y / 1000.0, "int_N" => si.Last.N / 1000.0, "xi" => si.Last.Xi, "eta" => si.Last.Eta,
                "tgt_x" => tg.X, "tgt_y" => tg.Y,
                _ => throw new InvalidDataException(col),
            };
        }
        var scale = cols!.Select((c, j) => c == "psi_deg" ? 360.0 : rows.Max(r => Math.Abs(r[j]))).ToArray();
        var maxDiff = new double[cols.Length];
        var worstTick = new long[cols.Length];
        var iTick = Array.IndexOf(cols, "tick");
        var iX = Array.IndexOf(cols, "x");
        var iY = Array.IndexOf(cols, "y");
        var cmds = commands.OrderBy(c => c.Tick).ToList();
        var ci = 0;
        var ri = 0;
        var lastTick = (long)rows[^1][iTick];
        var track = 0.0;
        var (px, py) = (e.Motion.X, e.Motion.Y);
        var maxPosPct = 0.0;
        for (long tick = 0; tick <= lastTick; tick++)
        {
            if (ri < rows.Count && (long)rows[ri][iTick] == tick)
            {
                var row = rows[ri++];
                for (var j = 0; j < cols.Length; j++)
                {
                    var cs = Value(cols[j]);
                    var d = cols[j] == "psi_deg" ? Math.Abs(Units.WrapDeg180(cs - row[j])) : Math.Abs(cs - row[j]);
                    if (d > maxDiff[j]) { maxDiff[j] = d; worstTick[j] = tick; }
                }
                var dPos = Math.Sqrt((e.Motion.X - row[iX]) * (e.Motion.X - row[iX]) + (e.Motion.Y - row[iY]) * (e.Motion.Y - row[iY]));
                maxPosPct = Math.Max(maxPosPct, track > 1.0 ? 100.0 * dPos / track : 0.0);
            }
            if (tick == lastTick) break;
            while (ci < cmds.Count && cmds[ci].Tick == tick) e.Enqueue(cmds[ci++].Command);
            e.Step();
            track += Math.Sqrt((e.Motion.X - px) * (e.Motion.X - px) + (e.Motion.Y - py) * (e.Motion.Y - py));
            (px, py) = (e.Motion.X, e.Motion.Y);
        }
        Assert.Equal(rows.Count, ri);
        Assert.Equal(cmds.Count, ci);
        Assert.DoesNotContain(events, ev => ev.Kind == "commandRejected");
        _out.WriteLine($"航程 {track:F1} m;最大位置差 {maxPosPct:E3}% of track");
        for (var j = 0; j < cols.Length; j++)
        {
            if (cols[j] is "tick" or "t") continue;
            var rel = scale[j] > 1e-9 ? maxDiff[j] / scale[j] : maxDiff[j];
            _out.WriteLine($"  {cols[j]}: 最大差 {maxDiff[j]:E3}(尺度 {scale[j]:G6},相對 {rel:E3})@tick {worstTick[j]}");
            if (cols[j] is "x" or "y") continue;
            Assert.True(rel < 0.01, $"{cols[j]}:最大差 {maxDiff[j]:E3}(尺度 {scale[j]:G6})≥ 1%,@tick {worstTick[j]}");
        }
        Assert.True(maxPosPct < 1.0, $"位置差 {maxPosPct:F4}% ≥ 1%");
        // 事件序列(tick、phase、目標)與 Python 相同
        var got = events.Where(ev => ev.Kind == SimEventKinds.ShipInteraction).Select(ev => $"tick={ev.Tick} shipInteraction {ev.DataString("phase")} {ev.TargetId}").ToList();
        Assert.Equal(expectedEvents, got);
        // 交互作用有作用:航向偏離、側向速度不為 0
        Assert.True(Math.Abs(e.Motion.V) > 0.0);
    }
}
