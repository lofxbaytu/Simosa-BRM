// PPI 顯示模型:操作員設定(定向、運動、量程、偏心、向量、軌跡、EBL/VRM/PI…)、目前資料(自船、追蹤目標),
// 以及由模型計算某一時刻 PpiView 的純函數(畫面與點選共用同一份幾何)。

import {
  clampOffcentre,
  enuToScreen,
  orientationRotation,
  requiredVideoRange,
  trueMotionReset,
  type BearingRef,
  type Ebl,
  type Motion,
  type Orientation,
  type Point,
  type PpiView,
  type Vrm,
} from './geometry.js';
import { rangeAtLeast, type AisTarget, type OwnShipReport, type RadarTrack } from './protocol.js';
import type { VectorMode } from './arpa.js';

const KN = 1852 / 3600;
const RAD = Math.PI / 180;

export type Afterglow = 'off' | 'short' | 'long';
/** 回波軌跡長度(秒;0 = 關) */
export const TRAIL_CHOICES: readonly number[] = [0, 30, 60, 180, 360, 720];
export const VECTOR_CHOICES: readonly number[] = [1, 3, 6, 12, 24];

export interface PiLine {
  on: boolean;
  /** 方位(依 ref) */
  bearing: number;
  /** 垂直距離(nm;正 = 沿方位看去右側) */
  offsetNm: number;
  ref: BearingRef;
}

export type CursorTool = 'select' | 'acquire' | 'ebl1' | 'ebl2' | 'vrm1' | 'vrm2' | 'pi1' | 'pi2' | 'offcentre';

export interface PpiSettings {
  orientation: Orientation;
  motion: Motion;
  /** 航向向上的參考航向(選擇該模式時的航向) */
  courseRef: number;
  displayRangeNm: number;
  ringsOn: boolean;
  /** 相對運動偏心(圓半徑比例,畫面座標;0,0 = 置中) */
  offcentre: Point;
  vectorMode: VectorMode;
  vectorMin: number;
  pastPositions: boolean;
  trailSec: number;
  afterglow: Afterglow;
  ebl: [Ebl, Ebl];
  vrm: [Vrm, Vrm];
  pi: [PiLine, PiLine];
  headingLineOff: boolean;
  showGuardZone: boolean;
  tool: CursorTool;
}

export function defaultSettings(): PpiSettings {
  return {
    orientation: 'north-up',
    motion: 'relative',
    courseRef: 0,
    displayRangeNm: 6,
    ringsOn: true,
    offcentre: { x: 0, y: 0 },
    vectorMode: 'true',
    vectorMin: 6,
    pastPositions: true,
    trailSec: 0,
    afterglow: 'long',
    ebl: [
      { on: false, bearing: 45, ref: 'T' },
      { on: false, bearing: 90, ref: 'R' },
    ],
    vrm: [
      { on: false, rangeNm: 1 },
      { on: false, rangeNm: 2 },
    ],
    pi: [
      { on: false, bearing: 0, offsetNm: 0.5, ref: 'T' },
      { on: false, bearing: 0, offsetNm: -0.5, ref: 'T' },
    ],
    headingLineOff: false,
    showGuardZone: true,
    tool: 'select',
  };
}

/** 真運動錨點:重設時的自船 ENU 與畫面偏移比例 */
export interface TmAnchor {
  x: number;
  y: number;
  fx: number;
  fy: number;
  rotation: number;
}

/** 自船即時狀態(ARPA 指示值外推到目前影像時間) */
export interface OwnNow {
  x: number;
  y: number;
  heading: number;
  cog: number;
  sog: number;
  valid: boolean;
}

/** 由最近的 tracks.ownShip(時間 tOwn)以 COG/SOG 外推到 tNow(最多 3 s)。heading 取影像幀的指示航向。 */
export function extrapolateOwn(own: OwnShipReport | undefined, tOwn: number, tNow: number, headingNow: number): OwnNow {
  if (!own) return { x: 0, y: 0, heading: headingNow, cog: headingNow, sog: 0, valid: false };
  const dt = Number.isFinite(tNow) ? Math.min(3, Math.max(0, tNow - tOwn)) : 0;
  const v = own.sog * KN;
  return {
    x: own.x + v * Math.sin(own.cog * RAD) * dt,
    y: own.y + v * Math.cos(own.cog * RAD) * dt,
    heading: headingNow,
    cog: own.cog,
    sog: own.sog,
    valid: true,
  };
}

/** 目標位置外推(追蹤中且有真向量才外推;最多 3 s) */
export function extrapolateTrack(t: RadarTrack, tTracks: number, tNow: number): Point {
  if (t.state !== 'tracking' || t.course_deg === undefined || t.speed_kn === undefined) return { x: t.x, y: t.y };
  const dt = Number.isFinite(tNow) ? Math.min(3, Math.max(0, tNow - tTracks)) : 0;
  const v = t.speed_kn * KN;
  return { x: t.x + v * Math.sin(t.course_deg * RAD) * dt, y: t.y + v * Math.cos(t.course_deg * RAD) * dt };
}

