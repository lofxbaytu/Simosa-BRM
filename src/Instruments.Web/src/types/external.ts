// 拖船、錨與錨鏈、泊位(碰墊、繫纜)的狀態型別,對應 src/Contracts/state.schema.json 的 tugs[] / anchors[] / berth
// (docs/design/physics-tugs-anchors-mooring.md 第 5 節;C# SimCore 產生,Python 參考伺服器不輸出;舊狀態沒有這些欄位)。
// 力以噸力(t)、方向相對船艏順時針(度;0 向前、90 向右舷)、鏈長與纜長以公尺。

import type { Position } from './state.js';

/** unavailable:拖船離開、本練習不再可用(時間表或教官 tugControl;不可恢復)。 */
export type TugMode = 'push' | 'pull' | 'standby' | 'free' | 'unavailable';
export const TUG_MODES: readonly TugMode[] = ['push', 'pull', 'standby', 'free', 'unavailable'] as const;
/** 操作用模式(拖船面板的模式鈕;unavailable 不可恢復,不放在一般模式鈕)。 */
export const TUG_OPERATING_MODES: readonly TugMode[] = ['push', 'pull', 'standby', 'free'] as const;

export type TugStatus = 'standby' | 'connecting' | 'repositioning' | 'working' | 'free' | 'parted' | 'unavailable';
export const TUG_STATUSES: readonly TugStatus[] = ['standby', 'connecting', 'repositioning', 'working', 'free', 'parted', 'unavailable'] as const;

export interface TugState {
  id: string;
  name?: string;
  mode: TugMode;
  status: TugStatus;
  /** 實際出力 (t) */
  force_t: number;
  /** 實際力方向(相對船艏,度) */
  direction_deg: number;
  /** 命令方向(相對船艏,度) */
  directionOrder_deg?: number;
  /** 出力令 (%) */
  power_pct?: number;
  /** 自船對水速度造成的效能(0–1) */
  efficiency?: number;
  /** 繫柱拖力 (t) */
  bollardPull_t?: number;
  lineParted?: boolean;
  /** 示意顯示位置(非物理;拖船不是交通目標) */
  pos?: Position;
}

export type AnchorSide = 'port' | 'starboard';
export const ANCHOR_SIDES: readonly AnchorSide[] = ['port', 'starboard'] as const;

export type AnchorStatus = 'stowed' | 'running' | 'holding' | 'dragging' | 'heaving' | 'aweigh';
export const ANCHOR_STATUSES: readonly AnchorStatus[] = ['stowed', 'running', 'holding', 'dragging', 'heaving', 'aweigh'] as const;

export type AnchorAction = 'letGo' | 'veer' | 'heave' | 'brake' | 'release';
export const ANCHOR_ACTIONS: readonly AnchorAction[] = ['letGo', 'veer', 'heave', 'brake', 'release'] as const;

export interface AnchorState {
  side: AnchorSide;
  status: AnchorStatus;
  /** 送出鏈長 (m) */
  length_m: number;
  /** 送出鏈長(節);核心未給時由 length_m / 27.5 推得 */
  length_shackles?: number;
  maxLength_m?: number;
  /** 錨鏈孔處張力 (t) */
  tension_t: number;
  horizontalTension_t?: number;
  /** 躺底鏈長 (m) */
  onBottom_m: number;
  /** 目前抓地力 (t) */
  holding_t?: number;
  brake?: boolean;
  /** 已到鏈端 */
  fullyOut?: boolean;
  /** 錨位(收存時省略) */
  position?: Position;
}

export interface FenderContactState {
  fenderId: string;
  deflection_m: number;
  deflection_pct?: number;
  reaction_kN: number;
  /** 壓縮超過額定 */
  overload?: boolean;
}

export type MooringStatus = 'stowed' | 'fast' | 'heaving' | 'slacking' | 'letGo' | 'parted';
export const MOORING_STATUSES: readonly MooringStatus[] = ['stowed', 'fast', 'heaving', 'slacking', 'letGo', 'parted'] as const;

export type MooringAction = 'makeFast' | 'heave' | 'slack' | 'letGo';
export const MOORING_ACTIONS: readonly MooringAction[] = ['makeFast', 'heave', 'slack', 'letGo'] as const;

export interface MooringLineState {
  id: string;
  status: MooringStatus;
  tension_t: number;
  mbl_t?: number;
  bollardId?: string;
  /** 未受力纜長 (m;未繫時省略) */
  length_m?: number;
}

export interface BerthState {
  id: string;
  name?: string;
  /** 船體與碼頭面硬接觸中 */
  quayContact?: boolean;
  /** 目前受壓的碰墊 */
  contacts: FenderContactState[];
  lines: MooringLineState[];
}
