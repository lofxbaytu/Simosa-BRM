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
/// 平均二階波浪漂移力(情境 environment.waves;設計文件 docs/design/physics-waves.md):方向對稱性(左右舷鏡像、迎浪/順浪 sway = 0)、
/// 量級(FSB 尺度 Hs 2 m 橫浪約十餘 t,與強風同量級;Hs²、長波衰減)、淺水波長(有限水深色散關係)、Bessel/Ursell 近似、
/// 無 waves 時輸出/運動/雜湊不變、引擎施力與 state.waves、快照還原與紀錄/重播一致、結構檢查,
/// 以及 C#↔Python 跨語言夾具(Golden/waves_force_table.csv 相對 1e-12、waves_drift_run.csv 軌跡 &lt; 1%;export_waves_reference.py 產生)。
/// </summary>
public class WaveDriftTests
{
    private readonly ITestOutputHelper _out;
    public WaveDriftTests(ITestOutputHelper output) => _out = output;

    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private const double TonneForce = 9806.65;

    private static ScenarioWaves Waves(double hs, double tp, double dir, string? spectrum = null, double? gamma = null)
        => new() { SignificantHeightM = hs, PeakPeriodS = tp, DirectionDeg = dir, Spectrum = spectrum, Gamma = gamma };

    private static WaveDriftModel Model(double hs = 2.0, double tp = 7.0, double dir = 90.0, string? spectrum = null, double? gamma = null,
        LoadingCondition loading = LoadingCondition.Full, string ship = "FSB1")
        => new(TestData.Ship(ship), loading, Waves(hs, tp, dir, spectrum, gamma), supported: true);

    private static StateVector At(double psiDeg, double u = 0.0, double v = 0.0) => new(u, v, 0.0, 0.0, 0.0, Units.DegToRad(psiDeg));

