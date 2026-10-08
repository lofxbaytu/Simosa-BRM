using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Engine;

/// <summary>
/// 引擎完整狀態快照(規劃書第 5.3 節:每 10 s 一次,含亂數狀態與 tick,使重播可跳到任意時間接續)。
/// 以 JSON 序列化;double 以 System.Text.Json 的最短往返格式輸出,還原後位元一致。
/// </summary>
public sealed record EngineSnapshot
{
    public int Version { get; init; } = 1;
    public required string ShipId { get; init; }
    public required LoadingCondition Loading { get; init; }
    public required string ScenarioId { get; init; }
    public required string Dynamics { get; init; }
    public required long Tick { get; init; }
    public required double Dt { get; init; }
    public required MotionSnapshot Motion { get; init; }
    public required ActuatorSnapshot Actuators { get; init; }
    public required TelegraphOrder Telegraph { get; init; }
    public required EnvironmentSnapshot Environment { get; init; }
    public required int RandomSeed { get; init; }
    public required long RandomCount { get; init; }
    public required bool Frozen { get; init; }
    public required double TimeScale { get; init; }
    public required AutopilotSnapshot Autopilot { get; init; }
    public required IReadOnlyList<string> Faults { get; init; }
    public required bool Aground { get; init; }
    public int? StartsRemaining { get; init; }
    /// <summary>狀態鏈雜湊(hex)</summary>
    public required string StateHash { get; init; }

    public string ToJson() => ContractJson.Serialize(this);
    public static EngineSnapshot FromJson(string json)
        => ContractJson.Deserialize<EngineSnapshot>(json) ?? throw new InvalidDataException("無法解析快照 JSON");
}

public sealed record MotionSnapshot(double U, double V, double R, double X, double Y, double Psi);

public sealed record ActuatorSnapshot(
    double RudderOrderDeg, double RudderDeg, double RpmOrder, double Rpm, double ThrusterOrder, double ThrusterActual);

public sealed record EnvironmentSnapshot(
    double WindTrueSpeedMps, double WindTrueDirFromRad, double Gustiness, double GustFactor,
    double CurrentSetRad, double CurrentDriftMps,
    /// <summary>常數水深;null 表示使用 callback 水深(還原時沿用現有提供者)</summary>
    double? ConstantDepth);

public sealed record AutopilotSnapshot(bool Enabled, double HeadingRad, double RotLimitDegPerMin);
