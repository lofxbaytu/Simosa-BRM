// 流場疊圖的純函式(第二十一輪;docs/design/instructor-plot.md 第 3 節):情境 environment.current.field 規則網格的解析、
// 雙線性內插(與核心 CurrentField.Interpolate / Python CurrentField.interpolate 相同的運算順序)、畫面可見範圍內的節點取樣
//(最多 40 × 40;超過時等距抽樣)、箭頭長度/顏色比例、依 state 推得的 timeScale 當前比例 k(t),以及環境進階面板的兩個網格產生器
//(由均勻流產生、由兩端流速沿 x 或 y 線性內插)。不含任何 Canvas 呼叫,方便 vitest 直接驗證幾何。

import { degToRad, radToDeg, normalizeHeading } from './angles.js';

/** 規則網格(本地 ENU 公尺;節點 (x0 + i·dx, y0 + j·dy),i ∈ [0, nx)、j ∈ [0, ny))。 */
export interface FieldGrid {
  x0: number;
  y0: number;
  dx: number;
  dy: number;
  nx: number;
  ny: number;
}

/** 解析後的空間流場(u_kn[j][i] / v_kn[j][i] 為節點東向/北向分量,kn)。 */
export interface CurrentFieldSpec {
  grid: FieldGrid;
  u_kn: number[][];
  v_kn: number[][];
  outside: 'edge' | 'zero';
  timeScale: 'none' | 'tidal' | 'table';
}

/** 畫面取樣後的一支箭頭(節點位置與分量;speed_kn 為未乘 k(t) 的節點流速)。 */
export interface FieldArrow {
  i: number;
  j: number;
  x: number;
  y: number;
  u_kn: number;
  v_kn: number;
  speed_kn: number;
}

/** 取樣結果:箭頭與抽樣步距(兩軸各自),cell_m 為抽樣後相鄰箭頭的最小間距(m)。 */
export interface FieldSampling {
  arrows: FieldArrow[];
  strideX: number;
  strideY: number;
  cell_m: number;
}

/** 可見範圍(ENU 公尺;畫面中心、半寬/半高)。 */
export interface FieldViewport {
  cx: number;
  cy: number;
  halfWidth_m: number;
  halfHeight_m: number;
}

/** 單軸取樣上限(規劃:網格箭頭以 Canvas 一次繪製、最多 40 × 40)。 */
export const FIELD_MAX_SAMPLES_PER_AXIS = 40;

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function finite(v: unknown): number | undefined {
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
}

function numberRows(v: unknown, ny: number, nx: number): number[][] | null {
  if (!Array.isArray(v) || v.length !== ny) return null;
  const out: number[][] = [];
  for (const row of v) {
    if (!Array.isArray(row) || row.length !== nx) return null;
    const r: number[] = [];
    for (const x of row) {
      const n = finite(x);
      if (n === undefined) return null;
      r.push(n);
    }
    out.push(r);
  }
  return out;
}

/**
 * 解析情境的 environment.current.field(原樣物件)。只接受可畫的場:grid 的 dx/dy > 0、nx/ny 為 ≥ 2 的整數、
 * u_kn/v_kn 為 ny 列 × nx 行的有限數值;不合者回傳 null(結構問題由 scenario-editor 的 currentFieldIssues 報告)。
 */
export function parseCurrentField(raw: unknown): CurrentFieldSpec | null {
  if (!isRecord(raw) || !isRecord(raw['grid'])) return null;
  const g = raw['grid'];
  const x0 = finite(g['x0']);
  const y0 = finite(g['y0']);
  const dx = finite(g['dx']);
  const dy = finite(g['dy']);
  const nx = g['nx'];
  const ny = g['ny'];
  if (x0 === undefined || y0 === undefined || dx === undefined || dy === undefined || dx <= 0 || dy <= 0) return null;
  if (typeof nx !== 'number' || typeof ny !== 'number' || !Number.isInteger(nx) || !Number.isInteger(ny) || nx < 2 || ny < 2) return null;
  const u = numberRows(raw['u_kn'], ny, nx);
  const v = numberRows(raw['v_kn'], ny, nx);
  if (!u || !v) return null;
  const outside = String(raw['outside'] ?? 'edge').toLowerCase() === 'zero' ? 'zero' : 'edge';
  const tsRaw = String(raw['timeScale'] ?? 'none').toLowerCase();
  const timeScale = tsRaw === 'tidal' ? 'tidal' : tsRaw === 'table' ? 'table' : 'none';
  return { grid: { x0, y0, dx, dy, nx, ny }, u_kn: u, v_kn: v, outside, timeScale };
}

