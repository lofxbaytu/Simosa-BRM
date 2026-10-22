using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Visual.Shared.Ship;

/// <summary>
/// 拖船示意姿態(docs/design/visual-client.md 第 4 節;審查發現:推頂拖船艏向曾反了 180°)。
/// 契約 tugs[].direction_deg 為「實際力方向(相對船艏;0 向前、90 向右舷)」;SimCore ExternalForceModel.TugStates 的示意位置:
/// push 在連接點沿力方向的反側(半船寬 + 15 m)、pull 在連接點沿力方向一個纜長、其他在外側 30 m。
/// 因此 push 與 pull 的拖船艏向都 = 自船艏向 + 力方向:推頂時艏頂住自船(自船在拖船前方),拖拉時艏背向自船、以艉纜拖;
/// 位置一律取核心給的 pos。其他模式(standby/free)與自船同向。
/// </summary>
public static class TugPresentation
{
    /// <summary>示意拖船尺寸(m):LOA × 船寬 × 吃水。</summary>
    public const double LoaM = 28.0;
    public const double BeamM = 9.0;
    public const double DraftM = 3.5;

    /// <summary>拖船艏向(度 0–360)。</summary>
    public static double HeadingDeg(double ownHeadingDeg, string? mode, double directionDeg) => mode switch
    {
        "push" or "pull" => Units.NormalizeHeadingDeg(ownHeadingDeg + directionDeg),
        _ => Units.NormalizeHeadingDeg(ownHeadingDeg),
    };

    /// <summary>纜線在拖船上的連接點(船體座標 x 向艏,m):推頂自艏(艏頂自船)、拖拉自艉(拖纜絞機)、其他自船中。</summary>
    public static double LineAttachForwardM(string? mode) => mode switch
    {
        "push" => LoaM * 0.5,
        "pull" => -LoaM * 0.43,
        _ => 0.0,
    };
}
