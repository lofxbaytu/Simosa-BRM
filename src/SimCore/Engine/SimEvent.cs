using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Engine;

/// <summary>
/// 核心事件(紀錄 <c>event</c> 行、WebSocket <c>event</c> 框;規劃書第 5.3 節衍生事件、情境引擎設計第 3.2 節)。
/// JSON 欄位名與舊 <see cref="Traffic.TrafficEvent"/> 相同,新欄位(source/itemId/phase/visible/seq/data)選填;讀回時一律反序列化為 SimEvent。
/// </summary>
public record SimEvent
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("tick")] public required long Tick { get; init; }
    /// <summary>模擬時間(秒)</summary>
    [JsonPropertyName("t")] public required double T { get; init; }
    /// <summary>來源:traffic | timeline | assessment | instructor | engine</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = SimEventSources.Engine;
    /// <summary>時間表項目 / 評估規則 id</summary>
    [JsonPropertyName("itemId")] public string? ItemId { get; init; }
    [JsonPropertyName("targetId")] public string? TargetId { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("range_nm")] public double? RangeNm { get; init; }
    [JsonPropertyName("cpa_nm")] public double? CpaNm { get; init; }
    [JsonPropertyName("tcpa_min")] public double? TcpaMin { get; init; }
    /// <summary>事件發生時的情境階段(引擎發出時填入)</summary>
    [JsonPropertyName("phase")] public string? Phase { get; init; }
    /// <summary>學員站可顯示</summary>
    [JsonPropertyName("visible")] public bool Visible { get; init; }
    /// <summary>自情境開始的事件序號(重播比對用;引擎發出時填入)</summary>
    [JsonPropertyName("seq")] public long Seq { get; init; }
    /// <summary>動作/規則的附加資料(voiceScript 欄位、漸變參數、指標值…)</summary>
    [JsonPropertyName("data")] public JsonElement? Data { get; init; }

    /// <summary>data 物件中的字串欄位(無則 null)。</summary>
    public string? DataString(string name)
        => Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>data 物件中的數值欄位(無則 null)。</summary>
    public double? DataNumber(string name)
        => Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    /// <summary>data 物件中的布林欄位(無則 null)。</summary>
    public bool? DataBool(string name)
        => Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var e)
            ? e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    /// <summary>由匿名物件建立 data。</summary>
    public static JsonElement DataOf(object o) => JsonSerializer.SerializeToElement(o, Contracts.ContractJson.Options);
}

/// <summary>事件來源常數。</summary>
public static class SimEventSources
{
    public const string Engine = "engine";
    public const string Traffic = "traffic";
    public const string Timeline = "timeline";
    public const string Assessment = "assessment";
    public const string Instructor = "instructor";
}