/** 純 AIS 目標位置外推(自報告時刻以 COG/SOG 推到現在;最多 3 s,與 ARPA 目標相同) */
export function extrapolateAis(a: AisTarget, tTracks: number, tNow: number): Point {
  const dt = Number.isFinite(tNow) ? Math.min(3, Math.max(0, tNow - tTracks)) : 0;
  const v = a.sog_kn * KN;
  return { x: a.x + v * Math.sin(a.cog_deg * RAD) * dt, y: a.y + v * Math.cos(a.cog_deg * RAD) * dt };
}

/** 純 AIS 目標符號的畫面位置(id = MMSI;PPI 繪圖與點選判定共用,不依賴繪圖快取) */
export function aisScreenPoints(view: PpiView, list: readonly AisTarget[], own: { x: number; y: number }, tTracks: number, tNow: number): Array<{ id: number; x: number; y: number }> {
  return list.map((a) => {
    const p = extrapolateAis(a, tTracks, tNow);
    const sp = enuToScreen(view, p.x - own.x, p.y - own.y);
    return { id: a.mmsi, x: sp.x, y: sp.y };
  });
}

export interface ViewInput {
  width: number;
  height: number;
  /** 圓外保留給方位刻度的邊距(px) */
  marginPx: number;
  settings: PpiSettings;
  headingDeg: number;
  headingValid: boolean;
  own: OwnNow;
  tm: TmAnchor | null;
}

/** 畫面目前實際使用的運動模式:真運動需要自船位置與穩定化(航向有效、非艏向上) */
export function effectiveMotion(s: PpiSettings, own: OwnNow, headingValid: boolean): Motion {
  return s.motion === 'true' && own.valid && headingValid && s.orientation !== 'head-up' ? 'true' : 'relative';
}

/** 計算 PpiView。真運動時 tm 為錨點(null 時自船置中)。 */
export function computeView(inp: ViewInput): PpiView {
  const { width, height, settings: s } = inp;
  const cx = width / 2;
  const cy = height / 2;
  const radiusPx = Math.max(10, Math.min(width, height) / 2 - inp.marginPx);
  const rotationDeg = orientationRotation(s.orientation, inp.headingDeg, s.courseRef, inp.headingValid);
  const base: PpiView = { cx, cy, radiusPx, rangeNm: s.displayRangeNm, rotationDeg, ownX: cx, ownY: cy };
  let off: Point;
  if (effectiveMotion(s, inp.own, inp.headingValid) === 'true' && inp.tm) {
    const p = enuToScreen({ ...base, ownX: 0, ownY: 0 }, inp.own.x - inp.tm.x, inp.own.y - inp.tm.y);
    off = { x: inp.tm.fx + p.x / radiusPx, y: inp.tm.fy + p.y / radiusPx };
  } else {
    off = s.offcentre;
  }
  const c = clampOffcentre(off.x * radiusPx, off.y * radiusPx, radiusPx, 0.95);
  return { ...base, ownX: cx + c.x, ownY: cy + c.y };
}

/** 依目前狀態更新真運動錨點:未設、旋轉改變或自船超出 0.7 R 時重設到航向反側。 */
export function updateTmAnchor(
  tm: TmAnchor | null,
  view: PpiView,
  own: OwnNow,
  active: boolean,
): TmAnchor | null {
  if (!active) return null;
  const fx = (view.ownX - view.cx) / view.radiusPx;
  const fy = (view.ownY - view.cy) / view.radiusPx;
  if (!tm || Math.abs(tm.rotation - view.rotationDeg) > 1e-6) {
    // 第一次進入真運動:由目前位置開始(相對運動置中 → 自船在圓心)
    const r = trueMotionReset(1, 0, own.cog - view.rotationDeg, 0, 0.6) ?? { x: 0, y: 0.6 };
    return { x: own.x, y: own.y, fx: r.x, fy: r.y, rotation: view.rotationDeg };
  }
  const reset = trueMotionReset(fx, fy, own.cog - view.rotationDeg);
  if (!reset) return tm;
  return { x: own.x, y: own.y, fx: reset.x, fy: reset.y, rotation: view.rotationDeg };
}

/** 偏心或真運動時的偏移比例(用於決定影像量程) */
export function offcentreFraction(view: PpiView): number {
  return Math.hypot(view.ownX - view.cx, view.ownY - view.cy) / view.radiusPx;
}

/**
 * 本畫面需要的伺服器影像量程(nm):置中時 = 顯示量程;偏心/真運動時加大到涵蓋最遠圓邊的標準量程
 * (上限 96 nm)。
 */
export function videoRangeFor(displayRangeNm: number, offcentre: number, scales?: readonly number[]): number {
  if (offcentre < 0.02) return rangeAtLeast(displayRangeNm, scales);
  return rangeAtLeast(requiredVideoRange(displayRangeNm, offcentre), scales);
}

/** 點選最近的目標(畫面距離 ≤ tolPx);無則回傳 null */
export function hitTestTarget(points: ReadonlyArray<{ id: number; x: number; y: number }>, x: number, y: number, tolPx: number): number | null {
  let best: number | null = null;
  let bestD = tolPx;
  for (const p of points) {
    const d = Math.hypot(p.x - x, p.y - y);
    if (d <= bestD) {
      bestD = d;
      best = p.id;
    }
  }
  return best;
}
