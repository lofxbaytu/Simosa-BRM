using System.Buffers.Binary;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics.External;

/// <summary>
/// 平均二階波浪漂移力的係數與離散化參數(設計文件 docs/design/physics-waves.md 第 3、4 節)。
/// 數值為文獻量級的估計值(兩船無耐航模型試驗;離線開發),集中於此以便校正;C# 與 Python 參考實作(external.py WAVE_DEFAULTS)相同。
/// </summary>
public sealed record WaveDriftCoefficients
{
    /// <summary>艏艉端平面寬度比 e:水線在艏艉端的半寬 = e·B/2(球艏/方艉的鈍度;Fujii–Takahashi 鈍度係數 B_f 落在 0.3–0.4)</summary>
    public double EndFullness { get; init; } = 0.3;
    /// <summary>反射係數的吃水修正倍率(Fujii–Takahashi 以 α_d(1.5·k·T) 取代 Ursell 的 α_d(k·T))</summary>
    public double DraftFactor { get; init; } = 1.5;
    /// <summary>λ/L 截止:D_L = 1 / (1 + (λ / (c·L))⁴),c = 2(短波漸近公式在 λ ≳ 2L 不再適用,長波時趨近零)</summary>
    public double CutoffWavelengthL { get; init; } = 2.0;
    /// <summary>浪譜離散成分數(ω 自 OmegaMinRatio·ω_p 到 OmegaMaxRatio·ω_p 等分,中點取樣)</summary>
    public int Components { get; init; } = 40;
    public double OmegaMinRatio { get; init; } = 0.5;
    public double OmegaMaxRatio { get; init; } = 3.0;
    /// <summary>水線離散站數(每舷;艏艉平面另計)</summary>
    public int Stations { get; init; } = 40;
    /// <summary>有限水深色散關係 Newton 迭代次數(固定次數,兩種語言結果逐位元相同)</summary>
    public int DispersionIterations { get; init; } = 12;
    /// <summary>海水密度(kg/m³)</summary>
    public double Rho { get; init; } = 1025.0;

    public static WaveDriftCoefficients Default { get; } = new();
}

/// <summary>一次漂移力計算的結果(船體座標;N、N·m):力、峰值波長(目前水深)、譜加權衰減/速度係數 K 與幾何積分 G。</summary>
public readonly record struct WaveDriftResult(double X, double Y, double N, double WavelengthM, double Factor, double Gx, double Gy, double Gn)
{
    public BodyForce Force => new(X, Y, N);
}

/// <summary>
/// 平均二階波浪漂移力(規劃書第 6.2 節 W 項的浪;R2/R3;設計文件 docs/design/physics-waves.md):
/// 以短波漸近(Faltinsen 1980 型)水線反射積分計算 surge/sway/yaw 的平均漂移力,強度 ∝ ρ g Hs²/16(= ρ g m0),
/// 每個浪譜成分乘以 Ursell 垂直障板反射係數平方 α_d(1.5·k·T)(吃水/波長)、λ/L 截止與遭遇頻率修正 (1 + U_w/c)²,
/// 再以 PM 或 JONSWAP 譜形加權。3 自由度:不含一階振盪運動與橫搖;無相位、無亂數(確定性)。
/// 情境有 environment.waves 且動力學接受外力時啟用(<see cref="Active"/>);沒有 waves 的情境零力、輸出與雜湊不變。
/// </summary>
public sealed class WaveDriftModel : IExternalForceField
{
    public const long HashMarker = 0x57415645_44524654; // "WAVEDRFT"
    public const int HashLongs = 8;
    public const double Gravity = 9.80665;
    private const double TwoPi = 2.0 * Math.PI;

    private readonly double[] _omegaRatio;
    private readonly double[] _weight;
    private readonly double[] _invCelerity;
    private readonly double[] _factor;
    private readonly double[] _segXa, _segYa, _segXb, _segYb;
    private readonly double _faceLength;
    private double _cachedDepth = double.NaN;
    private double _cachedPeakWavelength;

