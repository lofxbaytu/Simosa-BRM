// 簡化 ECDIS 投影(lib/ecdis/projection):北上/航向上的 ENU ↔ 畫面轉換與往返、航向無效退回北上、量程上下級、方位/距離、
// 原點反推(latLonToEnu 的反函數)、EBL 線端點與點選反算、VRM 半徑、速度向量終點、比例尺。
import { describe, expect, it } from 'vitest';
import {
  CHART_RANGES_NM,
  bearingDistance,
  chartRotation,
  chartToEnu,
  eblLine,
  eblVrmFromChart,
  enuToChart,
  enuToLatLon,
  inferOrigin,
  pxPerMetre,
  scaleBarMetres,
  stepRange,
  vectorEnd,
  vrmRadiusPx,
  type ChartView,
} from '../src/lib/ecdis/projection.js';
import { latLonToEnu } from '../src/lib/timeline.js';
import { M_PER_NM } from '../src/radar/geometry.js';

function view(rotationDeg = 0, centre = { x: 1000, y: 2000 }, rangeNm = 3): ChartView {
  return { width: 800, height: 600, centreX: centre.x, centreY: centre.y, rangeNm, rotationDeg };
}

describe('ECDIS 投影', () => {
  it('北上:中心在畫面中央、北向上、東向右;3 nm 量程 = 半高 300 px', () => {
    const v = view(0);
    expect(enuToChart(v, 1000, 2000)).toEqual({ x: 400, y: 300 });
    const north = enuToChart(v, 1000, 2000 + 3 * M_PER_NM);
    expect(north.x).toBeCloseTo(400, 6);
    expect(north.y).toBeCloseTo(0, 6);
    const east = enuToChart(v, 1000 + 1852, 2000);
    expect(east.x).toBeCloseTo(400 + 100, 6);
    expect(east.y).toBeCloseTo(300, 6);
    expect(pxPerMetre(v) * 3 * M_PER_NM).toBeCloseTo(300, 9);
  });

  it('航向上 090:北在畫面左方;往返轉換一致', () => {
    const v = view(90);
    const north = enuToChart(v, 1000, 2000 + 1852);
    expect(north.x).toBeCloseTo(300, 6);
    expect(north.y).toBeCloseTo(300, 6);
    const p = chartToEnu(v, 123, 456);
    const back = enuToChart(v, p.x, p.y);
    expect(back.x).toBeCloseTo(123, 6);
    expect(back.y).toBeCloseTo(456, 6);
  });

  it('chartRotation:北上 0;航向上用參考航向;電羅經無效退回北上', () => {
    expect(chartRotation('north-up', 123)).toBe(0);
    expect(chartRotation('course-up', 123.4)).toBeCloseTo(123.4);
    expect(chartRotation('course-up', -10)).toBe(350);
    expect(chartRotation('course-up', 123, false)).toBe(0);
  });

  it('量程上下級在 0.25–24 nm 內;未知值回預設 3', () => {
    expect(stepRange(3, 1)).toBe(6);
    expect(stepRange(3, -1)).toBe(1.5);
    expect(stepRange(24, 1)).toBe(24);
    expect(stepRange(0.25, -1)).toBe(0.25);
    expect(stepRange(7, 1)).toBe(3);
    expect(CHART_RANGES_NM[0]).toBe(0.25);
    expect(CHART_RANGES_NM[CHART_RANGES_NM.length - 1]).toBe(24);
  });

  it('bearingDistance:東北 1 nm', () => {
    const d = 1852 / Math.SQRT2;
    const r = bearingDistance({ x: 0, y: 0 }, { x: d, y: d });
    expect(r.bearing_deg).toBeCloseTo(45, 6);
    expect(r.distance_nm).toBeCloseTo(1, 6);
    expect(bearingDistance({ x: 0, y: 0 }, { x: 0, y: 0 }).bearing_deg).toBe(0);
  });

  it('inferOrigin 反推核心原點,與 latLonToEnu 往返一致;enuToLatLon 為反函數', () => {
    const origin = { lat: 22.53, lon: 120.24 };
    const p = latLonToEnu(origin, 22.41174, 120.1127);
    const o = inferOrigin({ lat: 22.41174, lon: 120.1127, x: p.x, y: p.y });
    expect(o).not.toBeNull();
    expect(o!.lat).toBeCloseTo(origin.lat, 6);
    expect(o!.lon).toBeCloseTo(origin.lon, 6);
    const ll = enuToLatLon(origin, p.x, p.y);
    expect(ll.lat).toBeCloseTo(22.41174, 8);
    expect(ll.lon).toBeCloseTo(120.1127, 8);
    expect(inferOrigin({ lat: 0, lon: 0, x: 10, y: 10 })).toBeNull();
  });

  it('EBL 線沿真方位(航向上時依畫面旋轉);點選反算回方位與距離;VRM 半徑', () => {
    const v = view(90);
    const own = { x: 1000, y: 2000 };
    const l = eblLine(v, own, 90); // 真方位 090 在航向上 090 的畫面為正上方
    expect(l.to.x).toBeCloseTo(l.from.x, 6);
    expect(l.to.y).toBeLessThan(l.from.y);
    const r = eblVrmFromChart(v, own, 400, 300 - 100); // 正上方 100 px = 1 nm
    expect(r.bearing_deg).toBeCloseTo(90, 6);
    expect(r.range_nm).toBeCloseTo(1, 6);
    expect(vrmRadiusPx(v, 1.5)).toBeCloseTo(150, 9);
  });

  it('vectorEnd:10 kn 向 090 的 6 分鐘向量 = 1 nm 向東;比例尺取整數距離', () => {
    const e = vectorEnd({ x: 0, y: 0 }, 90, 10, 6);
    expect(e.x).toBeCloseTo(1852, 6);
    expect(e.y).toBeCloseTo(0, 6);
    expect(scaleBarMetres(view(0))).toBe(2 * M_PER_NM); // 800 px / 5 = 160 px ≈ 1.6 nm → 2 nm
    expect(scaleBarMetres(view(0, { x: 0, y: 0 }, 0.25))).toBe(500);
  });
});
