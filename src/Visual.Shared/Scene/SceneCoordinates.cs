using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Visual.Shared.Scene;

/// <summary>場景座標(Godot 右手系:x 東、y 上、z 南;前方為 −z)。</summary>
public readonly record struct ScenePoint(double X, double Y, double Z)
{
    public static ScenePoint operator +(ScenePoint a, ScenePoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>
/// 觀察者(相機)三個軸在世界場景座標的單位向量:Right 畫面右方、Up 畫面上方、Forward 視線方向(Godot 相機的 −z)。
/// 由 <see cref="SceneCoordinates.ObserverBasis"/> 產生;與 Visual.Godot CameraRig.AttitudeBasis 的 Basis 合成相同。
/// </summary>
public readonly record struct ObserverAxes(ScenePoint Right, ScenePoint Up, ScenePoint Forward);

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
    /// 眼位/桅頂等高於水線的點隨船傾往右舷偏、舷側點隨船傾下沉(docs/design/visual-client.md 第 4 節「橫搖、縱搖與升沉」)。
    /// </summary>
    public static ScenePoint BodyToWorldOffset(double forward, double starboard, double up, double headingDeg, double rollDeg)
        => BodyToWorldOffset(forward, starboard, up, headingDeg, rollDeg, 0.0, 0.0);

    /// <summary>
    /// 船體座標點經完整船體姿態(橫搖、縱搖、升沉、艏向)後的世界場景偏移(不含船的水平位置;需求 R2 浪一階運動的顯示端)。
    /// <b>合成順序固定為船舶運動學慣用的 Euler ZYX(艏搖 → 縱搖 → 橫搖)</b>:
    /// 世界 = 升沉平移 · Ry(−艏向) · Rx(縱搖) · Rz(−橫搖) · 船體點,即船體點先繞艏艉軸橫搖(<see cref="HeelOffset"/>)、
    /// 再繞水平的橫軸縱搖(<see cref="PitchOffset"/>)、再轉艏向、最後整船垂直平移升沉。
    /// 選這個順序(而不是先縱搖再橫搖)的理由:艏艉線只受縱搖與艏向影響,其水平方位恆等於 state.heading(橫搖不會把船艏甩離艏向),
    /// 縱搖軸維持水平;另一順序在橫搖 φ、縱搖 θ 同時存在時船艏方位會偏 ≈ sin θ·sin φ。
    /// 正負號:rollDeg 右傾為正(右舷下沉)、pitchDeg 艏仰為正(艏部上升、高於水線的點往艉移)、heaveM 向上為正;轉軸在船舯水線中心線
    /// (state.waveMotion.heave_m 為船舯的垂向位移)。三者皆 0 時與只有艏向的換算相同。
    /// </summary>
    public static ScenePoint BodyToWorldOffset(double forward, double starboard, double up, double headingDeg, double rollDeg, double pitchDeg, double heaveM)
    {
        var (s, u) = HeelOffset(starboard, up, rollDeg);
        var (f, u2) = PitchOffset(forward, u, pitchDeg);
        var h = Units.DegToRad(headingDeg);
        // ENU:東 = fwd·sin h + stbd·cos h;北 = fwd·cos h − stbd·sin h
        var east = f * Math.Sin(h) + s * Math.Cos(h);
        var north = f * Math.Cos(h) - s * Math.Sin(h);
        return FromEnu(east, north, heaveM == 0.0 ? u2 : u2 + heaveM);
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
    /// 縱搖對船體縱剖面座標的影響(繞橫軸旋轉,軸在船舯水線):艏仰 θ(度,艏仰正)時艏部上升、艉部下沉、高於水線的點往艉移:
    /// fwd' = fwd·cos θ − up·sin θ、up' = fwd·sin θ + up·cos θ。θ = 0 時原樣(無 waveMotion 的情境數值不變)。
    /// </summary>
    public static (double Forward, double Up) PitchOffset(double forward, double up, double pitchDeg)
    {
        if (pitchDeg == 0.0) return (forward, up);
        var r = Units.DegToRad(pitchDeg);
        var (c, s) = (Math.Cos(r), Math.Sin(r));
        return (forward * c - up * s, forward * s + up * c);
    }

    /// <summary>
    /// 觀察者(相機)隨船運動時的三個軸(世界場景座標的單位向量):
    /// Ry(−艏向) · Rx(船縱搖) · Rz(−船橫搖) · Ry(−視角偏航) · Rx(視角俯仰) 作用於相機的右/上/前(−z)軸 —
    /// 即視角先在船體座標內定向(偏航右正、俯仰上正),再隨船體姿態依 <see cref="BodyToWorldOffset(double, double, double, double, double, double, double)"/> 的順序轉到世界。
    /// 駕駛台前視(偏航 0)時艏仰使視線上抬(地平線在畫面中下移)、右傾使畫面順時針橫滾(地平線右端升高);
    /// 艉視(偏航 180°)兩者皆反號;翼橋側視時兩者互相混合(見 <see cref="ObserverMotionComponents"/>)。升沉只平移眼位,不改變軸向。
    /// Visual.Godot 的 CameraRig.AttitudeBasis 以 Godot Basis 做同一合成;此函式為其雙精度對照,供測試釘住順序與正負號。
    /// </summary>
    public static ObserverAxes ObserverBasis(double headingDeg, double rollDeg, double pitchDeg, double viewYawDeg, double viewPitchDeg)
    {
        var y = Units.DegToRad(viewYawDeg);
        var p = Units.DegToRad(viewPitchDeg);
        var (cy, sy, cp, sp) = (Math.Cos(y), Math.Sin(y), Math.Cos(p), Math.Sin(p));
        // 相機軸在船體座標(向艏、向右舷、向上)的分量:先俯仰(繞相機橫軸)再偏航(繞船體垂直軸)
        ScenePoint World(double f, double s, double u) => BodyToWorldOffset(f, s, u, headingDeg, rollDeg, pitchDeg, 0.0);
        return new ObserverAxes(
            Right: World(-sy, cy, 0.0),
            Up: World(-sp * cy, -sp * sy, cp),
            Forward: World(cp * cy, cp * sy, sp));
    }

    /// <summary>
    /// 地平線在畫面中的位置(由相機三軸推得;測試與文件用來定義正負號):
    /// ElevationDeg = 地平線相對畫面中心的仰角(度;正 = 地平線在畫面中心上方,即相機俯視;相機上仰時為負 → 地平線下移),
    /// TiltDeg = 地平線相對畫面水平線的傾斜(度;正 = 地平線右端較高,即相機順時針橫滾、船右傾時的駕駛台前視)。
    /// 視線垂直向上/向下時傾斜無定義,回傳 0。
    /// </summary>
    public static (double ElevationDeg, double TiltDeg) HorizonInView(ObserverAxes axes)
    {
        var elevation = -Units.RadToDeg(Math.Asin(Math.Clamp(axes.Forward.Y, -1.0, 1.0)));
        // 地平線方向 = 視線 × 世界向上(水平、指向畫面右方);在畫面右/上軸的分量決定傾斜
        var (lx, lz) = (-axes.Forward.Z, axes.Forward.X);
        var along = lx * axes.Right.X + lz * axes.Right.Z;
        var across = lx * axes.Up.X + lz * axes.Up.Z;
        if (Math.Abs(along) < 1e-12 && Math.Abs(across) < 1e-12) return (elevation, 0.0);
        return (elevation, Units.RadToDeg(Math.Atan2(across, along)));
    }

    /// <summary>
    /// 觀察者(相機)隨船橫搖 φ 與縱搖 θ 時,相機繞自身前方軸的有效橫滾(度,順時針正 = 畫面右側下沉)與繞自身橫軸的有效俯仰增量(度,上正)
    /// 的小角度近似:相機相對艏向偏航 yaw 時 roll = φ·cos(yaw) + θ·sin(yaw)、pitch = θ·cos(yaw) − φ·sin(yaw)
    /// (駕駛台前視:艏仰全為上仰;艉視兩者反號;右翼橋向右看時艏仰使畫面右側(艉方)下沉)。精確合成見 <see cref="ObserverBasis"/>。
    /// </summary>
    public static (double RollDeg, double PitchDeg) ObserverMotionComponents(double rollDeg, double pitchDeg, double cameraYawDeg)
    {
        var y = Units.DegToRad(cameraYawDeg);
        var (c, s) = (Math.Cos(y), Math.Sin(y));
        return (rollDeg * c + pitchDeg * s, pitchDeg * c - rollDeg * s);
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
