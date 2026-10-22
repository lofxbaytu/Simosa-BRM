// 感測器真值/指示值、警報管理(BAM,MSC.302(87))與 BNWAS(MSC.128(75))的狀態型別,
// 對應 src/Contracts/state.schema.json 的 truth / sensors / autopilot / alerts[] / bnwas(docs/design/sensors-alerts.md 第 2–4 節)。
// 這些欄位皆由 C# SimCore 產生,Python 參考伺服器與舊紀錄沒有;解析不到時為 undefined(alerts 為空陣列)。

import type { Position } from './state.js';

/** 警報優先序(高 → 低)。 */
export type AlertPriority = 'emergencyAlarm' | 'alarm' | 'warning' | 'caution';

export const ALERT_PRIORITIES: readonly AlertPriority[] = ['emergencyAlarm', 'alarm', 'warning', 'caution'] as const;

/** 警報類別:A = 需在相關工作站處理、B = 可由中央警報面板確認、C = 只能在來源處理(本版未使用)。 */
export type AlertCategory = 'A' | 'B' | 'C';

export const ALERT_CATEGORIES: readonly AlertCategory[] = ['A', 'B', 'C'] as const;

/** 工作站(MSC.302(87) 第 6 節相關工作站與責任轉移;docs/design/sensors-alerts.md 第 3.6 節):駕駛台主站(預設)、左/右翼、機控室。 */
export type AlertStation = 'bridge' | 'portWing' | 'stbdWing' | 'ecr';

export const ALERT_STATIONS: readonly AlertStation[] = ['bridge', 'portWing', 'stbdWing', 'ecr'] as const;

/** 警報狀態(MSC.302 狀態機;normal 的警報不出現在清單)。 */
export type AlertState = 'activeUnacknowledged' | 'activeSilenced' | 'activeAcknowledged' | 'rectifiedUnacknowledged';

export const ALERT_STATES: readonly AlertState[] = ['activeUnacknowledged', 'activeSilenced', 'activeAcknowledged', 'rectifiedUnacknowledged'] as const;

/** state.alerts[] 的一筆。 */
export interface AlertItem {
  /** 警報 id:故障類 = 故障名稱、門檻類 shallowWater/offHeading/headingMonitor、bnwas */
  id: string;
  /** NMEA ALR 警報編號 */
  number: number;
  priority: AlertPriority;
  category: AlertCategory;
  /** 來源設備:gyro | gnss | echoSounder | log | autopilot | steering | engine | thruster | bnwas */
  source: string;
  state: AlertState;
  /** 顯示文字(繁體中文) */
  text: string;
  /** 最近一次成立的模擬時間 (s) */
  raisedAt_s: number;
  acknowledgedAt_s?: number;
  /** 靜音結束時間(activeSilenced 時) */
  silencedUntil_s?: number;
  /** 曾升級(warning 未確認逾時 → alarm;BNWAS 第二階段 caution → alarm) */
  escalated: boolean;
  /** 成立次數 */
  count: number;
  /** 目前有聲響(activeUnacknowledged 且優先序為 emergencyAlarm/alarm/warning) */
  audible: boolean;
  /** 負責站(相關工作站;核心本版成立時皆為 bridge;舊核心無此欄位時 undefined = bridge) */
  station?: AlertStation;
  /** 責任已轉移到的站(MSC.302(87) responsibility transferred;未轉移時無) */
  transferredTo?: AlertStation;
}

/** BNWAS 模式:off(預設)、auto(自動舵啟用時作動)、on。 */
export type BnwasMode = 'off' | 'auto' | 'on';

export const BNWAS_MODES: readonly BnwasMode[] = ['off', 'auto', 'on'] as const;

/** BNWAS 階段:0 休眠、1 駕駛台視覺、2 駕駛台聲響、3 後備值班官/船長。 */
export type BnwasStage = 0 | 1 | 2 | 3;

export interface BnwasStatus {
  mode: BnwasMode;
  /** 作動中(on,或 auto 且自動舵啟用) */
  active: boolean;
  stage: BnwasStage;
  /** 距下一階段秒數(階段 0 為距第 1 階段;未作動或第 3 階段省略) */
  remaining_s?: number;
  /** 休眠期 (s),180–720 */
  dormant_s: number;
}

/** 真值(教官站/講評用;學員端儀器不顯示)。 */
export interface TruthState {
  heading: number;
  pos: Position;
  cog: number;
  /** kn */
  sog: number;
  /** kn */
  stw: number;
  /** m */
  depthBelowKeel: number;
  /** 指示航向 − 真航向(度,−180..180,右正) */
  headingError_deg: number;
  /** 指示位置與真位置距離 (m) */
  positionError_m: number;
}

/** 感測器有效性與磁羅經。 */
export interface SensorStatus {
  headingValid: boolean;
  positionValid: boolean;
  depthValid: boolean;
  stwValid: boolean;
  /** 磁羅經指示(度,真航向 + 剩餘誤差;不受電羅經故障影響) */
  magneticHeading: number;
}

/** 核心的自動舵狀態(state.autopilot)。 */
export interface AutopilotStatus {
  enabled: boolean;
  /** 設定航向(度) */
  setHeading: number;
  rotLimit_degPerMin: number;
  /** 可啟用(無 autopilotFailure 且電羅經有效) */
  available: boolean;
}