    public WaveDriftCoefficients Coefficients { get; }
    /// <summary>有義波高 Hs(m):情境值,或 setEnvironment waves 設定/漸變的目前值(<see cref="SetSignificantHeight"/>);只影響力的強度 ∝ Hs²</summary>
    public double SignificantHeightM { get; private set; }
    public double PeakPeriodS { get; }
    /// <summary>浪來向(弧度,自北順時針;氣象慣例,與風相同)</summary>
    public double DirectionRad { get; }
    /// <summary>浪來向(度,0–360)</summary>
    public double DirectionDeg { get; }
    public string Spectrum { get; }
    public double Gamma { get; }
    public bool Supported { get; }
    public bool Active => Supported;
    public double L { get; }
    public double B { get; }
    public double T { get; }
    public double Cb { get; }
    /// <summary>水線進流段/去流段長度(m)</summary>
    public double EntranceLengthM { get; }
    /// <summary>最近一次步末計算(雜湊與 state.waves;引擎於每步末與還原後更新)</summary>
    public WaveDriftResult Last { get; private set; }

    public WaveDriftModel(ShipParticulars ship, LoadingCondition loading, ScenarioWaves waves, bool supported, WaveDriftCoefficients? coefficients = null)
    {
        Supported = supported;
        var c = Coefficients = coefficients ?? WaveDriftCoefficients.Default;
        var lc = ship.GetLoading(loading);
        L = ship.Hull.LengthBetweenPerpendiculars_m;
        B = ship.Hull.Breadth_m;
        T = lc.DraftMean_m ?? lc.MaxDraft_m;
        Cb = lc.BlockCoefficient ?? 0.75;
        SignificantHeightM = waves.SignificantHeightM ?? 0.0;
        PeakPeriodS = waves.PeakPeriodS ?? 1.0;
        DirectionDeg = Units.NormalizeHeadingDeg(waves.DirectionDeg ?? 0.0);
        DirectionRad = Units.DegToRad(waves.DirectionDeg ?? 0.0);
        Spectrum = (waves.Spectrum ?? "pm").ToLowerInvariant();
        Gamma = waves.Gamma ?? 3.3;

        // 浪譜成分:ω_i/ω_p 在 [min, max] 等分取中點;權重為譜形(PM:ρ⁻⁵·exp(−1.25ρ⁻⁴);JONSWAP 再乘 γ^exp(−(ρ−1)²/2σ²)),正規化成總和 1
        var n = c.Components;
        _omegaRatio = new double[n];
        _weight = new double[n];
        _invCelerity = new double[n];
        _factor = new double[n];
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var rr = c.OmegaMinRatio + (i + 0.5) * ((c.OmegaMaxRatio - c.OmegaMinRatio) / n);
            var r4 = rr * rr * rr * rr;
            var f = Math.Exp(-1.25 / r4) / (r4 * rr);
            if (Spectrum == "jonswap")
            {
                var sig = rr <= 1.0 ? 0.07 : 0.09;
                var d = rr - 1.0;
                f *= Math.Pow(Gamma, Math.Exp(-(d * d) / (2.0 * sig * sig)));
            }
            _omegaRatio[i] = rr;
            _weight[i] = f;
            sum += f;
        }
        for (var i = 0; i < n; i++) _weight[i] = _weight[i] / sum;

