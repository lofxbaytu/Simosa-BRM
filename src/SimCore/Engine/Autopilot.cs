using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Engine;

/// <summary>
/// 簡易航向自動舵(規劃書第 6.2 節:PID + Nomoto;此處為串級 P 控制:航向誤差 → 目標 ROT(限幅)→ 舵令),
/// 供情境與儀器測試;正式版待 MMG 校正後以 K、T 整定並加入天候調整與航向偏差警報。
/// </summary>
public sealed class Autopilot
{
    public bool Enabled { get; set; }
    /// <summary>設定航向(弧度)</summary>
    public double HeadingRad { get; set; }
    /// <summary>ROT 限制(度/分)</summary>
    public double RotLimitDegPerMin { get; set; } = 15.0;
    /// <summary>航向誤差 → 目標 ROT 的增益(度/分 每 度)</summary>
    public double HeadingGain { get; set; } = 1.0;
    /// <summary>ROT 誤差 → 舵令的增益(度 每 度/分)</summary>
    public double RateGain { get; set; } = 1.0;

    /// <summary>計算舵令(度,右正)。</summary>
    public double RudderOrderDeg(double headingRad, double yawRateRadPerSec, double rudderMaxDeg)
    {
        var errDeg = Units.RadToDeg(Units.WrapRadPi(HeadingRad - headingRad));
        var rotDesired = Units.Clamp(HeadingGain * errDeg, -RotLimitDegPerMin, RotLimitDegPerMin);
        var rot = Units.RadPerSecToDegPerMin(yawRateRadPerSec);
        return Units.Clamp(RateGain * (rotDesired - rot), -rudderMaxDeg, rudderMaxDeg);
    }
}
