using System.Buffers.Binary;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics;

/// <summary>
/// 解耦的 1 自由度線性橫搖(規劃書第 6.2 節預留的第 4 自由度;R2「部分:解耦 1 自由度橫搖(不回饋 3-DOF)」;設計文件 docs/design/physics-roll.md):
/// φ'' + 2ζω φ' + ω² φ = ω²·(φ_turn + φ_wind) + 浪激勵。φ 右傾為正(弧度);φ_turn 為迴轉離心傾角 −atan(v_c·r/g)·turnHeelGain
/// (右轉 r &gt; 0 向外 = 左傾,故為負)、φ_wind 為風傾力矩的等效靜傾角(側面積 × 視風速平方 × 力臂 / (Δ g GM))·windHeelGain(風自右舷來 → 向下風 = 左傾);
/// 浪激勵為 ω²·φ_wave(t),φ_wave 為自船位置的有效波斜率(船體橫向;3–5 個浪譜成分的正弦和,相位由情境種子的
/// <see cref="DeterministicRandom"/> 產生一次並進快照;遭遇頻率由船在地固波場中移動自然產生)。線性單自由度對遭遇頻率的響應即 RAO
/// |φ|/α = 1/√((1 − r²)² + (2ζr)²),r = ω_e/ω。
/// 不回饋到 3 自由度運動:相位由獨立的 <see cref="DeterministicRandom"/>(同情境種子)抽取,引擎本身的陣風亂數序列與 3-DOF 運動啟用前後逐位元相同;
/// 未啟用(情境無 ship.roll 或 enabled = false)時不建立模型,state.roll 省略、雜湊不追加區塊,所有既有情境、夾具與黃金測試不變。
/// 固定步長 RK4,與主迴圈同步(每步以步首狀態與步首環境取樣推進);純函數步進(<see cref="Integrate"/>、<see cref="ComputeTurnHeel"/>、
/// <see cref="ComputeWindHeel"/>、<see cref="ComputeWaveSlope"/>),C# 與 Python 參考實作(roll.py)逐項同運算順序。
/// </summary>
public sealed class RollModel
{
    public const long HashMarker = 0x524F4C4C_444F4631; // "ROLLDOF1"
    public const int HashLongs = 5;
    public const double Gravity = 9.80665;
    private const double TwoPi = 2.0 * Math.PI;
    /// <summary>浪激勵的浪譜成分數(ω_i/ω_p 在 [OmegaMinRatio, OmegaMaxRatio] 等分取中點)</summary>
    public const int WaveComponents = 5;
    public const double OmegaMinRatio = 0.6;
    public const double OmegaMaxRatio = 2.0;
    /// <summary>GM 預設(m;particulars loadingConditions.&lt;loading&gt;.gm_m 缺值時;ASSUMED,設計文件第 4 節)</summary>
    public const double DefaultGm = 1.2;
    /// <summary>阻尼比預設(ASSUMED:商船無舭龍骨 0.03–0.05、有舭龍骨 0.06–0.10;兩船皆有舭龍骨)</summary>
    public const double DefaultDampingRatio = 0.08;
    /// <summary>Weiss 公式 T = 2·c·B/√GM 的係數 c(ASSUMED 0.4;IMO 天氣衡準 C ≈ 0.37–0.40)</summary>
    public const double WeissCoefficient = 0.4;
    /// <summary>空氣密度(kg/m³)</summary>
    public const double AirDensity = 1.225;
    /// <summary>風傾用的橫向風阻係數(ASSUMED 0.7;Blendermann 正橫 C_Dt 量級)</summary>
    public const double WindDragCoefficient = 0.7;
    /// <summary>海水密度(kg/m³;排水量缺值時 Δ = Cb·L·B·T·ρ 估計,與 Python particulars._resolve_loading 相同)</summary>
    public const double WaterDensity = 1025.0;

    private readonly double[] _phases;
    private readonly double[] _omega = new double[WaveComponents];
    private readonly double[] _wavenumber = new double[WaveComponents];
    /// <summary>每單位 Hs 的波斜率振幅 k_i·√(2 w_i)/4(rad/m)</summary>
    private readonly double[] _slopePerHs = new double[WaveComponents];
    private double _waveDirRad;
    private bool _hasWaves;

    // ------------------------------------------------------------------ 設定(建構時定案)

