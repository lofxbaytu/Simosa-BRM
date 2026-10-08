using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics.Mmg;

/// <summary>淺水修正後的倍率與附加質量(由該步的水深算出;深水時全部為 1 / 基準值)。</summary>
public readonly record struct MmgShallowFactors(
    double LinearSway, double LinearYaw, double Nonlinear, double Resistance,
    double Mx, double My, double Jzz)
{
    public static MmgShallowFactors Deep(double mx, double my, double jzz) => new(1.0, 1.0, 1.0, 1.0, mx, my, jzz);
}

/// <summary>力的分解(除錯、報告與數值一致性測試用;與 Python ForceBreakdown 對應)。</summary>
public readonly record struct MmgForces(
    double XH, double YH, double NH,
    double XP, double YP, double NP,
    double XR, double YR, double NR,
    double XT, double YT, double NT,
    double XW, double YW, double NW,
    double Thrust, double Torque, double J, double Kt, double AlphaR, double UR)
{
    public double X => XH + XP + XR + XT + XW;
    public double Y => YH + YP + YR + YT + YW;
    public double N => NH + NP + NR + NT + NW;
}

/// <summary>
/// 3 自由度 MMG 船舶運動模型(規劃書第 6.2 節;Yasukawa &amp; Yoshimura 2015 式 (1) 至 (3)),
/// 逐式移植自 Python 參考實作 src/Tools.Calibration/simosa_brm/mmg.py 的 <c>forces</c> / <c>_derivs</c>:
/// <list type="bullet">
/// <item>船體:線性/非線性多項式(X、Y 以 ½ρLdU²、N 以 ½ρL²dU² 無因次化,v' = v/U、r' = rL/U),含 x_G 耦合的 2×2 聯立求解;
/// 大漂角(船舯漂角 |β|)與艏搖通道(atan(½L|r|/|u|))以 smoothstep 權重混入截面橫流阻力(10 條帶),線性升力項隨 |cos β| 淡出;
/// 阻力永遠與縱向速度反向(倒退時向前推),橫流區改用 u|u|。</item>
/// <item>螺槳:伴流 w_P = w_P0·exp(−C·β_P²),Wageningen B 系列二次 K_T/K_Q(<see cref="MmgPropeller"/>),J &gt; jMax 時截止(風車區);
/// 倒車時的橫向力(右旋槳艉向左)。</item>
/// <item>舵:MMG 標準舵模型(u_R 含滑流加速 κ、η 面積加權;流向由面積加權平均流速決定,倒退中正車 u_R 仍為正)、γ_R±、l_R、
/// Schilling 分段升力、滑流旋轉造成的有效舵角偏移 swirlAngle(隨滑流加速因子,上限 5 倍)、t_R、a_H、x_H。</item>
/// <item>艏側推:推力隨 |u| 以 exp(−ln2·(u/u_half)²) 衰減。</item>
/// <item>風:Blendermann (1994) 參數式,相對風由對地速度算(受風面積估計與 Python 相同,由係數檔讀入)。</item>
/// <item>均勻流:u、v 為對水速度,流速只進入運動學(<see cref="IShipDynamics.Kinematics"/>)與相對風。</item>
/// <item>淺水:Kijima 型倍率 f = 1 + a·(T/(h−T))^n 作用於線性/非線性導數、阻力與附加質量(h/T ≥ 11 無修正);squat 用 ICORELS。</item>
/// </list>
/// 模型為純函數:所有跨步狀態(主機換向狀態機、舵機、側推延遲)在 <see cref="ActuatorModel"/>(引擎狀態、快照與雜湊)。
/// 係數檔 <see cref="MmgCoefficients"/>;致動器參數由 <see cref="ActuatorParameters"/> 提供給引擎。
/// </summary>
public sealed class MmgDynamics : IShipDynamics
{
    public const string Name = "mmg/1";
    public string ModelName => Name;

