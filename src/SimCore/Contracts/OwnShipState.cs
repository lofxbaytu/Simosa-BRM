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
    /// <summary>
    /// UTC 時刻(ISO 8601,毫秒,Z)= 練習開始時刻 + t;開始時刻為情境 startTimeUtc,未給時為主控程序載入情境時的實際時鐘
    /// (EngineOptions.Clock;引擎寫回情境,重播沿用紀錄標頭的情境);兩者皆無(單元測試)時省略。Python 參考實作亦輸出;不進雜湊,視景據此決定日夜。
    /// </summary>
    [JsonPropertyName("utc")] public string? Utc { get; init; }
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
    /// <summary>有效水深(m)= 基底水深(含 zones 覆寫與 setEnvironment 漸變)+ 潮高</summary>
    [JsonPropertyName("waterDepth")] public double? WaterDepth { get; init; }
    /// <summary>潮高(m;情境有 environment.tide 時輸出,由練習時間決定;無潮汐情境省略)</summary>
    [JsonPropertyName("tideHeight_m")] public double? TideHeightM { get; init; }
    [JsonPropertyName("squat")] public double? Squat { get; init; }
    [JsonPropertyName("wind")] public required WindState Wind { get; init; }
    [JsonPropertyName("current")] public required CurrentState Current { get; init; }
    /// <summary>流場來源("table" | "tidal";情境 environment.current 為時變流場且未被 setEnvironment 覆寫時輸出;常數流省略)</summary>
    [JsonPropertyName("currentSource")] public string? CurrentSource { get; init; }
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
    /// <summary>
    /// 真值(教官站/講評用;設計文件 sensors-alerts.md 第 2 節):heading/pos/cog/sog/stw/depthBelowKeel 頂層欄位為「指示值」(感測器故障時與真值不同),
    /// 此區塊為模擬的真實值與誤差。學員端儀器不應顯示。
    /// </summary>
    [JsonPropertyName("truth")] public TruthState? Truth { get; init; }
    /// <summary>感測器有效性與磁羅經(儀器端據以標示「無效/可疑」)</summary>
    [JsonPropertyName("sensors")] public SensorStatus? Sensors { get; init; }
    /// <summary>自動舵(啟用、設定航向、ROT 限制、是否可用)</summary>
    [JsonPropertyName("autopilot")] public AutopilotStatus? Autopilot { get; init; }
    /// <summary>警報清單(BAM;依優先序、成立時間;無警報時省略)</summary>
    [JsonPropertyName("alerts")] public IReadOnlyList<AlertStatus>? Alerts { get; init; }
    /// <summary>BNWAS(MSC.128(75))狀態</summary>
    [JsonPropertyName("bnwas")] public BnwasStatus? Bnwas { get; init; }
    /// <summary>拖船(情境無拖船時省略;設計文件 physics-tugs-anchors-mooring.md)</summary>
    [JsonPropertyName("tugs")] public IReadOnlyList<TugStatusState>? Tugs { get; init; }
    /// <summary>左右錨(particulars 無錨資料時省略)</summary>
    [JsonPropertyName("anchors")] public IReadOnlyList<AnchorStatusState>? Anchors { get; init; }
    /// <summary>泊位碰墊接觸與繫纜(情境無 berth 時省略)</summary>
    [JsonPropertyName("berth")] public BerthStatusState? Berth { get; init; }
    /// <summary>岸壁效應(情境無 banks 時省略;設計文件 physics-bank-effect.md 第 5 節)</summary>
    [JsonPropertyName("bank")] public Physics.External.BankEffectState? Bank { get; init; }
    /// <summary>浪與平均漂移力(情境無 environment.waves 時省略;設計文件 physics-waves.md 第 5 節;視景以 significantHeight_m 畫海面)</summary>
    [JsonPropertyName("waves")] public Physics.External.WaveDriftState? Waves { get; init; }
    /// <summary>船間交互作用(只在情境 environment.shipInteraction.enabled 且動力學接受外力時輸出;docs/design/physics-ship-interaction.md 第 5 節)</summary>
    [JsonPropertyName("shipInteraction")] public Physics.External.ShipInteractionState? ShipInteraction { get; init; }
    /// <summary>橫搖(解耦 1 自由度線性模型;只在情境 ship.roll.enabled 時輸出,無則省略且其他欄位不變;視景相機與傾斜儀據此顯示)</summary>
    [JsonPropertyName("roll")] public RollState? Roll { get; init; }
    /// <summary>雷達環境(情境 environment.radar 換算為 ENU;情境未給時省略;docs/design/radar-arpa.md 第 2.1 節)</summary>
    [JsonPropertyName("radarEnvironment")] public SimosaBRM.SimCore.Scenario.RadarEnvironmentState? RadarEnvironment { get; init; }
}

