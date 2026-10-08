import { describe, expect, it } from 'vitest';
import { getShipConfig, telegraphStep } from '../src/lib/ship-config.js';
import { bodyToEnu, chooseGridSpacing, hullOutline } from '../src/lib/ship-geometry.js';

describe('船舶顯示參數(來自 data/ships/*/particulars.json)', () => {
  it('FSB1:Schilling 舵 70°、一般 35°、MCR 178 rpm', () => {
    const c = getShipConfig('FSB1');
    expect(c.lpp_m).toBe(99);
    expect(c.breadth_m).toBe(18);
    expect(c.rudderMax_deg).toBe(70);
    expect(c.rudderNormalMax_deg).toBe(35);
    expect(c.rpmMax).toBe(180);
    expect(c.rudderRate_degPerS).toBeCloseTo(65 / 19.2, 1);
    expect(telegraphStep(c, 'NAVF')).toMatchObject({ rpm: 169, speedLoaded_kn: 13 });
    expect(telegraphStep(c, 'EFAS').rpm).toBe(-150);
    expect(telegraphStep(c, 'STOP').rpm).toBe(0);
    expect(c.thrusterTurnRate_degPerMin).toBe(20);
  });
  it('FSB2:一般舵 35°、MCR 147 rpm、缺 EFAS 時用 FAS 轉速', () => {
    const c = getShipConfig('FSB2');
    expect(c.rudderMax_deg).toBe(35);
    expect(c.rudderNormalMax_deg).toBe(35);
    expect(c.rpmMax).toBe(150);
    expect(c.rudderRate_degPerS).toBeCloseTo(65 / 14, 1);
    expect(telegraphStep(c, 'EFAS').rpm).toBe(-103);
    expect(telegraphStep(c, 'FAS').rpm).toBe(-103);
    expect(c.thrusterIneffectiveAbove_kn).toBe(5);
  });
  it('倒車參考速度為負且依前進速度折算', () => {
    const c = getShipConfig('FSB1');
    const has = telegraphStep(c, 'HAS');
    expect(has.rpm).toBe(-100);
    expect(has.speedLoaded_kn).toBeLessThan(0);
    expect(has.speedLoaded_kn).toBeCloseTo(-0.6 * 7.5, 1);
  });
  it('車鐘 11 段順序由 EFAS 到 NAVF,轉速單調遞增', () => {
    for (const id of ['FSB1', 'FSB2'] as const) {
      const rpms = getShipConfig(id).telegraph.map((t) => t.rpm);
      expect(rpms).toHaveLength(11);
      for (let i = 1; i < rpms.length; i++) expect(rpms[i]!).toBeGreaterThanOrEqual(rpms[i - 1]!);
    }
  });
});

describe('船體外形與座標轉換', () => {
  it('外形依 LPP 與 B 縮放', () => {
    const pts = hullOutline(100, 20);
    const xs = pts.map((p) => p.x);
    const ys = pts.map((p) => p.y);
    expect(Math.max(...xs)).toBe(50);
    expect(Math.min(...xs)).toBe(-50);
    expect(Math.max(...ys)).toBe(10);
    expect(Math.min(...ys)).toBe(-10);
  });
  it('船體→ENU:航向 090 時艏向東', () => {
    const bow = bodyToEnu({ x: 50, y: 0 }, 90);
    expect(bow.x).toBeCloseTo(50);
    expect(bow.y).toBeCloseTo(0);
    const stbd = bodyToEnu({ x: 0, y: 10 }, 90);
    expect(stbd.x).toBeCloseTo(0);
    expect(stbd.y).toBeCloseTo(-10);
    const north = bodyToEnu({ x: 50, y: 0 }, 0, { x: 5, y: 5 });
    expect(north).toEqual({ x: 5, y: 55 });
  });
  it('格線間距隨比例尺選擇', () => {
    expect(chooseGridSpacing(1)).toBe(200);
    expect(chooseGridSpacing(0.1)).toBe(2000);
    expect(chooseGridSpacing(10)).toBe(20);
  });
});
