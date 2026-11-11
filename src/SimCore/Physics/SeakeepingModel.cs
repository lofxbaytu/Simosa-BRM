using System.Buffers.Binary;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics;

/// <summary>一次浪一階運動計算的結果(SI、弧度):升沉(m,向上正)、縱搖(rad,艏仰正)與兩者的速率(m/s、rad/s)。</summary>
public readonly record struct SeakeepingResult(double Heave, double Pitch, double HeaveRate, double PitchRate);

/// <summary>
/// 浪一階振盪運動:升沉(heave)與縱搖(pitch)(規劃書第 6.2 節環境外力的浪;R2「浪」;設計文件 docs/design/physics-waves.md 第 11 節)。
/// 線性、只輸出、不回饋 3 自由度運動(與解耦橫搖 <see cref="RollModel"/> 同原則):u/v/r/x/y/ψ、陣風亂數序列與未啟用時逐位元相同。
/// <para>
/// 波場與橫搖的浪激勵是同一個:<see cref="RollModel.WaveComponents"/> 個浪譜成分(ω_i = ω_p·ρ_i,ρ_i 在 [0.6, 2.0] 等分取中點;PM / JONSWAP 權重正規化;
/// 振幅 a_i = (Hs/4)·√(2 w_i);深水波數 k_i = ω_i²/g),相位 ε_i 由情境種子以 <see cref="RollModel.DrawPhases"/> 抽取(同一序列,不另造亂數);
/// 地固波場 η = Σ a_i·sin(θ_i),θ_i = ω_i t − k_i s + ε_i,s = −(x·sin D + y·cos D) 為沿浪傳播方向的座標(D = 來向)。
/// </para>
/// <para>
/// 每個成分以閉式解(線性單自由度對簡諧激勵的穩態響應)合成,不積分、無跨步狀態:
/// 遭遇頻率 ω_e,i = dθ_i/dt = ω_i + k_i·V_D(V_D = 對地速度朝浪源的分量);χ = 來向 − 航向(0 = 頂浪、±90° = 橫浪),x_i = k_i·L_pp·cos χ / 2。
/// 升沉:ω_h² = g·A_wp/(∇ + A_33)(A_33 = ∇,ASSUMED;A_wp = C_wp·L·B,C_wp = (1 + 2C_b)/3);
/// 激勵 = a_i·sinc(x_i)·exp(−k_i T/2)(沿船長平均的波面 × Smith 修正);z = Σ 激勵·RAO(r)·sin(θ_i − δ),r = ω_e/ω_h。
/// 縱搖:ω_p² = g·∇·GM_L/I_55 = g·GM_L/(1.2·(k_yy L)²)(I_55 = ρ∇(k_yy L)²·1.2,附加慣量 ASSUMED;GM_L 情境值或 3·C_wp²·L²/(40·C_b·T));
/// 激勵 = a_i·k_i·cos χ·F_p(x_i)·exp(−k_i T/2),F_p(x) = 3(sin x − x cos x)/x³(沿船長的波面一次矩;長波時 → 沿船長的波斜率 a k cos χ,
/// 橫浪 cos χ = 0 → 0);θ = Σ 激勵·RAO(r)·cos(θ_i − δ)。RAO(r) = 1/√((1 − r²)² + (2ζr)²)、相位滯後 δ = atan2(2ζr, 1 − r²)。
/// 速率為解析微分(RAO、δ、χ 視為緩變):z' = Σ A·ω_e·cos(θ_i − δ)、θ' = −Σ A·ω_e·sin(θ_i − δ)。
/// </para>
/// <para>
/// θ_i 以自船位置取值(與橫搖相同),船速/航向改變時波的相位連續;RAO 與 δ 則直接代入當下的 ω_e、長度濾波代入當下的 χ(不模擬暫態:船速或航向急變時
/// 各成分的振幅與相位滯後立刻跳到新的穩態值,輸出可能有小的不連續,可接受;設計文件第 11.3、11.6 節)。
/// 縱搖激勵採用以縱向復原力矩正規化的一次矩 F_p(長波極限 = 波斜率、保留正負號);規劃時的近似式 a·k·|2(sin x/x − cos x)/x| 與它只差無因次因子 k·L/3(第 11.3 節註)。
/// 確定性:輸出只由步末狀態、時刻、目前 Hs、浪設定與種子決定(純函數),
/// 快照不需新欄位(還原後重算);只在情境 ship.seakeeping.enabled 且有浪(Hs &gt; 0)時輸出 state.waveMotion 並追加雜湊區塊,
/// 其餘情境的輸出、雜湊、夾具與黃金測試不變。C# 與 Python 參考實作(seakeeping.py)逐項同運算順序。
/// </para>
/// </summary>
public sealed class SeakeepingModel
{
    public const long HashMarker = 0x5345414B_45455031; // "SEAKEEP1"
    public const int HashLongs = 5;
    public const double Gravity = RollModel.Gravity;
    private const double TwoPi = 2.0 * Math.PI;
    /// <summary>浪譜成分數與頻率範圍:與橫搖的浪激勵相同(同一個地固波場)</summary>
    public const int WaveComponents = RollModel.WaveComponents;
    public const double OmegaMinRatio = RollModel.OmegaMinRatio;
    public const double OmegaMaxRatio = RollModel.OmegaMaxRatio;
    /// <summary>升沉阻尼比預設(ASSUMED 0.35:升沉/縱搖的興波阻尼大,文獻量級 0.2–0.5)</summary>
    public const double DefaultHeaveDampingRatio = 0.35;
    /// <summary>縱搖阻尼比預設(ASSUMED 0.35)</summary>
    public const double DefaultPitchDampingRatio = 0.35;
    /// <summary>縱搖迴轉半徑比 k_yy/L 預設(ASSUMED 0.25;商船 0.24–0.26)</summary>
    public const double DefaultPitchGyradiusRatio = 0.25;
    /// <summary>升沉附加質量比 A_33/(ρ∇)(ASSUMED 1.0:A_33 ≈ 排水質量)</summary>
    public const double HeaveAddedMassRatio = 1.0;
    /// <summary>縱搖慣量的附加慣量倍率 I_55 = ρ∇(k_yy L)²·1.2(ASSUMED)</summary>
    public const double PitchAddedInertiaFactor = 1.2;
    /// <summary>海水密度(kg/m³;排水量 → 排水體積)</summary>
    public const double WaterDensity = RollModel.WaterDensity;
    /// <summary>|x| 小於此值時 sinc(x) 取 1(sin x / x 在 x = 0 無定義)</summary>
    public const double SincSmallX = 1e-8;
    /// <summary>|x| 小於此值時 F_p(x) 以級數 1 − x²/10 + x⁴/280 計算(直接式的相消誤差 ∝ 1/x²;此處級數截斷誤差 &lt; 1e-12)</summary>
    public const double PitchFilterSeriesX = 0.05;