        // 水線:平行中體 + 拋物線進流/去流段(端部半寬 e·B/2),艏艉另有寬 e·B 的平面;站數 Stations(每舷)
        var e = c.EndFullness;
        var cw = (1.0 + 2.0 * Cb) / 3.0;
        var le = Math.Min(0.5 * L, Math.Max(0.1 * L, 1.5 * (1.0 - cw) * L / (1.0 - e)));
        EntranceLengthM = le;
        var lp = L - 2.0 * le;
        var m = c.Stations;
        _segXa = new double[m]; _segYa = new double[m]; _segXb = new double[m]; _segYb = new double[m];
        for (var j = 0; j < m; j++)
        {
            var xa = -0.5 * L + j * (L / m);
            var xb = -0.5 * L + (j + 1) * (L / m);
            _segXa[j] = xa; _segYa[j] = HalfBreadth(xa, lp, le, e);
            _segXb[j] = xb; _segYb[j] = HalfBreadth(xb, lp, le, e);
        }
        _faceLength = 2.0 * HalfBreadth(0.5 * L, lp, le, e);
    }

    /// <summary>情境有 environment.waves 時建立(否則 null)。</summary>
    public static WaveDriftModel? Create(ShipParticulars ship, LoadingCondition loading, Scenario.Scenario sc, bool supported)
        => Create(ship, loading, sc.Environment.Waves, supported);

    /// <summary>依浪設定建立(null = 無浪 → null);引擎以目前設定(情境值或 setEnvironment waves 變更後的值)呼叫。</summary>
    public static WaveDriftModel? Create(ShipParticulars ship, LoadingCondition loading, ScenarioWaves? waves, bool supported)
        => waves is { } w ? new WaveDriftModel(ship, loading, w, supported) : null;

    /// <summary>
    /// 練習中變更有義波高(setEnvironment waves 的 Hs,含漸變的每步插值;設計文件 physics-waves.md「練習中變更」):
    /// 只改力的強度 ∝ Hs²,譜權重、水線幾何與衰減係數不變(與以該 Hs 重建模型的結果逐位元相同);負值視為 0。
    /// 呼叫者須於步末(或變更後)以 <see cref="UpdateLast"/> 重算 <see cref="Last"/>。
    /// </summary>
    public void SetSignificantHeight(double hs) => SignificantHeightM = hs > 0.0 ? hs : 0.0;

    /// <summary>半寬 y(x):|x| ≤ Lp/2 為 B/2;進流/去流段 (B/2)·(e + (1 − e)·(1 − s²)),s = (|x| − Lp/2)/L_e。</summary>
    private double HalfBreadth(double x, double lp, double le, double e)
    {
        var ax = Math.Abs(x);
        if (ax <= 0.5 * lp) return 0.5 * B;
        var s = (ax - 0.5 * lp) / le;
        return 0.5 * B * (e + (1.0 - e) * (1.0 - s * s));
    }

    /// <summary>步末(與還原後):以目前狀態與所在有效水深重算 <see cref="Last"/>(雜湊與 state.waves)。</summary>
    public void UpdateLast(in StateVector s, double waterDepth) => Last = Compute(s, waterDepth);

    public BodyForce Evaluate(in StateVector state, in EnvironmentSample env) => Compute(state, env.WaterDepthM).Force;

    /// <summary>
    /// 漂移力(設計文件第 3 節):χ = 來向 − 航向(船體座標,0 = 迎浪、+90° = 右舷來),d = (cos χ, sin χ);
    /// K = Σ w_i·α_d,i·D_L,i·max(0, 1 + U_w·k_i/ω_i)²,U_w = u·d_x + v·d_y(朝浪源的對水速度分量);
    /// G = Σ_{n·d &gt; 0} (n·d)²·(−n)·dl 與力矩 Σ (x·f_y − y·f_x);F = ρ g Hs²/16 · K · G。
    /// </summary>
    public WaveDriftResult Compute(in StateVector s, double waterDepth)
    {
        EnsureDepth(waterDepth);
        var chi = Units.WrapRadPi(DirectionRad - s.Psi);
        var dx = Math.Cos(chi);
        var dy = Math.Sin(chi);
        var uw = s.U * dx + s.V * dy;
        var k = 0.0;
        for (var i = 0; i < _weight.Length; i++)
        {
            var sp = 1.0 + uw * _invCelerity[i];
            if (sp < 0.0) sp = 0.0;
            k += _weight[i] * _factor[i] * (sp * sp);
        }
        double gx = 0.0, gy = 0.0, gn = 0.0;
        for (var j = 0; j < _segXa.Length; j++)
        {
            var xa = _segXa[j]; var ya = _segYa[j]; var xb = _segXb[j]; var yb = _segYb[j];
            var dxs = xb - xa; var dys = yb - ya;
            var len = Math.Sqrt(dxs * dxs + dys * dys);
            var nx = -dys / len; var ny = dxs / len;
            var mx = 0.5 * (xa + xb); var my = 0.5 * (ya + yb);
            // 右舷段(外法向 (nx, ny))
            var cs = nx * dx + ny * dy;
            if (cs > 0.0)
            {
                var p = cs * cs * len;
                var fx = -p * nx; var fy = -p * ny;
                gx += fx; gy += fy; gn += mx * fy - my * fx;
            }
            // 左舷鏡像段(外法向 (nx, −ny)、中點 (mx, −my))
            var cp = nx * dx - ny * dy;
            if (cp > 0.0)
            {
                var p = cp * cp * len;
                var fx = -p * nx; var fy = p * ny;
                gx += fx; gy += fy; gn += mx * fy + my * fx;
            }
        }
        // 艏平面(法向 +x)與艉平面(法向 −x):力沿 x、作用於中心線,無力矩
        if (dx > 0.0) gx += -(dx * dx * _faceLength);
        else if (dx < 0.0) gx += dx * dx * _faceLength;
        var q = Coefficients.Rho * Gravity * SignificantHeightM * SignificantHeightM / 16.0 * k;
        return new WaveDriftResult(q * gx, q * gy, q * gn, _cachedPeakWavelength, k, gx, gy, gn);
    }

    /// <summary>水深改變時重算各成分的波數、1/c 與衰減係數(α_d·D_L)及峰值波長(同一步四個 RK4 階段水深相同,只算一次)。</summary>
    private void EnsureDepth(double depth)
    {
        if (depth == _cachedDepth) return;
        _cachedDepth = depth;
        var c = Coefficients;
        var omegaP = TwoPi / PeakPeriodS;
        for (var i = 0; i < _weight.Length; i++)
        {
            var omega = omegaP * _omegaRatio[i];
            var k = WaveNumber(omega, depth, c.DispersionIterations);
            _invCelerity[i] = k / omega;
            var lambda = TwoPi / k;
            var ad = UrsellReflection(c.DraftFactor * k * T);
            var rl = lambda / (c.CutoffWavelengthL * L);
            var rl2 = rl * rl;
            var dl = 1.0 / (1.0 + rl2 * rl2);
            _factor[i] = ad * dl;
        }
        _cachedPeakWavelength = TwoPi / WaveNumber(omegaP, depth, c.DispersionIterations);
    }

    /// <summary>
    /// 波數 k:深水 k0 = ω²/g;水深 h &lt; λ0/2(λ0 = 2π/k0)時以有限水深色散關係 ω² = g·k·tanh(k·h) 用固定次數 Newton 迭代(自 k0 起算)求解;
    /// h ≤ 0 或無限大視為深水。
    /// </summary>
    public static double WaveNumber(double omega, double depth, int iterations = 12)
    {
        var w2 = omega * omega;
        var k0 = w2 / Gravity;
        if (!(depth > 0.0) || double.IsInfinity(depth)) return k0;
        // 一律以有限水深色散關係自 k0 起 Newton 迭代(深水時收斂回 k0;不以 h = λ0/2 切換分支,避免越過該等深線時 0.37 % 的跳變)
        var k = k0;
        for (var n = 0; n < iterations; n++)
        {
            var kh = k * depth;
            var th = Math.Tanh(kh);
            var ch = Math.Cosh(kh);
            var f = Gravity * k * th - w2;
            var fp = Gravity * (th + kh / (ch * ch));
            k = k - f / fp;
        }
        return k;
    }

    /// <summary>峰值波長(m):2π / k(ω_p, h)。</summary>
    public static double PeakWavelength(double peakPeriodS, double depth, int iterations = 12) => TwoPi / WaveNumber(TwoPi / peakPeriodS, depth, iterations);

    /// <summary>
    /// Ursell(1947)垂直障板反射係數平方 α_d(x) = π²I1²(x) / (π²I1²(x) + K1²(x))(x = 1.5·k·T 為 Fujii–Takahashi 修正);
    /// I1、K1 以 Abramowitz &amp; Stegun 9.8.3/9.8.4、9.8.7/9.8.8 多項式近似(|誤差| ≲ 1e-7),x ≥ 3.75 以漸近式比值 K1/(πI1) 計算避免 e^x 溢位;x ≤ 0 為 0。
    /// </summary>
    public static double UrsellReflection(double x)
    {
        if (!(x > 0.0)) return 0.0;
        double ratio;
        if (x >= 3.75)
        {
            var t = 3.75 / x;
            var pi1 = 0.39894228 + t * (-0.03988024 + t * (-0.00362018 + t * (0.00163801 + t * (-0.01031555 + t * (0.02282967 + t * (-0.02895312 + t * (0.01787654 + t * (-0.00420059))))))));
            var s = 2.0 / x;
            var pk1 = 1.25331414 + s * (0.23498619 + s * (-0.03655620 + s * (0.01504268 + s * (-0.00780353 + s * (0.00325614 + s * (-0.00068245))))));
            ratio = pk1 * Math.Exp(-2.0 * x) / (Math.PI * pi1);
        }
        else ratio = BesselK1(x) / (Math.PI * BesselI1(x));
        return 1.0 / (1.0 + ratio * ratio);
    }

    /// <summary>修正 Bessel 函數 I1(x),x &lt; 3.75:A&amp;S 9.8.3;x ≥ 3.75:9.8.4。</summary>
    public static double BesselI1(double x)
    {
        if (x < 3.75)
        {
            var t = x / 3.75;
            var t2 = t * t;
            return x * (0.5 + t2 * (0.87890594 + t2 * (0.51498869 + t2 * (0.15084934 + t2 * (0.02658733 + t2 * (0.00301532 + t2 * 0.00032411))))));
        }
        var u = 3.75 / x;
        var p = 0.39894228 + u * (-0.03988024 + u * (-0.00362018 + u * (0.00163801 + u * (-0.01031555 + u * (0.02282967 + u * (-0.02895312 + u * (0.01787654 + u * (-0.00420059))))))));
        return p * Math.Exp(x) / Math.Sqrt(x);
    }

    /// <summary>修正 Bessel 函數 K1(x),0 &lt; x ≤ 2:A&amp;S 9.8.7;x &gt; 2:9.8.8。</summary>
    public static double BesselK1(double x)
    {
        if (x <= 2.0)
        {
            var h = x / 2.0;
            var h2 = h * h;
            return (x * Math.Log(h) * BesselI1(x) + 1.0 + h2 * (0.15443144 + h2 * (-0.67278579 + h2 * (-0.18156897 + h2 * (-0.01919402 + h2 * (-0.00110404 + h2 * (-0.00004686))))))) / x;
        }
        var s = 2.0 / x;
        var p = 1.25331414 + s * (0.23498619 + s * (-0.03655620 + s * (0.01504268 + s * (-0.00780353 + s * (0.00325614 + s * (-0.00068245))))));
        return p * Math.Exp(-x) / Math.Sqrt(x);
    }

    // ------------------------------------------------------------------ 雜湊

    /// <summary>雜湊區塊(只在 Active 時追加):標記、Hs、Tp、來向(弧度)、峰值波長、步末 X、Y、N。</summary>
    public int WriteHash(Span<byte> buf)
    {
        var o = 0;
        void L64(Span<byte> b, long v) { BinaryPrimitives.WriteInt64LittleEndian(b[o..], v); o += 8; }
        void D64(Span<byte> b, double v) => L64(b, BitConverter.DoubleToInt64Bits(v));
        var r = Last;
        L64(buf, HashMarker);
        D64(buf, SignificantHeightM); D64(buf, PeakPeriodS); D64(buf, DirectionRad);
        D64(buf, r.WavelengthM); D64(buf, r.X); D64(buf, r.Y); D64(buf, r.N);
        return o;
    }

    // ------------------------------------------------------------------ 狀態

    /// <summary>state.waves(對外 m、s、度、kN、kN·m)。</summary>
    public WaveDriftState State()
    {
        var r = Last;
        return new WaveDriftState
        {
            SignificantHeightM = SignificantHeightM,
            PeakPeriodS = PeakPeriodS,
            DirectionDeg = DirectionDeg,
            WavelengthM = r.WavelengthM,
            Spectrum = Spectrum,
            XKN = r.X / 1000.0,
            YKN = r.Y / 1000.0,
            NKNm = r.N / 1000.0,
        };
    }
}