/** 由情境物件(environment.current.field 路徑)取場;無則 null。 */
export function currentFieldOfScenario(raw: unknown): CurrentFieldSpec | null {
  if (!isRecord(raw) || !isRecord(raw['environment']) || !isRecord(raw['environment']['current'])) return null;
  return parseCurrentField(raw['environment']['current']['field']);
}

/**
 * 雙線性內插(kn;運算順序與核心相同):fx = (x − x0)/dx、fy = (y − y0)/dy;outside zero 且任一超出 [0, n−1] → (0, 0);
 * 否則夾到範圍後 i = min(⌊fx⌋, nx−2)、j = min(⌊fy⌋, ny−2)、tx = fx − i、ty = fy − j,先沿 x 再沿 y。
 */
export function fieldVectorAt(field: CurrentFieldSpec, x: number, y: number): { u_kn: number; v_kn: number } {
  const g = field.grid;
  let fx = (x - g.x0) / g.dx;
  let fy = (y - g.y0) / g.dy;
  if (field.outside === 'zero' && (fx < 0 || fy < 0 || fx > g.nx - 1 || fy > g.ny - 1)) return { u_kn: 0, v_kn: 0 };
  fx = Math.min(g.nx - 1, Math.max(0, fx));
  fy = Math.min(g.ny - 1, Math.max(0, fy));
  const i = Math.min(Math.floor(fx), g.nx - 2);
  const j = Math.min(Math.floor(fy), g.ny - 2);
  const tx = fx - i;
  const ty = fy - j;
  const lerp = (rows: number[][]): number => {
    const r0 = rows[j]!;
    const r1 = rows[j + 1]!;
    const a = r0[i]! + (r0[i + 1]! - r0[i]!) * tx;
    const b = r1[i]! + (r1[i + 1]! - r1[i]!) * tx;
    return a + (b - a) * ty;
  };
  return { u_kn: lerp(field.u_kn), v_kn: lerp(field.v_kn) };
}

/** 向量 → 流向(去向,0–360)與流速(kn)。 */
export function vectorToSetDrift(u_kn: number, v_kn: number): { set: number; drift: number } {
  const drift = Math.hypot(u_kn, v_kn);
  return { set: drift > 0 ? normalizeHeading(radToDeg(Math.atan2(u_kn, v_kn))) : 0, drift };
}

/** 流向/流速 → 東向/北向分量(kn)。 */
export function setDriftToVector(set_deg: number, drift_kn: number): { u_kn: number; v_kn: number } {
  const r = degToRad(set_deg);
  return { u_kn: drift_kn * Math.sin(r), v_kn: drift_kn * Math.cos(r) };
}

/** 全部節點的最大流速(kn;箭頭長度/顏色的參考)。 */
const maxSpeedCache = new WeakMap<CurrentFieldSpec, number>();

/** 網格最大流速(kn);以 field 物件為鍵快取,避免每幀走訪整個網格(審查第二十一輪:每幀工作量上限)。 */
export function fieldMaxSpeed(field: CurrentFieldSpec): number {
  const cached = maxSpeedCache.get(field);
  if (cached !== undefined) return cached;
  let max = 0;
  for (let j = 0; j < field.grid.ny; j++) {
    for (let i = 0; i < field.grid.nx; i++) {
      const s = Math.hypot(field.u_kn[j]![i]!, field.v_kn[j]![i]!);
      if (s > max) max = s;
    }
  }
  maxSpeedCache.set(field, max);
  return max;
}