    /// <summary>內嵌情境:<paramref name="waves"/> 為 environment 之下的 waves 一行(兩格縮排)。</summary>
    private static Scenario.Scenario Yaml(string waves, double speedKn = 0.0, string telegraph = "STOP", double waterDepth = 30.0, string loading = "full", string extra = "")
        => ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_waves",
            "seed: 7",
            $"ship: {{ id: FSB1, loading: {loading} }}",
            "origin: { lat: 23.80, lon: 120.15 }",
            $"initial: {{ position: {{ x: 0, y: 0 }}, heading: 0, speed: {speedKn.ToString(CultureInfo.InvariantCulture)}, telegraph: {telegraph} }}",
            "environment:",
            "  wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }",
            "  current: { set: 0, drift: 0 }",
            $"  waterDepth: {waterDepth.ToString(CultureInfo.InvariantCulture)}",
            waves,
            extra,
        }) + "\n");

    private const string BeamWaves = "  waves: { significantHeight_m: 2.0, peakPeriod_s: 7.0, direction_deg: 90 }";

    private static void AssertRel(double expected, double actual, double rel, string what)
    {
        var scale = Math.Max(1.0, Math.Abs(expected));
        Assert.True(Math.Abs(expected - actual) <= rel * scale, $"{what}: expected {expected:R} actual {actual:R}");
    }

    // ------------------------------------------------------------------ 純函數

    [Theory]
    [InlineData(45.0, 0.0, 0.0, 0.0)]
    [InlineData(45.0, 0.0, 3.0, 0.2)]
    [InlineData(120.0, 30.0, 5.0, -0.4)]
    [InlineData(170.0, 200.0, -1.5, 0.0)]
    public void PortStarboardMirrorSymmetry(double dir, double psi, double u, double v)
    {
        // 鏡像:來向 −χ、側向速度 −v 時 X 相同,Y 與 N 反號
        var m = Model(dir: dir);
        var mirror = Model(dir: Units.NormalizeHeadingDeg(2.0 * psi - dir));
        var a = m.Compute(At(psi, u, v), 30.0);
        var b = mirror.Compute(At(psi, u, -v), 30.0);
        AssertRel(a.X, b.X, 1e-9, "X");
        AssertRel(-a.Y, b.Y, 1e-9, "Y");
        AssertRel(-a.N, b.N, 1e-9, "N");
        Assert.Equal(a.WavelengthM, b.WavelengthM);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(180.0, 0.0)]
    [InlineData(90.0, 90.0)]
    [InlineData(250.0, 70.0)]
    public void HeadAndFollowingSeasGiveNoSwayOrYaw(double dir, double psi)
    {
        var r = Model(dir: dir).Compute(At(psi, 2.0), 30.0);
        Assert.True(Math.Abs(r.Y) <= 1e-9, $"Y = {r.Y:R}");
        Assert.True(Math.Abs(r.N) <= 1e-9, $"N = {r.N:R}");
        Assert.NotEqual(0.0, r.X);
        // 迎浪為阻力(X < 0,船速使遭遇頻率升高而增大);順浪推船(X > 0)
        var rel = Units.WrapDeg180(dir - psi);
        if (Math.Abs(rel) < 1.0) Assert.True(r.X < 0.0);
        else Assert.True(r.X > 0.0);
    }

    [Fact]
    public void BeamSeasMagnitudeIsComparableToStrongWindAndScalesWithHs()
    {
        // FSB1 滿載、Hs 2 m / Tp 7 s 自右舷正橫、靜止:側向漂移力約 17–18 t 向左舷(設計文件第 4.2 節量級檢查)
        var r = Model().Compute(At(0.0), 50.0);
        _out.WriteLine($"beam Hs2 Tp7: X {r.X / TonneForce:F3} t, Y {r.Y / TonneForce:F2} t, N {r.N / 1000:F1} kN·m, K {r.Factor:F3}, λ {r.WavelengthM:F1} m");
        Assert.InRange(r.Y / TonneForce, -25.0, -10.0);
        Assert.True(Math.Abs(r.X) < 1e-6);
        Assert.True(Math.Abs(r.N) < 1e-6);
        // 與風力同量級:35 kn 正橫風(Blendermann,A_L 575 m²、C_Dt 0.7)約 8 t;浪漂移力在其 1–4 倍之間
        var wind35 = 0.5 * 1.225 * Math.Pow(Units.KnToMps(35.0), 2) * 574.87 * 0.7;
        Assert.InRange(Math.Abs(r.Y) / wind35, 1.0, 4.0);
        // ∝ Hs²
        var half = Model(hs: 1.0).Compute(At(0.0), 50.0);
        AssertRel(r.Y / 4.0, half.Y, 1e-12, "Hs² scaling");
        // 長波衰減:Tp 12 s(λ ≈ 225 m ≈ 2.3 L)小於 Tp 7 s 的 1/3;Tp 4 s(短波)接近完全反射 K ≈ 1
        var longWave = Model(tp: 12.0).Compute(At(0.0), 50.0);
        var shortWave = Model(tp: 4.0).Compute(At(0.0), 50.0);
        Assert.True(Math.Abs(longWave.Y) < Math.Abs(r.Y) / 3.0, $"Tp12 {longWave.Y / TonneForce:F2} t vs Tp7 {r.Y / TonneForce:F2} t");
        Assert.InRange(shortWave.Factor, 0.95, 1.0);
        Assert.InRange(longWave.Factor, 0.0, 0.25);
        // 壓載(吃水 3.72 m)在相同浪況下反射較少 → 力較小
        var ballast = Model(loading: LoadingCondition.Ballast).Compute(At(0.0), 50.0);
        Assert.True(Math.Abs(ballast.Y) < Math.Abs(r.Y));
        Assert.True(Math.Abs(ballast.Y) > 0.3 * Math.Abs(r.Y));
    }

    [Fact]
    public void BowQuarteringSeasTurnBowAwayFromWavesAndSpeedChangesEncounterFactor()
    {
        // 浪自右舷艏 045°、靜止:Y 向左舷(負)、N 艏向左(負,船趨向正橫受浪)
        var r = Model(dir: 45.0).Compute(At(0.0), 50.0);
        Assert.True(r.Y < 0.0 && r.N < 0.0 && r.X < 0.0, $"X {r.X} Y {r.Y} N {r.N}");
        // 迎浪前進 10 kn:遭遇頻率升高,K 約為靜止的 2 倍;順浪前進則大幅減小
        var still = Model(dir: 0.0).Compute(At(0.0), 50.0);
        var ahead = Model(dir: 0.0).Compute(At(0.0, Units.KnToMps(10.0)), 50.0);
        var following = Model(dir: 180.0).Compute(At(0.0, Units.KnToMps(10.0)), 50.0);
        Assert.InRange(ahead.Factor / still.Factor, 1.5, 3.0);
        Assert.InRange(following.Factor / still.Factor, 0.0, 0.4);
        // JONSWAP(較尖)與 PM 不同、γ = 1 的 JONSWAP 等於 PM
        var pm = Model(spectrum: "pm").Compute(At(0.0), 50.0);
        var js = Model(spectrum: "jonswap", gamma: 3.3).Compute(At(0.0), 50.0);
        var js1 = Model(spectrum: "jonswap", gamma: 1.0).Compute(At(0.0), 50.0);
        Assert.NotEqual(pm.Factor, js.Factor);
        AssertRel(pm.Factor, js1.Factor, 1e-12, "γ = 1");
    }

    [Fact]
    public void ShallowWaterWavelengthFollowsFiniteDepthDispersion()
    {
        const double tp = 7.0;
        var omega = 2.0 * Math.PI / tp;
        var lambda0 = 2.0 * Math.PI * WaveDriftModel.Gravity / (omega * omega); // 76.48 m
        // 一律有限水深色散:h = 50 m(kh ≈ 4.1)與深水差 tanh(kh) ≈ 0.06 %,連續而非在 λ0/2 切換
        var l50 = WaveDriftModel.PeakWavelength(tp, 50.0);
        Assert.True(l50 < lambda0 && l50 > 0.999 * lambda0, $"h 50: λ {l50} vs λ0 {lambda0}");
        Assert.Equal(lambda0, WaveDriftModel.PeakWavelength(tp, double.PositiveInfinity), 12);
        Assert.Equal(lambda0, WaveDriftModel.PeakWavelength(tp, 0.0), 12);            // 無水深資料視為深水
        foreach (var h in new[] { 38.0, 20.0, 10.0, 5.0, 2.0 })
        {
            var lambda = WaveDriftModel.PeakWavelength(tp, h);
            Assert.True(lambda < lambda0, $"h {h}: λ {lambda} ≥ λ0");
            var k = 2.0 * Math.PI / lambda;
            Assert.True(Math.Abs(WaveDriftModel.Gravity * k * Math.Tanh(k * h) - omega * omega) < 1e-9, $"h {h}: 色散關係殘差");
        }
        Assert.True(WaveDriftModel.PeakWavelength(tp, 5.0) < WaveDriftModel.PeakWavelength(tp, 20.0));
        // 模型輸出的 wavelength_m 隨自船水深改變;淺水波較短 → 反射較多 → K 較大
        var deep = Model().Compute(At(0.0), 50.0);
        var shallow = Model().Compute(At(0.0), 8.0);
        Assert.Equal(WaveDriftModel.PeakWavelength(tp, 50.0), deep.WavelengthM, 12);
        Assert.Equal(WaveDriftModel.PeakWavelength(tp, 8.0), shallow.WavelengthM, 12);
        Assert.True(shallow.Factor > deep.Factor);
    }

    [Fact]
    public void BesselAndUrsellApproximationsMatchTables()
    {
        Assert.Equal(0.5651591, WaveDriftModel.BesselI1(1.0), 6);
        Assert.Equal(0.6019072, WaveDriftModel.BesselK1(1.0), 6);
        Assert.Equal(24.33564, WaveDriftModel.BesselI1(5.0), 4);
        Assert.Equal(0.004044613, WaveDriftModel.BesselK1(5.0), 8);
        Assert.Equal(0.0, WaveDriftModel.UrsellReflection(0.0));
        Assert.Equal(0.0, WaveDriftModel.UrsellReflection(-1.0));
        var prev = 0.0;
        foreach (var x in new[] { 0.1, 0.3, 0.5, 0.7, 1.0, 1.5, 2.0, 3.0, 3.74, 3.76, 5.0, 10.0, 100.0, 1000.0 })
        {
            var a = WaveDriftModel.UrsellReflection(x);
            Assert.InRange(a, prev - 1e-6, 1.0);
            prev = a;
        }
        Assert.Equal(1.0, WaveDriftModel.UrsellReflection(1000.0), 12);
        Assert.InRange(WaveDriftModel.UrsellReflection(1.0), 0.89, 0.91);
    }

    // ------------------------------------------------------------------ 引擎

    [Fact]
    public void WithoutWavesOutputsAndMotionAreUnchanged()
    {
        var e = TestData.MinimalEngine();
        Assert.Null(e.Waves);
        TestData.RunTo(e, 3);
        var s = e.BuildState();
        Assert.Null(s.Waves);
        Assert.DoesNotContain("\"waves\"", ContractJson.Serialize(s));
        // Hs = 0 的 waves 區段:力恰為 0,運動與無 waves 逐位元相同;state.waves 仍輸出(零力),雜湊因追加區塊而不同
        var plain = MmgEngine(Yaml("", speedKn: 6.0, telegraph: "HAH"));
        var zeroHs = MmgEngine(Yaml("  waves: { significantHeight_m: 0, peakPeriod_s: 7, direction_deg: 90 }", speedKn: 6.0, telegraph: "HAH"));
        Assert.NotNull(zeroHs.Waves);
        Assert.True(zeroHs.Waves!.Active);
        for (var i = 0; i < 250; i++) { plain.Step(); zeroHs.Step(); }
        Assert.Equal(plain.Motion, zeroHs.Motion);
        var st = zeroHs.BuildState();
        Assert.NotNull(st.Waves);
        Assert.Equal(0.0, st.Waves!.XKN);
        Assert.Equal(0.0, st.Waves.YKN);
        Assert.Equal(0.0, st.Waves.SignificantHeightM);
        Assert.NotEqual(plain.StateHash, zeroHs.StateHash);
    }

    [Fact]
    public void EngineAppliesDriftForceAndReportsStateWaves()
    {
        // 靜止、浪自 090(右舷正橫):船向左舷(西)漂移;state.waves 帶出 Hs/Tp/來向/波長與力
        var e = MmgEngine(Yaml(BeamWaves));
        Assert.True(e.Waves is { Active: true });
        var s0 = e.BuildState();
        Assert.NotNull(s0.Waves);
        Assert.Equal(2.0, s0.Waves!.SignificantHeightM);
        Assert.Equal(7.0, s0.Waves.PeakPeriodS);
        Assert.Equal(90.0, s0.Waves.DirectionDeg);
        Assert.Equal("pm", s0.Waves.Spectrum);
        Assert.Equal(WaveDriftModel.PeakWavelength(7.0, 30.0), s0.Waves.WavelengthM, 12);
        Assert.True(s0.Waves.YKN < -100.0, $"Y {s0.Waves.YKN} kN");
        TestData.RunTo(e, 30);
        var s = e.BuildState();
        Assert.True(e.Motion.V < -0.05, $"v {e.Motion.V}");
        Assert.True(e.Motion.X < -1.0, $"x {e.Motion.X}");
        Assert.True(s.Waves!.YKN < 0.0);
        var json = ContractJson.Serialize(s);
        Assert.Contains("\"waves\":{\"significantHeight_m\":2", json);
        Assert.Contains("\"wavelength_m\":", json);
        Assert.Contains("\"Y_kN\":", json);
        var back = ContractJson.Deserialize<OwnShipState>(json)!;
        Assert.Equal(s.Waves.WavelengthM, back.Waves!.WavelengthM);
        // 無浪的同一情境靜止不動
        var calm = MmgEngine(Yaml(""));
        TestData.RunTo(calm, 30);
        Assert.True(Math.Abs(calm.Motion.X) < 1e-6 && Math.Abs(calm.Motion.V) < 1e-9);
    }

    [Fact]
    public void WavesCombineWithTugsAndBanks()
    {
        // 拖船 + 浪:合力 = 拖船 + 浪(SumForceField);只有浪時直接取浪
        const string tug = "tugs:\n  - { id: TG1, bollardPull_t: 30, attach: midStbd, riseTime_s: 10, initial: { mode: push, direction_deg: 270, power_pct: 50 } }";
        var e = MmgEngine(Yaml(BeamWaves, extra: tug));
        Assert.True(e.External.Active && e.Waves is { Active: true });
        TestData.RunTo(e, 5);
        var env = e.Environment.SampleAt(e.Motion.X, e.Motion.Y);
        var total = e.External.Evaluate(e.Motion, env) + e.Waves!.Evaluate(e.Motion, env);
        Assert.NotEqual(0.0, total.Y);
        Assert.Equal(e.Waves.Last.Y, e.Waves.Compute(e.Motion, e.Environment.DepthAt(e.Motion.X, e.Motion.Y)).Y);
    }

    [Fact]
    public void SnapshotRestoreContinuesBitIdentical()
    {
        var sc = Yaml("  waves: { significantHeight_m: 2.5, peakPeriod_s: 8.0, direction_deg: 135, spectrum: jonswap }", speedKn: 6.0, telegraph: "HAH", waterDepth: 15.0);
        var e = MmgEngine(sc);
        TestData.RunTo(e, 10);
        var snap = e.TakeSnapshot();
        TestData.RunTo(e, 20);
        var (hash, s20, last) = (e.StateHash, e.BuildState(), e.Waves!.Last);
        e.Restore(snap);
        Assert.Equal(snap.Tick, e.Tick);
        Assert.Equal(e.Waves!.Compute(e.Motion, e.Environment.DepthAt(e.Motion.X, e.Motion.Y)), e.Waves.Last);
        TestData.RunTo(e, 20);
        Assert.Equal(hash, e.StateHash);
        Assert.Equal(last, e.Waves.Last);
        Assert.Equal(s20.Waves!.YKN, e.BuildState().Waves!.YKN);
        // 同一輸入重跑雜湊相同
        var again = MmgEngine(sc);
        TestData.RunTo(again, 20);
        Assert.Equal(hash, again.StateHash);
    }

    [Fact]
    public void RecordReplayReproducesHashWithWaves()
    {
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, "waves_drift_run.yaml"));
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"waves-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        try
        {
            TestData.RunTo(e, 40);
            e.Enqueue(SimCommand.Rudder(10));
            TestData.RunTo(e, 60);
            w.Close();
            Assert.NotNull(e.BuildState().Waves);
            var r = Replayer.Replay(path, TestData.Ship, Mmg);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("  waves: { significantHeight_m: -1, peakPeriod_s: 7, direction_deg: 90 }", "environment.waves.significantHeight_m", "≥ 0")]
    [InlineData("  waves: { peakPeriod_s: 7, direction_deg: 90 }", "environment.waves.significantHeight_m", "significantHeight_m")]
    [InlineData("  waves: { significantHeight_m: 1, peakPeriod_s: 0, direction_deg: 90 }", "environment.waves.peakPeriod_s", "必須為正")]
    [InlineData("  waves: { significantHeight_m: 1, direction_deg: 90 }", "environment.waves.peakPeriod_s", "必須為正")]
    [InlineData("  waves: { significantHeight_m: 1, peakPeriod_s: 7, direction_deg: 400 }", "environment.waves.direction_deg", "0–360")]
    [InlineData("  waves: { significantHeight_m: 1, peakPeriod_s: 7, direction_deg: -5 }", "environment.waves.direction_deg", "0–360")]
    [InlineData("  waves: { significantHeight_m: 1, peakPeriod_s: 7 }", "environment.waves.direction_deg", "0–360")]
    [InlineData("  waves: { significantHeight_m: 1, peakPeriod_s: 7, direction_deg: 90, spectrum: bretschneider }", "environment.waves.spectrum", "pm | jonswap")]
    [InlineData("  waves: { significantHeight_m: 1, peakPeriod_s: 7, direction_deg: 90, gamma: 0.5 }", "environment.waves.gamma", "≥ 1")]
    public void StructuralIssuesRejectBadWaves(string waves, string path, string message)
    {
        var sc = Yaml(waves);
        var issues = sc.StructuralIssues().ToList();
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Error && i.Path == path && i.Message.Contains(message));
        Assert.Throws<InvalidDataException>(() => sc.Validate());
    }

    [Fact]
    public void ValidWavesHaveNoStructuralIssuesAndDefaults()
    {
        Assert.Empty(Yaml(BeamWaves).StructuralIssues());
        Assert.Empty(Yaml("  waves: { significantHeight_m: 0, peakPeriod_s: 12.5, direction_deg: 360, spectrum: JONSWAP, gamma: 1 }").StructuralIssues());
        var sc = Yaml("  waves: { significantHeight_m: 1.2, peakPeriod_s: 6, direction_deg: 360, spectrum: JONSWAP }").Validate();
        var m = WaveDriftModel.Create(TestData.Fsb1(), LoadingCondition.Full, sc, supported: true)!;
        Assert.Equal("jonswap", m.Spectrum);
        Assert.Equal(3.3, m.Gamma);
        Assert.Equal(0.0, m.DirectionDeg);           // 360 → 0
        Assert.Equal("jonswap", m.State().Spectrum);
        Assert.Null(WaveDriftModel.Create(TestData.Fsb1(), LoadingCondition.Full, Yaml("").Validate(), supported: true));
        // 動力學不接受外力時不啟用(不施力、不輸出、不追加雜湊)
        var unsupported = new WaveDriftModel(TestData.Fsb1(), LoadingCondition.Full, Waves(2, 7, 90), supported: false);
        Assert.False(unsupported.Active);
    }

    // ------------------------------------------------------------------ 跨語言夾具

    /// <summary>純函數取樣表:Python export_waves_reference.py 的每列以同船舶/裝載建模,同狀態與水深 Compute,相對差 ≤ 1e-12。</summary>
    [Fact]
    public void GoldenForceTableMatchesPythonReference()
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = File.ReadLines(Path.Combine(GoldenDirectory, "waves_force_table.csv")).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var cols = lines[0].Split(',');
        Assert.Equal(new[] { "ship", "loading", "Hs", "Tp", "dir_deg", "spectrum", "gamma", "depth_m", "psi_deg", "u", "v", "wavelength_m", "factor", "X_N", "Y_N", "N_Nm" }, cols);
        var rows = lines.Skip(1).Select(l => l.Split(',')).ToList();
        Assert.True(rows.Count >= 100, $"{rows.Count} rows");
        var models = new Dictionary<string, WaveDriftModel>();
        var worst = 0.0;
        string? worstWhat = null;
        foreach (var c in rows)
        {
            var key = string.Join("|", c.Take(7));
            if (!models.TryGetValue(key, out var m))
            {
                var loading = Enum.Parse<LoadingCondition>(c[1], ignoreCase: true);
                m = new WaveDriftModel(TestData.Ship(c[0]), loading,
                    Waves(double.Parse(c[2], inv), double.Parse(c[3], inv), double.Parse(c[4], inv), c[5], double.Parse(c[6], inv)), supported: true);
                models[key] = m;
            }
            var r = m.Compute(At(double.Parse(c[8], inv), double.Parse(c[9], inv), double.Parse(c[10], inv)), double.Parse(c[7], inv));
            foreach (var (name, expected, actual) in new[]
                     {
                         ("wavelength_m", double.Parse(c[11], inv), r.WavelengthM), ("factor", double.Parse(c[12], inv), r.Factor),
                         ("X_N", double.Parse(c[13], inv), r.X), ("Y_N", double.Parse(c[14], inv), r.Y), ("N_Nm", double.Parse(c[15], inv), r.N),
                     })
            {
                var rel = Math.Abs(expected - actual) / Math.Max(1.0, Math.Abs(expected));
                if (rel > worst) { worst = rel; worstWhat = $"{key} depth {c[7]} psi {c[8]} u {c[9]} v {c[10]} {name}: Python {expected:R} C# {actual:R}"; }
                Assert.True(rel <= 1e-12, $"{key} depth {c[7]} psi {c[8]} u {c[9]} v {c[10]} {name}: Python {expected:R} vs C# {actual:R}(相對差 {rel:E2})");
            }
        }
        _out.WriteLine($"{rows.Count} 列、{models.Count} 個模型;最大相對差 {worst:E2}({worstWhat})");
    }

    /// <summary>積分案例:同一份 YAML 建立 MMG 引擎,依標頭指令推進,每 1 s 比對位置/航向/速度/漂移力/波長(規劃書第 14 章 &lt; 1%)。</summary>
    [Fact]
    public void GoldenRunMatchesPythonWithinOnePercent()
    {
        var inv = CultureInfo.InvariantCulture;
        string? scenario = null;
        var commands = new List<(long Tick, SimCommand Command)>();
        string[]? cols = null;
        var rows = new List<double[]>();
        foreach (var raw in File.ReadLines(Path.Combine(GoldenDirectory, "waves_drift_run.csv")))
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
                continue;
            }
            if (cols is null) { cols = line.Split(','); continue; }
            rows.Add(line.Split(',').Select(v => double.Parse(v, inv)).ToArray());
        }
        Assert.NotNull(cols);
        Assert.Equal("waves_drift_run.yaml", scenario);
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, scenario!));
        var e = MmgEngine(sc);
        Assert.True(e.Waves is { Active: true });
        var events = TestData.Capture(e);
        double Value(string col)
        {
            var m = e.Motion;
            return col switch
            {
                "tick" => e.Tick, "t" => e.Time, "x" => m.X, "y" => m.Y, "psi_deg" => Units.NormalizeHeadingDeg(Units.RadToDeg(m.Psi)),
                "u" => m.U, "v" => m.V, "r" => m.R, "rpm" => e.Actuators.Rpm,
                "wave_X" => e.Waves!.Last.X / 1000.0, "wave_Y" => e.Waves!.Last.Y / 1000.0, "wave_N" => e.Waves!.Last.N / 1000.0,
                "wavelength_m" => e.Waves!.Last.WavelengthM,
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
        // 漂移力有作用:航向偏離 0°、船漂向左舷(浪自右舷艏)
        Assert.True(Math.Abs(e.Motion.V) > 0.0);
    }
}
