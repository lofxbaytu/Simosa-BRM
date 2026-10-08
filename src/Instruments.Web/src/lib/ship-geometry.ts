// 自船外形(鳥瞰圖用):以 LPP 與船寬為比例的簡化多邊形,船體座標 x 向艏、y 向右舷(公尺)。

export interface Point {
  x: number;
  y: number;
}

/** 無因次外形(x/L, y/B),艏在 +0.5,艉在 −0.5。 */
const HULL_SHAPE: ReadonlyArray<readonly [number, number]> = [
  [0.5, 0],
  [0.36, 0.38],
  [0.22, 0.5],
  [-0.4, 0.5],
  [-0.48, 0.42],
  [-0.5, 0.28],
  [-0.5, -0.28],
  [-0.48, -0.42],
  [-0.4, -0.5],
  [0.22, -0.5],
  [0.36, -0.38],
];

/** 上層建築(艉部駕駛台)無因次外形。 */
const SUPERSTRUCTURE_SHAPE: ReadonlyArray<readonly [number, number]> = [
  [-0.28, 0.36],
  [-0.44, 0.36],
  [-0.44, -0.36],
  [-0.28, -0.36],
];

export function hullOutline(lpp_m: number, breadth_m: number): Point[] {
  return HULL_SHAPE.map(([x, y]) => ({ x: x * lpp_m, y: y * breadth_m }));
}

export function superstructureOutline(lpp_m: number, breadth_m: number): Point[] {
  return SUPERSTRUCTURE_SHAPE.map(([x, y]) => ({ x: x * lpp_m, y: y * breadth_m }));
}

/**
 * 把船體座標點轉成本地 ENU(東 x、北 y):
 * east = x·sinψ + y·cosψ,north = x·cosψ − y·sinψ(ψ 為航向,度)。
 */
export function bodyToEnu(point: Point, headingDeg: number, origin: Point = { x: 0, y: 0 }): Point {
  const psi = (headingDeg * Math.PI) / 180;
  const s = Math.sin(psi);
  const c = Math.cos(psi);
  return {
    x: origin.x + point.x * s + point.y * c,
    y: origin.y + point.x * c - point.y * s,
  };
}

/** 選擇適合目前比例尺的格線間距(公尺),目標為螢幕上約 80–200 px 一格。 */
export function chooseGridSpacing(pxPerMeter: number, targetPx = 120): number {
  const raw = targetPx / pxPerMeter;
  const steps = [10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
  for (const s of steps) if (s >= raw) return s;
  return steps[steps.length - 1]!;
}