    public bool Enabled { get; }
    /// <summary>自然橫搖週期(s):情境值,或 Weiss 公式 T = 2·0.4·B/√GM</summary>
    public double NaturalPeriodS { get; }
    /// <summary>自然角頻率 ω = 2π/T(rad/s)</summary>
    public double Omega { get; }
    /// <summary>阻尼比 ζ</summary>
    public double DampingRatio { get; }
    public double WindHeelGain { get; }
    public double TurnHeelGain { get; }
    public double WaveGain { get; }
    /// <summary>橫穩心高 GM(m;particulars 或預設 <see cref="DefaultGm"/>)</summary>
    public double Gm { get; }
    /// <summary>GM 來自 particulars(false = 採用預設值 ASSUMED)</summary>
    public bool GmFromParticulars { get; }
    /// <summary>排水質量(kg)</summary>
    public double MassKg { get; }
    /// <summary>風壓側面積 A_L(m²;particulars windage.lateralArea_m2 或與 Python particulars.windage_areas 相同的估計)</summary>
    public double LateralAreaM2 { get; }
    /// <summary>風傾力臂 z_w(m):側面積形心在水線上的高度 + T/2(水下側向阻力中心約在半吃水)</summary>
    public double WindLeverM { get; }
    public double B { get; }
    public double T { get; }
    /// <summary>浪激勵相位 ε_i(弧度,[0, 2π);由情境種子抽取一次,進快照)</summary>
    public IReadOnlyList<double> Phases => _phases;
    /// <summary>目前有浪設定(Tp/來向/浪譜;Hs 於每步傳入)</summary>
    public bool HasWaves => _hasWaves;

    // ------------------------------------------------------------------ 狀態(每步推進;進快照與雜湊)

    /// <summary>橫搖角 φ(弧度,右傾正)</summary>
    public double Angle { get; private set; }
    /// <summary>橫搖角速度 φ'(rad/s)</summary>
    public double Rate { get; private set; }
    /// <summary>最近一步的靜傾角 φ_turn + φ_wind(弧度;步首狀態計算)</summary>
    public double StaticHeel { get; private set; }
    public double TurnHeel { get; private set; }
    public double WindHeel { get; private set; }
    /// <summary>最近一步步首的有效波斜率 φ_wave(弧度;無浪為 0)</summary>
    public double WaveSlope { get; private set; }

    public RollModel(ShipParticulars ship, LoadingCondition loading, ScenarioRoll cfg, int seed)
        : this(ship, loading, cfg, DrawPhases(seed)) { }

