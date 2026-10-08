using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Environment;

/// <summary>
/// 環境狀態(規劃書第 6.2 節 W、C 項與淺水 B 項的輸入):真風(速、來向)、均勻流(去向、流速)、水深。
/// 內部 SI/弧度;陣風係數由引擎以情境種子的亂數更新。
/// </summary>
public sealed class EnvironmentConditions
{
    /// <summary>真風速(m/s,未含陣風)</summary>
    public double WindTrueSpeedMps { get; set; }
    /// <summary>真風來向(弧度,自北順時針)</summary>
    public double WindTrueDirFromRad { get; set; }
    /// <summary>陣風強度(0 = 無;0.2 表示 ±20 %)</summary>
    public double Gustiness { get; set; }
    /// <summary>目前陣風係數(1 = 平均風)</summary>
    public double GustFactor { get; set; } = 1.0;
    /// <summary>流向(去向,弧度)</summary>
    public double CurrentSetRad { get; set; }
    /// <summary>流速(m/s)</summary>
    public double CurrentDriftMps { get; set; }
    public IDepthProvider Depth { get; set; } = new ConstantDepth(50.0);

    public double WindEffectiveSpeedMps => WindTrueSpeedMps * GustFactor;

    /// <summary>在指定位置取樣,供動力學模型使用。</summary>
    public EnvironmentSample SampleAt(double xEast, double yNorth)
    {
        var w = WindEffectiveSpeedMps;
        // 風「來向」θ:空氣流動方向為 θ+180°,向量 = (−W sinθ, −W cosθ)
        var windE = -w * Math.Sin(WindTrueDirFromRad);
        var windN = -w * Math.Cos(WindTrueDirFromRad);
        // 流「去向」:向量 = (C sinθ, C cosθ)
        var curE = CurrentDriftMps * Math.Sin(CurrentSetRad);
        var curN = CurrentDriftMps * Math.Cos(CurrentSetRad);
        return new EnvironmentSample(windE, windN, curE, curN, Depth.DepthAt(xEast, yNorth));
    }
}

/// <summary>動力學模型每步看到的環境向量(ENU,m/s)與水深(m)。</summary>
public readonly record struct EnvironmentSample(
    double WindEastMps, double WindNorthMps,
    double CurrentEastMps, double CurrentNorthMps,
    double WaterDepthM)
{
    public double WindSpeedMps => Math.Sqrt(WindEastMps * WindEastMps + WindNorthMps * WindNorthMps);
    public double CurrentSpeedMps => Math.Sqrt(CurrentEastMps * CurrentEastMps + CurrentNorthMps * CurrentNorthMps);
}

/// <summary>相對風、COG/SOG、squat 與 UKC 的計算(規劃書第 6.2 節淺水項;第 8.3 節 UKC 指標)。</summary>
public static class EnvironmentMath
{
    /// <summary>
    /// 相對(視)風:空氣相對船的速度 = 風向量 − 船對地速度向量。
    /// 回傳 (相對風速 m/s, 相對風來向 弧度,相對艏向 0 = 迎艏、π/2 = 右舷來)。
    /// </summary>
    public static (double SpeedMps, double FromRelRad) RelativeWind(
        double windEastMps, double windNorthMps, double shipVelEastMps, double shipVelNorthMps, double headingRad)
    {
        var aE = windEastMps - shipVelEastMps;
        var aN = windNorthMps - shipVelNorthMps;
        var speed = Math.Sqrt(aE * aE + aN * aN);
        if (speed < 1e-9) return (0.0, 0.0);
        // 來向 = 空氣流動方向的反向
        var fromAbs = Math.Atan2(-aE, -aN);
        return (speed, Units.NormalizeHeadingRad(fromAbs - headingRad));
    }

    /// <summary>視風在船體座標的分量(x 向艏、y 向右舷),供風力/漂移模型使用。</summary>
    public static (double Ax, double Ay) ApparentWindBody(
        double windEastMps, double windNorthMps, double shipVelEastMps, double shipVelNorthMps, double headingRad)
    {
        var aE = windEastMps - shipVelEastMps;
        var aN = windNorthMps - shipVelNorthMps;
        var c = Math.Cos(headingRad);
        var s = Math.Sin(headingRad);
        // ENU → 船體:x_b = E sinψ + N cosψ;y_b = E cosψ − N sinψ
        return (aE * s + aN * c, aE * c - aN * s);
    }

    /// <summary>船體速度(對水)加流 → 對地速度向量(ENU)。</summary>
    public static (double VelEast, double VelNorth) GroundVelocity(double u, double v, double headingRad, double currentEast, double currentNorth)
    {
        var c = Math.Cos(headingRad);
        var s = Math.Sin(headingRad);
        return (u * s + v * c + currentEast, u * c - v * s + currentNorth);
    }

    /// <summary>對地速度向量 → (SOG m/s, COG 弧度 0–2π)。速度極小時 COG 以航向代替。</summary>
    public static (double SogMps, double CogRad) CogSog(double velEast, double velNorth, double headingRad)
    {
        var sog = Math.Sqrt(velEast * velEast + velNorth * velNorth);
        if (sog < 1e-6) return (0.0, Units.NormalizeHeadingRad(headingRad));
        return (sog, Units.NormalizeHeadingRad(Math.Atan2(velEast, velNorth)));
    }

    /// <summary>
    /// Squat(Barrass 開闊水域簡式):S ≈ Cb·V²/100(m,V 為對水速度 kn)。限制水域約為 2 倍;
    /// 日後依規劃書第 6.2 節改用 PIANC 2014 的 Huuska/ICORELS 公式並考慮航道截面比。
    /// </summary>
    public static double SquatBarrass(double blockCoefficient, double speedKn, bool confined = false)
    {
        var s = blockCoefficient * speedKn * speedKn / 100.0;
        return confined ? 2.0 * s : s;
    }

    /// <summary>龍骨下餘裕水深 = 水深 − 最大吃水 − squat。</summary>
    public static double UnderKeelClearance(double waterDepth, double maxDraft, double squat) => waterDepth - maxDraft - squat;
}
