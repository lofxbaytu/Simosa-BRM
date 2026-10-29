using System.Globalization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using Xunit.Abstractions;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 解耦 1 自由度橫搖(情境 ship.roll;設計文件 docs/design/physics-roll.md):靜傾符號(右轉向外 = 左傾、風自右舷來 = 左傾)與風速平方,
/// 自然週期(Weiss 公式、GM 預設 ASSUMED)與自由衰減,浪激勵振幅 ∝ Hs、迎浪/順浪無激勵,相位確定性(同種子同相位、不同種子不同),
/// 無 roll 時輸出/雜湊位元不變且啟用時 3-DOF 運動位元不變(不回饋),共振附近振幅量級,練習中變更浪,快照/還原與紀錄/重播一致,
/// 結構檢查,以及 C#↔Python 跨語言夾具(Golden/roll_run.{yaml,csv};export_roll_reference.py 產生;φ、φ' 每秒 1e-9)。
/// </summary>
public class RollModelTests
{
    private readonly ITestOutputHelper _out;
    public RollModelTests(ITestOutputHelper output) => _out = output;

    private const string RollOn = "roll: { enabled: true }";

    /// <summary>內嵌情境:<paramref name="roll"/> 為 ship 之下的 roll 區段(一行物件;空字串 = 無);<paramref name="waves"/> 為 environment 之下的 waves 一行(兩格縮排)。</summary>
    private static Scenario.Scenario Yaml(string roll = RollOn, double speedKn = 0.0, string telegraph = "STOP", double windKn = 0.0, double windDir = 0.0,
        double gustiness = 0.0, string waves = "", double waterDepth = 30.0, string loading = "full", int seed = 7, string extra = "")
        => ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_roll",
            $"seed: {seed}",
            $"ship: {{ id: FSB1, loading: {loading}{(roll.Length > 0 ? ", " + roll : "")} }}",
            "origin: { lat: 23.80, lon: 120.15 }",
            $"initial: {{ position: {{ x: 0, y: 0 }}, heading: 0, speed: {speedKn.ToString(CultureInfo.InvariantCulture)}, telegraph: {telegraph} }}",
            "environment:",
            $"  wind: {{ trueSpeed: {windKn.ToString(CultureInfo.InvariantCulture)}, trueDir: {windDir.ToString(CultureInfo.InvariantCulture)}, gustiness: {gustiness.ToString(CultureInfo.InvariantCulture)} }}",
            "  current: { set: 0, drift: 0 }",
            $"  waterDepth: {waterDepth.ToString(CultureInfo.InvariantCulture)}",
            waves,
            extra,
        }) + "\n");

    private static ScenarioRoll Cfg(double? period = null, double? zeta = null, double? wind = null, double? turn = null, double? wave = null)
        => new() { Enabled = true, NaturalPeriodS = period, DampingRatio = zeta, WindHeelGain = wind, TurnHeelGain = turn, WaveGain = wave };

    private static RollModel Model(ScenarioRoll? cfg = null, int seed = 7, LoadingCondition loading = LoadingCondition.Full, string ship = "FSB1")
        => new(TestData.Ship(ship), loading, cfg ?? Cfg(), seed);

    private static ScenarioWaves Waves(double hs, double tp, double dir, string? spectrum = null, double? gamma = null)
        => new() { SignificantHeightM = hs, PeakPeriodS = tp, DirectionDeg = dir, Spectrum = spectrum, Gamma = gamma };

    private static void AssertRel(double expected, double actual, double rel, string what)
    {
        var scale = Math.Max(1.0, Math.Abs(expected));
        Assert.True(Math.Abs(expected - actual) <= rel * scale, $"{what}: expected {expected:R} actual {actual:R}");
    }

    private static void Apply(SimulationEngine e, SimCommand cmd)
    {
        e.Enqueue(cmd);
        e.ProcessPendingCommands();
    }

    // ------------------------------------------------------------------ 設定與純函數

    [Fact]
    public void DefaultNaturalPeriodFollowsWeissFormulaAndGmFallback()
    {
        // FSB1 滿載 GM 1.96 m(particulars):T = 2·0.4·18/√1.96 = 10.286 s;壓載 GM 4.15 m:7.07 s;FSB2 無 GM → 預設 1.2 m(ASSUMED):T = 0.8·20/√1.2 = 14.6 s
        var full = Model();
        Assert.True(full.GmFromParticulars);
        Assert.Equal(1.96, full.Gm);
        Assert.Equal(2.0 * 0.4 * 18.0 / Math.Sqrt(1.96), full.NaturalPeriodS, 12);
        Assert.InRange(full.NaturalPeriodS, 10.2, 10.4);
        var ballast = Model(loading: LoadingCondition.Ballast);
        Assert.Equal(4.15, ballast.Gm);
        Assert.InRange(ballast.NaturalPeriodS, 7.0, 7.2);
        var intermediate = Model(loading: LoadingCondition.Intermediate); // 由 full/ballast 平均合成 GM(與編輯器提示一致)
        Assert.True(intermediate.GmFromParticulars);
        Assert.Equal((1.96 + 4.15) / 2.0, intermediate.Gm, 12);
        var fsb2 = Model(ship: "FSB2");
        Assert.False(fsb2.GmFromParticulars);
        Assert.Equal(RollModel.DefaultGm, fsb2.Gm);
        Assert.InRange(fsb2.NaturalPeriodS, 14.5, 14.7);
        // FSB2 壓載排水量/Cb 缺值 → 依 Cb·L·B·T·ρ 估計(與 Python 同式),仍可建立
        var fsb2Ballast = Model(ship: "FSB2", loading: LoadingCondition.Ballast);
        Assert.InRange(fsb2Ballast.MassKg / 1000.0, 7000.0, 10000.0);
        // 情境給定值優先;預設 ζ 0.08、gain 1
        var given = Model(Cfg(period: 8.0, zeta: 0.05, wind: 0.5, turn: 1.4, wave: 0.8));
        Assert.Equal(8.0, given.NaturalPeriodS);
        Assert.Equal(2.0 * Math.PI / 8.0, given.Omega, 12);
        Assert.Equal(0.05, given.DampingRatio);
        Assert.Equal((0.5, 1.4, 0.8), (given.WindHeelGain, given.TurnHeelGain, given.WaveGain));
        Assert.Equal((RollModel.DefaultDampingRatio, 1.0, 1.0, 1.0), (full.DampingRatio, full.WindHeelGain, full.TurnHeelGain, full.WaveGain));
        // 風壓側面積與 Python particulars.windage_areas 的估計相同(FSB1 滿載 575 m²);力臂 = 形心高 + T/2
        Assert.InRange(full.LateralAreaM2, 574.0, 576.0);
        Assert.InRange(full.WindLeverM, 6.0, 7.5);
        _out.WriteLine($"FSB1 full: T {full.NaturalPeriodS:F3} s, A_L {full.LateralAreaM2:F1} m², z_w {full.WindLeverM:F2} m, Δ {full.MassKg / 1000:F0} t");
    }

    [Fact]
    public void TurnHeelIsOutwardAndEqualsCentrifugalAngle()
    {
        const double u = 5.0, r = 0.03;
        var right = RollModel.ComputeTurnHeel(u, 0.0, r, 1.0);
        var left = RollModel.ComputeTurnHeel(u, 0.0, -r, 1.0);
        // 右轉(r > 0)向外傾 = 左傾(負);左轉右傾(正);大小 = atan(v_c·r/g)
        Assert.True(right < 0.0 && left > 0.0, $"right {right} left {left}");
        Assert.Equal(-Math.Atan(u * r / RollModel.Gravity), right, 15);
        Assert.Equal(-right, left, 15);
        // v_c 為對水速度 √(u² + v²);gain 線性;靜止或無迴轉為 0
        Assert.Equal(-Math.Atan(Math.Sqrt(9.0 + 16.0) * r / RollModel.Gravity), RollModel.ComputeTurnHeel(3.0, 4.0, r, 1.0), 15);
        Assert.Equal(2.0 * right, RollModel.ComputeTurnHeel(u, 0.0, r, 2.0), 15);
        Assert.Equal(0.0, RollModel.ComputeTurnHeel(0.0, 0.0, r, 1.0));
        Assert.Equal(0.0, RollModel.ComputeTurnHeel(u, 0.0, 0.0, 1.0));
    }

    [Fact]
    public void WindHeelIsLeewardAndScalesWithWindSpeedSquared()
    {
        var m = Model();
        const double w = 18.0; // 35 kn ≈ 18 m/s
        // 風自右舷來(船艏向北,風自東 → 風向量 (−w, 0)):向下風 = 左傾(負);自左舷來為正;迎艏風無橫向分量 → 0
        var stbd = RollModel.ComputeWindHeel(-w, 0.0, 0.0, 0.0, 0.0, m.LateralAreaM2, m.WindLeverM, m.MassKg, m.Gm, 1.0);
        var port = RollModel.ComputeWindHeel(w, 0.0, 0.0, 0.0, 0.0, m.LateralAreaM2, m.WindLeverM, m.MassKg, m.Gm, 1.0);
        var head = RollModel.ComputeWindHeel(0.0, -w, 0.0, 0.0, 0.0, m.LateralAreaM2, m.WindLeverM, m.MassKg, m.Gm, 1.0);
        Assert.True(stbd < 0.0 && port > 0.0, $"stbd {stbd} port {port}");
        Assert.Equal(-stbd, port, 15);
        Assert.Equal(0.0, head);
        // ∝ 風速平方(tan φ 精確 4 倍)
        var twice = RollModel.ComputeWindHeel(-2.0 * w, 0.0, 0.0, 0.0, 0.0, m.LateralAreaM2, m.WindLeverM, m.MassKg, m.Gm, 1.0);
        AssertRel(4.0 * Math.Tan(stbd), Math.Tan(twice), 1e-12, "V² scaling");
        // 視風:船以 w 向東航行、無風 → 視風自右舷 w,與靜止受東風相同;航向旋轉 90° 後同一真風變成迎艏風
        var apparent = RollModel.ComputeWindHeel(0.0, 0.0, w, 0.0, 0.0, m.LateralAreaM2, m.WindLeverM, m.MassKg, m.Gm, 1.0);
        Assert.Equal(stbd, apparent, 15);
        Assert.Equal(0.0, RollModel.ComputeWindHeel(-w, 0.0, 0.0, 0.0, Math.PI / 2.0, m.LateralAreaM2, m.WindLeverM, m.MassKg, m.Gm, 1.0), 12);
        Assert.Equal(0.0, RollModel.ComputeWindHeel(-w, 0.0, 0.0, 0.0, 0.0, m.LateralAreaM2, m.WindLeverM, m.MassKg, m.Gm, 0.0));
        // 量級:FSB1 滿載 35 kn 正橫風靜傾約 0.2°(GM 1.96 m、A_L 575 m²;設計文件第 4.2 節)
        var deg = Units.RadToDeg(stbd);
        _out.WriteLine($"FSB1 full, 35 kn beam wind: static heel {deg:F3}°");
        Assert.InRange(Math.Abs(deg), 0.1, 1.0);
    }

    [Fact]
    public void FreeDecayHasDampedNaturalPeriodAndLogarithmicDecrement()
    {
        const double tn = 10.0, zeta = 0.08, dt = 0.02;
        var omega = 2.0 * Math.PI / tn;
        double angle = Units.DegToRad(10.0), rate = 0.0, t = 0.0;
        var crossings = new List<double>();
        var peaks = new List<double>();
        double prev = angle, prevRate = rate;
        for (var i = 0; i < 6000; i++)
        {
            (angle, rate) = RollModel.Integrate(angle, rate, omega, zeta, 0.0, null, t, dt);
            t += dt;
            if (prev < 0.0 && angle >= 0.0) crossings.Add(t);
            if (prevRate > 0.0 && rate <= 0.0 && angle > 0.0) peaks.Add(angle);
            prev = angle; prevRate = rate;
        }
        // 阻尼自然週期 T_d = T_n/√(1 − ζ²)
        var td = tn / Math.Sqrt(1.0 - zeta * zeta);
        Assert.True(crossings.Count >= 5);
        for (var i = 1; i < crossings.Count; i++) Assert.InRange(crossings[i] - crossings[i - 1], td * 0.99, td * 1.01);
        // 對數減幅:相鄰正峰比 = exp(−2πζ/√(1 − ζ²))
        var ratio = Math.Exp(-2.0 * Math.PI * zeta / Math.Sqrt(1.0 - zeta * zeta));
        Assert.True(peaks.Count >= 4);
        for (var i = 1; i < peaks.Count; i++) AssertRel(ratio, peaks[i] / peaks[i - 1], 0.02, $"peak ratio {i}");
        // 靜傾為常數時收斂到該值
        angle = 0.0; rate = 0.0; t = 0.0;
        for (var i = 0; i < 15000; i++) { (angle, rate) = RollModel.Integrate(angle, rate, omega, zeta, 0.05, null, t, dt); t += dt; }
        Assert.Equal(0.05, angle, 6);
        Assert.Equal(0.0, rate, 6);
    }

    [Fact]
    public void WaveExcitationScalesWithHsAndVanishesForHeadOrFollowingSeas()
    {
        var m = Model();
        Assert.False(m.HasWaves);
        Assert.Equal(0.0, m.WaveSlopeAt(0.0, 0.0, 0.0, 2.0, 12.3));
        m.SetWaves(Waves(2.0, 8.0, 90.0));
        Assert.True(m.HasWaves);
        var a1 = m.WaveSlopeAt(0.0, 0.0, 0.0, 1.0, 12.3);
        var a2 = m.WaveSlopeAt(0.0, 0.0, 0.0, 2.0, 12.3);
        Assert.NotEqual(0.0, a1);
        Assert.Equal(2.0 * a1, a2, 15);
        Assert.NotEqual(a2, m.WaveSlopeAt(0.0, 0.0, 0.0, 2.0, 13.3));
        // 迎浪(航向 090 = 來向)與順浪(航向 270)無橫向波斜率;自左舷來(航向 180)反號
        Assert.Equal(0.0, m.WaveSlopeAt(0.0, 0.0, Units.DegToRad(90.0), 2.0, 12.3), 15);
        Assert.Equal(0.0, m.WaveSlopeAt(0.0, 0.0, Units.DegToRad(270.0), 2.0, 12.3), 15);
        Assert.Equal(-a2, m.WaveSlopeAt(0.0, 0.0, Units.DegToRad(180.0), 2.0, 12.3), 15);
        // Hs = 0 或清除浪 → 0;波斜率振幅上限 Σ α_i(Hs 2 m / Tp 8 s 約數度)
        Assert.Equal(0.0, m.WaveSlopeAt(0.0, 0.0, 0.0, 0.0, 12.3));
        var maxSlope = 0.0;
        for (var t = 0.0; t < 200.0; t += 0.1) maxSlope = Math.Max(maxSlope, Math.Abs(m.WaveSlopeAt(0.0, 0.0, 0.0, 2.0, t)));
        _out.WriteLine($"Hs 2 m / Tp 8 s beam: max wave slope {Units.RadToDeg(maxSlope):F2}°");
        Assert.InRange(Units.RadToDeg(maxSlope), 1.0, 10.0);
        // waveGain 線性;鏡像來向(270)在相同位置的斜率反號(sin χ 反號、地固波場位置座標 s 反號但原點處 s = 0)
        var gained = Model(Cfg(wave: 0.5));
        gained.SetWaves(Waves(2.0, 8.0, 90.0));
        Assert.Equal(0.5 * a2, gained.WaveSlopeAt(0.0, 0.0, 0.0, 2.0, 12.3), 15);
        m.SetWaves(null);
        Assert.False(m.HasWaves);
        Assert.Equal(0.0, m.WaveSlopeAt(0.0, 0.0, 0.0, 2.0, 12.3));
    }

    [Fact]
    public void PhasesAreDeterministicFromSeed()
    {
        var a = RollModel.DrawPhases(7);
        var b = RollModel.DrawPhases(7);
        var c = RollModel.DrawPhases(8);
        Assert.Equal(RollModel.WaveComponents, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.All(a, p => Assert.InRange(p, 0.0, 2.0 * Math.PI));
        // .NET Random(7).NextDouble() 首值 0.38322046929189024(Python roll.DotNetRandom 逐位元移植,test_roll.py 以同值核對)
        Assert.Equal(2.0 * Math.PI * 0.38322046929189024, a[0], 15);
        Assert.Equal(2.0 * Math.PI * 0.24866858415709278, RollModel.DrawPhases(1)[0], 15);
        // 引擎的相位 = 情境種子;不同種子的兩個引擎在相同浪況下橫搖不同
        var e7 = new SimulationEngine(TestData.Fsb1(), Yaml(waves: "  waves: { significantHeight_m: 2, peakPeriod_s: 8, direction_deg: 90 }", seed: 7));
        var e8 = new SimulationEngine(TestData.Fsb1(), Yaml(waves: "  waves: { significantHeight_m: 2, peakPeriod_s: 8, direction_deg: 90 }", seed: 8));
        Assert.Equal(a, e7.Roll!.Phases);
        Assert.Equal(c, e8.Roll!.Phases);
        TestData.RunTo(e7, 20);
        TestData.RunTo(e8, 20);
        Assert.NotEqual(e7.Roll.Angle, e8.Roll.Angle);
        // 相同種子重建 → 相同
        var again = new SimulationEngine(TestData.Fsb1(), Yaml(waves: "  waves: { significantHeight_m: 2, peakPeriod_s: 8, direction_deg: 90 }", seed: 7));
        TestData.RunTo(again, 20);
        Assert.Equal(e7.Roll.Angle, again.Roll!.Angle);
        Assert.Equal(e7.StateHash, again.StateHash);
    }

    // ------------------------------------------------------------------ 引擎

    [Fact]
    public void WithoutRollOutputsAndHashAreUnchangedAndEnabledRollDoesNotFeedBack()
    {
        const string waves = "  waves: { significantHeight_m: 1.5, peakPeriod_s: 8, direction_deg: 60 }";
        var plain = new SimulationEngine(TestData.Fsb1(), Yaml(roll: "", speedKn: 8.0, telegraph: "HAH", windKn: 20.0, windDir: 90.0, gustiness: 0.3, waves: waves));
        var disabled = new SimulationEngine(TestData.Fsb1(), Yaml(roll: "roll: { enabled: false, dampingRatio: 0.1 }", speedKn: 8.0, telegraph: "HAH", windKn: 20.0, windDir: 90.0, gustiness: 0.3, waves: waves));
        var enabled = new SimulationEngine(TestData.Fsb1(), Yaml(roll: RollOn, speedKn: 8.0, telegraph: "HAH", windKn: 20.0, windDir: 90.0, gustiness: 0.3, waves: waves));
        Assert.Null(plain.Roll);
        Assert.Null(disabled.Roll);
        Assert.NotNull(enabled.Roll);
        Apply(plain, SimCommand.Rudder(15)); Apply(disabled, SimCommand.Rudder(15)); Apply(enabled, SimCommand.Rudder(15));
        for (var i = 0; i < 400; i++) { plain.Step(); disabled.Step(); enabled.Step(); }
        // 無 roll / enabled = false:狀態 JSON 與雜湊逐位元相同,無 roll 欄位
        var plainJson = ContractJson.Serialize(plain.BuildState());
        Assert.Equal(plainJson, ContractJson.Serialize(disabled.BuildState()));
        Assert.Equal(plain.StateHash, disabled.StateHash);
        Assert.DoesNotContain("\"roll\"", plainJson);
        Assert.Null(plain.BuildState().Roll);
        Assert.DoesNotContain("\"roll\"", disabled.CreateSnapshot().ToJson());
        // 啟用:3-DOF 運動、陣風係數與亂數計數逐位元相同(不回饋、相位用獨立產生器),雜湊因 ROLLDOF1 區塊而不同,state.roll 存在
        Assert.Equal(plain.Motion, enabled.Motion);
        Assert.Equal(plain.Environment.GustFactor, enabled.Environment.GustFactor);
        Assert.Equal(plain.Random.Count, enabled.Random.Count);
        Assert.NotEqual(plain.StateHash, enabled.StateHash);
        var s = enabled.BuildState();
        Assert.NotNull(s.Roll);
        Assert.NotEqual(0.0, s.Roll!.AngleDeg);
        Assert.Equal(enabled.Roll!.NaturalPeriodS, s.Roll.NaturalPeriodS);
        var json = ContractJson.Serialize(s);
        Assert.Contains("\"roll\":{\"angle_deg\":", json);
        Assert.Contains("\"staticHeel_deg\":", json);
        var back = ContractJson.Deserialize<OwnShipState>(json)!;
        Assert.Equal(s.Roll.AngleDeg, back.Roll!.AngleDeg);
        Assert.Contains("\"roll\":{\"angle\":", enabled.CreateSnapshot().ToJson());
    }

    [Fact]
    public void EngineReportsStaticHeelInTurnAndWindWithPlausibleMagnitudes()
    {
        // FSB1 滿載 15 kn、35 kn 正橫風(自右舷):無浪時橫搖收斂到靜傾(左傾);之後 35° 右舵迴轉 → 迴轉外傾(左傾)加大
        var e = MmgEngine(Yaml(speedKn: 15.0, telegraph: "NAVF", windKn: 35.0, windDir: 90.0, waterDepth: 1000.0));
        Assert.NotNull(e.Roll);
        TestData.RunTo(e, 90);
        var s = e.BuildState();
        Assert.NotNull(s.Roll);
        var windHeel = Units.RadToDeg(e.Roll!.WindHeel);
        _out.WriteLine($"15 kn, 35 kn beam wind from stbd: wind heel {windHeel:F3}°, static {s.Roll!.StaticHeelDeg:F3}°, angle {s.Roll.AngleDeg:F3}°");
        Assert.True(windHeel < 0.0, $"wind heel {windHeel}");
        Assert.InRange(Math.Abs(windHeel), 0.05, 1.0);
        Assert.InRange(Math.Abs(s.Roll.AngleDeg - s.Roll.StaticHeelDeg), 0.0, 0.05);  // 無浪:約 9 個週期後動態分量 < 0.05°
        Apply(e, SimCommand.Rudder(35));
        TestData.RunTo(e, 300);
        var turn = e.BuildState().Roll!;
        var turnHeel = Units.RadToDeg(e.Roll.TurnHeel);
        var rotDegPerMin = Units.RadPerSecToDegPerMin(e.Motion.R);
        _out.WriteLine($"35° stbd rudder steady turn: ROT {rotDegPerMin:F1}°/min, V {Units.MpsToKn(Math.Sqrt(e.Motion.U * e.Motion.U + e.Motion.V * e.Motion.V)):F1} kn, turn heel {turnHeel:F3}°, static {turn.StaticHeelDeg:F3}°");
        Assert.True(e.Motion.R > 0.0 && turnHeel < 0.0, $"r {e.Motion.R} turn heel {turnHeel}");
        Assert.InRange(Math.Abs(turnHeel), 0.3, 5.0);
        Assert.Equal(Units.RadToDeg(e.Roll.TurnHeel + e.Roll.WindHeel), turn.StaticHeelDeg, 9);
        // 左轉時向右傾
        var left = MmgEngine(Yaml(speedKn: 15.0, telegraph: "NAVF", waterDepth: 1000.0));
        Apply(left, SimCommand.Rudder(-35));
        TestData.RunTo(left, 200);
        Assert.True(left.Motion.R < 0.0 && left.Roll!.TurnHeel > 0.0 && left.BuildState().Roll!.AngleDeg > 0.0);
    }

    [Fact]
    public void ResonantBeamSeasRollAmplitudeIsPlausibleAndLargerThanOffResonance()
    {
        // 靜止、Hs 2 m 正橫浪(自右舷)、Tp = 自然週期(10.29 s):共振附近振幅數度到二十度;Tp 4 s(短波、遭遇頻率遠高於 ω_n)與 Tp 20 s(長波、波斜率小)較小
        var tn = Model().NaturalPeriodS;
        double MaxAngle(double tp)
        {
            var e = new SimulationEngine(TestData.Fsb1(), Yaml(waves: $"  waves: {{ significantHeight_m: 2, peakPeriod_s: {tp.ToString("R", CultureInfo.InvariantCulture)}, direction_deg: 90 }}"));
            var max = 0.0;
            for (var i = 0; i < 50 * 300; i++)
            {
                e.Step();
                max = Math.Max(max, Math.Abs(e.Roll!.Angle));
            }
            return Units.RadToDeg(max);
        }
        var resonant = MaxAngle(tn);
        var shortWave = MaxAngle(4.0);
        var longWave = MaxAngle(20.0);
        _out.WriteLine($"Hs 2 m beam seas: Tp = Tn {tn:F2} s → max {resonant:F2}°; Tp 4 s → {shortWave:F2}°; Tp 20 s → {longWave:F2}°");
        Assert.InRange(resonant, 3.0, 30.0);
        // 短波(Tp 4 s,λ 25 m)波斜率大而 RAO 小:本模型無短波有效波斜率折減(設計文件第 7 節),振幅約為共振的一半
        Assert.True(shortWave < 0.7 * resonant, $"short {shortWave} vs resonant {resonant}");
        Assert.True(longWave < resonant, $"long {longWave} vs resonant {resonant}");
        // 線性:相同運動輸入下 Hs 減半(或 waveGain 減半)→ φ 減半(引擎內 Hs 也改變漂移力與位置/航向,故以純模型同輸入比較)
        var m1 = Model(); var m2 = Model(); var g5 = Model(Cfg(wave: 0.5));
        var beam = Waves(2.0, 9.0, 90.0);
        m1.SetWaves(beam); m2.SetWaves(beam); g5.SetWaves(beam);
        var still = new StateVector(0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
        var calm = new EnvironmentSample(0.0, 0.0, 0.0, 0.0, 30.0);
        for (var i = 0; i < 6000; i++)
        {
            var t = i * Dt;
            m1.Step(still, calm, 1.0, t, Dt);
            m2.Step(still, calm, 2.0, t, Dt);
            g5.Step(still, calm, 2.0, t, Dt);
        }
        Assert.NotEqual(0.0, m1.Angle);
        AssertRel(2.0 * m1.Angle, m2.Angle, 1e-12, "Hs linearity");
        AssertRel(m1.Angle, g5.Angle, 1e-12, "waveGain linearity");
    }

    [Fact]
    public void SetEnvironmentWavesChangesExcitationAndRampedHsIsUsed()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Yaml());
        Assert.False(e.Roll!.HasWaves);
        TestData.RunTo(e, 5);
        Assert.Equal(0.0, e.Roll.WaveSlope);
        Assert.Equal(0.0, e.Roll.Angle);
        // 指令加浪(正橫):激勵成分建立,之後有波斜率與橫搖;Hs 漸變中以模型目前 Hs 計算
        Apply(e, SimCommand.SetWaves(2.0, 8.0, 90.0, rampS: 20.0));
        Assert.True(e.Roll.HasWaves);
        TestData.RunTo(e, 10);
        Assert.NotEqual(0.0, e.Roll.WaveSlope);
        var midHs = e.Waves!.SignificantHeightM;
        Assert.InRange(midHs, 0.1, 1.9);
        var slopeAtHs = e.Roll.WaveSlopeAt(e.Motion.X, e.Motion.Y, e.Motion.Psi, midHs, e.Time);
        Assert.Equal(slopeAtHs, e.Roll.WaveSlopeAt(e.Motion.X, e.Motion.Y, e.Motion.Psi, 1.0, e.Time) * midHs, 12);
        TestData.RunTo(e, 60);
        Assert.NotEqual(0.0, e.Roll.Angle);
        // 關閉浪:不再激勵,橫搖自由衰減
        Apply(e, SimCommand.ClearWaves());
        Assert.False(e.Roll.HasWaves);
        e.Step();
        Assert.Equal(0.0, e.Roll.WaveSlope);
        var a0 = Math.Abs(e.Roll.Angle);
        TestData.RunTo(e, 160);
        Assert.True(Math.Abs(e.Roll.Angle) < a0 * 0.1 + 1e-6);
    }

    [Fact]
    public void SnapshotRestoreContinuesBitIdentical()
    {
        var sc = Yaml(speedKn: 8.0, telegraph: "HAH", windKn: 25.0, windDir: 60.0, gustiness: 0.2, waves: "  waves: { significantHeight_m: 2, peakPeriod_s: 9, direction_deg: 120, spectrum: jonswap }");
        var e = MmgEngine(sc);
        Apply(e, SimCommand.Rudder(20));
        TestData.RunTo(e, 10);
        var snap = e.TakeSnapshot();
        Assert.NotNull(snap.Roll);
        Assert.Equal(e.Roll!.Phases, snap.Roll!.Phases);
        TestData.RunTo(e, 40);
        var (hash, s40, angle, rate) = (e.StateHash, e.BuildState(), e.Roll.Angle, e.Roll.Rate);
        e.Restore(snap);
        Assert.Equal(snap.Tick, e.Tick);
        Assert.Equal(snap.Roll.Angle, e.Roll!.Angle);
        Assert.Equal(snap.Roll.Rate, e.Roll.Rate);
        Assert.Equal(snap.Roll.Phases, e.Roll.Phases);
        Assert.True(e.Roll.HasWaves);
        TestData.RunTo(e, 40);
        Assert.Equal(hash, e.StateHash);
        Assert.Equal(angle, e.Roll.Angle);
        Assert.Equal(rate, e.Roll.Rate);
        Assert.Equal(s40.Roll!.AngleDeg, e.BuildState().Roll!.AngleDeg);
        // 快照 JSON 往返後還原亦相同;同一輸入重跑雜湊相同
        var roundTrip = EngineSnapshot.FromJson(snap.ToJson());
        Assert.Equal(snap.Roll.Phases, roundTrip.Roll!.Phases);
        e.Restore(roundTrip);
        TestData.RunTo(e, 40);
        Assert.Equal(hash, e.StateHash);
        var again = MmgEngine(sc);
        Apply(again, SimCommand.Rudder(20));
        TestData.RunTo(again, 40);
        Assert.Equal(hash, again.StateHash);
        // 舊快照(無 Roll 欄位)還原 → 橫搖回到初始(φ = 0、相位依種子)
        var legacy = snap with { Roll = null };
        e.Restore(legacy);
        Assert.Equal(0.0, e.Roll!.Angle);
        Assert.Equal(RollModel.DrawPhases(sc.Seed), e.Roll.Phases);
    }

    [Fact]
    public void RecordReplayReproducesHashWithRoll()
    {
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, "roll_run.yaml"));
        Assert.True(sc.Ship.Roll is { Enabled: true });
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, (s, l) => Physics.Mmg.MmgDynamics.Load(TestData.Root, s, l));
        var path = Path.Combine(TestData.TestRecordsDirectory, $"roll-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        try
        {
            TestData.RunTo(e, 30);
            e.Enqueue(SimCommand.Rudder(20));
            TestData.RunTo(e, 60);
            w.Close();
            Assert.NotNull(e.BuildState().Roll);
            var r = Replayer.Replay(path, TestData.Ship, (s, l) => Physics.Mmg.MmgDynamics.Load(TestData.Root, s, l));
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ 結構檢查

    [Theory]
    [InlineData("roll: { enabled: true, naturalPeriod_s: 0 }", "ship.roll.naturalPeriod_s", "必須為正")]
    [InlineData("roll: { enabled: true, naturalPeriod_s: -5 }", "ship.roll.naturalPeriod_s", "必須為正")]
    [InlineData("roll: { enabled: true, dampingRatio: 0 }", "ship.roll.dampingRatio", "0–1")]
    [InlineData("roll: { enabled: true, dampingRatio: 1 }", "ship.roll.dampingRatio", "0–1")]
    [InlineData("roll: { enabled: false, dampingRatio: 1.5 }", "ship.roll.dampingRatio", "0–1")]
    [InlineData("roll: { enabled: true, windHeelGain: -0.1 }", "ship.roll.windHeelGain", "≥ 0")]
    [InlineData("roll: { enabled: true, turnHeelGain: -1 }", "ship.roll.turnHeelGain", "≥ 0")]
    [InlineData("roll: { enabled: true, waveGain: -2 }", "ship.roll.waveGain", "≥ 0")]
    public void StructuralIssuesRejectBadRoll(string roll, string path, string message)
    {
        var sc = Yaml(roll: roll);
        var issues = sc.StructuralIssues().ToList();
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Error && i.Path == path && i.Message.Contains(message));
        Assert.Throws<InvalidDataException>(() => sc.Validate());
    }

    [Fact]
    public void ValidRollHasNoStructuralIssuesAndDefaultsApply()
    {
        Assert.Empty(Yaml(roll: RollOn).StructuralIssues());
        Assert.Empty(Yaml(roll: "roll: { enabled: true, naturalPeriod_s: 9, dampingRatio: 0.05, windHeelGain: 0, turnHeelGain: 1.4, waveGain: 0.8 }").StructuralIssues());
        Assert.Empty(Yaml(roll: "").StructuralIssues());
        var sc = Yaml(roll: "roll: { enabled: true, naturalPeriod_s: 9, dampingRatio: 0.05, windHeelGain: 0, turnHeelGain: 1.4, waveGain: 0.8 }");
        var r = sc.Ship.Roll!;
        Assert.Equal((true, 9.0, 0.05, 0.0, 1.4, 0.8), (r.Enabled, r.NaturalPeriodS, r.DampingRatio, r.WindHeelGain, r.TurnHeelGain, r.WaveGain));
        // 情境 JSON(紀錄標頭)往返保留 ship.roll;無 roll 的情境不寫出該鍵
        var json = sc.ToJson();
        Assert.Contains("\"roll\":{\"enabled\":true,\"naturalPeriod_s\":9", json);
        Assert.Equal(9.0, Scenario.Scenario.FromJson(json).Ship.Roll!.NaturalPeriodS);
        Assert.DoesNotContain("\"roll\"", Yaml(roll: "").ToJson());
    }

    // ------------------------------------------------------------------ 跨語言夾具

    private sealed record Golden(string ScenarioFile, List<(long Tick, SimCommand Command)> Commands, string[] Columns, List<double[]> Rows);

    private static Golden LoadGolden(string file)
    {
        var inv = CultureInfo.InvariantCulture;
        string? scenario = null;
        var commands = new List<(long, SimCommand)>();
        string[]? cols = null;
        var rows = new List<double[]>();
        foreach (var raw in File.ReadLines(Path.Combine(GoldenDirectory, file)))
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
                    commands.Add((long.Parse(rest[..sp], inv), ContractJson.Deserialize<SimCommand>(rest[(sp + 1)..]) ?? throw new InvalidDataException(line)));
                }
                continue;
            }
            if (cols is null) { cols = line.Split(','); continue; }
            rows.Add(line.Split(',').Select(v => double.Parse(v, inv)).ToArray());
        }
        return new Golden(scenario ?? throw new InvalidDataException($"{file} 缺少 # scenario:"), commands, cols ?? throw new InvalidDataException($"{file} 缺少欄名"), rows);
    }

    private static double Value(SimulationEngine e, string col)
    {
        var m = e.Motion;
        var r = e.Roll!;
        return col switch
        {
            "tick" => e.Tick,
            "t" => e.Time,
            "x" => m.X,
            "y" => m.Y,
            "psi_deg" => Units.NormalizeHeadingDeg(Units.RadToDeg(m.Psi)),
            "u" => m.U,
            "v" => m.V,
            "r" => m.R,
            "roll_deg" => Units.RadToDeg(r.Angle),
            "rollRate_degps" => Units.RadToDeg(r.Rate),
            "staticHeel_deg" => Units.RadToDeg(r.StaticHeel),
            "turnHeel_deg" => Units.RadToDeg(r.TurnHeel),
            "windHeel_deg" => Units.RadToDeg(r.WindHeel),
            "waveSlope_deg" => Units.RadToDeg(r.WaveSlope),
            _ => throw new ArgumentException(col),
        };
    }

    [Fact]
    public void GoldenRollRunMatchesPythonReference()
    {
        // Python 參考實作(roll.py 同公式同運算順序)的 300 s 積分案例:φ、φ' 與靜傾/波斜率每秒 |差| ≤ 1e-9·max(1, |值|);位置差 / 航程 < 1%
        var g = LoadGolden("roll_run.csv");
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, g.ScenarioFile));
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, (s, l) => Physics.Mmg.MmgDynamics.Load(TestData.Root, s, l), new EngineOptions { Dt = Dt, AutoSnapshotIntervalTicks = 0 });
        Assert.NotNull(e.Roll);
        Assert.Equal(301, g.Rows.Count);
        var tickCol = Array.IndexOf(g.Columns, "tick");
        var worst = new Dictionary<string, double>();
        var ci = 0;
        var cmds = g.Commands.OrderBy(c => c.Tick).ToList();
        var pathLength = 0.0;
        var prev = (e.Motion.X, e.Motion.Y);
        foreach (var row in g.Rows)
        {
            var tick = (long)row[tickCol];
            while (e.Tick < tick)
            {
                while (ci < cmds.Count && cmds[ci].Tick == e.Tick) { e.Enqueue(cmds[ci].Command); ci++; }
                e.Step();
                pathLength += Math.Sqrt((e.Motion.X - prev.X) * (e.Motion.X - prev.X) + (e.Motion.Y - prev.Y) * (e.Motion.Y - prev.Y));
                prev = (e.Motion.X, e.Motion.Y);
            }
            for (var j = 0; j < g.Columns.Length; j++)
            {
                var col = g.Columns[j];
                var expected = row[j];
                var actual = Value(e, col);
                var diff = Math.Abs(expected - actual) / Math.Max(1.0, Math.Abs(expected));
                if (!worst.TryGetValue(col, out var w) || diff > w) worst[col] = diff;
                if (col is "roll_deg" or "rollRate_degps" or "staticHeel_deg" or "turnHeel_deg" or "windHeel_deg" or "waveSlope_deg")
                    Assert.True(diff <= 1e-9, $"tick {tick} {col}: Python {expected:R} C# {actual:R}(差 {diff:E2})");
                else if (col is "x" or "y")
                    Assert.True(Math.Abs(expected - actual) <= 0.01 * Math.Max(1.0, pathLength), $"tick {tick} {col}: Python {expected:R} C# {actual:R}");
                else Assert.True(diff <= 1e-2, $"tick {tick} {col}: Python {expected:R} C# {actual:R}");
            }
        }
        Assert.Equal(cmds.Count, ci);
        _out.WriteLine(string.Join(", ", worst.Select(kv => $"{kv.Key} {kv.Value:E1}")));
        var maxRoll = g.Rows.Max(r => Math.Abs(r[Array.IndexOf(g.Columns, "roll_deg")]));
        _out.WriteLine($"max |φ| {maxRoll:F2}°, path {pathLength:F0} m");
        Assert.InRange(maxRoll, 3.0, 30.0);
    }
}