/// <summary>新事件種類(交通事件種類見 <see cref="Traffic.TrafficEventKinds"/>,全部保留)。</summary>
public static class SimEventKinds
{
    // timeline
    public const string TimelineTriggered = "timelineTriggered";
    public const string TimelineArmed = "timelineArmed";
    public const string TimelineDisarmed = "timelineDisarmed";
    public const string FaultInjected = "faultInjected";
    public const string FaultCleared = "faultCleared";
    public const string EnvironmentChange = "environmentChange";
    public const string EnvironmentRampDone = "environmentRampDone";
    public const string VoiceScript = "voiceScript";
    public const string Message = "message";
    public const string PhaseChanged = "phaseChanged";
    public const string Frozen = "frozen";
    public const string Resumed = "resumed";
    public const string AutopilotOverride = "autopilotOverride";
    public const string OwnShipOverride = "ownShipOverride";
    public const string TimeScaleChanged = "timeScaleChanged";
    public const string ExerciseEnded = "exerciseEnded";
    /// <summary>操舵模式變更(指令 steeringMode、autopilot 或 rudder 解除自動舵/NFU;data {from, to, pump})</summary>
    public const string SteeringModeChanged = "steeringModeChanged";
    /// <summary>指令被拒絕、狀態不變(source 同指令來源):data {command, reason, args};目前只有 emergency 模式下的自動舵啟用(D35)</summary>
    public const string CommandRejected = "commandRejected";
    // engine
    public const string Grounding = "grounding";
    /// <summary>自動舵因故障/航向無效自動脫開(data {reason: autopilotFailure|headingInvalid, fault});模式回 fu、舵停在原處</summary>
    public const string AutopilotTripped = "autopilotTripped";
    // 警報管理(BAM,MSC.302(87);itemId = 警報 id;data {id, priority, category, source, state, count, raisedAt_s, ackTime_s?, silencedUntil_s?, …})
    /// <summary>警報成立(data.repeat:rectifiedUnacknowledged 期間再成立)</summary>
    public const string AlertRaised = "alertRaised";
    /// <summary>警報確認(data.ackTime_s = 確認 − 最近一次成立;rectifiedUnacknowledged 確認後 state = normal)</summary>
    public const string AlertAcknowledged = "alertAcknowledged";
    /// <summary>警報靜音(data.silencedUntil_s)</summary>
    public const string AlertSilenced = "alertSilenced";
    /// <summary>靜音期滿仍未確認,重新響起</summary>
    public const string AlertReannunciated = "alertReannunciated";
    /// <summary>警報條件解除(data.state = normal 或 rectifiedUnacknowledged)</summary>
    public const string AlertRectified = "alertRectified";
    /// <summary>警報升級(data {from, to, reason: unacknowledged|source, after_s?})</summary>
    public const string AlertEscalated = "alertEscalated";
    // BNWAS(MSC.128(75))
    /// <summary>BNWAS 階段變更(data {stage, from, reason: dormantElapsed|notReset|inactive})</summary>
    public const string BnwasStage = "bnwasStage";
    /// <summary>BNWAS 重設(data {fromStage, by:指令名})</summary>
    public const string BnwasReset = "bnwasReset";
    /// <summary>BNWAS 模式變更(data {from, to, fromStage, dormant_s, stage2_s, stage3_s})</summary>
    public const string BnwasModeChanged = "bnwasModeChanged";
    // assessment
    public const string RouteLegChanged = "routeLegChanged";
    public const string RouteCompleted = "routeCompleted";
    public const string CheckpointPassed = "checkpointPassed";
    public const string ReportPointPassed = "reportPointPassed";
    public const string XtdExceeded = "xtdExceeded";
    public const string XtdRestored = "xtdRestored";
    public const string ZoneEntered = "zoneEntered";
    public const string ZoneExited = "zoneExited";
    public const string UkcAlarm = "ukcAlarm";
    public const string UkcClear = "ukcClear";
    public const string SpeedLimitExceeded = "speedLimitExceeded";
    public const string SpeedLimitRestored = "speedLimitRestored";
    public const string EncounterStarted = "encounterStarted";
    public const string EncounterFirstAction = "encounterFirstAction";
    public const string EncounterClosed = "encounterClosed";
    /// <summary>必要事件開始計時(after 事件發生;data {after, afterItemId, afterSeq, start_t, within_s, deadline_t, penalty})</summary>
    public const string RequiredEventPending = "requiredEventPending";
    public const string RequiredEventMet = "requiredEventMet";
    public const string RequiredEventMissed = "requiredEventMissed";
    public const string HardFail = "hardFail";
    public const string BandWarn = "bandWarn";
    public const string BandFail = "bandFail";
    // instructor
    public const string Acknowledge = "acknowledge";
    public const string TimelineManual = "timelineManual";
    // 外力:拖船、錨、碰墊、繫纜(設計文件 docs/design/physics-tugs-anchors-mooring.md 第 6 節;itemId = 拖船 id / 錨側 / 碰墊 id / 纜 id)
    /// <summary>拖船命令被接受(data {id, mode, from, status, direction_deg, power_pct};source 同指令)</summary>
    public const string TugCommand = "tugCommand";
    /// <summary>拖纜斷裂(data {id, force_t, mode, reason: fault, stillPushing})</summary>
    public const string TugLineParted = "tugLineParted";
    /// <summary>拋錨(letGo 指令;data {side, action, targetLength_m, targetLength_shackles, length_m, status})</summary>
    public const string AnchorLetGo = "anchorLetGo";
    /// <summary>其他錨指令 veer/heave/brake/release(data 同上)</summary>
    public const string AnchorCommand = "anchorCommand";
    /// <summary>錨已帶上:letGo 後剎車握持、錨抓住、對地速度 &lt; 0.15 m/s(data {side, length_m, tension_t})</summary>
    public const string AnchorBroughtUp = "anchorBroughtUp";
    /// <summary>走錨開始(水平張力 &gt; 抓地力;data {side, tension_t, holding_t, length_m})</summary>
    public const string AnchorDragging = "anchorDragging";
    /// <summary>走錨停止(水平張力 ≤ 0.95 × 抓地力)</summary>
    public const string AnchorHolding = "anchorHolding";
    /// <summary>錨離底(起錨時鏈長 ≤ 錨鏈孔至海床高度)</summary>
    public const string AnchorAweigh = "anchorAweigh";
    /// <summary>錨收妥(鏈長 0)</summary>
    public const string AnchorHoused = "anchorHoused";
    /// <summary>錨鏈全部送出(到鏈端;data {side, length_m, shackles})</summary>
    public const string AnchorChainFullyOut = "anchorChainFullyOut";
    /// <summary>錨鏈孔張力超過剎車握持力,鏈被拉出(data {side, tension_t, brakeHolding_t, length_m})</summary>
    public const string AnchorBrakeSlipping = "anchorBrakeSlipping";
    /// <summary>起錨時錨鏈孔張力超過絞盤過載拉力,停轉(data {side, tension_t, windlassLimit_t})</summary>
    public const string AnchorWindlassStalled = "anchorWindlassStalled";
    /// <summary>碰墊接觸峰值(壓縮由增轉減時,或接觸結束;data {fenderId, normalSpeed_mps, energy_kNm, maxReaction_kN, maxDeflection_m, deflection_pct, ratedEnergy_kNm, ratedReaction_kN, overload})</summary>
    public const string FenderContact = "fenderContact";
    /// <summary>船體與碼頭面硬接觸開始(data {berthId, penetration_m, normalSpeed_mps})</summary>
    public const string QuayContact = "quayContact";
    /// <summary>繫纜指令被接受(data {lineId, action, status, bollardId, tension_t, length_m, restLength_m})</summary>
    public const string MooringCommand = "mooringCommand";
    /// <summary>繫纜斷裂(張力 &gt; MBL;data {lineId, tension_t, mbl_t, bollardId})</summary>
    public const string MooringLineParted = "mooringLineParted";
}

