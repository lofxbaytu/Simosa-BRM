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
    /// <summary>操舵模式(規劃書第 7.2 節操舵模式選擇;Python 參考實作不輸出)</summary>
    [JsonPropertyName("steering")] public SteeringState? Steering { get; init; }
    /// <summary>已出現的目標船(情境無目標船時省略;規劃書第 5.2 節 Traffic)</summary>
    [JsonPropertyName("targets")] public IReadOnlyList<TargetState>? Targets { get; init; }
    /// <summary>能見度(nm;未設定時省略;無物理效果,儀器端自行反應)</summary>
    [JsonPropertyName("visibility_nm")] public double? VisibilityNm { get; init; }
    /// <summary>情境引擎狀態(階段、參數集版次、時間表、評估;情境引擎設計第 5.1 節)</summary>
    [JsonPropertyName("scenario")] public ScenarioStatus? Scenario { get; init; }
    /// <summary>航線狀態(XTD/下一航點;規劃書第 7.2 節 conning 最低資訊);情境無 route 時省略</summary>
    [JsonPropertyName("route")] public RouteStatus? Route { get; init; }
}

/// <summary>情境引擎狀態摘要(state.schema.json `scenario`)。</summary>
public sealed record ScenarioStatus
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("phase")] public required string Phase { get; init; }
    [JsonPropertyName("parameterSetVersion")] public string? ParameterSetVersion { get; init; }
    /// <summary>時間表項目累計觸發次數</summary>
    [JsonPropertyName("timelineFired")] public int? TimelineFired { get; init; }
    /// <summary>待執行(delay_s)的動作數</summary>
    [JsonPropertyName("timelinePending")] public int? TimelinePending { get; init; }
    [JsonPropertyName("hardFailed")] public bool? HardFailed { get; init; }
    [JsonPropertyName("score")] public double? Score { get; init; }
    [JsonPropertyName("ended")] public bool? Ended { get; init; }
}

/// <summary>航線狀態(state.schema.json `route`);xtd_m 右正左負。</summary>
public sealed record RouteStatus
{
    [JsonPropertyName("legIndex")] public int? LegIndex { get; init; }
    [JsonPropertyName("nextWaypoint")] public string? NextWaypoint { get; init; }
    [JsonPropertyName("xtd_m")] public double? XtdM { get; init; }
    [JsonPropertyName("xtdLimit_m")] public double? XtdLimitM { get; init; }
    [JsonPropertyName("distanceToWaypoint_nm")] public double? DistanceToWaypointNm { get; init; }
    [JsonPropertyName("bearingToWaypoint_deg")] public double? BearingToWaypointDeg { get; init; }
    [JsonPropertyName("legCourse_deg")] public double? LegCourseDeg { get; init; }
    [JsonPropertyName("speedLimit_kn")] public double? SpeedLimitKn { get; init; }
    [JsonPropertyName("complete")] public bool? Complete { get; init; }
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

/// <summary>操舵模式狀態(state.schema.json <c>steering</c>):mode、使用中的舵機泵("1"|"2"|"both")、nfu 時舵是否正被推動(其他模式省略)。</summary>
public sealed record SteeringState
{
    [JsonPropertyName("mode")] public required SteeringMode Mode { get; init; }
    [JsonPropertyName("pump")] public required string Pump { get; init; }
    [JsonPropertyName("nfuActive")] public bool? NfuActive { get; init; }
}

public sealed record StateFlags
{
    [JsonPropertyName("frozen")] public bool Frozen { get; init; }
    [JsonPropertyName("aground")] public bool Aground { get; init; }
    [JsonPropertyName("collision")] public bool Collision { get; init; }
}
