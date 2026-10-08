using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics;

/// <summary>
/// 暫代動力學模型,讓整條管線(引擎、紀錄、NMEA、儀器)先能運作;MMG 完整模型(規劃書第 6.2 節)
/// 之後依 Python 參考實作移植並以同一介面替換。內容:
/// <list type="bullet">
/// <item>艏搖:一階 Nomoto(T ṙ + r = K δ),K = K'·U/L、T = T'·L/U,T 限制在 [TMin, TMax];倒車時舵效減半且反向。</item>
/// <item>縱向:u 一階滯後到「轉速對應航速」(由 particulars 車鐘表內插;倒車以前進曲線的 60 % 估計),轉舵時目標航速依舵角比例降低。</item>
/// <item>橫向:v 一階滯後到 −c·r·L(迴轉時船舯向外漂)加視風造成的 2 % 風壓漂移。</item>
/// <item>艏側推:在低速產生迴轉率 ROT0·order·1/(1+(U/U_half)²),超過 notEffectiveAbove_kn 無效。</item>
/// <item>淺水:h/T &lt; 1.5 時 T 增大(反應變慢)。</item>
/// </list>
/// 不具備的行為:迴旋時的速度–迴轉率耦合、單俥倒車艉偏、風力矩、浪、岸壁效應(均屬 MMG 移植範圍)。
/// </summary>
public sealed class PlaceholderDynamics : IShipDynamics
{
    public string ModelName => "placeholder-nomoto/1";

    private readonly double _lpp;
    private readonly double _draft;
    private readonly IReadOnlyList<(double Rpm, double SpeedMps)> _speedCurve;
    private readonly double _thrusterRot0RadPerSec;
    private readonly double _thrusterCutoffMps;
    private readonly double _rudderMaxRad;

    /// <summary>無因次 Nomoto 增益 K'(由 FSB1 35° 迴旋圈戰術直徑約 2.8 L 反推)。</summary>
    public double KPrime { get; init; } = 1.3;
    /// <summary>無因次 Nomoto 時間常數 T'。</summary>
    public double TPrime { get; init; } = 2.0;
    public double TMinSec { get; init; } = 5.0;
    public double TMaxSec { get; init; } = 60.0;
    /// <summary>縱向速度時間常數(秒)。</summary>
    public double SpeedTimeConstantSec { get; init; } = 120.0;
    /// <summary>滿舵時的目標航速損失比例(FSB1 試俥:14.5 kn 進入、定常迴轉 4.6–4.9 kn)。</summary>
    public double TurnSpeedLossAtMaxRudder { get; init; } = 0.65;
    /// <summary>迴轉漂移係數 c:v_target = −c·r·L。</summary>
    public double SwayDriftCoefficient { get; init; } = 0.38;
    /// <summary>風壓漂移係數(橫向速度 = 係數 × 視風橫向分量)。</summary>
    public double LeewayCoefficient { get; init; } = 0.02;
    /// <summary>倒車時以前進 rpm–航速曲線乘此係數估計倒車航速。</summary>
    public double AsternSpeedFactor { get; init; } = 0.6;
    /// <summary>側推效能減半的前進速度(m/s)。</summary>
    public double ThrusterHalfSpeedMps { get; init; } = Units.KnToMps(2.5);

    public PlaceholderDynamics(ShipParticulars ship, LoadingCondition loading)
    {
        _lpp = ship.Hull.LengthBetweenPerpendiculars_m;
        _draft = ship.GetLoading(loading).DraftMean_m ?? ship.GetLoading(loading).MaxDraft_m;
        if (_draft <= 0) _draft = ship.Hull.Depth_m * 0.6;
        _speedCurve = ship.AheadSpeedCurve(loading).Select(p => (p.Rpm, Units.KnToMps(p.SpeedKn))).ToList();
        _rudderMaxRad = Units.DegToRad(ship.Rudder.NormalMaxAngleDeg);
        var bt = ship.BowThruster;
        _thrusterRot0RadPerSec = bt is null ? 0.0 : Units.DegPerMinToRadPerSec(bt.TurningRateAtZeroSpeed_degPerMin ?? 20.0);
        _thrusterCutoffMps = bt?.NotEffectiveAbove_kn is { } cut ? Units.KnToMps(cut) : double.PositiveInfinity;
    }

    /// <summary>轉速 → 穩態對水航速(m/s),由車鐘表線性內插;超出表尾依最後一段外插。</summary>
    public double SteadySpeedForRpm(double rpm)
    {
        var mag = Math.Abs(rpm);
        var sign = rpm < 0 ? -AsternSpeedFactor : 1.0;
        if (_speedCurve.Count < 2) return sign * mag * 0.05;
        for (var i = 1; i < _speedCurve.Count; i++)
        {
            var (r0, s0) = _speedCurve[i - 1];
            var (r1, s1) = _speedCurve[i];
            if (mag <= r1 || i == _speedCurve.Count - 1)
            {
                var f = r1 > r0 ? (mag - r0) / (r1 - r0) : 0.0;
                return sign * (s0 + f * (s1 - s0));
            }
        }
        return 0.0;
    }

    public StateVector Derivative(double t, in StateVector s, in EnvironmentSample env, in ControlInput c)
    {
        var uAbs = Math.Max(Math.Abs(s.U), 0.3);

        // --- 艏搖(Nomoto 一階) ---
        var k = KPrime * uAbs / _lpp;
        var tau = Units.Clamp(TPrime * _lpp / uAbs, TMinSec, TMaxSec);
        // 淺水:h/T < 1.5 時反應變慢
        var hOverT = env.WaterDepthM > 0 ? env.WaterDepthM / _draft : double.PositiveInfinity;
        if (hOverT < 1.5) tau *= 1.0 + 0.5 * (1.5 - Math.Max(hOverT, 1.0));
        var rudderEffect = s.U >= 0 ? 1.0 : -0.5;
        var rRudder = k * c.RudderRad * rudderEffect;
        // 側推:低速有效
        var thrEff = 1.0 / (1.0 + Math.Pow(Math.Abs(s.U) / ThrusterHalfSpeedMps, 2));
        if (Math.Abs(s.U) > _thrusterCutoffMps) thrEff = 0.0;
        var rThruster = _thrusterRot0RadPerSec * c.Thruster * thrEff;
        var dr = (rRudder + rThruster - s.R) / tau;

        // --- 縱向 ---
        var uSteady = SteadySpeedForRpm(c.Rpm);
        var loss = TurnSpeedLossAtMaxRudder * Math.Min(1.0, Math.Abs(c.RudderRad) / _rudderMaxRad);
        var uTarget = uSteady * (1.0 - loss);
        var du = (uTarget - s.U) / SpeedTimeConstantSec;

        // --- 橫向 ---
        var (velE, velN) = EnvironmentMath.GroundVelocity(s.U, s.V, s.Psi, env.CurrentEastMps, env.CurrentNorthMps);
        var (_, ay) = EnvironmentMath.ApparentWindBody(env.WindEastMps, env.WindNorthMps, velE, velN, s.Psi);
        var vTarget = -SwayDriftCoefficient * s.R * _lpp + LeewayCoefficient * ay;
        var dv = (vTarget - s.V) / tau;

        var (dx, dy, dpsi) = IShipDynamics.Kinematics(s, env);
        return new StateVector(du, dv, dr, dx, dy, dpsi);
    }
}