    private readonly double[] _phases;
    private readonly double[] _omega = new double[WaveComponents];
    private readonly double[] _wavenumber = new double[WaveComponents];
    /// <summary>每單位 Hs 的波幅 √(2 w_i)/4(無因次)</summary>
    private readonly double[] _ampPerHs = new double[WaveComponents];
    /// <summary>Smith 修正 exp(−k_i·T/2)</summary>
    private readonly double[] _smith = new double[WaveComponents];
    private double _waveDirRad;
    private bool _hasWaves;

    // ------------------------------------------------------------------ 設定(建構時定案)

    public bool Enabled { get; }
    /// <summary>垂線間長 L_pp(m)</summary>
    public double L { get; }
    public double B { get; }
    /// <summary>平均吃水(m)</summary>
    public double T { get; }
    /// <summary>方形係數 C_b(particulars;缺值時由滿載 C_b 依吃水比估計,與橫搖/Python particulars 同式)</summary>
    public double Cb { get; }
    /// <summary>水線面係數 C_wp = (1 + 2·C_b)/3(ASSUMED;與波浪漂移力的水線幾何同式)</summary>
    public double Cwp { get; }
    /// <summary>水線面積 A_wp = C_wp·L·B(m²)</summary>
    public double WaterplaneAreaM2 { get; }
    /// <summary>排水體積 ∇(m³;particulars 排水量 / ρ,缺值時 C_b·L·B·T)</summary>
    public double VolumeM3 { get; }
    /// <summary>升沉自然角頻率 ω_h(rad/s)</summary>
    public double HeaveOmega { get; }
    public double HeaveNaturalPeriodS => TwoPi / HeaveOmega;
    public double HeaveDampingRatio { get; }
    /// <summary>縱穩心高 GM_L(m):情境 gmL_m,或估計 3·C_wp²·L²/(40·C_b·T)(≈ BM_L;FSB 兩船約 1.0–1.2 L)</summary>
    public double GmL { get; }
    /// <summary>GM_L 來自情境(false = 由 particulars 估計,ASSUMED)</summary>
    public bool GmLFromScenario { get; }
    /// <summary>縱搖迴轉半徑比 k_yy/L</summary>
    public double PitchGyradiusRatio { get; }
    /// <summary>縱搖自然角頻率 ω_p(rad/s)</summary>
    public double PitchOmega { get; }
    public double PitchNaturalPeriodS => TwoPi / PitchOmega;
    public double PitchDampingRatio { get; }
    /// <summary>浪成分相位 ε_i(弧度,[0, 2π);與橫搖相同:情境種子的 <see cref="RollModel.DrawPhases"/>)</summary>
    public IReadOnlyList<double> Phases => _phases;
    /// <summary>目前有浪設定(Tp/來向/浪譜;Hs 於每次計算傳入)</summary>
    public bool HasWaves => _hasWaves;

