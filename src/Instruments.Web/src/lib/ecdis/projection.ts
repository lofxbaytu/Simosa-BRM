// 簡化 ECDIS 的海圖投影與幾何(docs/design/ecdis-lite.md 第 2 節):畫面中心、量程、北上/航向上旋轉、ENU ↔ 畫面座標、
// 游標方位/距離、EBL/VRM(兩組)。座標換算沿用雷達頁 radar/geometry 的純函數(以 ChartView 轉成等價 PpiView),
// 經緯度 ↔ ENU 沿用 lib/timeline 的平面近似(111 320 m/度),與教官站航跡圖、核心示意座標一致(公尺級差異只供顯示)。

import { M_PER_NM, enuToScreen, screenToEnu, screenToPolar, type Point, type PpiView } from '../../radar/geometry.js';
import { normalizeHeading } from '../angles.js';
import { latLonToEnu } from '../timeline.js';

export type ChartOrientation = 'north-up' | 'course-up';

export const CHART_ORIENTATION_LABEL: Readonly<Record<ChartOrientation, string>> = {
  'north-up': 'N UP 北上',
  'course-up': 'C UP 航向上',
};

/** 可選量程(nm;畫面中心到上緣的距離)。 */
export const CHART_RANGES_NM: readonly number[] = [0.25, 0.5, 0.75, 1.5, 3, 6, 12, 24] as const;

export const DEFAULT_RANGE_NM = 3;

/** 速度向量長度(分鐘;IEC 61174 常用 6 分鐘)。 */
export const VECTOR_MINUTES = 6;

export interface ChartView {
  /** 畫布尺寸(CSS px) */
  width: number;
  height: number;
  /** 畫面中心的 ENU 座標 (m) */
  centreX: number;
  centreY: number;
  /** 量程 (nm):畫面中心到上緣的距離 */
  rangeNm: number;
  /** 畫面正上方所指的真方位(北上 0、航向上 = 參考航向) */
  rotationDeg: number;
}

/** 每公尺像素數。 */
export function pxPerMetre(view: Pick<ChartView, 'height' | 'rangeNm'>): number {
  return view.height / 2 / (view.rangeNm * M_PER_NM);
}

/** 轉成雷達幾何的 PpiView(圓心 = 畫面中心、半徑 = 半高),以沿用 enuToScreen/screenToPolar 等函數。 */
export function toPpiView(view: ChartView): PpiView {
  return { cx: view.width / 2, cy: view.height / 2, radiusPx: view.height / 2, rangeNm: view.rangeNm, rotationDeg: view.rotationDeg, ownX: view.width / 2, ownY: view.height / 2 };
}

/** ENU (m) → 畫面座標(px;x 向右、y 向下)。 */
export function enuToChart(view: ChartView, x: number, y: number): Point {
  return enuToScreen(toPpiView(view), x - view.centreX, y - view.centreY);
}

/** 畫面座標 → ENU (m)。 */
export function chartToEnu(view: ChartView, px: number, py: number): { x: number; y: number } {
  const d = screenToEnu(toPpiView(view), px, py);
  return { x: view.centreX + d.dE, y: view.centreY + d.dN };
}

/**
 * 畫面向上的真方位:北上 0;航向上用選擇該模式時擷取的參考航向(不隨艏搖抖動),電羅經無效時退回北上
 * (無穩定航向可用,與雷達頁「航向無效只能艏向上」的概念相反:海圖以地理為基準,因此退回北上)。
 */
export function chartRotation(orientation: ChartOrientation, courseRefDeg: number, headingValid = true): number {
  if (orientation === 'north-up' || !headingValid) return 0;
  return normalizeHeading(courseRefDeg);
}

/** 量程上下一級(超出範圍時不變)。 */
export function stepRange(current: number, dir: 1 | -1): number {
  const i = CHART_RANGES_NM.indexOf(current);
  if (i < 0) return DEFAULT_RANGE_NM;
  const j = Math.min(CHART_RANGES_NM.length - 1, Math.max(0, i + dir));
  return CHART_RANGES_NM[j] ?? current;
}

export interface BearingDistance {
  /** 真方位(度,0–360) */
  bearing_deg: number;
  distance_m: number;
  distance_nm: number;
}