/** 網格的 ENU 外框(節點範圍)。 */
export function fieldBounds(grid: FieldGrid): { minX: number; minY: number; maxX: number; maxY: number } {
  return { minX: grid.x0, minY: grid.y0, maxX: grid.x0 + (grid.nx - 1) * grid.dx, maxY: grid.y0 + (grid.ny - 1) * grid.dy };
}

/**
 * 取樣畫面可見範圍內的節點:兩軸各自以 stride = ⌈n / maxPerAxis⌉ 等距抽樣(保證 ≤ maxPerAxis × maxPerAxis 支),
 * 再只保留落在可見範圍(外擴一格)內的節點;節點值為原始資料,不內插。viewport 為 null 時取全部(抽樣後)節點。
 */
export function sampleFieldArrows(field: CurrentFieldSpec, viewport: FieldViewport | null, maxPerAxis = FIELD_MAX_SAMPLES_PER_AXIS): FieldSampling {
  const g = field.grid;
  const strideX = Math.max(1, Math.ceil(g.nx / Math.max(1, maxPerAxis)));
  const strideY = Math.max(1, Math.ceil(g.ny / Math.max(1, maxPerAxis)));
  const arrows: FieldArrow[] = [];
  const minX = viewport ? viewport.cx - viewport.halfWidth_m - g.dx * strideX : Number.NEGATIVE_INFINITY;
  const maxX = viewport ? viewport.cx + viewport.halfWidth_m + g.dx * strideX : Number.POSITIVE_INFINITY;
  const minY = viewport ? viewport.cy - viewport.halfHeight_m - g.dy * strideY : Number.NEGATIVE_INFINITY;
  const maxY = viewport ? viewport.cy + viewport.halfHeight_m + g.dy * strideY : Number.POSITIVE_INFINITY;
  for (let j = 0; j < g.ny; j += strideY) {
    const y = g.y0 + j * g.dy;
    if (y < minY || y > maxY) continue;
    for (let i = 0; i < g.nx; i += strideX) {
      const x = g.x0 + i * g.dx;
      if (x < minX || x > maxX) continue;
      const u = field.u_kn[j]![i]!;
      const v = field.v_kn[j]![i]!;
      arrows.push({ i, j, x, y, u_kn: u, v_kn: v, speed_kn: Math.hypot(u, v) });
    }
  }
  return { arrows, strideX, strideY, cell_m: Math.min(g.dx * strideX, g.dy * strideY) };
}

/** 箭頭像素長度:格距的 0.8 倍 × (流速 / 最大流速),上限 maxPx;流速 0 → 0(畫成點)。 */
export function arrowLengthPx(speed_kn: number, maxSpeed_kn: number, cellPx: number, maxPx = 60): number {
  if (speed_kn <= 0 || maxSpeed_kn <= 0) return 0;
  const len = cellPx * 0.8 * (speed_kn / maxSpeed_kn);
  return Math.min(maxPx, Math.max(0, len));
}

/**
 * 流速顏色(依最大流速正規化):低速偏青藍(色相 200°)→ 高速偏紅(色相 0°),飽和/亮度固定;
 * 回傳 CSS hsl() 字串;maxSpeed 為 0 時取中性灰。
 */
export function speedColor(speed_kn: number, maxSpeed_kn: number, alpha = 1): string {
  if (maxSpeed_kn <= 0) return alpha >= 1 ? 'hsl(0 0% 60%)' : `hsl(0 0% 60% / ${alpha})`;
  const ratio = Math.min(1, Math.max(0, speed_kn / maxSpeed_kn));
  const hue = Math.round(200 * (1 - ratio));
  return alpha >= 1 ? `hsl(${hue} 80% 55%)` : `hsl(${hue} 80% 55% / ${alpha})`;
}

/** 推得 k(t) 所需的狀態欄位(state.current 為自船位置的值、state.currentSource 標示場是否作用中)。 */
export interface FieldScaleInput {
  pos: { x: number; y: number };
  current: { set: number; drift: number };
  currentSource?: string | undefined;
}

