// 角度工具:航向正規化、最短角差、度/弧度換算。

export const DEG_PER_RAD = 180 / Math.PI;
export const RAD_PER_DEG = Math.PI / 180;

/** 把任意角度正規化到 [0, 360)。NaN/無限大回傳 NaN。 */
export function normalizeHeading(deg: number): number {
  if (!Number.isFinite(deg)) return Number.NaN;
  let h = deg % 360;
  if (h < 0) h += 360;
  // 處理 -0 與浮點誤差造成的 360
  if (h >= 360) h -= 360;
  return h === 0 ? 0 : h;
}

/** 把角度正規化到 (-180, 180]。 */
export function normalizeRelative(deg: number): number {
  if (!Number.isFinite(deg)) return Number.NaN;
  let a = normalizeHeading(deg);
  if (a > 180) a -= 360;
  return a;
}

/**
 * 由 from 轉到 to 的最短角差(度),右轉為正、左轉為負,範圍 (-180, 180]。
 * 例:headingDifference(350, 10) = 20;headingDifference(10, 350) = -20。
 */
export function headingDifference(from: number, to: number): number {
  return normalizeRelative(to - from);
}

/** 相對方位:目標方位相對於自船航向,右舷正、左舷負。 */
export function relativeBearing(heading: number, bearing: number): number {
  return headingDifference(heading, bearing);
}

export function degToRad(deg: number): number {
  return deg * RAD_PER_DEG;
}

export function radToDeg(rad: number): number {
  return rad * DEG_PER_RAD;
}

/** 限幅 */
export function clamp(value: number, min: number, max: number): number {
  if (Number.isNaN(value)) return min;
  return value < min ? min : value > max ? max : value;
}

/** 節 → 公尺/秒 */
export const MPS_PER_KNOT = 1852 / 3600;

export function knotsToMps(kn: number): number {
  return kn * MPS_PER_KNOT;
}

export function mpsToKnots(mps: number): number {
  return mps / MPS_PER_KNOT;
}