    private const double Ln2 = 0.6931471805599453;
    private const double Gravity = 9.80665;
    private const double NuWater = 1.19e-6; // 海水運動黏度(15 °C),與 Python coefficients.NU_WATER 相同

    public MmgCoefficients Coefficients { get; }
    public ShipParticulars Ship { get; }
    public LoadingCondition LoadingCondition { get; }
    public MmgPropeller Propeller { get; }
    public ActuatorParameters ActuatorParameters { get; }

    // ---- 參考量與質量(因次化) ----
    public double L { get; }
    public double Draft { get; }
    public double Rho { get; }
    public double RhoAir { get; }
    public double Mass { get; }
    public double XG { get; }
    public double M { get; }
    public double Mx0 { get; }
    public double My0 { get; }
    public double Izz { get; }
    public double Jzz0 { get; }
    public double Loa { get; }
    /// <summary>排水體積 ∇ = m/ρ</summary>
    public double Volume { get; }

    private readonly HullCoefficients _h;
    private readonly ResistanceCoefficients _res;
    private readonly double _cfCd, _cfA, _cfB, _cfYa, _cfYb, _uFloor;
    private readonly double _jMax, _wP0, _tP, _xP, _wakeC;
    private readonly bool _rightHanded;
    private readonly double _aR, _fAlpha, _schLin, _schMax, _schSlope, _deltaNeutral, _swirlAngle;
    private readonly bool _schEnabled;
    private readonly double _tR, _aH, _xH, _xR, _gRMinus, _gRPlus, _lR, _eps, _kappa, _eta;
    private readonly double _thrX, _thrT, _thrUHalf;
    private readonly double _wAL, _wAT, _wSL, _wCDt, _wCDlH, _wCDlT, _wDelta;
    private readonly ShallowWaterCoefficients _sw;
    private readonly double _squatCs;
    private readonly double _nMax;

    public MmgDynamics(ShipParticulars ship, LoadingCondition loading, MmgCoefficients c)
    {
        Ship = ship;
        LoadingCondition = loading;
        Coefficients = c;
        var rf = c.Reference;
        L = rf.Length_m;
        Draft = rf.Draft_m;
        Rho = rf.Density_kgm3;
        RhoAir = rf.AirDensity_kgm3;
        Mass = rf.Mass_kg;
        XG = rf.XG_m;
        var ndM = 0.5 * Rho * L * L * Draft;
        var ndI = 0.5 * Rho * Math.Pow(L, 4) * Draft;
        M = c.Mass.M * ndM;
        Mx0 = c.Mass.Mx * ndM;
        My0 = c.Mass.My * ndM;
        Izz = c.Mass.Izz * ndI;
        Jzz0 = c.Mass.Jzz * ndI;
        _h = c.Hull;
        _res = c.Hull.Resistance;
        var cf = c.Hull.CrossFlow;
        _cfCd = cf.Cd;
        _cfA = Units.DegToRad(cf.BlendStart_deg);
        _cfB = Units.DegToRad(cf.BlendEnd_deg);
        _cfYa = Units.DegToRad(cf.YawBlendStart_deg);
        _cfYb = Units.DegToRad(cf.YawBlendEnd_deg);
        _uFloor = cf.UFloor_mps;
        var p = c.Propeller;
        Propeller = new MmgPropeller(p);
        _jMax = p.JMax;
        _wP0 = p.WP0;
        _tP = p.TP;
        _xP = p.XP * L;
        _wakeC = p.WakeDriftFactor;
        _rightHanded = (p.Rotation ?? "right").StartsWith("r", StringComparison.OrdinalIgnoreCase);
        var rd = c.Rudder;
        _aR = rd.Area_m2;
        _fAlpha = rd.FAlpha;
        _schEnabled = rd.Schilling.Enabled;
        _schLin = Units.DegToRad(rd.Schilling.LinearLimit_deg);
        _schMax = Units.DegToRad(rd.Schilling.MaxAngle_deg);
        _schSlope = rd.Schilling.HighLiftSlopeFactor;
        _deltaNeutral = Units.DegToRad(rd.NeutralAngle_deg);
        _swirlAngle = Units.DegToRad(rd.SwirlAngle_deg);
        _tR = rd.TR;
        _aH = rd.AH;
        _xH = rd.XH * L;
        _xR = rd.XR * L;
        _gRMinus = rd.GammaRMinus;
        _gRPlus = rd.GammaRPlus;
        _lR = rd.LR * L;
        _eps = rd.Epsilon;
        _kappa = rd.Kappa;
        _eta = rd.Eta;
        var th = c.Thruster;
        _thrX = th.X_m;
        _thrT = th.Installed ? th.NominalThrust_kN * 1e3 * th.Effectiveness : 0.0;
        _thrUHalf = Units.KnToMps(th.HalfThrustSpeed_kn);
        var w = c.Wind;
        _wAL = w.LateralArea_m2;
        _wAT = w.FrontalArea_m2;
        _wSL = w.LateralCentroid_m;
        _wCDt = w.CDt;
        _wCDlH = w.CDlHead;
        _wCDlT = w.CDlTail;
        _wDelta = w.Delta;
        Loa = ship.Hull.LengthOverall_m > 0 ? ship.Hull.LengthOverall_m : L;
        _sw = c.ShallowWater;
        _squatCs = c.Squat.Cs;
        Volume = Mass / Rho;
        _nMax = c.Engine.MaxRpm / 60.0;
        ActuatorParameters = ActuatorParameters.FromCoefficients(c, ship);
    }

