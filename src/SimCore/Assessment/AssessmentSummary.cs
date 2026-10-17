using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Assessment;

/// <summary>
/// 評估摘要(紀錄 <c>summary</c> 行、WebSocket assessment 框;情境引擎設計第 4.8 節)。不可變;<c>advisory</c> 依 A-I/12
/// 標示自動評分僅輔助、最終由評估員判定(規劃書第 8.3 節)。輸出單位與欄位後綴一致(_nm、_m、_s、_kn、_deg、_degPerMin)。
/// </summary>
public sealed record AssessmentSummary
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("advisory")] public bool Advisory { get; init; } = true;
    [JsonPropertyName("final")] public required bool Final { get; init; }
    [JsonPropertyName("scenarioId")] public required string ScenarioId { get; init; }
    [JsonPropertyName("shipId")] public required string ShipId { get; init; }
    [JsonPropertyName("loading")] public required LoadingCondition Loading { get; init; }
    [JsonPropertyName("parameterSet")] public SummaryParameterSet? ParameterSet { get; init; }
    [JsonPropertyName("phases")] public required IReadOnlyList<SummaryPhase> Phases { get; init; }
    [JsonPropertyName("general")] public required SummaryGeneral General { get; init; }
    [JsonPropertyName("collisionAvoidance")] public required SummaryCollisionAvoidance CollisionAvoidance { get; init; }
    [JsonPropertyName("track")] public SummaryTrack? Track { get; init; }
    [JsonPropertyName("ukc")] public required SummaryUkc Ukc { get; init; }
    [JsonPropertyName("manoeuvring")] public required SummaryManoeuvring Manoeuvring { get; init; }
    [JsonPropertyName("faultResponses")] public required IReadOnlyList<FaultResponseRecord> FaultResponses { get; init; }
    [JsonPropertyName("events")] public required SummaryEvents Events { get; init; }
    [JsonPropertyName("assessment")] public required SummaryAssessment Assessment { get; init; }

    public string ToJson() => ContractJson.Serialize(this);
}

public sealed record SummaryParameterSet
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
}

public sealed record SummaryPhase
{
    [JsonPropertyName("phase")] public required string Phase { get; init; }
    [JsonPropertyName("from_s")] public required double FromS { get; init; }
    [JsonPropertyName("to_s")] public required double ToS { get; init; }
}

public sealed record SummaryGeneral
{
    [JsonPropertyName("duration_s")] public required double DurationS { get; init; }
    /// <summary>凍結次數(凍結期間模擬時間不前進,故不以秒計;情境引擎設計偏離備註)</summary>
    [JsonPropertyName("freezes")] public required int Freezes { get; init; }
    [JsonPropertyName("distance_nm")] public required double DistanceNm { get; init; }
    [JsonPropertyName("avgSog_kn")] public required double AvgSogKn { get; init; }
    [JsonPropertyName("maxSog_kn")] public required double MaxSogKn { get; init; }
}

public sealed record SummaryCollisionAvoidance
{
    /// <summary>非 escort 目標的最小實際通過距離(nm;尚無目標時 null)</summary>
    [JsonPropertyName("minCpa_nm")] public double? MinCpaNm { get; init; }
    [JsonPropertyName("encounters")] public required int Encounters { get; init; }
    [JsonPropertyName("collisions")] public required int Collisions { get; init; }
    [JsonPropertyName("targets")] public required IReadOnlyList<EncounterRecord> Targets { get; init; }
}

public sealed record SummaryTrack
{
    [JsonPropertyName("meanAbsXtd_m")] public required double MeanAbsXtdM { get; init; }
    [JsonPropertyName("maxXtd_m")] public required double MaxXtdM { get; init; }
    [JsonPropertyName("maxXtdAt_s")] public double? MaxXtdAtS { get; init; }
    [JsonPropertyName("maxXtdLeg")] public int? MaxXtdLeg { get; init; }
    [JsonPropertyName("timeOutsideCorridor_s")] public required double TimeOutsideCorridorS { get; init; }
    [JsonPropertyName("corridorExcursions")] public required int CorridorExcursions { get; init; }
    [JsonPropertyName("fractionOutsideCorridor")] public required double FractionOutsideCorridor { get; init; }
    [JsonPropertyName("minDistanceToCorridorEdge_m")] public double? MinDistanceToCorridorEdgeM { get; init; }
    [JsonPropertyName("minDistanceToNoGo_m")] public double? MinDistanceToNoGoM { get; init; }
    [JsonPropertyName("meanHeadingDeviation_deg")] public required double MeanHeadingDeviationDeg { get; init; }
    [JsonPropertyName("legIndex")] public required int LegIndex { get; init; }
    [JsonPropertyName("complete")] public required bool Complete { get; init; }
    [JsonPropertyName("offRoute")] public required bool OffRoute { get; init; }
    [JsonPropertyName("waypoints")] public required IReadOnlyList<PassedWaypoint> Waypoints { get; init; }
}

