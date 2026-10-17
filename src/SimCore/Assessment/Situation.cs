using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Assessment;

/// <summary>評估步(每秒)取樣的自船狀態(對外單位:度、節、m;內部弧度另附),供時間表條件與各追蹤器共用。</summary>
public sealed record Situation
{
    public required long Tick { get; init; }
    public required double Time { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double HeadingRad { get; init; }
    public double HeadingDeg => Units.NormalizeHeadingDeg(Units.RadToDeg(HeadingRad));
    public required double SogKn { get; init; }
    public required double StwKn { get; init; }
    public required double RotDegPerMin { get; init; }
    public required double RudderDeg { get; init; }
    public required double RudderOrderDeg { get; init; }
    public required TelegraphOrder Telegraph { get; init; }
    public required double Rpm { get; init; }
    public required double ThrusterOrder { get; init; }
    public required bool AutopilotEnabled { get; init; }
    public required double UkcM { get; init; }
    public required double SquatM { get; init; }
    public required double WaterDepthM { get; init; }
    public required double MaxDraftM { get; init; }
    public required double LoaM { get; init; }
    public required double VelE { get; init; }
    public required double VelN { get; init; }
}
