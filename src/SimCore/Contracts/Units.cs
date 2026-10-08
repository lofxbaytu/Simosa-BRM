namespace SimosaBRM.SimCore.Contracts;

/// <summary>
/// 單位換算與角度正規化(規劃書第 11.3 節:核心內部 SI 與弧度;對外介面用度、節、航向 0–360、ROT 度/分)。
/// </summary>
public static class Units
{
    /// <summary>1 國際浬 = 1852 m;1 kn = 1852/3600 m/s。</summary>
    public const double MetresPerNauticalMile = 1852.0;
    public const double KnotToMetresPerSecond = MetresPerNauticalMile / 3600.0;
    public const double DegreesPerRadian = 180.0 / Math.PI;
    public const double RadiansPerDegree = Math.PI / 180.0;
    public const double TwoPi = 2.0 * Math.PI;

    public static double DegToRad(double degrees) => degrees * RadiansPerDegree;
    public static double RadToDeg(double radians) => radians * DegreesPerRadian;

    public static double KnToMps(double knots) => knots * KnotToMetresPerSecond;
    public static double MpsToKn(double metresPerSecond) => metresPerSecond / KnotToMetresPerSecond;

    /// <summary>艏搖角速度 rad/s → 迴轉率 度/分(右轉正)。</summary>
    public static double RadPerSecToDegPerMin(double radPerSec) => radPerSec * DegreesPerRadian * 60.0;

    /// <summary>迴轉率 度/分 → rad/s。</summary>
    public static double DegPerMinToRadPerSec(double degPerMin) => degPerMin / 60.0 * RadiansPerDegree;

    /// <summary>航向正規化到 [0, 360)。輸入可為任意實數(含負值與超過 360)。</summary>
    public static double NormalizeHeadingDeg(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return 0.0;
        var h = degrees % 360.0;
        if (h < 0) h += 360.0;
        // 浮點誤差可能讓 -1e-15 % 360 + 360 == 360.0,一律折回 0
        if (h >= 360.0) h -= 360.0;
        return h;
    }

    /// <summary>角度(弧度)正規化到 [0, 2π)。</summary>
    public static double NormalizeHeadingRad(double radians)
    {
        if (double.IsNaN(radians) || double.IsInfinity(radians)) return 0.0;
        var a = radians % TwoPi;
        if (a < 0) a += TwoPi;
        if (a >= TwoPi) a -= TwoPi;
        return a;
    }

    /// <summary>角度差正規化到 (−180, 180],供航向誤差計算(目標 − 現在;正值表示須右轉)。</summary>
    public static double WrapDeg180(double degrees)
    {
        var d = degrees % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d <= -180.0) d += 360.0;
        return d;
    }

    /// <summary>角度差正規化到 (−π, π]。</summary>
    public static double WrapRadPi(double radians)
    {
        var a = radians % TwoPi;
        if (a > Math.PI) a -= TwoPi;
        if (a <= -Math.PI) a += TwoPi;
        return a;
    }

    public static double Clamp(double value, double min, double max) => value < min ? min : (value > max ? max : value);
}