public sealed record PassedWaypoint
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("passedAt_s")] public required double PassedAtS { get; init; }
    [JsonPropertyName("xtd_m")] public required double XtdM { get; init; }
    [JsonPropertyName("distance_m")] public required double DistanceM { get; init; }
}

public sealed record SummaryUkc
{
    [JsonPropertyName("min_m")] public double? MinM { get; init; }
    [JsonPropertyName("minAt_s")] public double? MinAtS { get; init; }
    [JsonPropertyName("minSquat_m")] public double? MinSquatM { get; init; }
    [JsonPropertyName("timeBelowLimit_s")] public required double TimeBelowLimitS { get; init; }
    [JsonPropertyName("excursions")] public required int Excursions { get; init; }
    [JsonPropertyName("groundings")] public required int Groundings { get; init; }
    [JsonPropertyName("limit_m")] public double? LimitM { get; init; }
    [JsonPropertyName("limitSource")] public string? LimitSource { get; init; }
}

public sealed record SummaryManoeuvring
{
    [JsonPropertyName("rudderDistribution_s")] public required Dictionary<string, double> RudderDistributionS { get; init; }
    [JsonPropertyName("hardOverCount")] public required int HardOverCount { get; init; }
    [JsonPropertyName("hardOverTime_s")] public required double HardOverTimeS { get; init; }
    [JsonPropertyName("rudderOrders")] public required int RudderOrders { get; init; }
    [JsonPropertyName("maxRot_degPerMin")] public required double MaxRotDegPerMin { get; init; }
    [JsonPropertyName("maxRotAt_s")] public double? MaxRotAtS { get; init; }
    [JsonPropertyName("timeRotOverLimit_s")] public required double TimeRotOverLimitS { get; init; }
    [JsonPropertyName("rotExcursions")] public required int RotExcursions { get; init; }
    [JsonPropertyName("telegraphChanges")] public required int TelegraphChanges { get; init; }
    [JsonPropertyName("telegraphOrders")] public required IReadOnlyList<TelegraphSpan> TelegraphOrders { get; init; }
    [JsonPropertyName("rpmOrders")] public required int RpmOrders { get; init; }
    [JsonPropertyName("thrusterOrders")] public required int ThrusterOrders { get; init; }
    [JsonPropertyName("thrusterTime_s")] public required double ThrusterTimeS { get; init; }
    [JsonPropertyName("timeOverSpeedLimit_s")] public required double TimeOverSpeedLimitS { get; init; }
    [JsonPropertyName("maxOverSpeed_kn")] public required double MaxOverSpeedKn { get; init; }
    [JsonPropertyName("speedLimitSource")] public string? SpeedLimitSource { get; init; }
    [JsonPropertyName("autopilotTime_s")] public required double AutopilotTimeS { get; init; }
    [JsonPropertyName("autopilotSwitches")] public required int AutopilotSwitches { get; init; }
}

public sealed record TelegraphSpan
{
    [JsonPropertyName("order")] public required string Order { get; init; }
    [JsonPropertyName("from_s")] public required double FromS { get; init; }
    [JsonPropertyName("to_s")] public required double ToS { get; init; }
    [JsonPropertyName("duration_s")] public required double DurationS { get; init; }
}

public sealed record SummaryEvents
{
    [JsonPropertyName("total")] public required long Total { get; init; }
    [JsonPropertyName("byKind")] public required Dictionary<string, long> ByKind { get; init; }
}

