// 核心事件型別,對應設計文件 docs/design/scenario-engine.md 第 3.2 節的 SimEvent(紀錄 event 行與 WebSocket
// {"type":"event","event":{…}} 推送框)。舊的 TrafficEvent 欄位(kind/tick/t/targetId/message/range_nm/cpa_nm/tcpa_min)
// 不變,新欄位(source/itemId/phase/visible/seq/data)皆選填。

/** 事件來源:traffic(目標船)| timeline(時間表)| assessment(評估)| instructor(教官指令)| engine(引擎)。 */
export type SimEventSource = 'traffic' | 'timeline' | 'assessment' | 'instructor' | 'engine';

export const SIM_EVENT_SOURCES: readonly SimEventSource[] = ['traffic', 'timeline', 'assessment', 'instructor', 'engine'] as const;

/** 既有交通事件 kind(src/SimCore/Traffic/TrafficEvent.cs)。 */
export type TrafficEventKind = 'collision' | 'cpaAlarm' | 'cpaClear' | 'targetActivated' | 'waypointReached' | 'scriptStep' | 'colreg' | 'soundSignal' | 'targetControl';

/** 時間表/教官/引擎事件 kind(設計文件第 3.2 節)。 */
export type TimelineEventKind =
  | 'timelineTriggered'
  | 'timelineArmed'
  | 'timelineDisarmed'
  | 'faultInjected'
  | 'faultCleared'
  | 'environmentChange'
  | 'environmentRampDone'
  | 'voiceScript'
  | 'message'
  | 'phaseChanged'
  | 'frozen'
  | 'resumed'
  | 'autopilotOverride'
  | 'ownShipOverride'
  | 'timeScaleChanged'
  | 'exerciseEnded'
  | 'grounding';

/** 評估事件 kind。 */
export type AssessmentEventKind =
  | 'routeLegChanged'
  | 'routeCompleted'
  | 'checkpointPassed'
  | 'reportPointPassed'
  | 'xtdExceeded'
  | 'xtdRestored'
  | 'zoneEntered'
  | 'zoneExited'
  | 'ukcAlarm'
  | 'ukcClear'
  | 'speedLimitExceeded'
  | 'speedLimitRestored'
  | 'encounterStarted'
  | 'encounterFirstAction'
  | 'encounterClosed'
  | 'requiredEventMet'
  | 'requiredEventMissed'
  | 'hardFail'
  | 'bandWarn'
  | 'bandFail';

/** 教官指令產生的事件 kind;steeringModeChanged / commandRejected 來源依指令來源(instructor 或 timeline,D32/D35)。
 *  scenarioLoadRejected:loadScenario(檔案或 args.scenario 內嵌)載入/驗證失敗、核心狀態不變;data {scenarioId, origin, path?, issueCount, issues[]{path, message, severity}};
 *  帶外事件(seq -1、不進紀錄與雜湊),只由 WebSocket 事件框送教官站。 */
export type InstructorEventKind = 'acknowledge' | 'timelineManual' | 'steeringModeChanged' | 'commandRejected' | 'scenarioLoadRejected' | 'radarControl';

/** 警報管理/BNWAS/自動舵脫開事件 kind(docs/design/sensors-alerts.md 第 3.4、4 節;bnwasReset 的來源依指令來源,其餘 engine)。 */
export type AlertEventKind =
  | 'alertRaised'
  | 'alertSilenced'
  | 'alertReannunciated'
  | 'alertAcknowledged'
  | 'alertRectified'
  | 'alertEscalated'
  | 'alertTransferred'
  | 'bnwasStage'
  | 'bnwasReset'
  | 'bnwasModeChanged'
  | 'autopilotTripped';

/** 拖船、錨、泊位事件 kind(docs/design/physics-tugs-anchors-mooring.md 第 6.2 節):tugCommand / anchorLetGo / anchorCommand /
 *  mooringCommand 的來源依指令來源,其餘 engine。 */
export type ExternalForceEventKind =
  | 'tugCommand'
  | 'tugLineParted'
  | 'anchorLetGo'
  | 'anchorCommand'
  | 'anchorBroughtUp'
  | 'anchorDragging'
  | 'anchorHolding'
  | 'anchorAweigh'
  | 'anchorHoused'
  | 'anchorChainFullyOut'
  | 'anchorBrakeSlipping'
  | 'anchorWindlassStalled'
  | 'fenderContact'
  | 'quayContact'
  | 'mooringCommand'
  | 'mooringLineParted';

