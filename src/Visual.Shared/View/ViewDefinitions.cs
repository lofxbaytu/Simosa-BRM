using SimosaBRM.Visual.Shared.Scene;
using SimosaBRM.Visual.Shared.Ship;

namespace SimosaBRM.Visual.Shared.View;

/// <summary>視角種類(鍵盤 1–5;啟動參數 --view)。</summary>
public enum ViewKind
{
    Bridge = 1,
    PortWing = 2,
    StbdWing = 3,
    Aft = 4,
    Overview = 5,
}

/// <summary>
/// 一個視角:眼位(船體座標:x 向艏、y 右舷、z 水線以上;原點在船舯水線中心線)、相對艏向的偏航/俯仰、視野角。
/// 鳥瞰為固定於自船上方的俯視(跟隨位置、不跟隨艏向與船體運動)。
/// </summary>
public sealed record ViewDefinition
{
    public required ViewKind Kind { get; init; }
    public required string Label { get; init; }
    public required double EyeForward { get; init; }
    public required double EyeStarboard { get; init; }
    public required double EyeUp { get; init; }
    /// <summary>相對艏向的偏航(度,右正)</summary>
    public required double YawDeg { get; init; }
    /// <summary>俯仰(度,上正)</summary>
    public required double PitchDeg { get; init; }
    public required double FovDeg { get; init; }
    /// <summary>true = 不隨艏向旋轉(鳥瞰北向上)</summary>
    public bool FixedNorthUp { get; init; }

    /// <summary>
    /// 觀察者隨船運動(駕駛台/翼橋/艉視:眼位固定在船上,隨船橫搖、縱搖與升沉);鳥瞰固定於自船上方、不隨船運動。
    /// state.roll / state.waveMotion 不存在時對應量為 0,畫面與未實作前相同(docs/design/visual-client.md 第 4 節「橫搖、縱搖與升沉」)。
    /// </summary>
    public bool FollowsShipMotion => !FixedNorthUp;

    /// <summary>
    /// 觀察者隨船傾(駕駛台/翼橋/艉視:眼位固定在船上,船右傾時畫面地平線向左傾斜);鳥瞰固定於自船上方、不隨船傾。
    /// state.roll 不存在時橫搖為 0,畫面與未實作橫搖前相同。
    /// </summary>
    public bool FollowsRoll => FollowsShipMotion;

    /// <summary>此視角要套用到相機的船體橫搖角(度,右傾正;不隨船傾的視角為 0)。</summary>
    public double CameraRollDeg(double shipRollDeg) => FollowsRoll ? shipRollDeg : 0.0;

    /// <summary>此視角要套用到相機的船體縱搖角(度,艏仰正;不隨船運動的視角為 0)。</summary>
    public double CameraPitchDeg(double shipPitchDeg) => FollowsShipMotion ? shipPitchDeg : 0.0;

    /// <summary>此視角要套用到眼位的升沉(m,向上正;不隨船運動的視角為 0)。</summary>
    public double CameraHeaveM(double shipHeaveM) => FollowsShipMotion ? shipHeaveM : 0.0;

    /// <summary>
    /// 眼位相對自船位置(船舯水線中心線)的世界場景偏移:隨船的視角把眼位(船體座標)經橫搖 → 縱搖 → 艏向 → 升沉
    /// (<see cref="SceneCoordinates.BodyToWorldOffset(double, double, double, double, double, double, double)"/> 的固定順序)轉到世界;
    /// 鳥瞰為自船正上方 EyeUp,不隨艏向與船體運動。
    /// 例:駕駛台在船舯之後(EyeForward &lt; 0),艏仰時眼位下沉 |EyeForward|·sin θ 並往艉移 EyeUp·sin θ;升沉整個加在高度上。
    /// </summary>
    public ScenePoint EyeOffset(double headingDeg, double shipRollDeg, double shipPitchDeg, double shipHeaveM)
        => FixedNorthUp
            ? new ScenePoint(0.0, EyeUp, 0.0)
            : SceneCoordinates.BodyToWorldOffset(EyeForward, EyeStarboard, EyeUp, headingDeg, CameraRollDeg(shipRollDeg), CameraPitchDeg(shipPitchDeg), CameraHeaveM(shipHeaveM));

    /// <summary>
    /// 相機三軸(世界場景座標):隨船的視角 = Ry(−艏向)·Rx(船縱搖)·Rz(−船橫搖)·Ry(−(視角偏航 + 環視偏航))·Rx(視角俯仰 + 環視俯仰)
    /// (<see cref="SceneCoordinates.ObserverBasis"/>;駕駛台前視艏仰 → 視線上抬、地平線下移);鳥瞰北向上、不含艏向與船體運動。
    /// extraYawDeg / extraPitchDeg 為滑鼠環視的偏移(右正 / 上正)。
    /// </summary>
    public ObserverAxes ObserverBasis(double headingDeg, double shipRollDeg, double shipPitchDeg, double extraYawDeg = 0.0, double extraPitchDeg = 0.0)
        => SceneCoordinates.ObserverBasis(FixedNorthUp ? 0.0 : headingDeg, CameraRollDeg(shipRollDeg), CameraPitchDeg(shipPitchDeg), YawDeg + extraYawDeg, PitchDeg + extraPitchDeg);

    public static ViewDefinition For(ViewKind kind, HullParameters hull)
    {
        var eye = hull.EyeHeightAboveWater;
        return kind switch
        {
            ViewKind.Bridge => new() { Kind = kind, Label = "駕駛台前視", EyeForward = hull.BridgeX, EyeStarboard = 0, EyeUp = eye, YawDeg = 0, PitchDeg = -2, FovDeg = 60 },
            ViewKind.PortWing => new() { Kind = kind, Label = "左翼橋", EyeForward = hull.BridgeX, EyeStarboard = -hull.WingHalfWidth, EyeUp = eye, YawDeg = -60, PitchDeg = -8, FovDeg = 70 },
            ViewKind.StbdWing => new() { Kind = kind, Label = "右翼橋", EyeForward = hull.BridgeX, EyeStarboard = hull.WingHalfWidth, EyeUp = eye, YawDeg = 60, PitchDeg = -8, FovDeg = 70 },
            ViewKind.Aft => new() { Kind = kind, Label = "艉視", EyeForward = hull.BridgeX - 2, EyeStarboard = 0, EyeUp = eye + 1, YawDeg = 180, PitchDeg = -6, FovDeg = 60 },
            ViewKind.Overview => new() { Kind = kind, Label = "鳥瞰", EyeForward = 0, EyeStarboard = 0, EyeUp = Math.Max(300, hull.Loa * 4), YawDeg = 0, PitchDeg = -89, FovDeg = 55, FixedNorthUp = true },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public static ViewKind ParseKind(string s) => s.Trim().ToLowerInvariant() switch
    {
        "bridge" or "1" => ViewKind.Bridge,
        "portwing" or "port" or "2" => ViewKind.PortWing,
        "stbdwing" or "stbd" or "starboard" or "3" => ViewKind.StbdWing,
        "aft" or "4" => ViewKind.Aft,
        "overview" or "top" or "5" => ViewKind.Overview,
        _ => throw new ArgumentException($"未知的視角:{s}(bridge|portWing|stbdWing|aft|overview)"),
    };

    public static string KindName(ViewKind k) => k switch
    {
        ViewKind.Bridge => "bridge",
        ViewKind.PortWing => "portWing",
        ViewKind.StbdWing => "stbdWing",
        ViewKind.Aft => "aft",
        _ => "overview",
    };
}