/** 由 from 看 to 的真方位與距離(ENU)。 */
export function bearingDistance(from: { x: number; y: number }, to: { x: number; y: number }): BearingDistance {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const distance_m = Math.hypot(dx, dy);
  const bearing_deg = distance_m === 0 ? 0 : normalizeHeading((Math.atan2(dx, dy) * 180) / Math.PI);
  return { bearing_deg, distance_m, distance_nm: distance_m / M_PER_NM };
}

/** ENU (m) → 經緯度(latLonToEnu 的反函數;同一平面近似)。 */
export function enuToLatLon(origin: { lat: number; lon: number }, x: number, y: number): { lat: number; lon: number } {
  const latRad = (origin.lat * Math.PI) / 180;
  return { lat: origin.lat + y / 111_320, lon: origin.lon + x / (111_320 * Math.cos(latRad)) };
}

/**
 * 由狀態的位置(lat/lon 與 x/y 同時存在)反推核心的本地原點:使 latLonToEnu(origin, lat, lon) = (x, y)。
 * 情境不明(無 scenario id 或清單找不到)時用來把 data/areas 的示意幾何疊到正確位置;lat/lon 皆 0 視為無效。
 */
export function inferOrigin(pos: { lat: number; lon: number; x: number; y: number }): { lat: number; lon: number } | null {
  if (!Number.isFinite(pos.lat) || !Number.isFinite(pos.lon) || (pos.lat === 0 && pos.lon === 0)) return null;
  const lat0 = pos.lat - pos.y / 111_320;
  const lon0 = pos.lon - pos.x / (111_320 * Math.cos((lat0 * Math.PI) / 180));
  const o = { lat: lat0, lon: lon0 };
  const back = latLonToEnu(o, pos.lat, pos.lon);
  return Math.abs(back.x - pos.x) < 1 && Math.abs(back.y - pos.y) < 1 ? o : null;
}

/** 速度向量終點:自 pos 沿 cog 以 sog 行駛 minutes 分鐘。 */
export function vectorEnd(pos: { x: number; y: number }, cogDeg: number, sogKn: number, minutes = VECTOR_MINUTES): { x: number; y: number } {
  const d = sogKn * M_PER_NM * (minutes / 60);
  const a = (cogDeg * Math.PI) / 180;
  return { x: pos.x + d * Math.sin(a), y: pos.y + d * Math.cos(a) };
}

// ------------------------------------------------------------------ EBL / VRM(兩組,以自船為原點、真方位)

export interface ChartEbl {
  on: boolean;
  /** 真方位(度) */
  bearing_deg: number;
}

export interface ChartVrm {
  on: boolean;
  range_nm: number;
}

/** 自船畫面位置沿真方位畫到畫面外的線段端點。 */
export function eblLine(view: ChartView, own: { x: number; y: number }, bearingDeg: number): { from: Point; to: Point } {
  const from = enuToChart(view, own.x, own.y);
  const len = Math.hypot(view.width, view.height) + 10;
  const a = ((bearingDeg - view.rotationDeg) * Math.PI) / 180;
  return { from, to: { x: from.x + len * Math.sin(a), y: from.y - len * Math.cos(a) } };
}

/** VRM 圓半徑(px)。 */
export function vrmRadiusPx(view: ChartView, rangeNm: number): number {
  return rangeNm * M_PER_NM * pxPerMetre(view);
}

/** 畫面點擊點 → 相對自船的真方位(EBL)與距離 nm(VRM)。 */
export function eblVrmFromChart(view: ChartView, own: { x: number; y: number }, px: number, py: number): { bearing_deg: number; range_nm: number } {
  const o = enuToChart(view, own.x, own.y);
  const p = screenToPolar(toPpiView(view), px, py, o);
  return { bearing_deg: p.bearing, range_nm: p.range };
}

/** 距離圈間距(nm):量程的四分之一。 */
export function rangeRingSpacingNm(rangeNm: number): number {
  return rangeNm / 4;
}

/** 比例尺長度(m):畫面寬度約 1/5 的「整數」距離(10 m … 10 nm)。 */
export function scaleBarMetres(view: ChartView): number {
  const target = (view.width / 5) / pxPerMetre(view);
  const steps = [10, 20, 50, 100, 200, 500, 1000, M_PER_NM, 2 * M_PER_NM, 5 * M_PER_NM, 10 * M_PER_NM];
  for (const s of steps) if (s >= target) return s;
  return steps[steps.length - 1]!;
}
