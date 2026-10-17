// PPI 座標轉換(純函數,可單元測試):定向模式(艏向上/北向上/航向上)、真/相對運動、偏心、
// 方位/距離 ↔ 畫面座標、ENU ↔ 畫面座標、EBL/VRM 讀數、平行索引線與保護區幾何。
// 畫面座標:x 向右、y 向下(canvas);方位以度、順時針、0 = 真北;距離以 nm。

import { normalizeHeading, normalizeRelative } from '../lib/angles.js';

export const M_PER_NM = 1852;
const RAD = Math.PI / 180;

export type Orientation = 'head-up' | 'north-up' | 'course-up';
export type Motion = 'relative' | 'true';
export type BearingRef = 'T' | 'R';

export const ORIENTATION_LABEL: Record<Orientation, string> = {
  'head-up': 'H UP 艏向上',
  'north-up': 'N UP 北向上',
  'course-up': 'C UP 航向上',
};

export interface Point {
  x: number;
  y: number;
}

/** 某一時刻的 PPI 幾何。 */
export interface PpiView {
  /** PPI 圓心(px) */
  cx: number;
  cy: number;
  /** PPI 圓半徑(px) */
  radiusPx: number;
  /** 顯示量程(nm):圓半徑代表的距離 */
  rangeNm: number;
  /** 畫面正上方所指的真方位(度):北向上 0、艏向上 = 航向、航向向上 = 參考航向 */
  rotationDeg: number;
  /** 自船(CCRP)畫面位置(px):偏心或真運動時不在圓心 */
  ownX: number;
  ownY: number;
}

/**
 * 畫面向上的真方位。航向無效時只能艏向上且影像未穩定化(rotation 0,方位皆為相對)。
 * 航向向上使用選擇該模式時的參考航向(courseRefDeg),不隨 COG 抖動。
 */
export function orientationRotation(
  mode: Orientation,
  headingDeg: number,
  courseRefDeg: number,
  headingValid = true,
): number {
  if (!headingValid) return 0;
  switch (mode) {
    case 'north-up':
      return 0;
    case 'head-up':
      return normalizeHeading(headingDeg);
    case 'course-up':
      return normalizeHeading(courseRefDeg);
  }
}

export function pxPerNm(view: Pick<PpiView, 'radiusPx' | 'rangeNm'>): number {
  return view.radiusPx / view.rangeNm;
}

/** 由畫面原點(預設自船)沿真方位、距離(nm)求畫面座標。 */
export function polarToScreen(view: PpiView, trueBearingDeg: number, rangeNm: number, origin?: Point): Point {
  const o = origin ?? { x: view.ownX, y: view.ownY };
  const a = (trueBearingDeg - view.rotationDeg) * RAD;
  const r = rangeNm * pxPerNm(view);
  return { x: o.x + r * Math.sin(a), y: o.y - r * Math.cos(a) };
}

/** 畫面座標 → 相對自船(預設)的真方位(度,0–360)與距離(nm)。 */
export function screenToPolar(view: PpiView, x: number, y: number, origin?: Point): { bearing: number; range: number } {
  const o = origin ?? { x: view.ownX, y: view.ownY };
  const dx = x - o.x;
  const dy = y - o.y;
  const range = Math.hypot(dx, dy) / pxPerNm(view);
  if (range === 0) return { bearing: normalizeHeading(view.rotationDeg), range: 0 };
  const a = Math.atan2(dx, -dy) / RAD;
  return { bearing: normalizeHeading(a + view.rotationDeg), range };
}

/** 相對自船的 ENU 位移(m;x 東、y 北)→ 畫面座標。 */
export function enuToScreen(view: PpiView, dE: number, dN: number, origin?: Point): Point {
  const o = origin ?? { x: view.ownX, y: view.ownY };
  const k = pxPerNm(view) / M_PER_NM;
  const c = Math.cos(view.rotationDeg * RAD);
  const s = Math.sin(view.rotationDeg * RAD);
  return { x: o.x + k * (dE * c - dN * s), y: o.y - k * (dE * s + dN * c) };
}

/** 畫面座標 → 相對自船的 ENU 位移(m)。 */
export function screenToEnu(view: PpiView, x: number, y: number, origin?: Point): { dE: number; dN: number } {
  const o = origin ?? { x: view.ownX, y: view.ownY };
  const k = pxPerNm(view) / M_PER_NM;
  const u = (x - o.x) / k; // 畫面右
  const v = -(y - o.y) / k; // 畫面上
  const c = Math.cos(view.rotationDeg * RAD);
  const s = Math.sin(view.rotationDeg * RAD);
  return { dE: u * c + v * s, dN: -u * s + v * c };
}