    // ------------------------------------------------------------------ 最近一次結果(雜湊與 state.waveMotion)

    /// <summary>最近一次步末(或載入/還原/浪設定變更後)的結果;無浪或 Hs ≤ 0 時 null(不輸出、不進雜湊)</summary>
    public SeakeepingResult? Last { get; private set; }

    public SeakeepingModel(ShipParticulars ship, LoadingCondition loading, ScenarioSeakeeping cfg, int seed)
        : this(ship, loading, cfg, RollModel.DrawPhases(seed)) { }

    /// <summary>以給定相位建立(測試用);相位長度須為 <see cref="WaveComponents"/>。</summary>
    public SeakeepingModel(ShipParticulars ship, LoadingCondition loading, ScenarioSeakeeping cfg, double[] phases)
    {
        if (phases.Length != WaveComponents) throw new ArgumentException($"相位數須為 {WaveComponents}", nameof(phases));
        _phases = (double[])phases.Clone();
        Enabled = cfg.Enabled;
        var lc = ship.GetLoading(loading);
        var hull = ship.Hull;
        L = hull.LengthBetweenPerpendiculars_m;
        B = hull.Breadth_m;
        // 平均吃水:draftMean_m,缺值時艏艉平均(RollModel / Python particulars._resolve_loading 相同)
        T = lc.DraftMean_m ?? 0.5 * ((lc.DraftFore_m ?? 0.0) + (lc.DraftAft_m ?? 0.0));
        // C_b:particulars 值;缺值時由滿載 C_b 依吃水比估計(與 Python _resolve_loading 同式)
        double cb;
        if (lc.BlockCoefficient is { } cbGiven) cb = cbGiven;
        else
        {
            var full = ship.GetLoading(LoadingCondition.Full);
            var cbFull = full.BlockCoefficient ?? 0.75;
            var tFull = full.DraftMean_m ?? T;
            cb = cbFull - 0.11 * Math.Max(0.0, 1.0 - T / tFull);
        }
        Cb = cb;
        // 排水體積:particulars 排水量 / ρ;缺值時 C_b·L·B·T·ρ / ρ
        var dispT = lc.Displacement_t ?? cb * L * B * T * WaterDensity / 1000.0;
        VolumeM3 = dispT * 1000.0 / WaterDensity;
        Cwp = (1.0 + 2.0 * cb) / 3.0;
        WaterplaneAreaM2 = Cwp * L * B;
        // 升沉:ω_h² = ρ g A_wp / (ρ∇ + A_33),A_33 = HeaveAddedMassRatio·ρ∇
        HeaveOmega = Math.Sqrt(Gravity * WaterplaneAreaM2 / (VolumeM3 * (1.0 + HeaveAddedMassRatio)));
        HeaveDampingRatio = cfg.HeaveDampingRatio ?? DefaultHeaveDampingRatio;
        // 縱搖:ω_p² = ρ g ∇ GM_L / I_55,I_55 = ρ∇(k_yy L)²·PitchAddedInertiaFactor → g·GM_L / (factor·(k_yy L)²)
        GmLFromScenario = cfg.GmLM is not null;
        GmL = cfg.GmLM ?? 3.0 * Cwp * Cwp * L * L / (40.0 * cb * T);
        PitchGyradiusRatio = cfg.PitchGyradiusRatio ?? DefaultPitchGyradiusRatio;
        var kyy = PitchGyradiusRatio * L;
        PitchOmega = Math.Sqrt(Gravity * GmL / (PitchAddedInertiaFactor * (kyy * kyy)));
        PitchDampingRatio = cfg.PitchDampingRatio ?? DefaultPitchDampingRatio;
    }