    /// <summary>以給定相位建立(還原快照、測試用);相位長度須為 <see cref="WaveComponents"/>。</summary>
    public RollModel(ShipParticulars ship, LoadingCondition loading, ScenarioRoll cfg, double[] phases)
    {
        if (phases.Length != WaveComponents) throw new ArgumentException($"相位數須為 {WaveComponents}", nameof(phases));
        _phases = (double[])phases.Clone();
        Enabled = cfg.Enabled;
        var lc = ship.GetLoading(loading);
        var hull = ship.Hull;
        var lpp = hull.LengthBetweenPerpendiculars_m;
        var loa = hull.LengthOverall_m > 0 ? hull.LengthOverall_m : lpp;
        B = hull.Breadth_m;
        // 平均吃水:draftMean_m,缺值時艏艉平均(Python particulars._resolve_loading 相同)
        T = lc.DraftMean_m ?? 0.5 * ((lc.DraftFore_m ?? 0.0) + (lc.DraftAft_m ?? 0.0));
        GmFromParticulars = lc.Gm_m is { } gm && gm > 0.0;
        Gm = GmFromParticulars ? lc.Gm_m!.Value : DefaultGm;
        // 排水量:particulars 值,缺值時 Cb·L·B·T·ρ(Cb 缺值時由滿載 Cb 依吃水比估計;兩者皆與 Python _resolve_loading 同式)
        double dispT;
        if (lc.Displacement_t is { } disp) dispT = disp;
        else
        {
            double cb;
            if (lc.BlockCoefficient is { } cbGiven) cb = cbGiven;
            else
            {
                var full = ship.GetLoading(LoadingCondition.Full);
                var cbFull = full.BlockCoefficient ?? 0.75;
                var tFull = full.DraftMean_m ?? T;
                cb = cbFull - 0.11 * Math.Max(0.0, 1.0 - T / tFull);
            }
            dispT = cb * lpp * B * T * WaterDensity / 1000.0;
        }
        MassKg = dispT * 1000.0;
        // 風壓側面積與形心高度(particulars windage.lateralArea_m2 為 null 時以 LOA×乾舷 + 住艙(0.15 LOA × 11 m)+ 甲板設備(0.5 LOA × 3 m)估計,
        // 與 Python particulars.windage_areas 同式;形心高度以各部分面積加權)
        var freeboard = Math.Max(hull.Depth_m - T, 0.5);
        double? givenAl = null;
        if (ship.Extra is { } extra && extra.TryGetValue("windage", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Object
            && w.TryGetProperty("lateralArea_m2", out var al) && al.ValueKind == System.Text.Json.JsonValueKind.Number)
            givenAl = al.GetDouble();
        double zAbove;
        if (givenAl is { } alGiven && alGiven > 0.0)
        {
            LateralAreaM2 = alGiven;
            zAbove = 0.5 * (alGiven / loa);
        }
        else
        {
            var hullArea = loa * freeboard;
            var accLen = 0.15 * loa;
            var acc = accLen * 11.0;
            var deckLen = 0.5 * loa;
            var deck = deckLen * 3.0;
            LateralAreaM2 = hullArea + acc + deck;
            zAbove = (hullArea * (0.5 * freeboard) + acc * (freeboard + 5.5) + deck * (freeboard + 1.5)) / LateralAreaM2;
        }
        WindLeverM = zAbove + 0.5 * T;
        NaturalPeriodS = cfg.NaturalPeriodS ?? 2.0 * WeissCoefficient * B / Math.Sqrt(Gm);
        Omega = TwoPi / NaturalPeriodS;
        DampingRatio = cfg.DampingRatio ?? DefaultDampingRatio;
        WindHeelGain = cfg.WindHeelGain ?? 1.0;
        TurnHeelGain = cfg.TurnHeelGain ?? 1.0;
        WaveGain = cfg.WaveGain ?? 1.0;
    }

    /// <summary>情境 ship.roll.enabled 時建立(否則 null);相位由情境種子抽取。</summary>
    public static RollModel? Create(ShipParticulars ship, LoadingCondition loading, Scenario.Scenario sc)
        => sc.Ship.Roll is { Enabled: true } cfg ? new RollModel(ship, loading, cfg, sc.Seed) : null;

    /// <summary>浪激勵相位:以情境種子建立獨立的 <see cref="DeterministicRandom"/>,依序抽 <see cref="WaveComponents"/> 個 2π·NextDouble()(與 Python roll.draw_phases 相同序列)。</summary>
    public static double[] DrawPhases(int seed)
    {
        var rng = new DeterministicRandom(seed);
        var phases = new double[WaveComponents];
        for (var i = 0; i < WaveComponents; i++) phases[i] = TwoPi * rng.NextDouble();
        return phases;
    }

    // ------------------------------------------------------------------ 浪設定

    /// <summary>
    /// 設定(或清除)浪激勵的頻率成分:Tp、來向、浪譜與 γ(Hs 於每步傳入,支援漸變);相位不變。
    /// 成分 ω_i = ω_p·ρ_i(ρ_i 在 [0.6, 2.0] 等分取中點),權重為 PM / JONSWAP 譜形正規化(Σw_i = 1,與 WaveDriftModel 同式),
    /// 振幅 a_i = (Hs/4)·√(2 w_i)(Σ a_i²/2 = m0 = Hs²/16),深水波數 k_i = ω_i²/g,波斜率振幅 α_i = k_i·a_i。
    /// </summary>
    public void SetWaves(ScenarioWaves? waves)
    {
        if (waves is null)
        {
            _hasWaves = false;
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
            _slopePerHs[i] = k * Math.Sqrt(2.0 * w) / 4.0;
        }
        _hasWaves = true;
    }

    // ------------------------------------------------------------------ 純函數

    /// <summary>迴轉離心傾角(弧度):−atan(v_c·r/g)·gain,v_c = √(u² + v²) 對水速度、r 艏搖率(右轉正);右轉向外傾 = 左傾(負)。</summary>
    public static double ComputeTurnHeel(double u, double v, double r, double gain)
    {
        var vc = Math.Sqrt(u * u + v * v);
        return -Math.Atan(vc * r / Gravity) * gain;
    }

    /// <summary>
    /// 風傾等效靜傾角(弧度):視風(風向量 − 對地速度)在船體座標的分量 (a_x, a_y),橫向風力 F_y = ½ρ_a·C_Dt·A_L·|a|·a_y(右舷正;風自右舷來時 a_y &lt; 0),
    /// 力矩 F_y·z_w 與復原力矩 Δ g GM 平衡 → φ = atan(F_y·z_w/(Δ g GM))·gain(風自右舷來 → 向下風 = 左傾,負)。
    /// </summary>
    public static double ComputeWindHeel(double windEastMps, double windNorthMps, double velEastMps, double velNorthMps, double psi,
        double lateralAreaM2, double leverM, double massKg, double gm, double gain)
    {
        var (ax, ay) = EnvironmentMath.ApparentWindBody(windEastMps, windNorthMps, velEastMps, velNorthMps, psi);
        var speed = Math.Sqrt(ax * ax + ay * ay);
        var fy = 0.5 * AirDensity * WindDragCoefficient * lateralAreaM2 * speed * ay;
        return Math.Atan(fy * leverM / (massKg * Gravity * gm)) * gain;
    }

    /// <summary>
    /// 自船位置 (x, y)、航向 ψ、時刻 t 的有效波斜率 φ_wave(弧度;船體橫向、右傾正):
    /// 地固波場 η = Σ a_i·sin(ω_i t − k_i s + ε_i),s 為沿浪傳播方向(來向 + 180°)的座標 s = −(x·sin D + y·cos D);
    /// 沿傳播方向的斜率 ∂η/∂s = −Σ α_i cos θ_i,投影到船體橫向(傳播方向與右舷的夾角 cos = −sin χ,χ = 來向 − 航向),
    /// 甲板順著水面傾斜 → φ_wave = −sin χ·Hs·Σ (α_i/Hs)·cos θ_i·waveGain。迎浪/順浪 (sin χ = 0) 無激勵;無浪或 Hs ≤ 0 為 0。
    /// </summary>
    public double WaveSlopeAt(double x, double y, double psi, double hs, double t)
    {
        if (!_hasWaves || !(hs > 0.0)) return 0.0;
        return ComputeWaveSlope(_omega, _wavenumber, _slopePerHs, _phases, _waveDirRad, x, y, psi, hs, t, WaveGain);
    }

    /// <summary>有效波斜率(純函數版;見 <see cref="WaveSlopeAt"/>)。</summary>
    public static double ComputeWaveSlope(double[] omega, double[] wavenumber, double[] slopePerHs, double[] phases, double dirRad,
        double x, double y, double psi, double hs, double t, double gain)
    {
        var chi = Units.WrapRadPi(dirRad - psi);
        var sinChi = Math.Sin(chi);
        var s = -(x * Math.Sin(dirRad) + y * Math.Cos(dirRad));
        var sum = 0.0;
        for (var i = 0; i < omega.Length; i++)
        {
            var theta = omega[i] * t - wavenumber[i] * s + phases[i];
            sum += slopePerHs[i] * Math.Cos(theta);
        }
        return -sinChi * hs * sum * gain;
    }

    /// <summary>
    /// 一步 RK4(純函數):φ'' = ω²·(φ_static + φ_wave(t) − φ) − 2ζω·φ';φ_static 整步固定,φ_wave 於 t、t + dt/2、t + dt 取值
    /// (<paramref name="waveAt"/>;無浪時傳 null 視為 0)。回傳 (φ, φ')。
    /// </summary>
    public static (double Angle, double Rate) Integrate(double angle, double rate, double omega, double zeta, double staticHeel,
        Func<double, double>? waveAt, double t, double dt)
    {
        var w2 = omega * omega;
        var c1 = 2.0 * zeta * omega;
        var target0 = staticHeel + (waveAt?.Invoke(t) ?? 0.0);
        var targetH = staticHeel + (waveAt?.Invoke(t + 0.5 * dt) ?? 0.0);
        var target1 = staticHeel + (waveAt?.Invoke(t + dt) ?? 0.0);
        var k1a = rate;
        var k1r = w2 * (target0 - angle) - c1 * rate;
        var a2 = angle + 0.5 * dt * k1a;
        var r2 = rate + 0.5 * dt * k1r;
        var k2a = r2;
        var k2r = w2 * (targetH - a2) - c1 * r2;
        var a3 = angle + 0.5 * dt * k2a;
        var r3 = rate + 0.5 * dt * k2r;
        var k3a = r3;
        var k3r = w2 * (targetH - a3) - c1 * r3;
        var a4 = angle + dt * k3a;
        var r4 = rate + dt * k3r;
        var k4a = r4;
        var k4r = w2 * (target1 - a4) - c1 * r4;
        return (angle + dt / 6.0 * (k1a + 2.0 * k2a + 2.0 * k3a + k4a), rate + dt / 6.0 * (k1r + 2.0 * k2r + 2.0 * k3r + k4r));
    }

    // ------------------------------------------------------------------ 步進

    /// <summary>
    /// 推進一步(引擎於 3-DOF RK4 之前呼叫,輸入皆為步首值:狀態 <paramref name="s"/>、步首環境取樣 <paramref name="env"/>(含陣風的風、自船位置的流)、
    /// 目前 Hs(漸變中為插值;無浪 0)、步首時刻 t):先算 φ_turn、φ_wind(整步固定)與步首 φ_wave,再 RK4 積分 φ、φ'。
    /// </summary>
    public void Step(in StateVector s, in EnvironmentSample env, double hs, double t, double dt)
    {
        var (velE, velN) = EnvironmentMath.GroundVelocity(s.U, s.V, s.Psi, env.CurrentEastMps, env.CurrentNorthMps);
        TurnHeel = ComputeTurnHeel(s.U, s.V, s.R, TurnHeelGain);
        WindHeel = ComputeWindHeel(env.WindEastMps, env.WindNorthMps, velE, velN, s.Psi, LateralAreaM2, WindLeverM, MassKg, Gm, WindHeelGain);
        StaticHeel = TurnHeel + WindHeel;
        var x = s.X; var y = s.Y; var psi = s.Psi;
        Func<double, double>? waveAt = _hasWaves && hs > 0.0 ? tt => WaveSlopeAt(x, y, psi, hs, tt) : null;
        WaveSlope = waveAt?.Invoke(t) ?? 0.0;
        (Angle, Rate) = Integrate(Angle, Rate, Omega, DampingRatio, StaticHeel, waveAt, t, dt);
    }

    /// <summary>直接設定狀態(測試:自由衰減)。</summary>
    public void SetState(double angle, double rate)
    {
        Angle = angle;
        Rate = rate;
    }

    // ------------------------------------------------------------------ 快照 / 雜湊 / 狀態

    public RollSnapshot CreateSnapshot() => new(Angle, Rate, (double[])_phases.Clone(), StaticHeel, WaveSlope);

    /// <summary>還原快照:φ、φ'、相位(快照相位覆寫種子抽取的值;長度不符時保留種子值)、靜傾與波斜率。</summary>
    public void Restore(RollSnapshot snap)
    {
        Angle = snap.Angle;
        Rate = snap.Rate;
        if (snap.Phases is { Length: WaveComponents } ph) Array.Copy(ph, _phases, WaveComponents);
        StaticHeel = snap.StaticHeel;
        WaveSlope = snap.WaveSlope;
    }

    /// <summary>雜湊區塊(只在啟用時追加):標記 ROLLDOF1、φ、φ'、靜傾、步首波斜率。</summary>
    public int WriteHash(Span<byte> buf)
    {
        var o = 0;
        void L64(Span<byte> b, long v) { BinaryPrimitives.WriteInt64LittleEndian(b[o..], v); o += 8; }
        void D64(Span<byte> b, double v) => L64(b, BitConverter.DoubleToInt64Bits(v));
        L64(buf, HashMarker);
        D64(buf, Angle); D64(buf, Rate); D64(buf, StaticHeel); D64(buf, WaveSlope);
        return o;
    }

    /// <summary>state.roll(度、度/秒)。</summary>
    public RollState State() => new()
    {
        AngleDeg = Units.RadToDeg(Angle),
        RateDegps = Units.RadToDeg(Rate),
        NaturalPeriodS = NaturalPeriodS,
        StaticHeelDeg = Units.RadToDeg(StaticHeel),
    };
}

/// <summary>橫搖快照(只在情境啟用 ship.roll 時寫入 <see cref="EngineSnapshot"/>;φ、φ'(弧度)、浪激勵相位、靜傾與步首波斜率)。</summary>
public sealed record RollSnapshot(double Angle, double Rate, double[] Phases, double StaticHeel = 0.0, double WaveSlope = 0.0);