    /// <summary>由專案根目錄讀取係數檔建立模型(Host 工廠用)。</summary>
    public static MmgDynamics Load(string repoRoot, ShipParticulars ship, LoadingCondition loading)
        => new(ship, loading, MmgCoefficients.LoadFromDataRoot(repoRoot, ship.Id, loading));

    // ------------------------------------------------------------------ 淺水

    private double ShallowFactor(ShallowWaterGroup g, double x) => 1.0 + g.A * Math.Pow(x, g.N);

    /// <summary>該水深的淺水倍率;水深 ≤ 0、NaN 或無窮大視為深水。h/T ≥ 11 時無修正(與 Python _shallow_factor 相同)。</summary>
    public MmgShallowFactors ShallowFactors(double waterDepth)
    {
        if (!(waterDepth > 0.0) || double.IsInfinity(waterDepth)) return MmgShallowFactors.Deep(Mx0, My0, Jzz0);
        var ratio = waterDepth / Draft;
        if (ratio < _sw.MinDepthRatio) ratio = _sw.MinDepthRatio;
        var x = Math.Max(0.0, 1.0 / (ratio - 1.0) - 0.1);
        if (x <= 0.0) return MmgShallowFactors.Deep(Mx0, My0, Jzz0);
        return new MmgShallowFactors(
            ShallowFactor(_sw.LinearSway, x), ShallowFactor(_sw.LinearYaw, x), ShallowFactor(_sw.Nonlinear, x), ShallowFactor(_sw.Resistance, x),
            Mx0 * ShallowFactor(_sw.AddedMassSurge, x), My0 * ShallowFactor(_sw.AddedMassSway, x), Jzz0 * ShallowFactor(_sw.AddedInertia, x));
    }

    /// <summary>ICORELS squat(m):S = Cs·∇/L²·Fnh²/√(1−Fnh²),Fnh 上限 0.95;深水為 0。</summary>
    public double Squat(in StateVector s, double waterDepth)
    {
        if (!(waterDepth > 0.0) || double.IsInfinity(waterDepth)) return 0.0;
        var u = Math.Sqrt(s.U * s.U + s.V * s.V);
        var fnh = u / Math.Sqrt(Gravity * waterDepth);
        if (fnh >= 0.95) fnh = 0.95;
        return _squatCs * Volume / (L * L) * fnh * fnh / Math.Sqrt(1.0 - fnh * fnh);
    }

    double? IShipDynamics.Squat(in StateVector s, double waterDepth) => Squat(s, waterDepth);
    ActuatorParameters? IShipDynamics.ActuatorParameters => ActuatorParameters;

