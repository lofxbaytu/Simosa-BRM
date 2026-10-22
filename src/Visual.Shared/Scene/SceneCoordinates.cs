using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Visual.Shared.Scene;

/// <summary>場景座標(Godot 右手系:x 東、y 上、z 南;前方為 −z)。</summary>
public readonly record struct ScenePoint(double X, double Y, double Z)
{
    public static ScenePoint operator +(ScenePoint a, ScenePoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>
/// 座標系換算(docs/design/visual-client.md 第 3 節)。
/// 核心 ENU:x 東、y 北、z 上(公尺)。Godot 場景:右手系,x 東、y 上、z 南(即 −北);節點的「前方」為 −z,
/// 所以航向 0°(北)的船其節點 yaw 為 0,航向 h 的 yaw = −h(弧度;Godot 繞 +y 的正旋轉為由上俯視逆時針)。
/// 船體座標(MMG 慣例):x 向艏、y 向右舷、z 上 → 節點區域座標 (y, z, −x)。
/// </summary>
public static class SceneCoordinates
{
    /// <summary>ENU → 場景座標。</summary>
    public static ScenePoint FromEnu(double east, double north, double up = 0.0) => new(east, up, -north);

    /// <summary>場景座標 → ENU(東、北、上)。</summary>
    public static (double East, double North, double Up) ToEnu(ScenePoint p) => (p.X, -p.Z, p.Y);

    /// <summary>航向(度,順時針自北)→ 節點繞 y 軸的 yaw(弧度)。</summary>
    public static double HeadingToYawRad(double headingDeg) => -Units.DegToRad(headingDeg);

    /// <summary>yaw(弧度)→ 航向(度,0–360)。</summary>
    public static double YawRadToHeading(double yawRad) => Units.NormalizeHeadingDeg(-Units.RadToDeg(yawRad));

    /// <summary>船體座標(x 向艏、y 向右舷、z 上)→ 船節點的區域座標。</summary>
    public static ScenePoint FromBody(double forward, double starboard, double up) => new(starboard, up, -forward);

    /// <summary>船體座標點經航向旋轉後的世界場景偏移(不含船位置)。</summary>
    public static ScenePoint BodyToWorldOffset(double forward, double starboard, double up, double headingDeg)
    {
        var h = Units.DegToRad(headingDeg);
        // ENU:東 = fwd·sin h + stbd·cos h;北 = fwd·cos h − stbd·sin h
        var east = forward * Math.Sin(h) + starboard * Math.Cos(h);
        var north = forward * Math.Cos(h) - starboard * Math.Sin(h);
        return FromEnu(east, north, up);
    }

    /// <summary>方位(度)與距離(m)→ ENU 偏移。</summary>
    public static (double East, double North) FromBearing(double bearingDeg, double rangeM)
    {
        var b = Units.DegToRad(bearingDeg);
        return (rangeM * Math.Sin(b), rangeM * Math.Cos(b));
    }
}