/** 天線相對 CCRP 的 ENU 位移(m):fwd 向艏為正、stbd 向右舷為正。 */
export function antennaOffsetEnu(headingDeg: number, fwdM: number, stbdM: number): { dE: number; dN: number } {
  const h = headingDeg * RAD;
  return { dE: fwdM * Math.sin(h) + stbdM * Math.cos(h), dN: fwdM * Math.cos(h) - stbdM * Math.sin(h) };
}

/** 點是否在 PPI 圓內 */
export function insidePpi(view: PpiView, x: number, y: number, marginPx = 0): boolean {
  return Math.hypot(x - view.cx, y - view.cy) <= view.radiusPx + marginPx;
}

/** 由 p 沿單位向量 u 的射線與 PPI 圓的交點參數 t(px;p 在圓內時 > 0)。p 在圓外且不相交時回傳 null。 */
function rayCircle(view: PpiView, p: Point, u: Point): number | null {
  const fx = p.x - view.cx;
  const fy = p.y - view.cy;
  const b = fx * u.x + fy * u.y;
  const c = fx * fx + fy * fy - view.radiusPx * view.radiusPx;
  const disc = b * b - c;
  if (disc < 0) return null;
  const t = -b + Math.sqrt(disc);
  return t >= 0 ? t : null;
}

/** 由自船沿真方位到 PPI 圓邊的線段(艏向線、EBL)。 */
export function bearingLine(view: PpiView, trueBearingDeg: number, origin?: Point): { from: Point; to: Point } {
  const o = origin ?? { x: view.ownX, y: view.ownY };
  const a = (trueBearingDeg - view.rotationDeg) * RAD;
  const u = { x: Math.sin(a), y: -Math.cos(a) };
  const t = rayCircle(view, o, u) ?? 0;
  return { from: o, to: { x: o.x + u.x * t, y: o.y + u.y * t } };
}

/**
 * 平行索引線:與真方位 bearing 平行、垂直距離 offsetNm(正 = 沿該方位看去的右側)的直線與 PPI 圓的交段。
 * 不與圓相交時回傳 null。
 */
export function parallelIndexLine(view: PpiView, trueBearingDeg: number, offsetNm: number): { from: Point; to: Point } | null {
  const a = (trueBearingDeg - view.rotationDeg) * RAD;
  const u = { x: Math.sin(a), y: -Math.cos(a) };
  const n = { x: Math.cos(a), y: Math.sin(a) }; // u 順時針轉 90°(畫面右側)
  const d = offsetNm * pxPerNm(view);
  const p = { x: view.ownX + n.x * d, y: view.ownY + n.y * d };
  // 直線 p + s u 與圓:|p + s u − c|² = R²
  const fx = p.x - view.cx;
  const fy = p.y - view.cy;
  const b = fx * u.x + fy * u.y;
  const c = fx * fx + fy * fy - view.radiusPx * view.radiusPx;
  const disc = b * b - c;
  if (disc <= 0) return null;
  const sq = Math.sqrt(disc);
  const s1 = -b - sq;
  const s2 = -b + sq;
  return { from: { x: p.x + u.x * s1, y: p.y + u.y * s1 }, to: { x: p.x + u.x * s2, y: p.y + u.y * s2 } };
}

/** 點到直線(過 a、b)的有號垂直距離(px;正 = 由 a 往 b 看去的右側) */
export function signedDistanceToLine(a: Point, b: Point, p: Point): number {
  const ux = b.x - a.x;
  const uy = b.y - a.y;
  const len = Math.hypot(ux, uy);
  if (len === 0) return Math.hypot(p.x - a.x, p.y - a.y);
  // 畫面座標 y 向下:u 在畫面上順時針轉 90° 為 (−uy, ux),即「右側」單位法向量
  return ((p.x - a.x) * -uy + (p.y - a.y) * ux) / len;
}

// ---------------------------------------------------------------------------------------------
// EBL / VRM

export interface Ebl {
  on: boolean;
  /** 方位值(依 ref:真方位或相對艏向) */
  bearing: number;
  ref: BearingRef;
}

export interface Vrm {
  on: boolean;
  rangeNm: number;
}

/** EBL 的真方位與相對方位(度,0–360)。 */
export function eblBearings(ebl: Pick<Ebl, 'bearing' | 'ref'>, headingDeg: number): { trueDeg: number; relDeg: number } {
  const trueDeg = ebl.ref === 'T' ? normalizeHeading(ebl.bearing) : normalizeHeading(ebl.bearing + headingDeg);
  return { trueDeg, relDeg: normalizeHeading(trueDeg - headingDeg) };
}

