// 航跡圖點選編輯的純函式(第二十一輪;docs/design/instructor-plot.md 第 4 節):畫面像素 ↔ 本地 ENU 的換算、
// 可拖曳把手(handle)的命中測試、點擊新增時的插入位置(多邊形依最近邊、折線依最近航段或接在末端)、方向鍵微調步距、
// ENU ↔ 經緯度(與 lib/timeline.ts latLonToEnu 相同的 111 320 m/度平面近似)與草稿座標(DraftPosition)的互換。
// 不碰 DOM/Canvas;草稿層的套用(航點/頂點/自船/目標船)在 lib/scenario-editor-plot.ts。

import { latLonToEnu } from './timeline.js';
import type { DraftPosition, PositionMode } from './scenario-editor.js';

/** 畫面視圖:中心的 ENU 座標、比例(px/m)與畫布像素尺寸。 */
export interface PlotView {
  cx: number;
  cy: number;
  z: number;
  w: number;
  h: number;
}

export type PlotHandleKind = 'waypoint' | 'vertex' | 'own' | 'target';

/** 可拖曳把手(航點、區域頂點、自船初始位置、目標船初始位置)。 */
export interface PlotHandle {
  /** 唯一鍵:wp:<i>、zone:<zi>:<j>、own、target:<i> */
  key: string;
  kind: PlotHandleKind;
  x: number;
  y: number;
  label: string;
  /** 可刪除(航點/頂點) */
  removable: boolean;
}

export interface Point {
  x: number;
  y: number;
}

/** 把手命中半徑(px)。 */
export const HANDLE_HIT_RADIUS_PX = 9;

export function enuToPx(view: PlotView, x: number, y: number): [number, number] {
  return [view.w / 2 + (x - view.cx) * view.z, view.h / 2 - (y - view.cy) * view.z];
}

export function pxToEnu(view: PlotView, px: number, py: number): Point {
  return { x: view.cx + (px - view.w / 2) / view.z, y: view.cy - (py - view.h / 2) / view.z };
}

/** 像素位置最近的把手(距離 ≤ radiusPx);無則 null。距離相同時取排序在前者。 */
export function nearestHandle(handles: readonly PlotHandle[], px: number, py: number, view: PlotView, radiusPx = HANDLE_HIT_RADIUS_PX): PlotHandle | null {
  let best: PlotHandle | null = null;
  let bestD = radiusPx;
  for (const h of handles) {
    const [hx, hy] = enuToPx(view, h.x, h.y);
    const d = Math.hypot(hx - px, hy - py);
    if (d <= bestD && (best === null || d < bestD)) {
      best = h;
      bestD = d;
    }
  }
  return best;
}

/** 點到線段的距離與投影參數 t ∈ [0, 1]。 */
export function segmentDistance(a: Point, b: Point, p: Point): { distance: number; t: number } {
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const len2 = dx * dx + dy * dy;
  if (len2 === 0) return { distance: Math.hypot(p.x - a.x, p.y - a.y), t: 0 };
  const t = Math.min(1, Math.max(0, ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2));
  return { distance: Math.hypot(p.x - (a.x + dx * t), p.y - (a.y + dy * t)), t };
}

/**
 * 多邊形新增頂點的插入索引:找離點最近的邊 (k, k+1)(含閉合邊 (n−1, 0)),回傳 k+1(插在該邊兩端之間);
 * 頂點少於 3 時接在末端(回傳 n)。
 */
export function insertIndexOnPolygon(polygon: readonly Point[], p: Point): number {
  const n = polygon.length;
  if (n < 3) return n;
  let best = n;
  let bestD = Number.POSITIVE_INFINITY;
  for (let k = 0; k < n; k++) {
    const a = polygon[k]!;
    const b = polygon[(k + 1) % n]!;
    const { distance } = segmentDistance(a, b, p);
    if (distance < bestD) {
      bestD = distance;
      best = k + 1;
    }
  }
  return best;
}

