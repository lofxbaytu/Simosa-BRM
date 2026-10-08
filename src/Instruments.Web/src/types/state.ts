// 自船狀態型別,對應 src/Contracts/state.schema.json(OwnShipState)。
// 慣例:角度以度、航向 0–360、舵角右正左負、ROT 右轉正(度/分)、轉速倒車負。

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

export interface OwnShipState {
  /** 模擬時間(秒,自練習開始) */
  t: number;
  /** 核心步數(50 Hz) */
  tick: number;
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
}