/// <summary>
/// 橫搖狀態(state.schema.json <c>roll</c>;情境 <c>ship.roll.enabled</c> 時輸出):角度右傾正(度)、角速度(度/秒)、
/// 自然週期(秒)、靜傾角(迴轉離心 + 風傾的等效靜傾,右傾正;動態 = angle_deg − staticHeel_deg)。
/// </summary>
public sealed record RollState
{
    /// <summary>橫搖角(度,右傾正)</summary>
    [JsonPropertyName("angle_deg")] public required double AngleDeg { get; init; }
    /// <summary>橫搖角速度(度/秒,右傾方向正)</summary>
    [JsonPropertyName("rate_degps")] public required double RateDegps { get; init; }
    /// <summary>自然橫搖週期(秒)</summary>
    [JsonPropertyName("naturalPeriod_s")] public required double NaturalPeriodS { get; init; }
    /// <summary>靜傾角(度,右傾正;迴轉與風傾的等效靜傾)</summary>
    [JsonPropertyName("staticHeel_deg")] public required double StaticHeelDeg { get; init; }
}

/// <summary>真值區塊(state.schema.json <c>truth</c>)。</summary>
public sealed record TruthState
{
    [JsonPropertyName("heading")] public required double Heading { get; init; }
    [JsonPropertyName("pos")] public required GeoPosition Pos { get; init; }
    [JsonPropertyName("cog")] public required double Cog { get; init; }
    [JsonPropertyName("sog")] public required double Sog { get; init; }
    [JsonPropertyName("stw")] public required double Stw { get; init; }
    [JsonPropertyName("depthBelowKeel")] public required double DepthBelowKeel { get; init; }
    /// <summary>指示航向 − 真航向(度,−180..180;右正)</summary>
    [JsonPropertyName("headingError_deg")] public required double HeadingErrorDeg { get; init; }
    /// <summary>指示位置與真位置的距離(m)</summary>
    [JsonPropertyName("positionError_m")] public required double PositionErrorM { get; init; }
}

/// <summary>感測器有效性(state.schema.json <c>sensors</c>):false 時對應的頂層欄位為最後有效值(保持顯示),NMEA 句型標示無效或停送。</summary>
public sealed record SensorStatus
{
    [JsonPropertyName("headingValid")] public required bool HeadingValid { get; init; }
    [JsonPropertyName("positionValid")] public required bool PositionValid { get; init; }
    [JsonPropertyName("depthValid")] public required bool DepthValid { get; init; }
    [JsonPropertyName("stwValid")] public required bool StwValid { get; init; }
    /// <summary>磁羅經指示(修正自差/磁差後的真航向,度;不受電羅經故障影響)</summary>
    [JsonPropertyName("magneticHeading")] public required double MagneticHeading { get; init; }
}

/// <summary>自動舵狀態(state.schema.json <c>autopilot</c>)。</summary>
public sealed record AutopilotStatus
{
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    /// <summary>設定航向(度,0–360)</summary>
    [JsonPropertyName("setHeading")] public required double SetHeading { get; init; }
    [JsonPropertyName("rotLimit_degPerMin")] public required double RotLimitDegPerMin { get; init; }
    /// <summary>可啟用(無 autopilotFailure 且指示航向有效)</summary>
    [JsonPropertyName("available")] public required bool Available { get; init; }
}

/// <summary>警報(state.schema.json <c>alerts[]</c>;MSC.302(87))。</summary>
public sealed record AlertStatus
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    /// <summary>NMEA ALR 警報編號</summary>
    [JsonPropertyName("number")] public required int Number { get; init; }
    [JsonPropertyName("priority")] public required Alerts.AlertPriority Priority { get; init; }
    [JsonPropertyName("category")] public required Alerts.AlertCategory Category { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("state")] public required Alerts.AlertState State { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("raisedAt_s")] public required double RaisedAtS { get; init; }
    [JsonPropertyName("acknowledgedAt_s")] public double? AcknowledgedAtS { get; init; }
    [JsonPropertyName("silencedUntil_s")] public double? SilencedUntilS { get; init; }
    [JsonPropertyName("escalated")] public required bool Escalated { get; init; }
    [JsonPropertyName("count")] public required int Count { get; init; }
    /// <summary>目前有聲響(activeUnacknowledged 且優先序為 emergencyAlarm/alarm/warning)</summary>
    [JsonPropertyName("audible")] public required bool Audible { get; init; }
    /// <summary>負責站(相關工作站;bridge|portWing|stbdWing|ecr,本版成立時皆為 bridge)</summary>
    [JsonPropertyName("station")] public string Station { get; init; } = Alerts.AlertStations.Bridge;
    /// <summary>責任已轉移到的站(MSC.302(87) responsibility transferred;未轉移時省略)</summary>
    [JsonPropertyName("transferredTo")] public string? TransferredTo { get; init; }
}

/// <summary>BNWAS 狀態(state.schema.json <c>bnwas</c>)。</summary>
public sealed record BnwasStatus
{
    [JsonPropertyName("mode")] public required Scenario.BnwasMode Mode { get; init; }
    /// <summary>作動中(on,或 auto 且自動舵啟用)</summary>
    [JsonPropertyName("active")] public required bool Active { get; init; }
    /// <summary>0 休眠、1 視覺、2 駕駛台聲響、3 後備值班官</summary>
    [JsonPropertyName("stage")] public required int Stage { get; init; }
    /// <summary>距下一階段的秒數(未作動或第三階段時省略)</summary>
    [JsonPropertyName("remaining_s")] public double? RemainingS { get; init; }
    [JsonPropertyName("dormant_s")] public required double DormantS { get; init; }
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