/** 場的作用狀態與 timeScale 比例。 */
export interface FieldScale {
  /** 場是否作用中(state.currentSource 以 field 開頭;常數流/被 setEnvironment 覆寫後為 false) */
  active: boolean;
  /** 當前比例 k(t) ∈ [0, 1];無法推得(場在自船位置幾乎為零、timeScale none、場未作用)時為 1 */
  k: number;
  /** k 是否由狀態推得(否則為預設 1) */
  derived: boolean;
}

/** 推得 k 時場在自船位置的最小流速(kn);低於此值不以商推 k。 */
export const FIELD_SCALE_MIN_SPEED_KN = 0.05;

/**
 * 由狀態推得 timeScale 當前比例:核心把 k(t)·field(自船位置) 寫進 state.current,故 k = |state.current| / |field(自船位置)|
 *(夾到 [0, 1]);timeScale none、場未作用或場在自船位置幾乎為零時 k = 1。state 為 null(情境編輯器預覽)時 active 依場存在、k = 1。
 */
export function fieldScaleFromState(field: CurrentFieldSpec, state: FieldScaleInput | null): FieldScale {
  if (!state) return { active: true, k: 1, derived: false };
  const active = typeof state.currentSource === 'string' && state.currentSource.startsWith('field');
  if (!active) return { active: false, k: 1, derived: false };
  if (field.timeScale === 'none') return { active: true, k: 1, derived: false };
  const at = fieldVectorAt(field, state.pos.x, state.pos.y);
  const base = Math.hypot(at.u_kn, at.v_kn);
  if (base < FIELD_SCALE_MIN_SPEED_KN) return { active: true, k: 1, derived: false };
  return { active: true, k: Math.min(1, Math.max(0, state.current.drift / base)), derived: true };
}

// ------------------------------------------------------------------ 網格產生器(環境進階面板)

/** 均勻流 → 全部節點同一向量(set 去向、drift kn),ny 列 × nx 行;值四捨五入到 0.001 kn。 */
export function uniformFieldArrays(nx: number, ny: number, set_deg: number, drift_kn: number): { u_kn: number[][]; v_kn: number[][] } {
  const { u_kn, v_kn } = setDriftToVector(set_deg, drift_kn);
  const u = round3(u_kn);
  const v = round3(v_kn);
  return { u_kn: Array.from({ length: ny }, () => Array.from({ length: nx }, () => u)), v_kn: Array.from({ length: ny }, () => Array.from({ length: nx }, () => v)) };
}

export type FieldAxis = 'x' | 'y';

/**
 * 由兩端流速線性內插:沿 axis(x = 西→東、y = 南→北)從端點 A(i = 0 或 j = 0)到端點 B(i = nx−1 或 j = ny−1)
 * 以東/北分量線性內插(方向跨 360° 時向量內插,不會繞遠路);另一軸相同。節點數 1 時取端點 A。
 */
export function linearFieldArrays(nx: number, ny: number, axis: FieldAxis, endA: { set: number; drift: number }, endB: { set: number; drift: number }): { u_kn: number[][]; v_kn: number[][] } {
  const a = setDriftToVector(endA.set, endA.drift);
  const b = setDriftToVector(endB.set, endB.drift);
  const n = axis === 'x' ? nx : ny;
  const u_kn: number[][] = [];
  const v_kn: number[][] = [];
  for (let j = 0; j < ny; j++) {
    const ru: number[] = [];
    const rv: number[] = [];
    for (let i = 0; i < nx; i++) {
      const t = n <= 1 ? 0 : (axis === 'x' ? i : j) / (n - 1);
      ru.push(round3(a.u_kn + (b.u_kn - a.u_kn) * t));
      rv.push(round3(a.v_kn + (b.v_kn - a.v_kn) * t));
    }
    u_kn.push(ru);
    v_kn.push(rv);
  }
  return { u_kn, v_kn };
}

function round3(v: number): number {
  const r = Math.round(v * 1000) / 1000;
  return r === 0 ? 0 : r; // 避免 -0
}