    /// <summary>情境 ship.seakeeping.enabled 時建立(否則 null);相位由情境種子抽取(與橫搖同一序列)。</summary>
    public static SeakeepingModel? Create(ShipParticulars ship, LoadingCondition loading, Scenario.Scenario sc)
        => sc.Ship.Seakeeping is { Enabled: true } cfg ? new SeakeepingModel(ship, loading, cfg, sc.Seed) : null;

    // ------------------------------------------------------------------ 浪設定

    /// <summary>
    /// 設定(或清除)浪成分:Tp、來向、浪譜與 γ(Hs 於每次計算傳入,支援漸變);相位不變。成分、權重與波數的算式與
    /// <see cref="RollModel.SetWaves"/> 逐項相同(同一個波場;<c>SeakeepingModelTests.WaveFieldIsTheSameAsRollExcitationAndPhasesComeFromScenarioSeed</c> 以橫搖的波斜率核對),
    /// 另存每單位 Hs 的波幅 √(2 w_i)/4 與 Smith 修正 exp(−k_i·T/2)。清除時 <see cref="Last"/> 一併清除。
    /// </summary>
    public void SetWaves(ScenarioWaves? waves)
    {
        if (waves is null)
        {
            _hasWaves = false;
            Last = null;
            return;
        }
        var tp = waves.PeakPeriodS ?? 1.0;
        var spectrum = (waves.Spectrum ?? "pm").ToLowerInvariant();
        var gamma = waves.Gamma ?? 3.3;
        _waveDirRad = Units.DegToRad(waves.DirectionDeg ?? 0.0);
        var omegaP = TwoPi / tp;
        var weights = new double[WaveComponents];
        var sum = 0.0;
        for (var i = 0; i < WaveComponents; i++)
        {
            var rr = OmegaMinRatio + (i + 0.5) * ((OmegaMaxRatio - OmegaMinRatio) / WaveComponents);
            var r4 = rr * rr * rr * rr;
            var f = Math.Exp(-1.25 / r4) / (r4 * rr);
            if (spectrum == "jonswap")
            {
                var sig = rr <= 1.0 ? 0.07 : 0.09;
                var d = rr - 1.0;
                f *= Math.Pow(gamma, Math.Exp(-(d * d) / (2.0 * sig * sig)));
            }
            _omega[i] = omegaP * rr;
            weights[i] = f;
            sum += f;
        }
        for (var i = 0; i < WaveComponents; i++)
        {
            var w = weights[i] / sum;
            var k = _omega[i] * _omega[i] / Gravity;
            _wavenumber[i] = k;
            _ampPerHs[i] = Math.Sqrt(2.0 * w) / 4.0;
            _smith[i] = Math.Exp(-0.5 * k * T);
        }
        _hasWaves = true;
    }

    // ------------------------------------------------------------------ 純函數

    /// <summary>sinc(x) = sin x / x(|x| &lt; <see cref="SincSmallX"/> 時 1):沿船長均勻平均的波面相對於船舯波面的比值(升沉的長度濾波;可為負 = 反相)。</summary>
    public static double Sinc(double x) => Math.Abs(x) < SincSmallX ? 1.0 : Math.Sin(x) / x;