public sealed record SummaryAssessment
{
    [JsonPropertyName("score")] public required double Score { get; init; }
    [JsonPropertyName("passMark")] public required double PassMark { get; init; }
    [JsonPropertyName("passed")] public required bool Passed { get; init; }
    [JsonPropertyName("hardFailed")] public required bool HardFailed { get; init; }
    [JsonPropertyName("hardFails")] public required IReadOnlyList<HardFailRecord> HardFails { get; init; }
    [JsonPropertyName("bands")] public required IReadOnlyList<BandResult> Bands { get; init; }
    [JsonPropertyName("required")] public required IReadOnlyList<RequiredResult> Required { get; init; }
    [JsonPropertyName("parameters")] public required IReadOnlyList<SummaryParameter> Parameters { get; init; }
}

public sealed record HardFailRecord
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("rule")] public required string Rule { get; init; }
    [JsonPropertyName("t_s")] public required double TS { get; init; }
    [JsonPropertyName("value")] public double? Value { get; init; }
    [JsonPropertyName("limit")] public double? Limit { get; init; }
    [JsonPropertyName("paramKey")] public string? ParamKey { get; init; }
}

public sealed record BandResult
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("metric")] public required string Metric { get; init; }
    [JsonPropertyName("value")] public double? Value { get; init; }
    [JsonPropertyName("warn")] public double? Warn { get; init; }
    [JsonPropertyName("fail")] public double? Fail { get; init; }
    /// <summary>ok | warn | fail</summary>
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("penalty")] public required double Penalty { get; init; }
    [JsonPropertyName("paramKey")] public string? ParamKey { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("revision")] public string? Revision { get; init; }
    [JsonPropertyName("clause")] public string? Clause { get; init; }
}

public sealed record RequiredResult
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    /// <summary>notApplicable | pending | met | late | missed</summary>
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("dt_s")] public double? DtS { get; init; }
    [JsonPropertyName("within_s")] public required double WithinS { get; init; }
    [JsonPropertyName("by")] public string? By { get; init; }
    [JsonPropertyName("penalty")] public required double Penalty { get; init; }
    [JsonPropertyName("paramKey")] public string? ParamKey { get; init; }
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("revision")] public string? Revision { get; init; }
    [JsonPropertyName("clause")] public string? Clause { get; init; }
}

public sealed record SummaryParameter
{
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("value")] public required System.Text.Json.JsonElement Value { get; init; }
    [JsonPropertyName("unit")] public required string Unit { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("revision")] public string? Revision { get; init; }
    [JsonPropertyName("clause")] public string? Clause { get; init; }
}

/// <summary>每目標的會遇紀錄(第 4.4 節;summary 輸出)。</summary>
public sealed record EncounterRecord
{
    [JsonPropertyName("targetId")] public required string TargetId { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("situation")] public string? Situation { get; init; }
    [JsonPropertyName("detectedAt_s")] public double? DetectedAtS { get; init; }
    [JsonPropertyName("firstActionAt_s")] public double? FirstActionAtS { get; init; }
    [JsonPropertyName("timeToAction_s")] public double? TimeToActionS { get; init; }
    [JsonPropertyName("firstAction")] public string? FirstAction { get; init; }
    [JsonPropertyName("minCpa_nm")] public double? MinCpaNm { get; init; }
    [JsonPropertyName("minCpaAt_s")] public double? MinCpaAtS { get; init; }
    [JsonPropertyName("tcpaAtDetection_min")] public double? TcpaAtDetectionMin { get; init; }
    [JsonPropertyName("minRange_nm")] public double? MinRangeNm { get; init; }
    [JsonPropertyName("bcrAtDetection_nm")] public double? BcrAtDetectionNm { get; init; }
    [JsonPropertyName("bcrMin_nm")] public double? BcrMinNm { get; init; }
    [JsonPropertyName("courseChange_deg")] public double CourseChangeDeg { get; init; }
    [JsonPropertyName("speedChange_kn")] public double SpeedChangeKn { get; init; }
    [JsonPropertyName("substantial")] public bool Substantial { get; init; }
    [JsonPropertyName("cpaAlarmTime_s")] public double CpaAlarmTimeS { get; init; }
    [JsonPropertyName("closedAt_s")] public double? ClosedAtS { get; init; }
    [JsonPropertyName("escort")] public bool Escort { get; init; }
}