/** EBL 讀數文字,例:"045.0° T"、"350.0° R"。 */
export function eblReadout(ebl: Pick<Ebl, 'bearing' | 'ref'>, headingDeg: number): string {
  const { trueDeg, relDeg } = eblBearings(ebl, headingDeg);
  const v = ebl.ref === 'T' ? trueDeg : relDeg;
  return `${formatBearing(v)} ${ebl.ref}`;
}

/** 方位三位數一位小數,例:5.04 → "005.0°";359.96 → "000.0°" */
export function formatBearing(deg: number): string {
  if (!Number.isFinite(deg)) return '---.-°';
  let r = Math.round(normalizeHeading(deg) * 10) / 10;
  if (r >= 360) r -= 360;
  const whole = Math.floor(r);
  return `${String(whole).padStart(3, '0')}.${Math.round((r - whole) * 10)}°`;
}

/** 距離讀數(nm):< 1 nm 三位小數、< 10 nm 兩位、其餘一位。 */
export function formatRangeNm(nm: number): string {
  if (!Number.isFinite(nm)) return '-.--';
  const a = Math.abs(nm);
  return a < 1 ? nm.toFixed(3) : a < 10 ? nm.toFixed(2) : nm.toFixed(1);
}

/** VRM 讀數文字,例:"1.50 NM" */
export function vrmReadout(vrm: Pick<Vrm, 'rangeNm'>): string {
  return `${formatRangeNm(vrm.rangeNm)} NM`;
}

/** 把畫面點擊點轉為 EBL 方位(依 ref)與 VRM 距離。 */
export function eblVrmFromScreen(
  view: PpiView,
  x: number,
  y: number,
  ref: BearingRef,
  headingDeg: number,
): { bearing: number; rangeNm: number } {
  const p = screenToPolar(view, x, y);
  return { bearing: ref === 'T' ? p.bearing : normalizeHeading(p.bearing - headingDeg), rangeNm: p.range };
}

// ---------------------------------------------------------------------------------------------
// 偏心與真運動

/** 偏心上限(圓半徑比例;IEC 62388:至少 50%、不超過 75%) */
export const MAX_OFFCENTRE = 0.66;

/** 限制偏心位移(px,相對圓心)不超過 MAX_OFFCENTRE × 半徑。 */
export function clampOffcentre(dx: number, dy: number, radiusPx: number, max = MAX_OFFCENTRE): Point {
  const lim = max * radiusPx;
  const d = Math.hypot(dx, dy);
  if (d <= lim || d === 0) return { x: dx, y: dy };
  return { x: (dx * lim) / d, y: (dy * lim) / d };
}

/**
 * 真運動重設:自船畫面位置(相對圓心的偏移比例 fx, fy,單位 = 半徑)超過 limit 時,
 * 把自船重設到航向反方向 reset 比例處(使前方可視範圍最大)。回傳新的偏移比例,或 null(不需重設)。
 */
export function trueMotionReset(
  fx: number,
  fy: number,
  courseScreenDeg: number,
  limit = 0.7,
  reset = 0.6,
): Point | null {
  if (Math.hypot(fx, fy) <= limit) return null;
  const a = courseScreenDeg * RAD;
  return { x: -Math.sin(a) * reset, y: Math.cos(a) * reset };
}

/** 偏心或真運動時所需的影像量程(nm):影像要涵蓋從自船到 PPI 圓最遠邊的距離。 */
export function requiredVideoRange(displayRangeNm: number, offcentreFraction: number): number {
  return displayRangeNm * (1 + Math.min(1, Math.max(0, offcentreFraction)));
}

// ---------------------------------------------------------------------------------------------
// 保護區

/** 保護區方位範圍轉為真方位(from → to 順時針);全周時 full = true。 */
export function guardZoneTrueSector(
  gz: { from: number; to: number; relative: boolean },
  headingDeg: number,
): { from: number; to: number; full: boolean } {
  const full =
    Math.abs(gz.to - gz.from) >= 360 - 1e-9 ||
    (normalizeHeading(gz.from) === normalizeHeading(gz.to) && gz.to !== gz.from);
  const off = gz.relative ? headingDeg : 0;
  return { from: normalizeHeading(gz.from + off), to: normalizeHeading(gz.to + off), full };
}

/** 方位 b 是否落在 from → to(順時針)之間 */
export function bearingInSector(b: number, from: number, to: number): boolean {
  const x = normalizeHeading(b);
  const f = normalizeHeading(from);
  const t = normalizeHeading(to);
  return f <= t ? x >= f && x <= t : x >= f || x <= t;
}

/** 兩方位差(−180, 180] */
export function bearingDiff(a: number, b: number): number {
  return normalizeRelative(b - a);
}