/// <summary><see cref="SimEventKinds.CommandRejected"/> 的 data.reason 值。</summary>
public static class CommandRejectReasons
{
    /// <summary>應急操舵中不能啟用自動舵(需先 steeringMode fu;D35)</summary>
    public const string EmergencySteering = "emergencySteering";
    /// <summary>自動舵不可用(autopilotFailure 故障中,或電羅經失效使指示航向無效)</summary>
    public const string AutopilotUnavailable = "autopilotUnavailable";
    /// <summary>alertAcknowledge / alertSilence 的 id 不在警報目錄</summary>
    public const string UnknownAlert = "unknownAlert";
    // 外力指令(tugControl / anchor / mooring;設計文件 physics-tugs-anchors-mooring.md 第 6.2 節)
    /// <summary>動力學模型不支援外力(IShipDynamics.SupportsExternalForces = false)</summary>
    public const string ExternalForcesUnsupported = "externalForcesUnsupported";
    public const string UnknownTug = "unknownTug";
    public const string UnknownTugMode = "unknownTugMode";
    /// <summary>拖船不具此模式(情境 tugs[].modes)</summary>
    public const string TugModeUnavailable = "tugModeUnavailable";
    /// <summary>拖纜已斷(tugLineBreak 未清除),不能拉</summary>
    public const string TugLineParted = "tugLineParted";
    public const string InvalidPower = "invalidPower";
    public const string NoAnchorData = "noAnchorData";
    public const string UnknownAnchor = "unknownAnchor";
    public const string AnchorAlreadyOut = "anchorAlreadyOut";
    public const string AnchorStowed = "anchorStowed";
    public const string ChainFullyOut = "chainFullyOut";
    public const string InvalidTarget = "invalidTarget";
    public const string UnknownAction = "unknownAction";
    public const string NoBerth = "noBerth";
    public const string UnknownMooringLine = "unknownMooringLine";
    public const string UnknownBollard = "unknownBollard";
    public const string BollardOutOfReach = "bollardOutOfReach";
    public const string MooringLineNotFast = "mooringLineNotFast";
    public const string MooringLineParted = "mooringLineParted";
}
