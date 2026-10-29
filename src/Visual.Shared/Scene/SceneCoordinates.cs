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
        => BodyToWorldOffset(forward, starboard, up, headingDeg, 0.0);

    /// <summary>
    /// 船體座標點先經橫搖(rollDeg,右傾正)再經航向旋轉後的世界場景偏移(不含船位置):
    /// 眼位/桅頂等高於水線的點隨船傾往右舷偏、舷側點隨船傾下沉(docs/design/visual-client.md 第 4 節「橫搖視角」)。
    /// </summary>
    public static ScenePoint BodyToWorldOffset(double forward, double starboard, double up, double headingDeg, double rollDeg)
    {
        var (s, u) = HeelOffset(starboard, up, rollDeg);
        var h = Units.DegToRad(headingDeg);
        // ENU:東 = fwd·sin h + stbd·cos h;北 = fwd·cos h − stbd·sin h
        var east = forward * Math.Sin(h) + s * Math.Cos(h);
        var north = forward * Math.Cos(h) - s * Math.Sin(h);
        return FromEnu(east, north, u);
    }

    /// <summary>
    /// 橫搖對船體橫剖面座標的影響(繞艏艉軸旋轉):右傾 φ(度,右傾正)時,高於水線的點往右舷移、右舷側的點下沉:
    /// stbd' = stbd·cos φ + up·sin φ、up' = −stbd·sin φ + up·cos φ。φ = 0 時原樣(無橫搖情境數值不變)。
    /// </summary>
    public static (double Starboard, double Up) HeelOffset(double starboard, double up, double rollDeg)
    {
        if (rollDeg == 0.0) return (starboard, up);
        var r = Units.DegToRad(rollDeg);
        var (c, s) = (Math.Cos(r), Math.Sin(r));
        return (starboard * c + up * s, -starboard * s + up * c);
    }

    /// <summary>
    /// 觀察者(相機)隨船傾時,相機繞自身前方軸的有效橫滾(度,順時針正 = 畫面右側下沉)與繞自身橫軸的有效俯仰增量(度,上正):
    /// 相機相對艏向偏航 yaw 時,船的右傾 φ 分解為 roll = φ·cos(yaw)、pitch = −φ·sin(yaw)(小角度近似;艉視 yaw 180° 時橫滾反號、
    /// 右翼橋 yaw 60° 向右看時右傾使視線略為下俯)。Godot 以 Basis 精確合成(CameraRig),此函式供測試與 HUD 說明。
    /// </summary>
    public static (double RollDeg, double PitchDeg) ObserverHeelComponents(double rollDeg, double cameraYawDeg)
    {
        var y = Units.DegToRad(cameraYawDeg);
        return (rollDeg * Math.Cos(y), -rollDeg * Math.Sin(y));
    }

    /// <summary>方位(度)與距離(m)→ ENU 偏移。</summary>
    public static (double East, double North) FromBearing(double bearingDeg, double rangeM)
    {
        var b = Units.DegToRad(bearingDeg);
        return (rangeM * Math.Sin(b), rangeM * Math.Cos(b));
    }
}