export type SimEventKind = TrafficEventKind | TimelineEventKind | AssessmentEventKind | InstructorEventKind | AlertEventKind | ExternalForceEventKind;

export const SIM_EVENT_KINDS: readonly SimEventKind[] = [
  'collision',
  'cpaAlarm',
  'cpaClear',
  'targetActivated',
  'waypointReached',
  'scriptStep',
  'colreg',
  'soundSignal',
  'targetControl',
  'timelineTriggered',
  'timelineArmed',
  'timelineDisarmed',
  'faultInjected',
  'faultCleared',
  'environmentChange',
  'environmentRampDone',
  'voiceScript',
  'message',
  'phaseChanged',
  'frozen',
  'resumed',
  'autopilotOverride',
  'ownShipOverride',
  'timeScaleChanged',
  'exerciseEnded',
  'grounding',
  'routeLegChanged',
  'routeCompleted',
  'checkpointPassed',
  'reportPointPassed',
  'xtdExceeded',
  'xtdRestored',
  'zoneEntered',
  'zoneExited',
  'ukcAlarm',
  'ukcClear',
  'speedLimitExceeded',
  'speedLimitRestored',
  'encounterStarted',
  'encounterFirstAction',
  'encounterClosed',
  'requiredEventMet',
  'requiredEventMissed',
  'hardFail',
  'bandWarn',
  'bandFail',
  'acknowledge',
  'timelineManual',
  'steeringModeChanged',
  'commandRejected',
  'scenarioLoadRejected',
  'alertRaised',
  'alertSilenced',
  'alertReannunciated',
  'alertAcknowledged',
  'alertRectified',
  'alertEscalated',
  'alertTransferred',
  'bnwasStage',
  'bnwasReset',
  'bnwasModeChanged',
  'autopilotTripped',
  'radarControl',
  'tugCommand',
  'tugLineParted',
  'anchorLetGo',
  'anchorCommand',
  'anchorBroughtUp',
  'anchorDragging',
  'anchorHolding',
  'anchorAweigh',
  'anchorHoused',
  'anchorChainFullyOut',
  'anchorBrakeSlipping',
  'anchorWindlassStalled',
  'fenderContact',
  'quayContact',
  'mooringCommand',
  'mooringLineParted',
] as const;

export interface SimEvent {
  /** 事件種類;未列在 SimEventKind 的字串也保留(核心日後新增的 kind 不致讓教官站失效) */
  kind: SimEventKind | (string & {});
  tick: number;
  /** 模擬時間(秒) */
  t: number;
  source: SimEventSource;
  /** 時間表項目 / 評估規則 id */
  itemId?: string;
  targetId?: string;
  message?: string;
  range_nm?: number;
  cpa_nm?: number;
  tcpa_min?: number;
  /** 事件發生時的情境階段 */
  phase?: string;
  /** 學員站可顯示 */
  visible: boolean;
  /** 自情境開始的事件序號(重播比對用);紀錄或框未給時為 -1 */
  seq: number;
  /** 動作/規則的附加資料(voiceScript 欄位、漸變參數、指標值…) */
  data?: Record<string, unknown>;
}

/** 語音腳本事件的 data(設計文件第 1.1.2 節 voiceScript)。 */
export interface VoiceScriptData {
  role: string;
  channel: string;
  text?: string;
  file?: string;
  lang?: string;
  durationEstimate_s?: number;
}

/** 教官確認種類(command.schema.json acknowledge.args.kind)。 */
export type AcknowledgeKind =
  | 'verbal'
  | 'notifyEcr'
  | 'callMaster'
  | 'report'
  | 'checklist'
  | 'steeringMode'
  | 'anchorReady'
  | 'gyroCheck'
  | 'prediction'
  | 'driftCalculation'
  | 'anchorDecision'
  | 'notifyCompany'
  | 'notifySar'
  | 'lookoutReport'
  | 'other';

/** gyroCheck:羅經交叉核對發現異常(E08;sensors-alerts.md 第 8 節)。 */
/** 與 scenario.schema.json expectSpec.acknowledge.kind 列舉一致;不在清單內的種類送出時會被改成 other,情境的必要事件就無法達成。 */
export const ACKNOWLEDGE_KINDS: readonly AcknowledgeKind[] = [
  'verbal',
  'notifyEcr',
  'callMaster',
  'report',
  'checklist',
  'steeringMode',
  'anchorReady',
  'gyroCheck',
  'prediction',
  'driftCalculation',
  'anchorDecision',
  'notifyCompany',
  'notifySar',
  'lookoutReport',
  'other',
] as const;
