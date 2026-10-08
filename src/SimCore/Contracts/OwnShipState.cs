using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Contracts;

/// <summary>
/// 自船狀態廣播(src/Contracts/state.schema.json,25 Hz)。角度一律為度、航向 0–360、舵角右正左負、ROT 右轉正(度/分);
/// u、v、r 為船體座標 SI 值(規劃書第 6.2 節 MMG 慣例:原點船舯,x 向艏,y 向右舷)。
/// 欄位名稱以 JsonPropertyName 固定,避免命名原則變動影響契約。
/// </summary>
public sealed record OwnShipState
{
    /// <summary>模擬時間(秒,自練習開始)</summary>
    [JsonPropertyName("t")] public required double T { get; init; }
    /// <summary>核心步數(50 Hz)</summary>
    [JsonPropertyName("tick")] public required long Tick { get; init; }
    [JsonPropertyName("shipId")] public required string ShipId { get; init; }
    [JsonPropertyName("pos")] public required GeoPosition Pos { get; init; }
    /// <summary>航向(度,0–360)</summary>
    [JsonPropertyName("heading")] public required double Heading { get; init; }
    /// <summary>對地航向(度,0–360)</summary>
    [JsonPropertyName("cog")] public required double Cog { get; init; }
    /// <summary>對地速度(kn)</summary>
    [JsonPropertyName("sog")] public required double Sog { get; init; }
    /// <summary>對水速度(kn,縱向)</summary>
    [JsonPropertyName("stw")] public required double Stw { get; init; }
    /// <summary>迴轉率(度/分,右轉正)</summary>
    [JsonPropertyName("rot")] public required double Rot { get; init; }
    /// <summary>船體座標縱向速度(m/s,對水)</summary>
    [JsonPropertyName("u")] public required double U { get; init; }
    /// <summary>船體座標橫向速度(m/s,右正,船舯)</summary>
    [JsonPropertyName("v")] public required double V { get; init; }
    /// <summary>艏搖角速度(rad/s)</summary>
    [JsonPropertyName("r")] public required double R { get; init; }
    /// <summary>漂角(度)</summary>
    [JsonPropertyName("drift")] public double? Drift { get; init; }
    /// <summary>實際舵角(度,右正)</summary>
    [JsonPropertyName("rudder")] public required double Rudder { get; init; }
    /// <summary>舵令(度)</summary>
    [JsonPropertyName("rudderOrder")] public required double RudderOrder { get; init; }
    /// <summary>實際轉速(rpm,倒車負)</summary>
    [JsonPropertyName("rpm")] public required double Rpm { get; init; }
    [JsonPropertyName("rpmOrder")] public required double RpmOrder { get; init; }
    [JsonPropertyName("telegraph")] public required TelegraphOrder Telegraph { get; init; }
    [JsonPropertyName("thruster")] public ThrusterState? Thruster { get; init; }
    [JsonPropertyName("depthBelowKeel")] public required double DepthBelowKeel { get; init; }
    [JsonPropertyName("waterDepth")] public double? WaterDepth { get; init; }
    [JsonPropertyName("squat")] public double? Squat { get; init; }
    [JsonPropertyName("wind")] public required WindState Wind { get; init; }
    [JsonPropertyName("current")] public required CurrentState Current { get; init; }
    [JsonPropertyName("loading")] public required LoadingCondition Loading { get; init; }
    [JsonPropertyName("draft")] public DraftState? Draft { get; init; }
    [JsonPropertyName("engine")] public EngineStatus? Engine { get; init; }
    [JsonPropertyName("faults")] public IReadOnlyList<string>? Faults { get; init; }
    [JsonPropertyName("flags")] public StateFlags? Flags { get; init; }
    /// <summary>已出現的目標船(情境無目標船時省略;規劃書第 5.2 節 Traffic)</summary>
    [JsonPropertyName("targets")] public IReadOnlyList<TargetState>? Targets { get; init; }
}

/// <summary>位置:WGS-84 經緯度與本地 ENU(m)。</summary>
public sealed record GeoPosition
{
    [JsonPropertyName("lat")] public required double Lat { get; init; }
    [JsonPropertyName("lon")] public required double Lon { get; init; }
    /// <summary>本地 ENU 東向(m)</summary>
    [JsonPropertyName("x")] public required double X { get; init; }
    /// <summary>本地 ENU 北向(m)</summary>
    [JsonPropertyName("y")] public required double Y { get; init; }
}

/// <summary>艏側推:令 −1(推艏向左)至 +1(推艏向右),實際值為推力比例。</summary>
public sealed record ThrusterState
{
    [JsonPropertyName("order")] public required double Order { get; init; }
    [JsonPropertyName("actual")] public required double Actual { get; init; }
}

/// <summary>風:真風(速 kn、來向 度)與相對風(相對艏向,0 = 迎艏、90 = 右舷來)。</summary>
public sealed record WindState
{
    [JsonPropertyName("trueSpeed")] public required double TrueSpeed { get; init; }
    [JsonPropertyName("trueDir")] public required double TrueDir { get; init; }
    [JsonPropertyName("relSpeed")] public double? RelSpeed { get; init; }
    [JsonPropertyName("relDir")] public double? RelDir { get; init; }
}

/// <summary>流:流向(去向,度)與流速(kn)。</summary>
public sealed record CurrentState
{
    [JsonPropertyName("set")] public required double Set { get; init; }
    [JsonPropertyName("drift")] public required double Drift { get; init; }
}

public sealed record DraftState
{
    [JsonPropertyName("fore")] public required double Fore { get; init; }
    [JsonPropertyName("aft")] public required double Aft { get; init; }
}

public sealed record EngineStatus
{
    [JsonPropertyName("state")] public required EngineRunState State { get; init; }
    [JsonPropertyName("startsRemaining")] public int? StartsRemaining { get; init; }
    /// <summary>主機負荷百分比;JSON 名稱依 schema 為 load_pct(非 camelCase)。</summary>
    [JsonPropertyName("load_pct")] public double? LoadPct { get; init; }
}

public sealed record StateFlags
{
    [JsonPropertyName("frozen")] public bool Frozen { get; init; }
    [JsonPropertyName("aground")] public bool Aground { get; init; }
    [JsonPropertyName("collision")] public bool Collision { get; init; }
}