/**
 * 折線(航線)新增航點的插入索引:點落在某航段附近(垂距 ≤ maxOffset 且投影在段內 0 < t < 1)時插在該段中間(回傳 k+1),
 * 否則接在末端(回傳 n)。
 */
export function insertIndexOnPolyline(line: readonly Point[], p: Point, maxOffset: number): number {
  const n = line.length;
  let best = n;
  let bestD = maxOffset;
  for (let k = 0; k + 1 < n; k++) {
    const { distance, t } = segmentDistance(line[k]!, line[k + 1]!, p);
    if (t > 0 && t < 1 && distance <= bestD) {
      bestD = distance;
      best = k + 1;
    }
  }
  return best;
}

/** 方向鍵微調步距(m):約 10 px 對應的距離,取 1/2/5 × 10ⁿ 階;Shift 為 10 倍。 */
export function nudgeStep_m(z: number, large = false): number {
  const raw = 10 / Math.max(1e-9, z);
  const exp = Math.pow(10, Math.floor(Math.log10(raw)));
  const m = raw / exp;
  const step = (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * exp;
  return large ? step * 10 : step;
}

/** ENU → 經緯度(latLonToEnu 的反算;同一平面近似)。 */
export function enuToLatLon(origin: { lat: number; lon: number }, x: number, y: number): { lat: number; lon: number } {
  const latRad = (origin.lat * Math.PI) / 180;
  return { lat: origin.lat + y / 111_320, lon: origin.lon + x / (111_320 * Math.cos(latRad)) };
}

/** 草稿座標(數值)→ ENU;經緯度需 origin;非數值(參數引用)或資料不足回傳 null。 */
export function draftPositionToEnu(p: DraftPosition | undefined, origin: { lat: number; lon: number } | null): Point | null {
  if (!p) return null;
  if (typeof p.x === 'number' && typeof p.y === 'number') return { x: p.x, y: p.y };
  if (typeof p.lat === 'number' && typeof p.lon === 'number' && origin) return latLonToEnu(origin, p.lat, p.lon);
  return null;
}

/** 圖上點選要寫回的座標寫法:沿用該點既有寫法(有 lat/lon 數值 → 經緯度;有 x/y → ENU);新點依 fallback;經緯度無 origin 時退回 ENU。 */
export function pointPositionMode(existing: DraftPosition | undefined, fallback: PositionMode, origin: { lat: number; lon: number } | null): PositionMode {
  let mode: PositionMode = fallback;
  if (existing) {
    if (typeof existing.lat === 'number' && typeof existing.lon === 'number') mode = 'latlon';
    else if (typeof existing.x === 'number' && typeof existing.y === 'number') mode = 'enu';
  }
  return mode === 'latlon' && !origin ? 'enu' : mode;
}

/** ENU → 草稿座標:經緯度取 5 位小數(≈ 1 m)、ENU 取整數公尺。 */
export function draftPositionFromEnu(p: Point, mode: PositionMode, origin: { lat: number; lon: number } | null): DraftPosition {
  if (mode === 'latlon' && origin) {
    const g = enuToLatLon(origin, p.x, p.y);
    return { lat: round(g.lat, 5), lon: round(g.lon, 5) };
  }
  return { x: round(p.x, 0), y: round(p.y, 0) };
}

/** 四捨五入到 digits 位;避免 -0。 */
function round(v: number, digits: number): number {
  const f = Math.pow(10, digits);
  const r = Math.round(v * f) / f;
  return r === 0 ? 0 : r;
}

/** 座標讀數文字:x/y 公尺,有 origin 時附經緯度(小數 5 位)。 */
export function coordinateReadout(p: Point, origin: { lat: number; lon: number } | null): string {
  const xy = `x ${Math.round(p.x)} m  y ${Math.round(p.y)} m`;
  if (!origin) return xy;
  const g = enuToLatLon(origin, p.x, p.y);
  return `${xy} · ${g.lat.toFixed(5)}°${g.lat >= 0 ? 'N' : 'S'} ${g.lon.toFixed(5)}°${g.lon >= 0 ? 'E' : 'W'}`;
}
