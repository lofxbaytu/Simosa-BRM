// 簡化 ECDIS 的航線監控(docs/design/ecdis-lite.md 第 4 節;IEC 61174 航線監控的簡化):以情境 route(lib/plot-overlay 的 OverlayRoute,
// ENU 航點與各航段走廊半寬)與自船位置/COG/SOG 計算目前航段、XTD(右正左負)、到下一航點 DTG、以 SOG 的 TTG、預定航向、
// 轉向點倒數與下一航段轉向量;抵達航點(進入抵達半徑或越過航點的垂直線)自動切到下一航段,抵達終點標 complete。
// 頁面本地計算、不送核心指令;核心 state.route(legIndex/xtd_m)為評估依據,本模組只供顯示(兩者的航段切換規則可能略有不同)。

import { M_PER_NM } from '../../radar/geometry.js';
import { normalizeHeading, normalizeRelative } from '../angles.js';
import type { OverlayRoute, OverlayWaypoint } from '../plot-overlay.js';
import { legCourse } from '../timeline.js';

export interface Pt {
  x: number;
  y: number;
}

/** 預設抵達半徑(m;航點未給 arrivalRadius_m 時;約 0.1 nm) */
export const DEFAULT_ARRIVAL_RADIUS_M = 185;

/** 沿航段(a → b)的投影距離(m;a 為 0,可為負或超過航段長)。 */
export function alongTrack(a: Pt, b: Pt, p: Pt): number {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const len = Math.hypot(dx, dy);
  if (len === 0) return 0;
  return ((p.x - a.x) * dx + (p.y - a.y) * dy) / len;
}

/** 航跡偏差(m):相對航段 a → b,右舷正、左舷負(ENU x 東 y 北)。 */
export function crossTrackDistance(a: Pt, b: Pt, p: Pt): number {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const len = Math.hypot(dx, dy);
  if (len === 0) return Math.hypot(p.x - a.x, p.y - a.y);
  return (dy * (p.x - a.x) - dx * (p.y - a.y)) / len;
}

export interface RouteLegStatus {
  /** 目前航段索引(航段 i 由 waypoints[i] 到 waypoints[i+1]) */
  legIndex: number;
  legCount: number;
  nextWaypoint: OverlayWaypoint;
  nextIndex: number;
  /** 航跡偏差 (m),右正左負 */
  xtd_m: number;
  /** 走廊半寬 (m;航線/航點未給或為參數引用時 undefined) */
  xtdLimit_m: number | undefined;
  xtdExceeded: boolean;
  /** 到下一航點 (m) */
  dtg_m: number;
  /** 到終點(含剩餘航段)(m) */
  dtgTotal_m: number;
  /** 以 SOG 到下一航點 (s;SOG < 0.1 kn 時 null) */
  ttg_s: number | null;
  /** 預定航向 = 航段航向(度) */
  plannedCourse_deg: number;
  bearingToWaypoint_deg: number;
  /** 下一航段航向(終點前為 null) */
  nextCourse_deg: number | null;
  /** 轉向量(度;右正左負;終點前為 null) */
  turn_deg: number | null;
  /** 已抵達終點 */
  complete: boolean;
}

export function legXtdLimit(route: OverlayRoute, legIndex: number): number | undefined {
  // 航點的 xtdLimit_m 為「到達此航點的航段」走廊半寬(plot-overlay 已把航線預設值填入各航點)
  return route.waypoints[legIndex + 1]?.xtdLimit_m;
}

/** 已抵達航點:在抵達半徑內,或已越過航點處與航段垂直的線(along ≥ 航段長)。 */
export function arrivedAt(prev: Pt, wp: OverlayWaypoint, pos: Pt): boolean {
  const radius = wp.arrivalRadius_m ?? DEFAULT_ARRIVAL_RADIUS_M;
  if (Math.hypot(pos.x - wp.x, pos.y - wp.y) <= radius) return true;
  const len = Math.hypot(wp.x - prev.x, wp.y - prev.y);
  return len > 0 && alongTrack(prev, wp, pos) >= len;
}

export class RouteMonitor {
  private _legIndex: number;
  private _complete = false;

  constructor(
    readonly route: OverlayRoute,
    startLeg = 0,
  ) {
    this._legIndex = Math.min(Math.max(0, startLeg), Math.max(0, route.waypoints.length - 2));
  }

  get legIndex(): number {
    return this._legIndex;
  }

  get complete(): boolean {
    return this._complete;
  }

  /** 教官/操作員手動指定航段(0 起算)。 */
  setLeg(i: number): void {
    this._legIndex = Math.min(Math.max(0, Math.trunc(i)), Math.max(0, this.route.waypoints.length - 2));
    this._complete = false;
  }

  /** 每筆狀態呼叫:先處理抵達切換(可連續跳過多個已過的航點),再計算目前航段的數值;航點少於 2 回傳 null。 */
  update(pos: Pt, sogKn: number): RouteLegStatus | null {
    const wps = this.route.waypoints;
    if (wps.length < 2) return null;
    const lastLeg = wps.length - 2;
    while (!this._complete) {
      const a = wps[this._legIndex]!;
      const b = wps[this._legIndex + 1]!;
      if (!arrivedAt(a, b, pos)) break;
      if (this._legIndex >= lastLeg) {
        this._complete = true;
        break;
      }
      this._legIndex++;
    }
    const a = wps[this._legIndex]!;
    const b = wps[this._legIndex + 1]!;
    const xtd = crossTrackDistance(a, b, pos);
    const limit = legXtdLimit(this.route, this._legIndex);
    const dtg = Math.hypot(b.x - pos.x, b.y - pos.y);
    let total = dtg;
    for (let i = this._legIndex + 1; i < wps.length - 1; i++) total += Math.hypot(wps[i + 1]!.x - wps[i]!.x, wps[i + 1]!.y - wps[i]!.y);
    const speed_mps = sogKn * (M_PER_NM / 3600);
    const course = legCourse(a, b);
    const next = wps[this._legIndex + 2];
    const nextCourse = next ? legCourse(b, next) : null;
    return {
      legIndex: this._legIndex,
      legCount: wps.length - 1,
      nextWaypoint: b,
      nextIndex: this._legIndex + 1,
      xtd_m: xtd,
      xtdLimit_m: limit,
      xtdExceeded: limit !== undefined && Math.abs(xtd) > limit,
      dtg_m: dtg,
      dtgTotal_m: total,
      ttg_s: sogKn >= 0.1 ? dtg / speed_mps : null,
      plannedCourse_deg: course,
      bearingToWaypoint_deg: dtg === 0 ? course : normalizeHeading((Math.atan2(b.x - pos.x, b.y - pos.y) * 180) / Math.PI),
      nextCourse_deg: nextCourse,
      turn_deg: nextCourse === null ? null : normalizeRelative(nextCourse - course),
      complete: this._complete,
    };
  }
}

/** 秒 → "mm:ss"(≥ 1 小時 "h:mm:ss");null → "--:--"。 */
export function formatTtg(s: number | null): string {
  if (s === null || !Number.isFinite(s)) return '--:--';
  const t = Math.max(0, Math.round(s));
  const h = Math.floor(t / 3600);
  const m = Math.floor((t % 3600) / 60);
  const sec = t % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}` : `${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
}