/// <summary>會遇追蹤器的可變狀態(進快照;含不輸出的內部欄位)。</summary>
public sealed class EncounterTrack
{
    public string TargetId { get; set; } = "";
    public string? Name { get; set; }
    public string? Situation { get; set; }
    public double? DetectedAtS { get; set; }
    public double? FirstActionAtS { get; set; }
    public double? TimeToActionS { get; set; }
    public string? FirstAction { get; set; }
    public double? MinCpaNm { get; set; }
    public double? MinCpaAtS { get; set; }
    public double? TcpaAtDetectionMin { get; set; }
    public double? MinRangeNm { get; set; }
    public double? BcrAtDetectionNm { get; set; }
    public double? BcrMinNm { get; set; }
    public double CourseChangeDeg { get; set; }
    public double SpeedChangeKn { get; set; }
    public bool Substantial { get; set; }
    public double CpaAlarmTimeS { get; set; }
    public double? ClosedAtS { get; set; }
    public bool Escort { get; set; }
    public double Heading0Rad { get; set; }
    public double Stw0Kn { get; set; }
    public double? LastRangeNm { get; set; }
    public int OpeningSeconds { get; set; }
    [JsonIgnore] public bool Closed => ClosedAtS is not null;

    public EncounterRecord ToRecord() => new()
    {
        TargetId = TargetId, Name = Name, Situation = Situation, DetectedAtS = DetectedAtS, FirstActionAtS = FirstActionAtS, TimeToActionS = TimeToActionS,
        FirstAction = FirstAction, MinCpaNm = MinCpaNm, MinCpaAtS = MinCpaAtS, TcpaAtDetectionMin = TcpaAtDetectionMin, MinRangeNm = MinRangeNm,
        BcrAtDetectionNm = BcrAtDetectionNm, BcrMinNm = BcrMinNm, CourseChangeDeg = CourseChangeDeg, SpeedChangeKn = SpeedChangeKn,
        Substantial = Substantial, CpaAlarmTimeS = CpaAlarmTimeS, ClosedAtS = ClosedAtS, Escort = Escort,
    };
}

/// <summary>故障注入 → 應變時間戳(第 4.6 節);也是追蹤器的可變狀態。</summary>
public sealed class FaultResponseRecord
{
    [JsonPropertyName("fault")] public string Fault { get; set; } = "";
    [JsonPropertyName("itemId")] public string? ItemId { get; set; }
    [JsonPropertyName("injectedAt_s")] public double InjectedAtS { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("firstAction")] public FaultAction? FirstAction { get; set; }
    [JsonPropertyName("acknowledgements")] public List<FaultAcknowledgement> Acknowledgements { get; set; } = new();
    [JsonPropertyName("cleared_s")] public double? ClearedS { get; set; }
    [JsonPropertyName("withinFirstAction")] public bool? WithinFirstAction { get; set; }
    [JsonPropertyName("withinNotifyEcr")] public bool? WithinNotifyEcr { get; set; }
}

public sealed record FaultAction
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("t_s")] public required double TS { get; init; }
    [JsonPropertyName("dt_s")] public required double DtS { get; init; }
}

public sealed record FaultAcknowledgement
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("t_s")] public required double TS { get; init; }
    [JsonPropertyName("dt_s")] public required double DtS { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
}

/// <summary>學員指令的觀察(由引擎在套用指令時產生;供會遇首次行動、必要事件、故障應變與操縱指標)。</summary>
public sealed record ObservedAction(string Kind, long Tick, double Magnitude);

/// <summary>可偵測動作的名稱(與 ExpectSpec 簡單名稱相同)。</summary>
public static class ActionKinds
{
    public const string RudderOrder = "rudderOrder";
    public const string TelegraphChange = "telegraphChange";
    public const string RpmOrder = "rpmOrder";
    public const string ThrusterOrder = "thrusterOrder";
    public const string AutopilotOn = "autopilotOn";
    public const string AutopilotOff = "autopilotOff";
    public const string AutopilotHeading = "autopilotHeading";
    public const string SteeringModeChange = "steeringModeChange";
}