    /// <summary>
    /// 縱搖的長度濾波 F_p(x) = 3(sin x − x cos x)/x³(x → 0 時 1;|x| &lt; <see cref="PitchFilterSeriesX"/> 用級數 1 − x²/10 + x⁴/280):
    /// 均勻水線面上波面一次矩 (12/L³)∫ξ·η dξ 相對於船舯波斜率的比值(偶函數;可為負)。
    /// </summary>
    public static double PitchFilter(double x)
    {
        if (Math.Abs(x) < PitchFilterSeriesX)
        {
            var x2 = x * x;
            return 1.0 - x2 / 10.0 + x2 * x2 / 280.0;
        }
        return 3.0 * (Math.Sin(x) - x * Math.Cos(x)) / (x * x * x);
    }

    /// <summary>
    /// 線性單自由度的穩態響應:頻率比 r = ω_e/ω_n(可為負:順浪且船速超過波速時遭遇頻率為負,相位滯後隨之變號)、阻尼比 ζ →
    /// 放大倍率 RAO = 1/√((1 − r²)² + (2ζr)²) 與相位滯後 δ = atan2(2ζr, 1 − r²)(r → 0:1 與 0;r = 1:1/(2ζ) 與 π/2;r → ∞:0 與 π)。
    /// </summary>
    public static (double Rao, double Lag) Response(double r, double zeta)
    {
        var d = 1.0 - r * r;
        var q = 2.0 * zeta * r;
        return (1.0 / Math.Sqrt(d * d + q * q), Math.Atan2(q, d));
    }

    /// <summary>
    /// 升沉/縱搖與速率(純函數版;公式見類別說明):位置 (x 東, y 北)、航向 ψ、對地速度 (velEast, velNorth)、有義波高 hs、時刻 t。
    /// 各成分依序累加(C# 與 Python 同運算順序),最後乘 hs。
    /// </summary>
    public static SeakeepingResult Compute(double[] omega, double[] wavenumber, double[] ampPerHs, double[] smith, double[] phases, double dirRad,
        double x, double y, double psi, double velEast, double velNorth, double hs, double t,
        double lpp, double heaveOmega, double heaveZeta, double pitchOmega, double pitchZeta)
    {
        var chi = Units.WrapRadPi(dirRad - psi);
        var cosChi = Math.Cos(chi);
        var sinDir = Math.Sin(dirRad);
        var cosDir = Math.Cos(dirRad);
        var s = -(x * sinDir + y * cosDir);
        // 對地速度朝浪源的分量(= −ds/dt):頂浪前進為正 → 遭遇頻率升高
        var towards = velEast * sinDir + velNorth * cosDir;
        var heave = 0.0;
        var pitch = 0.0;
        var heaveRate = 0.0;
        var pitchRate = 0.0;
        for (var i = 0; i < omega.Length; i++)
        {
            var k = wavenumber[i];
            var omegaE = omega[i] + k * towards;
            var theta = omega[i] * t - k * s + phases[i];
            var xh = 0.5 * k * lpp * cosChi;
            var a = ampPerHs[i] * smith[i];
            var (raoH, lagH) = Response(omegaE / heaveOmega, heaveZeta);
            var ah = a * Sinc(xh) * raoH;
            var ph = theta - lagH;
            heave += ah * Math.Sin(ph);
            heaveRate += ah * omegaE * Math.Cos(ph);
            var (raoP, lagP) = Response(omegaE / pitchOmega, pitchZeta);
            var ap = a * k * cosChi * PitchFilter(xh) * raoP;
            var pp = theta - lagP;
            pitch += ap * Math.Cos(pp);
            pitchRate -= ap * omegaE * Math.Sin(pp);
        }
        return new SeakeepingResult(hs * heave, hs * pitch, hs * heaveRate, hs * pitchRate);
    }

