using System.Globalization;
using System.Text.Json.Nodes;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;
using Xunit.Abstractions;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 浪一階運動:升沉與縱搖(情境 ship.seakeeping;R2「浪」;設計文件 docs/design/physics-waves.md 第 11 節):自然週期與 ASSUMED 預設、
/// RAO 與長度濾波的極限、長波極限(升沉 → 波面、縱搖 → 波斜率)、橫浪無縱搖而頂浪/順浪最大、共振附近放大最大、速率為沿航跡的時間微分、
/// 與橫搖共用同一波場與相位、Hs 線性、無浪或 Hs = 0 無輸出、未啟用時輸出/雜湊位元不變且啟用時 3-DOF 運動位元不變(不回饋)、
/// 練習中變更浪、快照/還原與紀錄/重播一致、結構檢查、C#↔Python 跨語言夾具(Golden/seakeeping_table.csv 1e-12、seakeeping_run.{yaml,csv} 1e-9;
/// export_seakeeping_reference.py 產生)與情境 E17b_typhoon_seakeeping。
/// </summary>
public class SeakeepingModelTests
{
    private readonly ITestOutputHelper _out;
    public SeakeepingModelTests(ITestOutputHelper output) => _out = output;

    private const string SeaOn = "seakeeping: { enabled: true }";
    private const string QuarteringWaves = "  waves: { significantHeight_m: 2.5, peakPeriod_s: 8, direction_deg: 45, spectrum: jonswap }";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);

    /// <summary>
    /// 內嵌情境:<paramref name="seakeeping"/> 為 ship 之下的 seakeeping 區段(一行物件;空字串 = 無);<paramref name="waves"/> 為 environment 之下的 waves 一行(兩格縮排);
    /// 未指定車鐘 → 引擎取直航平衡轉速。
    /// </summary>
    private static Scenario.Scenario Yaml(string seakeeping = SeaOn, string ship = "FSB2", double speedKn = 0.0, double heading = 0.0, double windKn = 0.0,
        double windDir = 0.0, double gustiness = 0.0, string waves = "", double waterDepth = 1000.0, string loading = "full", int seed = 7, string extra = "")
        => ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_seakeeping",
            $"seed: {seed}",
            $"ship: {{ id: {ship}, loading: {loading}{(seakeeping.Length > 0 ? ", " + seakeeping : "")} }}",
            "origin: { lat: 23.80, lon: 120.15 }",
            $"initial: {{ position: {{ x: 0, y: 0 }}, heading: {heading.ToString(Inv)}, speed: {speedKn.ToString(Inv)} }}",
            "environment:",
            $"  wind: {{ trueSpeed: {windKn.ToString(Inv)}, trueDir: {windDir.ToString(Inv)}, gustiness: {gustiness.ToString(Inv)} }}",
            "  current: { set: 0, drift: 0 }",
            $"  waterDepth: {waterDepth.ToString(Inv)}",
            waves,
            extra,
        }) + "\n");

    private static SimulationEngine Engine(Scenario.Scenario sc)
        => new(TestData.Ship(sc.Ship.Id), sc, Mmg, new EngineOptions { Dt = Dt, AutoSnapshotIntervalTicks = 0 });

    private static ScenarioSeakeeping Cfg(double? heaveZeta = null, double? pitchZeta = null, double? gyradius = null, double? gmL = null)
        => new() { Enabled = true, HeaveDampingRatio = heaveZeta, PitchDampingRatio = pitchZeta, PitchGyradiusRatio = gyradius, GmLM = gmL };

    private static SeakeepingModel Model(ScenarioSeakeeping? cfg = null, int seed = 7, LoadingCondition loading = LoadingCondition.Full, string ship = "FSB2")
        => new(TestData.Ship(ship), loading, cfg ?? Cfg(), seed);

    private static ScenarioWaves Waves(double hs, double tp, double dir, string? spectrum = null, double? gamma = null)
        => new() { SignificantHeightM = hs, PeakPeriodS = tp, DirectionDeg = dir, Spectrum = spectrum, Gamma = gamma };

    private static StateVector At(double psiDeg, double u = 0.0, double v = 0.0, double x = 0.0, double y = 0.0)
        => new(u, v, 0.0, x, y, Units.DegToRad(psiDeg));

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

    /// <summary>等速直線航行(航向 psiDeg、對水速度 u、無流)取樣 <paramref name="seconds"/> 秒(每 0.2 s)的最大 |升沉|(m)與 |縱搖|(度)。</summary>
    private static (double Heave, double PitchDeg) Amplitudes(SeakeepingModel m, double psiDeg, double hs, double u = 0.0, double seconds = 1200.0)
    {
        var psi = Units.DegToRad(psiDeg);
        var maxH = 0.0;
        var maxP = 0.0;
        for (var i = 0; i <= (int)(seconds * 5); i++)
        {
            var t = i * 0.2;
            var r = m.Evaluate(new StateVector(u, 0.0, 0.0, u * Math.Sin(psi) * t, u * Math.Cos(psi) * t, psi), 0.0, 0.0, hs, t)!.Value;
            maxH = Math.Max(maxH, Math.Abs(r.Heave));
            maxP = Math.Max(maxP, Math.Abs(r.Pitch));
        }
        return (maxH, Units.RadToDeg(maxP));
    }

    // ------------------------------------------------------------------ 設定與純函數

    [Fact]
    public void NaturalPeriodsAndAssumedDefaultsFollowParticulars()
    {
        // FSB2 滿載(L 104、B 20、T 7.2、C_b 0.765、Δ 11,773 t):C_wp = (1 + 2C_b)/3 = 0.843、A_wp = 1,754 m²、∇ = 11,486 m³
        // → 升沉 ω_h² = g·A_wp/(2∇):T_h 7.26 s;GM_L = 3·C_wp²·L²/(40·C_b·T) = 104.7 m(1.01 L);縱搖 ω_p² = g·GM_L/(1.2·(0.25 L)²):T_p 5.58 s
        var fsb2 = Model();
        Assert.True(fsb2.Enabled);
        Assert.Equal((104.0, 20.0, 7.2, 0.765), (fsb2.L, fsb2.B, fsb2.T, fsb2.Cb));
        Assert.Equal((1.0 + 2.0 * 0.765) / 3.0, fsb2.Cwp, 15);
        Assert.Equal(fsb2.Cwp * 104.0 * 20.0, fsb2.WaterplaneAreaM2, 9);
        Assert.Equal(11773.2 * 1000.0 / 1025.0, fsb2.VolumeM3, 9);
        Assert.Equal(Math.Sqrt(SeakeepingModel.Gravity * fsb2.WaterplaneAreaM2 / (2.0 * fsb2.VolumeM3)), fsb2.HeaveOmega, 15);
        Assert.InRange(fsb2.HeaveNaturalPeriodS, 7.2, 7.3);
        Assert.False(fsb2.GmLFromScenario);
        Assert.Equal(3.0 * fsb2.Cwp * fsb2.Cwp * 104.0 * 104.0 / (40.0 * 0.765 * 7.2), fsb2.GmL, 12);
        Assert.InRange(fsb2.GmL / fsb2.L, 1.0, 1.2);
        Assert.Equal(Math.Sqrt(SeakeepingModel.Gravity * fsb2.GmL / (1.2 * 26.0 * 26.0)), fsb2.PitchOmega, 15);
        Assert.InRange(fsb2.PitchNaturalPeriodS, 5.5, 5.7);
        Assert.Equal((SeakeepingModel.DefaultHeaveDampingRatio, SeakeepingModel.DefaultPitchDampingRatio, SeakeepingModel.DefaultPitchGyradiusRatio),
            (fsb2.HeaveDampingRatio, fsb2.PitchDampingRatio, fsb2.PitchGyradiusRatio));
        Assert.Equal((0.35, 0.35, 0.25), (fsb2.HeaveDampingRatio, fsb2.PitchDampingRatio, fsb2.PitchGyradiusRatio));
        // FSB1 滿載:GM_L 119 m(1.20 L)、T_h 6.4 s、T_p 5.0 s;壓載吃水淺 → GM_L 較大、兩個週期較短
        var fsb1 = Model(ship: "FSB1");
        Assert.InRange(fsb1.GmL / fsb1.L, 1.0, 1.25);
        Assert.InRange(fsb1.HeaveNaturalPeriodS, 6.3, 6.5);
        Assert.InRange(fsb1.PitchNaturalPeriodS, 4.9, 5.1);
        var fsb1Ballast = Model(ship: "FSB1", loading: LoadingCondition.Ballast);
        Assert.True(fsb1Ballast.GmL > fsb1.GmL && fsb1Ballast.HeaveNaturalPeriodS < fsb1.HeaveNaturalPeriodS && fsb1Ballast.PitchNaturalPeriodS < fsb1.PitchNaturalPeriodS);
        // FSB2 壓載的 C_b 與排水量缺值 → 由滿載 C_b 依吃水比、C_b·L·B·T 估計(與橫搖/Python 同式),仍可建立
        var fsb2Ballast = Model(loading: LoadingCondition.Ballast);
        Assert.InRange(fsb2Ballast.Cb, 0.72, 0.75);
        Assert.Equal(fsb2Ballast.Cb * 104.0 * 20.0 * 5.45, fsb2Ballast.VolumeM3, 6);
        // 情境給定值優先:GM_L、k_yy/L 與阻尼比
        var given = Model(Cfg(heaveZeta: 0.2, pitchZeta: 0.5, gyradius: 0.22, gmL: 95.0));
        Assert.True(given.GmLFromScenario);
        Assert.Equal((95.0, 0.22, 0.2, 0.5), (given.GmL, given.PitchGyradiusRatio, given.HeaveDampingRatio, given.PitchDampingRatio));
        Assert.Equal(Math.Sqrt(SeakeepingModel.Gravity * 95.0 / (1.2 * (0.22 * 104.0) * (0.22 * 104.0))), given.PitchOmega, 15);
        Assert.Equal(fsb2.HeaveOmega, given.HeaveOmega);   // 升沉自然頻率不受這些設定影響
        _out.WriteLine($"FSB2 full: T_h {fsb2.HeaveNaturalPeriodS:F2} s, T_p {fsb2.PitchNaturalPeriodS:F2} s, GM_L {fsb2.GmL:F1} m; FSB1 full: T_h {fsb1.HeaveNaturalPeriodS:F2} s, T_p {fsb1.PitchNaturalPeriodS:F2} s, GM_L {fsb1.GmL:F1} m; " +
                       $"FSB1 ballast GM_L {fsb1Ballast.GmL:F1} m; FSB2 ballast C_b {fsb2Ballast.Cb:F3} GM_L {fsb2Ballast.GmL:F1} m");
    }

    [Fact]
    public void ResponseIsQuasiStaticAtLowFrequencyLargestNearResonanceAndVanishesAtHighFrequency()
    {
        const double zeta = 0.35;
        // r → 0:RAO 1、無滯後;r = 1:1/(2ζ)、滯後 π/2;r → ∞:RAO → 0、滯後 → π
        Assert.Equal((1.0, 0.0), SeakeepingModel.Response(0.0, zeta));
        var (rao1, lag1) = SeakeepingModel.Response(1.0, zeta);
        Assert.Equal(1.0 / (2.0 * zeta), rao1, 12);
        Assert.Equal(Math.PI / 2.0, lag1, 12);
        var (raoHigh, lagHigh) = SeakeepingModel.Response(20.0, zeta);
        Assert.InRange(raoHigh, 0.0, 0.003);
        Assert.InRange(lagHigh, Math.PI - 0.05, Math.PI);
        // RAO 最大值在共振附近 r = √(1 − 2ζ²)(= 0.869),值 1/(2ζ√(1 − ζ²))(= 1.525);掃描 r = 0.01…3 的最大值落在該處
        var rPeak = Math.Sqrt(1.0 - 2.0 * zeta * zeta);
        var peak = SeakeepingModel.Response(rPeak, zeta).Rao;
        Assert.Equal(1.0 / (2.0 * zeta * Math.Sqrt(1.0 - zeta * zeta)), peak, 12);
        var best = Enumerable.Range(1, 300).Select(i => i * 0.01).MaxBy(r => SeakeepingModel.Response(r, zeta).Rao);
        Assert.InRange(best, rPeak - 0.011, rPeak + 0.011);
        Assert.All(new[] { 0.2, 0.5, 1.3, 2.0, 3.0 }, r => Assert.True(SeakeepingModel.Response(r, zeta).Rao < peak));
        // 阻尼越小峰越高;遭遇頻率為負(順浪且船速超過波速)時 RAO 相同、滯後變號
        Assert.True(SeakeepingModel.Response(1.0, 0.1).Rao > 3.0 * rao1);
        var (raoNeg, lagNeg) = SeakeepingModel.Response(-0.7, zeta);
        var (raoPos, lagPos) = SeakeepingModel.Response(0.7, zeta);
        Assert.Equal(raoPos, raoNeg);
        Assert.Equal(-lagPos, lagNeg);
    }

    [Fact]
    public void LengthFiltersHaveUnitLongWaveLimitAndVanishWhenWavelengthFitsTheHull()
    {
        // 升沉:sinc(0) = 1、sinc(π) = 0(沿船長正好一個波長 → 平均波面為零)、偶函數
        Assert.Equal(1.0, SeakeepingModel.Sinc(0.0));
        Assert.Equal(1.0, SeakeepingModel.Sinc(1e-9));
        Assert.Equal(0.0, SeakeepingModel.Sinc(Math.PI), 15);
        Assert.Equal(SeakeepingModel.Sinc(1.3), SeakeepingModel.Sinc(-1.3));
        Assert.Equal(Math.Sin(2.0) / 2.0, SeakeepingModel.Sinc(2.0));
        // 縱搖:F_p(0) = 1、偶函數、級數與直接式在切換點連續、第一個零點 tan x = x(x ≈ 4.4934)之後變號
        Assert.Equal(1.0, SeakeepingModel.PitchFilter(0.0));
        Assert.Equal(SeakeepingModel.PitchFilter(0.8), SeakeepingModel.PitchFilter(-0.8));
        var below = SeakeepingModel.PitchFilter(SeakeepingModel.PitchFilterSeriesX - 1e-9);
        var above = SeakeepingModel.PitchFilter(SeakeepingModel.PitchFilterSeriesX + 1e-9);
        Assert.Equal(below, above, 9);
        Assert.Equal(1.0 - 0.01 / 10.0, SeakeepingModel.PitchFilter(0.1), 5);
        Assert.Equal(3.0 * (Math.Sin(2.0) - 2.0 * Math.Cos(2.0)) / 8.0, SeakeepingModel.PitchFilter(2.0));
        Assert.Equal(0.0, SeakeepingModel.PitchFilter(4.493409457909064), 12);
        Assert.True(SeakeepingModel.PitchFilter(4.0) > 0.0 && SeakeepingModel.PitchFilter(5.0) < 0.0);
    }

    [Fact]
    public void LongWavesHeaveFollowsWaveElevationAndPitchFollowsWaveSlope()
    {
        // 極長波(Tp 120 s:最短成分 λ ≈ 6 km ≫ L、遭遇頻率遠低於自然頻率):靜止船隨波面起伏 —— 升沉 ≈ 自船位置的波面高,
        // 頂浪縱搖 ≈ 沿艏向的波斜率(艏仰正 = 波面朝艏升高 = −∂η/∂s,s 為傳播方向)、順浪反號、橫浪為零;殘差(RAO 與相位滯後)< 最大值的 8%
        var m = Model();
        m.SetWaves(Waves(2.0, 120.0, 0.0));
        const double hs = 2.0, x = 30.0, y = -50.0;
        var maxEta = 0.0;
        var maxSlope = 0.0;
        for (var i = 0; i < 1200; i++)
        {
            maxEta = Math.Max(maxEta, Math.Abs(m.WaveElevationAt(x, y, hs, i * 0.5)));
            maxSlope = Math.Max(maxSlope, Math.Abs(m.WaveSlopeAlongPropagationAt(x, y, hs, i * 0.5)));
        }
        Assert.InRange(maxEta, 0.5, 2.0);
        Assert.InRange(maxSlope, 1e-4, 2e-3);
        var worstHeave = 0.0;
        var worstPitch = 0.0;
        for (var i = 0; i < 1200; i++)
        {
            var t = i * 0.5;
            var eta = m.WaveElevationAt(x, y, hs, t);
            var slope = m.WaveSlopeAlongPropagationAt(x, y, hs, t);
            var head = m.Evaluate(At(0.0, x: x, y: y), 0.0, 0.0, hs, t)!.Value;
            var following = m.Evaluate(At(180.0, x: x, y: y), 0.0, 0.0, hs, t)!.Value;
            var beam = m.Evaluate(At(90.0, x: x, y: y), 0.0, 0.0, hs, t)!.Value;
            worstHeave = Math.Max(worstHeave, Math.Max(Math.Abs(head.Heave - eta), Math.Abs(beam.Heave - eta)));
            worstPitch = Math.Max(worstPitch, Math.Max(Math.Abs(head.Pitch - (-slope)), Math.Abs(following.Pitch - slope)));
            Assert.InRange(Math.Abs(beam.Pitch), 0.0, 1e-12);
        }
        _out.WriteLine($"Tp 120 s, Hs 2 m: max |η| {maxEta:F3} m (heave residual {worstHeave:F3} m), max |slope| {maxSlope:E2} rad (pitch residual {worstPitch:E2} rad)");
        Assert.InRange(worstHeave, 0.0, 0.08 * maxEta);
        Assert.InRange(worstPitch, 0.0, 0.08 * maxSlope);
    }

    [Fact]
    public void BeamSeasGiveNoPitchWhileHeadAndFollowingSeasGiveMost()
    {
        // FSB2 滿載、靜止、Hs 2.5 m、浪自 000°:長波(Tp 14 s,峰值 λ ≈ 3 L)縱搖頂浪 = 順浪 > 斜浪 > 橫浪 = 0;升沉橫浪最大(沿船長無相位差)
        var m = Model();
        m.SetWaves(Waves(2.5, 14.0, 0.0));
        var head = Amplitudes(m, 0.0, 2.5);
        var oblique = Amplitudes(m, 45.0, 2.5);
        var beam = Amplitudes(m, 90.0, 2.5);
        var quarter = Amplitudes(m, 135.0, 2.5);
        var following = Amplitudes(m, 180.0, 2.5);
        _out.WriteLine($"Tp 14 s: pitch head {head.PitchDeg:F3}° 45° {oblique.PitchDeg:F3}° beam {beam.PitchDeg:E2}° 135° {quarter.PitchDeg:F3}° following {following.PitchDeg:F3}°; " +
                       $"heave head {head.Heave:F3} 45° {oblique.Heave:F3} beam {beam.Heave:F3} m");
        Assert.InRange(beam.PitchDeg, 0.0, 1e-9);
        Assert.True(head.PitchDeg > oblique.PitchDeg && oblique.PitchDeg > 0.5, $"head {head.PitchDeg} oblique {oblique.PitchDeg}");
        Assert.Equal(head.PitchDeg, following.PitchDeg, 9);      // 靜止:頂浪與順浪只差相位
        Assert.Equal(oblique.PitchDeg, quarter.PitchDeg, 9);
        Assert.InRange(head.PitchDeg, 0.8, 4.0);
        Assert.True(beam.Heave > oblique.Heave && oblique.Heave > head.Heave, $"heave beam {beam.Heave} oblique {oblique.Heave} head {head.Heave}");
        Assert.InRange(beam.Heave, 0.8, 2.5);
        // 短波(Tp 8 s,峰值 λ ≈ L):橫浪縱搖仍為零、頂浪/斜浪皆遠大於橫浪;沿船長約一個波長使頂浪升沉大幅抵消
        m.SetWaves(Waves(2.5, 8.0, 0.0));
        var head8 = Amplitudes(m, 0.0, 2.5);
        var beam8 = Amplitudes(m, 90.0, 2.5);
        Assert.InRange(beam8.PitchDeg, 0.0, 1e-9);
        Assert.InRange(head8.PitchDeg, 0.5, 3.0);
        Assert.True(head8.Heave < 0.4 * beam8.Heave, $"head {head8.Heave} beam {beam8.Heave}");
    }

    [Fact]
    public void LightlyDampedHeaveIsLargestWhenPeakPeriodIsNearNaturalPeriod()
    {
        // 橫浪(無長度濾波)、ζ_h 0.1:升沉最大值 / 波面最大值在 Tp ≈ T_h 時最大(約 2;5 個成分分散在 0.74–1.86 ω_p,Smith 修正 0.78),短週期(0.5 T_h:遭遇頻率遠高於自然頻率)與長週期(2.5 T_h:準靜態 ≈ 1)皆較小
        var m = Model(Cfg(heaveZeta: 0.1));
        var th = m.HeaveNaturalPeriodS;
        double Ratio(double tp)
        {
            m.SetWaves(Waves(2.0, tp, 90.0));
            var maxEta = 0.0;
            for (var i = 0; i <= 6000; i++) maxEta = Math.Max(maxEta, Math.Abs(m.WaveElevationAt(0.0, 0.0, 2.0, i * 0.2)));
            return Amplitudes(m, 0.0, 2.0).Heave / maxEta;
        }
        var resonant = Ratio(th);
        var shortWave = Ratio(0.5 * th);
        var longWave = Ratio(2.5 * th);
        _out.WriteLine($"ζ_h 0.1 beam seas heave/η: Tp = T_h {th:F2} s → {resonant:F2}; 0.5 T_h → {shortWave:F2}; 2.5 T_h → {longWave:F2}");
        Assert.True(resonant > 1.6, $"resonant {resonant}");
        Assert.True(shortWave < 0.5 * resonant && longWave < 0.7 * resonant, $"short {shortWave} long {longWave} resonant {resonant}");
        Assert.InRange(longWave, 0.8, 1.4);
        // 預設阻尼(0.35)時同一海況的放大較小
        var def = Model();
        def.SetWaves(Waves(2.0, th, 90.0));
        m.SetWaves(Waves(2.0, th, 90.0));
        Assert.True(Amplitudes(def, 0.0, 2.0).Heave < 0.6 * Amplitudes(m, 0.0, 2.0).Heave);
    }

    [Fact]
    public void RatesAreTimeDerivativesAlongTheTrackIncludingEncounterFrequencyAndCurrent()
    {
        // 等速直線運動(對水 u、v 加流):z'、θ' 等於沿航跡的數值微分(RAO、滯後為常數);頂浪前進使遭遇頻率升高
        var m = Model();
        m.SetWaves(Waves(2.5, 9.0, 30.0, "jonswap"));
        const double psiDeg = 70.0, u = 5.5, v = -0.3, curE = 0.4, curN = -0.6, hs = 2.5, h = 1e-4;
        var psi = Units.DegToRad(psiDeg);
        var (velE, velN) = SimosaBRM.SimCore.Environment.EnvironmentMath.GroundVelocity(u, v, psi, curE, curN);
        SeakeepingResult AtTime(double t) => m.Evaluate(new StateVector(u, v, 0.0, 100.0 + velE * t, -40.0 + velN * t, psi), curE, curN, hs, t)!.Value;
        foreach (var t in new[] { 3.0, 17.7, 123.4, 600.5 })
        {
            var r = AtTime(t);
            var dz = (AtTime(t + h).Heave - AtTime(t - h).Heave) / (2.0 * h);
            var dp = (AtTime(t + h).Pitch - AtTime(t - h).Pitch) / (2.0 * h);
            Assert.InRange(Math.Abs(r.HeaveRate - dz), 0.0, 1e-6);
            Assert.InRange(Math.Abs(r.PitchRate - dp), 0.0, 1e-7);
            Assert.NotEqual(0.0, r.HeaveRate);
            Assert.NotEqual(0.0, r.PitchRate);
        }
        // 遭遇頻率:同一海況頂浪前進時升沉速率的均方根大於靜止(每個成分 ω_e = ω + k·V 較高),順浪前進較低
        m.SetWaves(Waves(2.5, 14.0, 0.0));
        double RateRms(double headingDeg, double speed)
        {
            var ps = Units.DegToRad(headingDeg);
            var sum = 0.0;
            const int n = 6000;
            for (var i = 0; i < n; i++)
            {
                var t = i * 0.2;
                var r = m.Evaluate(new StateVector(speed, 0.0, 0.0, speed * Math.Sin(ps) * t, speed * Math.Cos(ps) * t, ps), 0.0, 0.0, 2.5, t)!.Value;
                sum += r.HeaveRate * r.HeaveRate;
            }
            return Math.Sqrt(sum / n);
        }
        var still = RateRms(0.0, 0.0);
        var into = RateRms(0.0, 6.0);
        var away = RateRms(180.0, 6.0);
        _out.WriteLine($"heave rate rms: still {still:F3}, head 6 m/s {into:F3}, following 6 m/s {away:F3} m/s");
        Assert.True(into > 1.3 * still && away < 0.8 * still, $"still {still} into {into} away {away}");
    }

    [Fact]
    public void WaveFieldIsTheSameAsRollExcitationAndPhasesComeFromScenarioSeed()
    {
        // 與橫搖共用同一組成分與相位:沿傳播方向的波斜率 = 正橫浪(χ = +90°)、waveGain 1 時橫搖的有效波斜率(逐成分同式)
        foreach (var seed in new[] { 3, 7, 20261111 })
        {
            var sea = Model(seed: seed);
            var roll = new RollModel(TestData.Fsb2(), LoadingCondition.Full, new ScenarioRoll { Enabled = true }, seed);
            Assert.Equal(RollModel.DrawPhases(seed), sea.Phases);
            Assert.Equal(roll.Phases, sea.Phases);
            foreach (var w in new[] { Waves(2.0, 9.0, 120.0, "jonswap"), Waves(1.5, 7.0, 300.0), Waves(3.0, 12.0, 45.0, "jonswap", 2.0) })
            {
                sea.SetWaves(w);
                roll.SetWaves(w);
                var psi = Units.DegToRad(w.DirectionDeg!.Value - 90.0);   // χ = 來向 − 航向 = +90°
                foreach (var (x, y, t) in new[] { (0.0, 0.0, 0.0), (120.0, -340.0, 55.5), (-2500.0, 900.0, 1234.56) })
                    AssertRel(roll.WaveSlopeAt(x, y, psi, 2.0, t), sea.WaveSlopeAlongPropagationAt(x, y, 2.0, t), 1e-14, $"seed {seed} slope");
            }
        }
        // 不同種子 → 不同相位 → 相同海況下輸出不同;同種子重建 → 相同
        var a = Model(seed: 7);
        var b = Model(seed: 8);
        var again = Model(seed: 7);
        foreach (var m in new[] { a, b, again }) m.SetWaves(Waves(2.5, 8.0, 45.0));
        var ra = a.Evaluate(At(0.0), 0.0, 0.0, 2.5, 10.0)!.Value;
        Assert.NotEqual(ra, b.Evaluate(At(0.0), 0.0, 0.0, 2.5, 10.0)!.Value);
        Assert.Equal(ra, again.Evaluate(At(0.0), 0.0, 0.0, 2.5, 10.0)!.Value);
        // 引擎:相位 = 情境種子
        var e = Engine(Yaml(waves: QuarteringWaves, seed: 11));
        Assert.Equal(RollModel.DrawPhases(11), e.Seakeeping!.Phases);
    }

    [Fact]
    public void OutputIsLinearInHsAndAbsentWithoutWavesOrWithZeroHs()
    {
        var m = Model();
        // 未設定浪 → 無輸出
        Assert.False(m.HasWaves);
        Assert.Null(m.Evaluate(At(0.0), 0.0, 0.0, 2.0, 5.0));
        Assert.Equal(0.0, m.WaveElevationAt(0.0, 0.0, 2.0, 5.0));
        m.SetWaves(Waves(2.0, 8.0, 45.0));
        Assert.True(m.HasWaves);
        // Hs = 0(或負、NaN)→ 無輸出
        Assert.Null(m.Evaluate(At(0.0), 0.0, 0.0, 0.0, 5.0));
        Assert.Null(m.Evaluate(At(0.0), 0.0, 0.0, -1.0, 5.0));
        Assert.Null(m.Evaluate(At(0.0), 0.0, 0.0, double.NaN, 5.0));
        // 線性:Hs 加倍 → 四個輸出皆加倍
        var s = new StateVector(4.0, 0.2, 0.0, 50.0, 80.0, Units.DegToRad(20.0));
        var r1 = m.Evaluate(s, 0.0, 0.0, 1.0, 33.3)!.Value;
        var r2 = m.Evaluate(s, 0.0, 0.0, 2.0, 33.3)!.Value;
        Assert.NotEqual(0.0, r1.Heave);
        AssertRel(2.0 * r1.Heave, r2.Heave, 1e-15, "heave");
        AssertRel(2.0 * r1.Pitch, r2.Pitch, 1e-15, "pitch");
        AssertRel(2.0 * r1.HeaveRate, r2.HeaveRate, 1e-15, "heave rate");
        AssertRel(2.0 * r1.PitchRate, r2.PitchRate, 1e-15, "pitch rate");
        // UpdateLast / State:度與度/秒;清除浪 → Last 與 State 為 null
        m.UpdateLast(s, 0.0, 0.0, 2.0, 33.3);
        Assert.Equal(r2, m.Last);
        var st = m.State()!;
        Assert.Equal((r2.Heave, Units.RadToDeg(r2.Pitch), r2.HeaveRate, Units.RadToDeg(r2.PitchRate)), (st.HeaveM, st.PitchDeg, st.HeaveRateMps, st.PitchRateDps));
        m.SetWaves(null);
        Assert.Null(m.Last);
        Assert.Null(m.State());
    }

    // ------------------------------------------------------------------ 引擎

    [Fact]
    public void WithoutSeakeepingOutputsAndHashAreUnchangedAndEnabledDoesNotFeedBack()
    {
        Scenario.Scenario Sc(string seakeeping) => Yaml(seakeeping: seakeeping, speedKn: 10.0, windKn: 20.0, windDir: 90.0, gustiness: 0.3, waves: QuarteringWaves);
        var plain = Engine(Sc(""));
        var disabled = Engine(Sc("seakeeping: { enabled: false, heaveDampingRatio: 0.2, gmL_m: 90 }"));
        var enabled = Engine(Sc(SeaOn));
        Assert.Null(plain.Seakeeping);
        Assert.Null(disabled.Seakeeping);
        Assert.NotNull(enabled.Seakeeping);
        // tick 0 即有輸出(載入時以初始狀態計算)
        Assert.NotNull(enabled.BuildState().WaveMotion);
        Assert.Null(plain.BuildState().WaveMotion);
        foreach (var e in new[] { plain, disabled, enabled }) Apply(e, SimCommand.Rudder(15));
        for (var i = 0; i < 500; i++) { plain.Step(); disabled.Step(); enabled.Step(); }
        // 無 seakeeping / enabled = false:狀態 JSON、快照與雜湊逐位元相同,無 waveMotion 欄位
        var plainJson = ContractJson.Serialize(plain.BuildState());
        Assert.Equal(plainJson, ContractJson.Serialize(disabled.BuildState()));
        Assert.Equal(plain.StateHash, disabled.StateHash);
        Assert.DoesNotContain("\"waveMotion\"", plainJson);
        Assert.Contains("\"waves\":{", plainJson);
        // 啟用:3-DOF 運動、陣風係數與亂數計數逐位元相同(不回饋、相位用獨立產生器),雜湊因 SEAKEEP1 區塊而不同,state.waveMotion 存在
        Assert.Equal(plain.Motion, enabled.Motion);
        Assert.Equal(plain.Environment.GustFactor, enabled.Environment.GustFactor);
        Assert.Equal(plain.Random.Count, enabled.Random.Count);
        Assert.NotEqual(plain.StateHash, enabled.StateHash);
        var s = enabled.BuildState();
        Assert.NotNull(s.WaveMotion);
        Assert.NotEqual(0.0, s.WaveMotion!.HeaveM);
        Assert.NotEqual(0.0, s.WaveMotion.PitchDeg);
        var last = enabled.Seakeeping!.Last!.Value;
        Assert.Equal((last.Heave, Units.RadToDeg(last.Pitch), last.HeaveRate, Units.RadToDeg(last.PitchRate)),
            (s.WaveMotion.HeaveM, s.WaveMotion.PitchDeg, s.WaveMotion.HeaveRateMps, s.WaveMotion.PitchRateDps));
        // 步末結果 = 以步末狀態、步末時刻與目前 Hs 的純函數
        Assert.Equal(last, enabled.Seakeeping.Evaluate(enabled.Motion, 0.0, 0.0, enabled.Waves!.SignificantHeightM, enabled.Time)!.Value);
        var json = ContractJson.Serialize(s);
        Assert.Contains("\"waveMotion\":{\"heave_m\":", json);
        Assert.Contains("\"pitch_deg\":", json);
        Assert.Contains("\"heaveRate_mps\":", json);
        Assert.Contains("\"pitchRate_dps\":", json);
        var back = ContractJson.Deserialize<OwnShipState>(json)!;
        Assert.Equal(s.WaveMotion, back.WaveMotion);
        // 其他欄位不變:去掉 waveMotion 後與未啟用的狀態 JSON 完全相同
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("waveMotion"));
        Assert.Equal(JsonNode.Parse(plainJson)!.ToJsonString(), node.ToJsonString());
        // 快照不需新欄位(純函數,還原後重算):除雜湊外與未啟用的快照 JSON 完全相同
        Assert.Equal(plain.CreateSnapshot().ToJson().Replace(plain.StateHash, "H"), enabled.CreateSnapshot().ToJson().Replace(enabled.StateHash, "H"));
    }

    [Fact]
    public void EnabledWithoutWavesOrWithZeroHsProducesNoOutputAndLeavesHashUnchanged()
    {
        // 啟用但情境無浪:無 state.waveMotion、不追加雜湊區塊 → 雜湊與未啟用逐位元相同
        var plain = Engine(Yaml(seakeeping: "", speedKn: 8.0));
        var enabled = Engine(Yaml(speedKn: 8.0));
        Assert.NotNull(enabled.Seakeeping);
        Assert.False(enabled.Seakeeping!.HasWaves);
        TestData.RunTo(plain, 20);
        TestData.RunTo(enabled, 20);
        Assert.Null(enabled.Seakeeping.Last);
        Assert.Null(enabled.BuildState().WaveMotion);
        Assert.Equal(plain.StateHash, enabled.StateHash);
        Assert.Equal(ContractJson.Serialize(plain.BuildState()), ContractJson.Serialize(enabled.BuildState()));
        // 有 waves 區段但 Hs = 0(零力;state.waves 仍輸出):同樣無 waveMotion、雜湊與未啟用相同
        const string zero = "  waves: { significantHeight_m: 0, peakPeriod_s: 8, direction_deg: 45 }";
        var plainZero = Engine(Yaml(seakeeping: "", speedKn: 8.0, waves: zero));
        var enabledZero = Engine(Yaml(speedKn: 8.0, waves: zero));
        TestData.RunTo(plainZero, 20);
        TestData.RunTo(enabledZero, 20);
        Assert.True(enabledZero.Seakeeping!.HasWaves);
        Assert.Null(enabledZero.BuildState().WaveMotion);
        Assert.NotNull(enabledZero.BuildState().Waves);
        Assert.Equal(plainZero.StateHash, enabledZero.StateHash);
    }

    [Fact]
    public void DynamicsWithoutExternalForceSupportStillOutputWaveMotion()
    {
        // 動力學不接受外力:平均漂移力停用(state.waves 省略、無 WAVEDRFT 區塊),但升沉/縱搖只需浪設定與運動狀態,照常輸出並進雜湊(與橫搖同原則)
        Scenario.Scenario Sc(string seakeeping) => Yaml(seakeeping: seakeeping, ship: "FSB1", speedKn: 6.0, waves: QuarteringWaves);
        var plain = new SimulationEngine(TestData.Fsb1(), Sc(""), (s, l) => new NoExternalDynamics(new PlaceholderDynamics(s, l)));
        var enabled = new SimulationEngine(TestData.Fsb1(), Sc(SeaOn), (s, l) => new NoExternalDynamics(new PlaceholderDynamics(s, l)));
        Assert.False(enabled.Dynamics.SupportsExternalForces);
        Assert.False(enabled.Waves!.Active);
        TestData.RunTo(plain, 10);
        TestData.RunTo(enabled, 10);
        var s = enabled.BuildState();
        Assert.Null(s.Waves);
        Assert.NotNull(s.WaveMotion);
        Assert.NotEqual(0.0, s.WaveMotion!.HeaveM);
        Assert.Equal(plain.Motion, enabled.Motion);
        Assert.NotEqual(plain.StateHash, enabled.StateHash);
    }

    [Fact]
    public void EngineHeaveAndPitchMagnitudesArePlausibleForQuarteringSeas()
    {
        // FSB2 滿載 12 kn、Hs 2.5 m / Tp 8 s(λ ≈ L)自 045°:升沉數十公分、縱搖 1–3°(設計文件第 11.4 節量級)
        var e = Engine(Yaml(speedKn: 12.0, waves: QuarteringWaves));
        var maxHeave = 0.0;
        var maxPitch = 0.0;
        var maxHeaveRate = 0.0;
        var maxPitchRate = 0.0;
        for (var i = 0; i < 50 * 300; i++)
        {
            e.Step();
            var wm = e.BuildState().WaveMotion!;
            maxHeave = Math.Max(maxHeave, Math.Abs(wm.HeaveM));
            maxPitch = Math.Max(maxPitch, Math.Abs(wm.PitchDeg));
            maxHeaveRate = Math.Max(maxHeaveRate, Math.Abs(wm.HeaveRateMps));
            maxPitchRate = Math.Max(maxPitchRate, Math.Abs(wm.PitchRateDps));
        }
        _out.WriteLine($"FSB2 12 kn, Hs 2.5 m Tp 8 s from 045: max |heave| {maxHeave:F3} m, |pitch| {maxPitch:F3}°, |heave rate| {maxHeaveRate:F3} m/s, |pitch rate| {maxPitchRate:F3}°/s");
        Assert.InRange(maxHeave, 0.15, 1.5);
        Assert.InRange(maxPitch, 0.8, 4.0);
        Assert.InRange(maxHeaveRate, 0.1, 2.0);
        Assert.InRange(maxPitchRate, 0.5, 6.0);
    }

    [Fact]
    public void SetEnvironmentWavesTurnsOutputOnScalesWithRampedHsAndOffAgain()
    {
        var e = Engine(Yaml(speedKn: 8.0));
        TestData.RunTo(e, 5);
        Assert.Null(e.BuildState().WaveMotion);
        var hashBefore = e.StateHash;
        // 指令加浪(Hs 自 0 漸變 20 s):漸變起點 Hs = 0 無輸出,第一步之後 Hs > 0 → 有輸出,以模型目前 Hs 計算
        Apply(e, SimCommand.SetWaves(2.0, 8.0, 45.0, rampS: 20.0));
        Assert.True(e.Seakeeping!.HasWaves);
        Assert.Null(e.BuildState().WaveMotion);
        Assert.Equal(hashBefore, e.StateHash);
        TestData.RunTo(e, 15);
        var midHs = e.Waves!.SignificantHeightM;
        Assert.InRange(midHs, 0.1, 1.9);
        var mid = e.Seakeeping.Last!.Value;
        var unit = e.Seakeeping.Evaluate(e.Motion, 0.0, 0.0, 1.0, e.Time)!.Value;
        AssertRel(unit.Heave * midHs, mid.Heave, 1e-14, "ramped heave");
        AssertRel(unit.Pitch * midHs, mid.Pitch, 1e-14, "ramped pitch");
        Assert.NotNull(e.BuildState().WaveMotion);
        TestData.RunTo(e, 40);
        Assert.Equal(2.0, e.Waves.SignificantHeightM);
        // 來向改為橫浪(部分更新):縱搖趨近零
        Apply(e, SimCommand.SetWaves(directionDeg: Units.NormalizeHeadingDeg(Units.RadToDeg(e.Motion.Psi) + 90.0)));
        Assert.InRange(Math.Abs(e.BuildState().WaveMotion!.PitchDeg), 0.0, 1e-9);
        Assert.NotEqual(0.0, e.BuildState().WaveMotion!.HeaveM);
        // 關閉浪:輸出消失、雜湊不再追加區塊
        Apply(e, SimCommand.ClearWaves());
        Assert.False(e.Seakeeping.HasWaves);
        Assert.Null(e.BuildState().WaveMotion);
        e.Step();
        Assert.Null(e.Seakeeping.Last);
        Assert.Null(e.BuildState().WaveMotion);
    }

    [Fact]
    public void SnapshotRestoreReproducesOutputAndHash()
    {
        var sc = Yaml(speedKn: 10.0, windKn: 25.0, windDir: 60.0, gustiness: 0.2, waves: QuarteringWaves);
        var e = Engine(sc);
        Apply(e, SimCommand.Rudder(20));
        TestData.RunTo(e, 10);
        var snap = e.TakeSnapshot();
        var at10 = e.BuildState().WaveMotion!;
        TestData.RunTo(e, 40);
        var (hash, at40, last40) = (e.StateHash, e.BuildState().WaveMotion!, e.Seakeeping!.Last!.Value);
        // 還原:步末結果以還原的狀態與時刻重算(與快照當時逐位元相同),續跑到同一時刻輸出與雜湊相同
        e.Restore(snap);
        Assert.Equal(snap.Tick, e.Tick);
        Assert.Equal(at10, e.BuildState().WaveMotion);
        TestData.RunTo(e, 40);
        Assert.Equal(hash, e.StateHash);
        Assert.Equal(last40, e.Seakeeping!.Last!.Value);
        Assert.Equal(at40, e.BuildState().WaveMotion);
        // 快照 JSON 往返後還原亦相同;還原到另一個新引擎亦相同;同一輸入重跑雜湊相同
        e.Restore(EngineSnapshot.FromJson(snap.ToJson()));
        TestData.RunTo(e, 40);
        Assert.Equal(hash, e.StateHash);
        var other = Engine(sc);
        other.Restore(snap);
        Assert.Equal(at10, other.BuildState().WaveMotion);
        TestData.RunTo(other, 40);
        Assert.Equal(hash, other.StateHash);
        Assert.Equal(at40, other.BuildState().WaveMotion);
        var again = Engine(sc);
        Apply(again, SimCommand.Rudder(20));
        TestData.RunTo(again, 40);
        Assert.Equal(hash, again.StateHash);
        Assert.Equal(at40, again.BuildState().WaveMotion);
        // 浪以指令變更後的快照(Hs 漸變中途):還原續跑亦相同
        Apply(e, SimCommand.SetWaves(1.0, 10.0, 200.0, rampS: 30.0));
        TestData.RunTo(e, 50);
        var mid = e.TakeSnapshot();
        var midMotion = e.BuildState().WaveMotion!;
        TestData.RunTo(e, 90);
        var (hash90, at90) = (e.StateHash, e.BuildState().WaveMotion!);
        e.Restore(mid);
        Assert.Equal(midMotion, e.BuildState().WaveMotion);
        TestData.RunTo(e, 90);
        Assert.Equal(hash90, e.StateHash);
        Assert.Equal(at90, e.BuildState().WaveMotion);
    }

    /// <summary>
    /// 凍結中立即改流(無 ramp):對地速度 → 遭遇頻率改變,state.waveMotion 當下就重算;此時取快照再還原,輸出與還原前逐位元相同
    ///(physics-waves.md 第 11.5 節;修正前凍結中仍顯示舊流的值、還原後才變成新流的值)。
    /// </summary>
    [Fact]
    public void ImmediateCurrentChangeWhileFrozenRefreshesOutputAndSurvivesRestore()
    {
        var e = Engine(Yaml(speedKn: 10.0, waves: QuarteringWaves));
        TestData.RunTo(e, 5);
        Apply(e, SimCommand.Freeze());
        var before = e.BuildState().WaveMotion!;
        Apply(e, SimCommand.SetEnvironment(currentSetDeg: 45, currentDriftKn: 3));
        var after = e.BuildState().WaveMotion!;
        Assert.NotEqual(before, after);
        var snap = e.TakeSnapshot();
        e.Restore(snap);
        Assert.Equal(after, e.BuildState().WaveMotion);
        // 還原到另一個新引擎(快照 JSON 往返)亦相同;解除凍結續跑,兩者雜湊與輸出相同
        var other = Engine(Yaml(speedKn: 10.0, waves: QuarteringWaves));
        other.Restore(EngineSnapshot.FromJson(snap.ToJson()));
        Assert.Equal(after, other.BuildState().WaveMotion);
        Apply(e, SimCommand.Resume());
        Apply(other, SimCommand.Resume());
        TestData.RunTo(e, 10);
        TestData.RunTo(other, 10);
        Assert.Equal(e.StateHash, other.StateHash);
        Assert.Equal(e.BuildState().WaveMotion, other.BuildState().WaveMotion);
        // 只改風(流不變)不影響升沉/縱搖:重算為純函數,輸出逐位元不變
        Apply(e, SimCommand.Freeze());
        var steady = e.BuildState().WaveMotion!;
        Apply(e, SimCommand.SetEnvironment(windTrueSpeedKn: 30, windTrueDirDeg: 90));
        Assert.Equal(steady, e.BuildState().WaveMotion);
    }

    [Fact]
    public void RecordReplayReproducesHashWithSeakeeping()
    {
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, "seakeeping_run.yaml"));
        Assert.True(sc.Ship.Seakeeping is { Enabled: true });
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"seakeeping-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        try
        {
            TestData.RunTo(e, 20);
            e.Enqueue(SimCommand.Rudder(15));
            TestData.RunTo(e, 40);
            e.Enqueue(SimCommand.SetWaves(1.5, 10.0, 300.0, rampS: 10.0));
            TestData.RunTo(e, 70);
            w.Close();
            Assert.NotNull(e.BuildState().WaveMotion);
            // 紀錄的狀態行帶 waveMotion
            Assert.Contains(File.ReadLines(path), l => l.Contains("\"waveMotion\":{\"heave_m\":"));
            var r = Replayer.Replay(path, TestData.Ship, Mmg);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ 結構檢查

    [Theory]
    [InlineData("seakeeping: { enabled: true, heaveDampingRatio: 0 }", "ship.seakeeping.heaveDampingRatio", "0–1")]
    [InlineData("seakeeping: { enabled: true, heaveDampingRatio: 1 }", "ship.seakeeping.heaveDampingRatio", "0–1")]
    [InlineData("seakeeping: { enabled: true, pitchDampingRatio: -0.1 }", "ship.seakeeping.pitchDampingRatio", "0–1")]
    [InlineData("seakeeping: { enabled: false, pitchDampingRatio: 1.5 }", "ship.seakeeping.pitchDampingRatio", "0–1")]
    [InlineData("seakeeping: { enabled: true, pitchGyradiusRatio: 0 }", "ship.seakeeping.pitchGyradiusRatio", "不超過 0.5")]
    [InlineData("seakeeping: { enabled: true, pitchGyradiusRatio: 0.6 }", "ship.seakeeping.pitchGyradiusRatio", "不超過 0.5")]
    [InlineData("seakeeping: { enabled: true, gmL_m: 0 }", "ship.seakeeping.gmL_m", "必須為正")]
    [InlineData("seakeeping: { enabled: true, gmL_m: -100 }", "ship.seakeeping.gmL_m", "必須為正")]
    public void StructuralIssuesRejectBadSeakeeping(string seakeeping, string path, string message)
    {
        var sc = Yaml(seakeeping: seakeeping);
        var issues = sc.StructuralIssues().ToList();
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Error && i.Path == path && i.Message.Contains(message));
        Assert.Throws<InvalidDataException>(() => sc.Validate());
    }

    [Fact]
    public void ValidSeakeepingHasNoStructuralIssuesAndRoundTripsThroughScenarioJson()
    {
        Assert.Empty(Yaml().StructuralIssues());
        Assert.Empty(Yaml(seakeeping: "").StructuralIssues());
        const string full = "seakeeping: { enabled: true, heaveDampingRatio: 0.3, pitchDampingRatio: 0.4, pitchGyradiusRatio: 0.26, gmL_m: 110 }";
        var sc = Yaml(seakeeping: full);
        Assert.Empty(sc.StructuralIssues());
        var k = sc.Ship.Seakeeping!;
        Assert.Equal((true, 0.3, 0.4, 0.26, 110.0), (k.Enabled, k.HeaveDampingRatio, k.PitchDampingRatio, k.PitchGyradiusRatio, k.GmLM));
        // 情境 JSON(紀錄標頭)往返保留 ship.seakeeping;無此區段的情境不寫出該鍵;與 ship.roll 可同時啟用
        var json = sc.ToJson();
        Assert.Contains("\"seakeeping\":{\"enabled\":true,\"heaveDampingRatio\":0.3,\"pitchDampingRatio\":0.4,\"pitchGyradiusRatio\":0.26,\"gmL_m\":110", json);
        Assert.Equal(110.0, Scenario.Scenario.FromJson(json).Ship.Seakeeping!.GmLM);
        Assert.DoesNotContain("\"seakeeping\"", Yaml(seakeeping: "").ToJson());
        var both = Engine(Yaml(seakeeping: "roll: { enabled: true }, " + SeaOn, speedKn: 8.0, waves: QuarteringWaves));
        TestData.RunTo(both, 10);
        var s = both.BuildState();
        Assert.NotNull(s.Roll);
        Assert.NotNull(s.WaveMotion);
        // 橫搖啟用與否不影響升沉/縱搖(兩者皆只輸出)
        var only = Engine(Yaml(speedKn: 8.0, waves: QuarteringWaves));
        TestData.RunTo(only, 10);
        Assert.Equal(only.BuildState().WaveMotion, s.WaveMotion);
    }

    [Fact]
    public void SchemasDescribeSeakeepingAndWaveMotionWithTheContractNamesAndDefaults()
    {
        // state.schema.json:waveMotion 選填(不在頂層 required),四個欄位皆必填,名稱與 C# 契約類別序列化結果一致
        using var state = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", "state.schema.json")));
        Assert.DoesNotContain("waveMotion", state.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var wm = state.RootElement.GetProperty("properties").GetProperty("waveMotion");
        var fields = new[] { "heave_m", "pitch_deg", "heaveRate_mps", "pitchRate_dps" };
        Assert.Equal(fields, wm.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(fields, wm.GetProperty("properties").EnumerateObject().Select(x => x.Name));
        var description = wm.GetProperty("description").GetString()!;
        Assert.Contains("向上為正", description);
        Assert.Contains("艏仰為正", description);
        var serialized = JsonNode.Parse(ContractJson.Serialize(new WaveMotionState { HeaveM = 0.1, PitchDeg = 0.2, HeaveRateMps = 0.3, PitchRateDps = 0.4 }))!.AsObject();
        Assert.Equal(fields, serialized.Select(kv => kv.Key));
        // scenario.schema.json:ship.seakeeping 的欄位與預設值(= 模型常數;ASSUMED 寫在說明)
        using var scenario = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", "scenario.schema.json")));
        var sk = scenario.RootElement.GetProperty("properties").GetProperty("ship").GetProperty("properties").GetProperty("seakeeping");
        var props = sk.GetProperty("properties");
        Assert.Equal(new[] { "enabled", "heaveDampingRatio", "pitchDampingRatio", "pitchGyradiusRatio", "gmL_m" }, props.EnumerateObject().Select(x => x.Name));
        Assert.False(props.GetProperty("enabled").GetProperty("default").GetBoolean());
        Assert.Equal(SeakeepingModel.DefaultHeaveDampingRatio, props.GetProperty("heaveDampingRatio").GetProperty("default").GetDouble());
        Assert.Equal(SeakeepingModel.DefaultPitchDampingRatio, props.GetProperty("pitchDampingRatio").GetProperty("default").GetDouble());
        Assert.Equal(SeakeepingModel.DefaultPitchGyradiusRatio, props.GetProperty("pitchGyradiusRatio").GetProperty("default").GetDouble());
        Assert.Equal(0.5, props.GetProperty("pitchGyradiusRatio").GetProperty("maximum").GetDouble());
        Assert.Contains("ASSUMED", sk.GetProperty("description").GetString());
        Assert.All(new[] { "heaveDampingRatio", "pitchDampingRatio", "pitchGyradiusRatio", "gmL_m" }, k => Assert.Contains("ASSUMED", props.GetProperty(k).GetProperty("description").GetString()));
    }

    // ------------------------------------------------------------------ 跨語言夾具

    private static ScenarioSeakeeping TableConfig(string name) => name switch
    {
        "default" => Cfg(),
        "tuned" => Cfg(heaveZeta: 0.2, pitchZeta: 0.5, gyradius: 0.22, gmL: 95.0),   // export_seakeeping_reference.py CONFIGS 同一對照
        _ => throw new ArgumentException(name),
    };

    [Fact]
    public void GoldenTableMatchesPythonReference()
    {
        // Python 參考實作(seakeeping.py 同公式同運算順序)的純函數取樣表:四個輸出逐列 |差| ≤ 1e-12·max(1, |值|)
        string[]? cols = null;
        var count = 0;
        var worst = 0.0;
        var models = new Dictionary<string, SeakeepingModel>();
        foreach (var raw in File.ReadLines(Path.Combine(GoldenDirectory, "seakeeping_table.csv")))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (cols is null)
            {
                cols = line.Split(',');
                Assert.Equal(new[] { "ship", "loading", "cfg", "seed", "Hs", "Tp", "dir_deg", "spectrum", "gamma", "x", "y", "psi_deg", "u", "v", "cur_e", "cur_n", "t",
                    "heave_m", "pitch_rad", "heaveRate_mps", "pitchRate_radps" }, cols);
                continue;
            }
            var f = line.Split(',');
            double D(int i) => double.Parse(f[i], Inv);
            var key = $"{f[0]}/{f[1]}/{f[2]}/{f[3]}";
            if (!models.TryGetValue(key, out var m))
                models[key] = m = new SeakeepingModel(TestData.Ship(f[0]), f[1] == "ballast" ? LoadingCondition.Ballast : LoadingCondition.Full, TableConfig(f[2]), int.Parse(f[3], Inv));
            m.SetWaves(Waves(D(4), D(5), D(6), f[7], D(8)));
            var r = m.Evaluate(new StateVector(D(12), D(13), 0.0, D(9), D(10), Units.DegToRad(D(11))), D(14), D(15), D(4), D(16))!.Value;
            var actual = new[] { r.Heave, r.Pitch, r.HeaveRate, r.PitchRate };
            for (var j = 0; j < 4; j++)
            {
                var expected = D(17 + j);
                var diff = Math.Abs(expected - actual[j]) / Math.Max(1.0, Math.Abs(expected));
                worst = Math.Max(worst, diff);
                Assert.True(diff <= 1e-12, $"row {count} ({key} Hs {f[4]} Tp {f[5]} dir {f[6]} psi {f[11]} t {f[16]}) {cols[17 + j]}: Python {expected:R} C# {actual[j]:R}");
            }
            count++;
        }
        Assert.Equal(420, count);
        Assert.Equal(12, models.Count);   // 4 船舶/裝載 ×(預設 2 種子 + 覆寫 1 種子)
        _out.WriteLine($"{count} rows, worst relative difference {worst:E2}");
    }

    private sealed record Golden(string ScenarioFile, string[] Columns, List<double[]> Rows);

    private static Golden LoadGolden(string file)
    {
        string? scenario = null;
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
                continue;
            }
            if (cols is null) { cols = line.Split(','); continue; }
            rows.Add(line.Split(',').Select(v => double.Parse(v, Inv)).ToArray());
        }
        return new Golden(scenario ?? throw new InvalidDataException($"{file} 缺少 # scenario:"), cols ?? throw new InvalidDataException($"{file} 缺少欄名"), rows);
    }

    private static double Value(SimulationEngine e, string col)
    {
        var m = e.Motion;
        var wm = e.BuildState().WaveMotion!;
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
            "heave_m" => wm.HeaveM,
            "pitch_deg" => wm.PitchDeg,
            "heaveRate_mps" => wm.HeaveRateMps,
            "pitchRate_dps" => wm.PitchRateDps,
            "waveElevation_m" => e.Seakeeping!.WaveElevationAt(m.X, m.Y, e.Waves!.SignificantHeightM, e.Time),
            _ => throw new ArgumentException(col),
        };
    }

    [Fact]
    public void GoldenSeakeepingRunMatchesPythonReference()
    {
        // Python 參考實作的 120 s 積分案例(FSB2 滿載 12 kn、Hs 2.5 m / Tp 8 s 自 045°):state.waveMotion 四欄與波面高每秒 |差| ≤ 1e-9·max(1, |值|)
        // (與橫搖夾具相同容差);位置差 / 航程 < 1%
        var g = LoadGolden("seakeeping_run.csv");
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, g.ScenarioFile));
        Assert.Equal(("FSB2", LoadingCondition.Full), (sc.Ship.Id, sc.Ship.Loading));
        var e = Engine(sc);
        Assert.NotNull(e.Seakeeping);
        Assert.Equal(121, g.Rows.Count);
        var tickCol = Array.IndexOf(g.Columns, "tick");
        var worst = new Dictionary<string, double>();
        var pathLength = 0.0;
        var prev = (e.Motion.X, e.Motion.Y);
        foreach (var row in g.Rows)
        {
            var tick = (long)row[tickCol];
            while (e.Tick < tick)
            {
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
                if (col is "heave_m" or "pitch_deg" or "heaveRate_mps" or "pitchRate_dps" or "waveElevation_m")
                    Assert.True(diff <= 1e-9, $"tick {tick} {col}: Python {expected:R} C# {actual:R}(差 {diff:E2})");
                else if (col is "x" or "y")
                    Assert.True(Math.Abs(expected - actual) <= 0.01 * Math.Max(1.0, pathLength), $"tick {tick} {col}: Python {expected:R} C# {actual:R}");
                else Assert.True(diff <= 1e-2, $"tick {tick} {col}: Python {expected:R} C# {actual:R}");
            }
        }
        _out.WriteLine(string.Join(", ", worst.Select(kv => $"{kv.Key} {kv.Value:E1}")));
        var maxHeave = g.Rows.Max(r => Math.Abs(r[Array.IndexOf(g.Columns, "heave_m")]));
        var maxPitch = g.Rows.Max(r => Math.Abs(r[Array.IndexOf(g.Columns, "pitch_deg")]));
        _out.WriteLine($"max |heave| {maxHeave:F3} m, max |pitch| {maxPitch:F3}°, path {pathLength:F0} m");
        Assert.InRange(maxHeave, 0.1, 1.5);
        Assert.InRange(maxPitch, 0.5, 4.0);
        Assert.InRange(pathLength, 650.0, 760.0);
    }

    // ------------------------------------------------------------------ 情境 E17b

    private const string E17 = "E17_typhoon_departure_kaohsiung";
    private const string E17b = "E17b_typhoon_seakeeping";

    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));

    [Fact]
    public void E17b_SharesE17GeometryAndAddsSwellWithSeakeeping()
    {
        var a = Load(E17);
        var b = Load(E17b);
        // E17 本體不變:無浪、無 seakeeping
        Assert.Null(a.Ship.Seakeeping);
        Assert.Null(a.Environment.Waves);
        // 變體:同一船/裝載/初始狀態、拖船、泊位與繫纜、區域、時間表項目與評估規則
        Assert.Equal((a.Ship.Id, a.Ship.Loading, a.Seed), (b.Ship.Id, b.Ship.Loading, b.Seed));
        Assert.Equal((a.Initial.Position!.X, a.Initial.Position.Y, a.Initial.Heading, a.Initial.Speed), (b.Initial.Position!.X, b.Initial.Position.Y, b.Initial.Heading, b.Initial.Speed));
        Assert.Equal(a.Tugs!.Select(t => (t.Id, t.BollardPullT, t.LineLengthM)), b.Tugs!.Select(t => (t.Id, t.BollardPullT, t.LineLengthM)));
        Assert.Equal((a.Berth!.Id, a.Berth.File), (b.Berth!.Id, b.Berth.File));
        Assert.Equal(a.Berth.Lines.Select(l => (l.Id, l.Bollard, l.Initial)), b.Berth.Lines.Select(l => (l.Id, l.Bollard, l.Initial)));
        Assert.Equal(a.Zones.Select(z => (z.Id, z.Type)), b.Zones.Select(z => (z.Id, z.Type)));
        Assert.Equal(a.Timeline.Select(t => (t.Id, t.Phase)), b.Timeline.Select(t => (t.Id, t.Phase)));
        Assert.Equal(a.Assessment.Required.Select(r => r.Id), b.Assessment.Required.Select(r => r.Id));
        Assert.Equal(a.Assessment.Bands.Select(r => r.Id), b.Assessment.Bands.Select(r => r.Id));
        Assert.Equal(a.Assessment.HardFail.Select(r => r.Id), b.Assessment.HardFail.Select(r => r.Id));
        Assert.Equal((a.Environment.Wind.TrueSpeed, a.Environment.Wind.TrueDir, a.Environment.WaterDepth), (b.Environment.Wind.TrueSpeed, b.Environment.Wind.TrueDir, b.Environment.WaterDepth));
        // 差異:ship.seakeeping 啟用(預設係數)、港內湧浪 Hs 0.5 m / Tp 10 s 自 225°,時間表隨風力把 Hs 漸增到 0.8、1.2 m
        var k = b.Ship.Seakeeping!;
        Assert.True(k.Enabled);
        Assert.True(k.HeaveDampingRatio is null && k.PitchDampingRatio is null && k.PitchGyradiusRatio is null && k.GmLM is null);
        var w = b.Environment.Waves!;
        Assert.Equal((0.5, 10.0, 225.0, "jonswap"), (w.SignificantHeightM, w.PeakPeriodS, w.DirectionDeg, w.Spectrum));
        static double Hs(Scenario.Scenario sc, string id) => sc.Timeline.Single(t => t.Id == id).Actions.Single(x => x.Kind == "setEnvironment").Payload.GetProperty("waves").GetProperty("significantHeight_m").GetDouble();
        Assert.Equal(0.8, Hs(b, "tugReduced"));
        Assert.Equal(1.2, Hs(b, "outerBands"));
        Assert.Empty(b.StructuralIssues());
        Assert.Empty(ScenarioValidator.Check(b, TestData.Fsb1(), null).Where(i => i.Severity == ScenarioIssueSeverity.Error));
    }

    /// <summary>
    /// E17b 無人操作(留在泊位)前 25 分鐘:升級時刻與 E17 相同(T+7 degraded);state.waveMotion 全程輸出,升沉/縱搖幅值隨湧浪 0.5 → 0.8 m 漸增而變大
    /// (泊位上靜止、浪自左舷艉 225°);湧浪的平均漂移力遠小於風力,無碼頭硬接觸、無斷纜;紀錄/重播一致。
    /// </summary>
    [Fact]
    public void E17b_NoOperatorHeaveAndPitchGrowWithSwellAndRecordReplays()
    {
        var sc = Load(E17b);
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var events = TestData.Capture(e);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"{E17b}-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        double early = 0.0, earlyPitch = 0.0, late = 0.0, latePitch = 0.0, maxDrift = 0.0;
        try
        {
            while (e.Time < 1500.0 && !e.Ended)
            {
                e.Step();
                var s = e.Seakeeping!.Last;
                Assert.NotNull(s);
                var heave = Math.Abs(s!.Value.Heave);
                var pitch = Math.Abs(Units.RadToDeg(s.Value.Pitch));
                if (e.Time <= 400.0) { early = Math.Max(early, heave); earlyPitch = Math.Max(earlyPitch, pitch); }
                else if (e.Time >= 1100.0) { late = Math.Max(late, heave); latePitch = Math.Max(latePitch, pitch); }
                var drift = e.Waves!.Last;
                maxDrift = Math.Max(maxDrift, Math.Sqrt(drift.X * drift.X + drift.Y * drift.Y));
            }
            w.Close();
            var st = e.BuildState();
            _out.WriteLine($"E17b: Hs 0.5 m (t ≤ 400 s) max |heave| {early:F3} m |pitch| {earlyPitch:F3}°; Hs 0.8 m (t ≥ 1100 s) max |heave| {late:F3} m |pitch| {latePitch:F3}°; " +
                           $"max wave drift {maxDrift / 1000.0:F1} kN; end Hs {st.Waves!.SignificantHeightM:F2} m");
            Assert.NotNull(st.WaveMotion);
            Assert.Equal(0.8, st.Waves.SignificantHeightM, 9);
            Assert.InRange(early, 0.03, 0.5);
            Assert.InRange(earlyPitch, 0.03, 1.0);
            Assert.True(late > 1.2 * early && latePitch > 1.2 * earlyPitch, $"heave {early} → {late}, pitch {earlyPitch} → {latePitch}");
            Assert.InRange(maxDrift, 0.0, 30_000.0);   // 平均漂移力 < 30 kN(40 kn 風力約百餘 kN)
            Assert.Equal(420.0, events.Single(x => x.Kind == SimEventKinds.PhaseChanged && x.DataString("to") == "degraded").T);
            Assert.Contains(events, x => x.Kind == SimEventKinds.EnvironmentRampDone && x.T == 1020.0 && x.Message == "waveHeight");
            Assert.DoesNotContain(events, x => x.Kind is "quayContact" or "mooringLineParted" or SimEventKinds.Grounding);
            var r = Replayer.Replay(path, TestData.Ship, Mmg);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }
}