/// <summary>
/// 浪狀態(state.waves;情境有 environment.waves 且動力學接受外力時輸出;C# 與 Python 參考實作皆輸出;設計文件 physics-waves.md 第 5 節)。
/// 力為船體座標:X 向艏正、Y 右舷正、N 艏向右正(與 u/v/r、MMG 相同慣例)。
/// </summary>
public sealed record WaveDriftState
{
    /// <summary>有義波高(m)</summary>
    [JsonPropertyName("significantHeight_m")] public required double SignificantHeightM { get; init; }
    /// <summary>譜峰週期(s)</summary>
    [JsonPropertyName("peakPeriod_s")] public required double PeakPeriodS { get; init; }
    /// <summary>浪來向(度,氣象慣例)</summary>
    [JsonPropertyName("direction_deg")] public required double DirectionDeg { get; init; }
    /// <summary>峰值波長(m;自船位置的有效水深,淺水時由有限水深色散關係求得)</summary>
    [JsonPropertyName("wavelength_m")] public required double WavelengthM { get; init; }
    /// <summary>浪譜 pm | jonswap</summary>
    [JsonPropertyName("spectrum")] public string? Spectrum { get; init; }
    /// <summary>平均漂移力 surge(kN,向艏正)</summary>
    [JsonPropertyName("X_kN")] public double? XKN { get; init; }
    /// <summary>平均漂移力 sway(kN,右舷正)</summary>
    [JsonPropertyName("Y_kN")] public double? YKN { get; init; }
    /// <summary>平均漂移力矩 yaw(kN·m,艏向右正)</summary>
    [JsonPropertyName("N_kNm")] public double? NKNm { get; init; }
}
