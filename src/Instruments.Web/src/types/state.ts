// 自船狀態型別,對應 src/Contracts/state.schema.json(OwnShipState)。
// 慣例:角度以度、航向 0–360、舵角右正左負、ROT 右轉正(度/分)、轉速倒車負。

import type { TargetState } from './target.js';
import type { AlertItem, AutopilotStatus, BnwasStatus, SensorStatus, TruthState } from './alerts.js';
import type { AnchorState, BerthState, TugState } from './external.js';

export type ShipId = 'FSB1' | 'FSB2';

export const SHIP_IDS: readonly ShipId[] = ['FSB1', 'FSB2'] as const;

/** 車鐘 11 段,由全速倒車(EFAS)排到海上全速(NAVF)。 */
export type TelegraphPosition =
  | 'EFAS'
  | 'FAS'
  | 'HAS'
  | 'SAS'
  | 'DSAS'
  | 'STOP'
  | 'DSAH'
  | 'SAH'
  | 'HAH'
  | 'FAH'
  | 'NAVF';

export const TELEGRAPH_POSITIONS: readonly TelegraphPosition[] = [
  'EFAS',
  'FAS',
  'HAS',
  'SAS',
  'DSAS',
  'STOP',
  'DSAH',
  'SAH',
  'HAH',
  'FAH',
  'NAVF',
] as const;

export type LoadingCondition = 'full' | 'ballast' | 'intermediate';

export type EngineRunState = 'stopped' | 'running' | 'starting' | 'failed';

export interface Position {
  lat: number;
  lon: number;
  /** 本地 ENU 東向 (m) */
  x: number;
  /** 本地 ENU 北向 (m) */
  y: number;
}

export interface Wind {
  /** 真風速 (kn) */
  trueSpeed: number;
  /** 真風向,來向 (度) */
  trueDir: number;
  relSpeed?: number;
  relDir?: number;
}

export interface Current {
  /** 流向(去向,度) */
  set: number;
  /** 流速 (kn) */
  drift: number;
}

export interface Thruster {
  /** 指令 −1 至 1(右推正) */
  order: number;
  actual: number;
}

export interface Draft {
  fore?: number;
  aft?: number;
}

export interface EngineStatus {
  state?: EngineRunState;
  startsRemaining?: number;
  load_pct?: number;
}

export interface StateFlags {
  frozen?: boolean;
  aground?: boolean;
  collision?: boolean;
}

/** 情境執行狀態(state.schema.json scenario,設計文件第 5.1 節;皆選填,核心無時間表時可省略)。 */
export interface ScenarioStatus {
  id: string;
  /** 情境階段:normal | degraded | emergency | 自訂 */
  phase: string;
  /** 公司程序參數集版次(例如 2026.10.1) */
  parameterSetVersion?: string;
  /** 時間表項目累計觸發次數(核心 TimelineEngine.TotalFired) */
  timelineFired?: number;
  /** 待執行(delay_s 排程中、尚未執行)的時間表動作數;「待觸發項目數」由教官站依事件推導(timeline-panel) */
  timelinePending?: number;
  hardFailed?: boolean;
  score?: number;
  ended?: boolean;
}

/** 航線狀態(state.schema.json route;同時滿足第 7.2 節 conning 的 XTD/下一航點)。 */
export interface RouteStatus {
  legIndex?: number;
  nextWaypoint?: string;
  /** 航跡偏差 (m),右正左負 */
  xtd_m?: number;
  xtdLimit_m?: number;
  distanceToWaypoint_nm?: number;
  bearingToWaypoint_deg?: number;
  legCourse_deg?: number;
  speedLimit_kn?: number;
  complete?: boolean;
}

/** 操舵模式(state.schema.json steering;C# SimCore 產生,Python 參考伺服器不輸出)。 */
export type SteeringModeValue = 'fu' | 'nfu' | 'autopilot' | 'emergency';

export const STEERING_MODES: readonly SteeringModeValue[] = ['fu', 'nfu', 'autopilot', 'emergency'] as const;

/** 使用中的舵機泵 */
export type SteeringPump = '1' | '2' | 'both';

export const STEERING_PUMPS: readonly SteeringPump[] = ['1', '2', 'both'] as const;

export interface SteeringStatus {
  mode: SteeringModeValue;
  pump: SteeringPump;
  /** nfu 模式時舵是否正被推動(direction port/starboard 中);其他模式省略 */
  nfuActive?: boolean;
}

export interface OwnShipState {
  /** 模擬時間(秒,自練習開始) */
  t: number;
  /** 核心步數(50 Hz) */
  tick: number;
  /** UTC 時刻(ISO 8601,毫秒,Z)= 練習開始時刻(情境 startTimeUtc,未給時為載入情境的實際時鐘)+ t;選填、不進雜湊 */
  utc?: string;
  shipId: ShipId;
  pos: Position;
  heading: number;
  cog: number;
  /** 對地速度 (kn) */
  sog: number;
  /** 對水速度 (kn),縱向 */
  stw: number;
  /** 迴轉率 (度/分),右轉正 */
  rot: number;
  /** 船體座標縱向速度 (m/s) */
  u: number;
  /** 船體座標橫向速度 (m/s),右正 */
  v: number;
  /** 艏搖角速度 (rad/s) */
  r: number;
  /** 漂角 (度) */
  drift?: number;
  /** 實際舵角 (度) */
  rudder: number;
  /** 舵令 (度) */
  rudderOrder: number;
  /** 實際轉速,倒車負 */
  rpm: number;
  rpmOrder: number;
  telegraph: TelegraphPosition;
  thruster?: Thruster;
  depthBelowKeel: number;
  waterDepth?: number;
  squat?: number;
  wind: Wind;
  current: Current;
  loading: LoadingCondition;
  draft?: Draft;
  engine?: EngineStatus;
  faults?: string[];
  flags?: StateFlags;
  /** 能見度 (nm);無物理效果,核心保存/漸變/廣播(設計文件第 1.5 節) */
  visibility_nm?: number;
  scenario?: ScenarioStatus;
  route?: RouteStatus;
  /** 操舵模式(指令 steeringMode 切換;核心不支援時省略,lib/steering.ts 由原始框取出) */
  steering?: SteeringStatus;
  /** 已出現的目標船(state.targets[];舊狀態或情境無目標船時為空陣列) */
  targets: TargetState[];
  /** 真值(教官站/講評用,學員端儀器不顯示;頂層 heading/pos/cog/sog/stw/depthBelowKeel 為指示值;C# SimCore 才有) */
  truth?: TruthState;
  /** 感測器有效性(false 時頂層對應欄位為最後有效值,儀器標「無效」)與磁羅經 */
  sensors?: SensorStatus;
  /** 核心的自動舵狀態(gyroFailure/autopilotFailure 時 available = false) */
  autopilot?: AutopilotStatus;
  /** 警報清單(BAM;無警報或舊狀態時省略;排序見 lib/alerts.ts sortAlerts) */
  alerts?: AlertItem[];
  /** BNWAS 狀態(核心不支援時省略) */
  bnwas?: BnwasStatus;
  /** 拖船(情境有拖船時才有;docs/design/physics-tugs-anchors-mooring.md 第 5 節;舊狀態省略 → 不顯示面板) */
  tugs?: TugState[];
  /** 左右錨(particulars 有錨資料時核心一律輸出;舊狀態或 Python 參考伺服器省略) */
  anchors?: AnchorState[];
  /** 泊位(情境有 berth 時才有):碰墊接觸、碼頭硬接觸、繫纜 */
  berth?: BerthState;
}