    /// <summary>
    /// 自船位置的波面高 η(m,向上正)= Hs·Σ (a_i/Hs)·sin(ω_i t − k_i s + ε_i)(診斷/測試:長波極限下升沉趨近此值;無浪或 Hs ≤ 0 為 0)。
    /// </summary>
    public double WaveElevationAt(double x, double y, double hs, double t)
    {
        if (!_hasWaves || !(hs > 0.0)) return 0.0;
        var s = -(x * Math.Sin(_waveDirRad) + y * Math.Cos(_waveDirRad));
        var sum = 0.0;
        for (var i = 0; i < WaveComponents; i++) sum += _ampPerHs[i] * Math.Sin(_omega[i] * t - _wavenumber[i] * s + _phases[i]);
        return hs * sum;
    }

    /// <summary>
    /// 自船位置沿浪傳播方向的波斜率 ∂η/∂s(rad)= −Hs·Σ k_i·(a_i/Hs)·cos(θ_i)(診斷/測試:等於正橫浪(χ = +90°)、waveGain 1 時橫搖的有效波斜率
    /// <see cref="RollModel.WaveSlopeAt"/>,用以核對兩個模型是同一個波場;無浪或 Hs ≤ 0 為 0)。
    /// </summary>
    public double WaveSlopeAlongPropagationAt(double x, double y, double hs, double t)
    {
        if (!_hasWaves || !(hs > 0.0)) return 0.0;
        var s = -(x * Math.Sin(_waveDirRad) + y * Math.Cos(_waveDirRad));
        var sum = 0.0;
        for (var i = 0; i < WaveComponents; i++) sum += _wavenumber[i] * _ampPerHs[i] * Math.Cos(_omega[i] * t - _wavenumber[i] * s + _phases[i]);
        return -hs * sum;
    }

    // ------------------------------------------------------------------ 計算

    /// <summary>
    /// 以狀態 <paramref name="s"/>、自船位置的流(ENU,m/s;對地速度 = 對水速度 + 流,決定遭遇頻率)、目前 Hs 與時刻 t 計算;
    /// 無浪設定或 Hs ≤ 0 → null(不輸出)。
    /// </summary>
    public SeakeepingResult? Evaluate(in StateVector s, double currentEastMps, double currentNorthMps, double hs, double t)
    {
        if (!_hasWaves || !(hs > 0.0)) return null;
        var (velE, velN) = Environment.EnvironmentMath.GroundVelocity(s.U, s.V, s.Psi, currentEastMps, currentNorthMps);
        return Compute(_omega, _wavenumber, _ampPerHs, _smith, _phases, _waveDirRad, s.X, s.Y, s.Psi, velE, velN, hs, t,
            L, HeaveOmega, HeaveDampingRatio, PitchOmega, PitchDampingRatio);
    }

    /// <summary>步末(與載入/還原/浪設定變更後):以目前狀態重算 <see cref="Last"/>(雜湊與 state.waveMotion)。</summary>
    public void UpdateLast(in StateVector s, double currentEastMps, double currentNorthMps, double hs, double t)
        => Last = Evaluate(s, currentEastMps, currentNorthMps, hs, t);

    // ------------------------------------------------------------------ 雜湊 / 狀態

    /// <summary>雜湊區塊(只在 <see cref="Last"/> 有值時追加):標記 SEAKEEP1、升沉(m)、縱搖(rad)、升沉速率(m/s)、縱搖速率(rad/s)。</summary>
    public int WriteHash(Span<byte> buf)
    {
        var o = 0;
        void L64(Span<byte> b, long v) { BinaryPrimitives.WriteInt64LittleEndian(b[o..], v); o += 8; }
        void D64(Span<byte> b, double v) => L64(b, BitConverter.DoubleToInt64Bits(v));
        var r = Last ?? default;
        L64(buf, HashMarker);
        D64(buf, r.Heave); D64(buf, r.Pitch); D64(buf, r.HeaveRate); D64(buf, r.PitchRate);
        return o;
    }

    /// <summary>state.waveMotion(m、度、m/s、度/秒);無輸出時 null。</summary>
    public WaveMotionState? State() => Last is { } r
        ? new WaveMotionState
        {
            HeaveM = r.Heave,
            PitchDeg = Units.RadToDeg(r.Pitch),
            HeaveRateMps = r.HeaveRate,
            PitchRateDps = Units.RadToDeg(r.PitchRate),
        }
        : null;
}
