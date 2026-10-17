// 目標船狀態型別,對應 src/Contracts/state.schema.json 的 $defs.target(核心 src/SimCore/Contracts/TargetState.cs 的 JSON 名稱;
// 規劃書第 5.2 節 Traffic、第 8.3 節避碰指標)。位置/航向/速度為真值;AIS 報告值(含注入錯誤)在 ais,AIS 關閉或不發送時省略。
// 距離與 CPA 用浬、TCPA 用分、方位為真方位(度);欄位名依 schema(含 range_nm 等非 camelCase 名稱)。

import type { Position } from './state.js';

/** AIS 報告(ITU-R M.1371 類型 1/5 欄位;含注入錯誤)。 */
export interface TargetAisReport {
  mmsi?: number;
  name?: string;
  callSign?: string;
  imo?: number;
  /** AIS 船型碼 */
  shipType?: number;
  navStatus?: number;
  lat?: number;
  lon?: number;
  cog?: number;
  sog?: number;
  heading?: number;
  rot?: number;
  dimToBow?: number;
  dimToStern?: number;
  dimToPort?: number;
  dimToStarboard?: number;
  draught?: number;
  destination?: string;
}

export interface TargetState {
  id: string;
  name: string;
  mmsi: number;
  /** cargo、tanker、fishing、pilot、tug、passenger、pleasure、sar、hsc、sailing、other */
  shipType: string;
  /** 總長 (m) */
  loa: number;
  beam: number;
  draft?: number;
  pos: Position;
  heading: number;
  cog: number;
  /** 對地速度 (kn) */
  sog: number;
  stw?: number;
  /** 迴轉率(度/分,右轉正) */
  rot: number;
  /** 自船至目標距離 (nm) */
  range_nm: number;
  /** 自船看目標的真方位 */
  bearing_deg: number;
  /** 相對自船艏向 0–360 */
  relBearing_deg?: number;
  cpa_nm: number;
  /** 負值 = 已通過 CPA */
  tcpa_min: number;
  /** 艏越距離:正 = 由自船艏前通過、負 = 由艉後通過;無交會時省略 */
  bcr_nm?: number;
  bct_min?: number;
  aisOn: boolean;
  /** 燈號類別:powerDriven、anchored、notUnderCommand、… */
  lights: string;
  /** 鳴放中的聲號;無時省略 */
  sound?: string;
  /** hold、waypoints、scripted、anchored、follow、colreg;manual = 教官覆寫中 */
  behaviour: string;
  /** COLREG 判定/行動,例如 crossing/giveWay/avoiding */
  colreg?: string;
  ais?: TargetAisReport;
}