    // ------------------------------------------------------------------ 阻力

    private static double Smoothstep(double x, double a, double b)
    {
        if (x <= a) return 0.0;
        if (x >= b) return 1.0;
        var t = (x - a) / (b - a);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>包到 (−π, π](與 Python wrap_pi 相同:fmod 後平移)。</summary>
    private static double WrapPi(double a)
    {
        a = (a + Math.PI) % Units.TwoPi;
        if (a < 0) a += Units.TwoPi;
        return a - Math.PI;
    }

    /// <summary>總阻力係數 R0'(U)(Python coefficients.r0_prime):黏性 ITTC-57·形狀因子·S/(L·d) + 興波 c_w·(Fn/Fn_ref)^q,含低速下限。</summary>
    public double R0Prime(double speed)
    {
        if (string.Equals(_res.Model, "constant", StringComparison.OrdinalIgnoreCase))
            return (_res.R0 ?? 0.0) * (_res.Scale ?? 1.0);
        var v = _res.Viscous ?? throw new InvalidDataException("hull.resistance.viscous 缺少");
        var w = _res.Wave ?? throw new InvalidDataException("hull.resistance.wave 缺少");
        var u = Math.Abs(speed);
        var re = Math.Max(u * L / NuWater, 1.0e6);
        var cfr = 0.075 / Math.Pow(Math.Log10(re) - 2.0, 2);
        var fn = u / Math.Sqrt(Gravity * L);
        var r0 = cfr * v.FormFactor * v.WettedSurface_m2 / (L * Draft) + w.Coefficient * Math.Pow(fn / w.FroudeRef, w.FroudeExponent);
        if (_res.LowSpeedFloor is { } fl && u < fl.Speed_mps && r0 < fl.R0) return fl.R0;
        return r0;
    }

    // ------------------------------------------------------------------ 力

    /// <summary>
    /// 船體座標的合力與分解(Python <c>MMGShip.forces</c>)。
    /// <paramref name="n"/> 為軸轉速 rps(倒車負);<paramref name="thr"/> 側推實際推力比例;<paramref name="psi"/> 航向;
    /// <paramref name="ugE"/>/<paramref name="ugN"/> 對地速度(相對風用);<paramref name="windE"/>/<paramref name="windN"/> 真風向量(ENU,m/s);
    /// <paramref name="f"/> 由 <see cref="ShallowFactors"/> 取得。
    /// </summary>
    public MmgForces Forces(double u, double v, double r, double delta, double n, double thr, double psi,
        double ugE, double ugN, double windE, double windN, in MmgShallowFactors f)
    {
        var L_ = L;
        var d = Draft;
        var rho = Rho;
        var h = _h;
        var U2 = u * u + v * v;
        var U = Math.Sqrt(U2);
        var Ue = U > _uFloor ? U : _uFloor;
        var vp = v / Ue;
        var rp = r * L_ / Ue;
        var beta = U > 1e-9 ? Math.Atan2(-v, u) : 0.0;

        // ---- 船體(式 (6)–(8)) ----
        var q = 0.5 * rho * L_ * d * U2;
        var r0 = R0Prime(U) * f.Resistance;
        var vp2 = vp * vp;
        var rp2 = rp * rp;
        var Xnl = q * (h.Xvv * vp2 + h.Xvr * vp * rp + h.Xrr * rp2 + h.Xvvvv * vp2 * vp2);
        var YHlin = q * f.LinearSway * (h.Yv * vp + h.Yr * rp);
        var NHlin = q * L_ * f.LinearYaw * (h.Nv * vp + h.Nr * rp);
        var YHnl = q * f.Nonlinear * (h.Yvvv * vp2 * vp + h.Yvvr * vp2 * rp + h.Yvrr * vp * rp2 + h.Yrrr * rp2 * rp);
        var NHnl = q * L_ * f.Nonlinear * (h.Nvvv * vp2 * vp + h.Nvvr * vp2 * rp + h.Nvrr * vp * rp2 + h.Nrrr * rp2 * rp);
        // 大漂角 / 低速:多項式只在 |β| 約 20–30°、r' 約 1 內有效,超出時與截面橫流阻力混合。
        // 權重取兩個通道的最大值:(1) 船舯漂角 |β|;(2) 艏搖在船艏/艉造成的局部流向角 atan(½L|r|/|u|)(U→0 時仍有艏搖阻尼)。
        var ab = Math.Abs(beta);
        if (ab > Math.PI / 2) ab = Math.PI - ab;
        var aYaw = Math.Atan2(0.5 * L_ * Math.Abs(r), Math.Abs(u));
        var wcf = Math.Max(Smoothstep(ab, _cfA, _cfB), Smoothstep(aYaw, _cfYa, _cfYb));
        // 阻力永遠與縱向速度反向(倒退時向前推):正常區 −R0'·½ρLdU²,橫流區改用 −R0'·½ρLd·u|u|(純橫移時縱向阻力為零)
        var su = u > 0.0 ? 1.0 : (u < 0.0 ? -1.0 : 0.0);
        var Xres = -0.5 * rho * L_ * d * r0 * ((1.0 - wcf) * U2 * su + wcf * u * Math.Abs(u));
        double XH, YH, NH;
        if (wcf > 0.0)
        {
            // 線性項為升力型(Munk 力矩 ∝ sin 2β),在橫流區隨 |cos β| 消失;多項式 X 項同樣淡出
            var fLin = 1.0 - wcf * (1.0 - Math.Abs(Math.Cos(beta)));
            var Ycf = 0.0;
            var Ncf = 0.0;
            const int nStrip = 10;
            var dx = L_ / nStrip;
            var coef = 0.5 * rho * d * _cfCd * f.Nonlinear * dx;
            for (var i = 0; i < nStrip; i++)
            {
                var xs = -0.5 * L_ + (i + 0.5) * dx;
                var vl = v + xs * r;
                var fs = -coef * vl * Math.Abs(vl);
                Ycf += fs;
                Ncf += xs * fs;
            }
            XH = Xres + (1.0 - wcf) * Xnl;
            YH = fLin * YHlin + (1.0 - wcf) * YHnl + wcf * Ycf;
            NH = fLin * NHlin + (1.0 - wcf) * NHnl + wcf * Ncf;
        }
        else
        {
            XH = Xres + Xnl;
            YH = YHlin + YHnl;
            NH = NHlin + NHnl;
        }

        // ---- 螺槳 ----
        var betaP = beta - _xP / L_ * rp;
        if (betaP > 1.5) betaP = 1.5;
        else if (betaP < -1.5) betaP = -1.5;
        var wP = _wP0 * Math.Exp(-_wakeC * betaP * betaP);
        var va = u * (1.0 - wP);
        var (T, Q, J, KT) = Propeller.ThrustTorque(n, va, rho);
        if (n > 0 && J > _jMax)
        {
            // 風車區:多項式截止在 jMax(有效 J 保留原值供報告)
            var (Tc, Qc, _, KTc) = Propeller.ThrustTorque(n, _jMax * n * Propeller.Diameter, rho);
            (T, Q, KT) = (Tc, Qc, KTc);
        }
        var XP = (1.0 - _tP) * T;
        var YP = 0.0;
        var NP = 0.0;
        if (n < 0.0 && T < 0.0)
        {
            // 倒車橫向力:右旋槳艉向左(bow to starboard)
            YP = _rightHanded ? -Propeller.SideForceFactor * Math.Abs(T) : Propeller.SideForceFactor * Math.Abs(T);
            NP = _xP * YP;
        }

        // ---- 舵 ----
        var swirl = 0.0;
        double uR;
        if (n > 0.0 && KT > 0.0)
        {
            var dp = Propeller.Diameter;
            var slip = Math.Sqrt(va * va + 8.0 * KT * n * n * dp * dp / Math.PI);
            var urCore = va + _kappa * (slip - va);
            uR = _eps * Math.Sqrt(_eta * urCore * urCore + (1.0 - _eta) * va * va);
            // 流向由舵面積加權的平均流速決定:倒退中正車時滑流仍由前向後流過舵,u_R > 0
            if (_eta * urCore + (1.0 - _eta) * va < 0.0) uR = -uR;
            // 滑流旋轉造成的有效舵角偏移:隨螺槳負荷(滑流加速因子)增大,上限 5 倍
            if (va > 1e-6 && _swirlAngle != 0.0)
            {
                var sfac = slip / va - 1.0;
                swirl = _swirlAngle * (sfac < 5.0 ? sfac : 5.0);
            }
        }
        else
        {
            uR = _eps * va;
        }
        var betaR = beta - _lR / L_ * rp;
        var gR = betaR > 0 ? _gRPlus : _gRMinus;
        var vR = U * gR * betaR;
        var UR2 = uR * uR + vR * vR;
        var alpha = WrapPi((delta - _deltaNeutral - swirl) - Math.Atan2(vR, uR));
        var a = Math.Abs(alpha);
        double cn;
        if (a <= _schLin) cn = _fAlpha * Math.Sin(a);
        else if (_schEnabled && a <= _schMax) cn = _fAlpha * (Math.Sin(_schLin) + _schSlope * (a - _schLin));
        else if (_schEnabled) cn = _fAlpha * (Math.Sin(_schLin) + _schSlope * (_schMax - _schLin));
        else cn = a <= Math.PI / 2 ? _fAlpha * Math.Sin(a) : _fAlpha * Math.Sin(Math.PI - a); // 一般舵:失速簡化
        var FN = 0.5 * rho * _aR * UR2 * Math.CopySign(cn, alpha);
        var cd = Math.Cos(delta);
        var sd = Math.Sin(delta);
        var XR = -(1.0 - _tR) * FN * sd;
        var YR = -(1.0 + _aH) * FN * cd;
        var NR = -(_xR + _aH * _xH) * FN * cd;

        // ---- 艏側推 ----
        double XT = 0.0, YT = 0.0, NT = 0.0;
        if (thr != 0.0 && _thrT > 0.0)
        {
            var uu = Math.Abs(u) / _thrUHalf;
            var fade = Math.Exp(-Ln2 * uu * uu);
            YT = thr * _thrT * fade;
            NT = _thrX * YT;
        }

        // ---- 風(Blendermann 1994) ----
        double XW = 0.0, YW = 0.0, NW = 0.0;
        if (windE * windE + windN * windN > 0.0)
        {
            var we = windE - ugE;
            var wn = windN - ugN;
            var sp = Math.Sin(psi);
            var cp = Math.Cos(psi);
            var uRw = we * sp + wn * cp;
            var vRw = we * cp - wn * sp;
            var Vrw2 = uRw * uRw + vRw * vRw;
            if (Vrw2 > 1e-6)
            {
                var epsw = Math.Atan2(-vRw, -uRw); // 0 頂風;正 = 風自右舷
                var ae = Math.Abs(epsw);
                var cdl = ae <= Math.PI / 2 ? _wCDlH : _wCDlT; // C_Dl,AF(以正面積為基準)
                var s2 = Math.Sin(2.0 * ae);
                // 分母中的 C_Dl 以側面積 A_L 為基準:C_Dl = C_Dl,AF·A_F/A_L(MSS blendermann94 同)
                var cdlAl = _wAL > 0.0 ? cdl * _wAT / _wAL : cdl;
                var den = 1.0 - 0.5 * _wDelta * (1.0 - cdlAl / _wCDt) * s2 * s2;
                var qa = 0.5 * RhoAir * Vrw2;
                XW = -qa * _wAT * cdl * Math.Cos(ae) / den;
                var cy = _wCDt * Math.Sin(ae) / den;
                var cnw = (_wSL / Loa - 0.18 * (ae - Math.PI / 2)) * cy;
                var sgn = epsw >= 0 ? 1.0 : -1.0;
                YW = -sgn * qa * _wAL * cy;
                NW = -sgn * qa * _wAL * Loa * cnw;
            }
        }

        return new MmgForces(XH, YH, NH, XP, YP, NP, XR, YR, NR, XT, YT, NT, XW, YW, NW, T, Q, J, KT, alpha, uR);
    }

    // ------------------------------------------------------------------ 導數

    public StateVector Derivative(double t, in StateVector s, in EnvironmentSample env, in ControlInput c)
    {
        var f = ShallowFactors(env.WaterDepthM);
        var (ugE, ugN) = EnvironmentMath.GroundVelocity(s.U, s.V, s.Psi, env.CurrentEastMps, env.CurrentNorthMps);
        var forces = Forces(s.U, s.V, s.R, c.RudderRad, c.Rpm / 60.0, c.Thruster, s.Psi, ugE, ugN, env.WindEastMps, env.WindNorthMps, f);
        var (du, dv, dr) = Accelerations(s.U, s.V, s.R, forces.X, forces.Y, forces.N, f);
        var (dx, dy, dpsi) = IShipDynamics.Kinematics(s, env);
        return new StateVector(du, dv, dr, dx, dy, dpsi);
    }

    /// <summary>
    /// 運動方程式(Yasukawa &amp; Yoshimura 2015 式 (1)–(3),含 x_G 耦合):
    /// (m+m_x)u̇ − (m+m_y)v r − x_G m r² = X;(m+m_y)v̇ + (m+m_x)u r + x_G m ṙ = Y;(I_zz + x_G² m + J_zz)ṙ + x_G m(v̇ + u r) = N。
    /// </summary>
    public (double Du, double Dv, double Dr) Accelerations(double u, double v, double r, double X, double Y, double N, in MmgShallowFactors f)
    {
        var m = M;
        var mx = f.Mx;
        var my = f.My;
        var xg = XG;
        var du = (X + (m + my) * v * r + xg * m * r * r) / (m + mx);
        var a11 = m + my;
        var a12 = xg * m;
        var a22 = Izz + f.Jzz + xg * xg * m;
        var b1 = Y - (m + mx) * u * r;
        var b2 = N - xg * m * u * r;
        var det = a11 * a22 - a12 * a12;
        var dv = (b1 * a22 - a12 * b2) / det;
        var dr = (a11 * b2 - a12 * b1) / det;
        return (du, dv, dr);
    }

    // ------------------------------------------------------------------ 穩態輔助

    /// <summary>直航穩態:v = r = 0、舵在中立角、無風時 X_H + X_P 的合力(N)。</summary>
    public double SurgeBalance(double u, double nRps, double waterDepth = double.PositiveInfinity)
    {
        var f = ShallowFactors(waterDepth);
        return Forces(u, 0.0, 0.0, _deltaNeutral, nRps, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, f).X;
    }

    /// <summary>給定轉速的直航穩態速度(m/s),二分法(與 Python steady_speed_for_rpm 相同)。</summary>
    public double SteadySpeedForRpm(double rpm, double waterDepth = double.PositiveInfinity)
    {
        var n = rpm / 60.0;
        if (n <= 0) return 0.0;
        double lo = 0.0, hi = 20.0;
        for (var i = 0; i < 60; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (SurgeBalance(mid, n, waterDepth) > 0) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>給定直航速度所需轉速(rpm),二分法(可超過 maxRpm,供初始化用)。</summary>
    public double SteadyRpmForSpeed(double u, double waterDepth = double.PositiveInfinity)
    {
        if (u <= 0) return 0.0;
        double lo = 0.0, hi = 3.0 * _nMax;
        for (var i = 0; i < 60; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (SurgeBalance(u, mid, waterDepth) < 0) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi) * 60.0;
    }

    double? IShipDynamics.SteadyRpmForSpeed(double speedMps, double waterDepth) => SteadyRpmForSpeed(speedMps, waterDepth);
}
