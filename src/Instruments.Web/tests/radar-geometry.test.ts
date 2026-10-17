// PPI 座標轉換:各定向模式下方位/距離 → 畫面座標與反轉換、ENU、偏心、天線偏移、艏向線/EBL 端點、
// 平行索引線、EBL/VRM 讀數、保護區扇區、真運動重設與影像量程。
import { describe, expect, it } from 'vitest';
import {
  antennaOffsetEnu,
  bearingInSector,
  bearingLine,
  clampOffcentre,
  eblBearings,
  eblReadout,
  eblVrmFromScreen,
  enuToScreen,
  formatBearing,
  formatRangeNm,
  guardZoneTrueSector,
  orientationRotation,
  parallelIndexLine,
  polarToScreen,
  screenToEnu,
  screenToPolar,
  signedDistanceToLine,
  trueMotionReset,
  vrmReadout,
  type PpiView,
} from '../src/radar/geometry.js';

function view(rotationDeg: number, own = { x: 500, y: 500 }): PpiView {
  return { cx: 500, cy: 500, radiusPx: 400, rangeNm: 4, rotationDeg, ownX: own.x, ownY: own.y };
}

const close = (a: { x: number; y: number }, b: { x: number; y: number }, eps = 1e-6) => {
  expect(a.x).toBeCloseTo(b.x, 6);
  expect(a.y).toBeCloseTo(b.y, 6);
  void eps;
};

describe('定向模式', () => {
  it('畫面向上的真方位:北向上 0、艏向上 = 航向、航向向上 = 參考航向;航向無效一律 0', () => {
    expect(orientationRotation('north-up', 47, 40)).toBe(0);
    expect(orientationRotation('head-up', 47, 40)).toBe(47);
    expect(orientationRotation('course-up', 47, 40)).toBe(40);
    expect(orientationRotation('head-up', -10, 0)).toBe(350);
    expect(orientationRotation('north-up', 47, 40, false)).toBe(0);
    expect(orientationRotation('course-up', 47, 40, false)).toBe(0);
  });

  it('北向上:真方位 090、2 nm → 圓心右方 200 px;000 → 上方', () => {
    const v = view(0);
    close(polarToScreen(v, 90, 2), { x: 700, y: 500 });
    close(polarToScreen(v, 0, 1), { x: 500, y: 400 });
    close(polarToScreen(v, 180, 1), { x: 500, y: 600 });
    close(polarToScreen(v, 270, 4), { x: 100, y: 500 });
  });

  it('艏向上(航向 045):真方位 045 在正上方,真方位 135 在右方', () => {
    const v = view(orientationRotation('head-up', 45, 0));
    close(polarToScreen(v, 45, 2), { x: 500, y: 300 });
    close(polarToScreen(v, 135, 2), { x: 700, y: 500 });
    close(polarToScreen(v, 0, 2), { x: 500 - 200 * Math.SQRT1_2, y: 500 - 200 * Math.SQRT1_2 });
  });

  it('航向向上(參考航向 300):真方位 300 在正上方、030 在右方', () => {
    const v = view(orientationRotation('course-up', 10, 300));
    close(polarToScreen(v, 300, 1), { x: 500, y: 400 });
    close(polarToScreen(v, 30, 1), { x: 600, y: 500 });
  });

  it('screenToPolar 是 polarToScreen 的反函數(各定向)', () => {
    for (const rot of [0, 45, 123.4, 300]) {
      const v = view(rot, { x: 430, y: 610 });
      for (const [b, r] of [
        [0, 1],
        [37.5, 2.2],
        [181, 3.9],
        [359.5, 0.4],
      ] as const) {
        const p = polarToScreen(v, b, r);
        const q = screenToPolar(v, p.x, p.y);
        expect(q.range).toBeCloseTo(r, 9);
        expect(Math.abs(((q.bearing - b + 540) % 360) - 180)).toBeLessThan(1e-9);
      }
    }
  });

  it('ENU → 畫面與極座標一致;screenToEnu 為反函數', () => {
    for (const rot of [0, 90, 211]) {
      const v = view(rot);
      const b = 60;
      const rNm = 1.5;
      const dE = rNm * 1852 * Math.sin((b * Math.PI) / 180);
      const dN = rNm * 1852 * Math.cos((b * Math.PI) / 180);
      close(enuToScreen(v, dE, dN), polarToScreen(v, b, rNm));
      const p = enuToScreen(v, dE, dN);
      const back = screenToEnu(v, p.x, p.y);
      expect(back.dE).toBeCloseTo(dE, 6);
      expect(back.dN).toBeCloseTo(dN, 6);
    }
  });

  it('偏心:自船不在圓心時座標以自船為原點;偏心限制 66% 半徑', () => {
    const v = view(0, { x: 500, y: 700 });
    close(polarToScreen(v, 0, 1), { x: 500, y: 600 });
    const q = screenToPolar(v, 500, 500);
    expect(q.bearing).toBe(0);
    expect(q.range).toBeCloseTo(2, 9);
    const c = clampOffcentre(0, 400, 400);
    expect(c.y).toBeCloseTo(264, 9);
    expect(clampOffcentre(10, -20, 400)).toEqual({ x: 10, y: -20 });
  });

  it('天線偏移:航向 090、天線在船舯後 30 m → 自船西方 30 m', () => {
    const a = antennaOffsetEnu(90, -30, 0);
    expect(a.dE).toBeCloseTo(-30, 9);
    expect(a.dN).toBeCloseTo(0, 9);
    const b = antennaOffsetEnu(0, 0, 5);
    expect(b.dE).toBeCloseTo(5, 9);
  });
});

describe('艏向線、EBL、VRM、平行索引線', () => {
  it('艏向線/EBL 由自船到圓邊(偏心時也在圓上)', () => {
    const v = view(0, { x: 500, y: 700 });
    const l = bearingLine(v, 0);
    expect(l.to.x).toBeCloseTo(500, 9);
    expect(l.to.y).toBeCloseTo(100, 9);
    const d = Math.hypot(bearingLine(v, 73).to.x - 500, bearingLine(v, 73).to.y - 500);
    expect(d).toBeCloseTo(400, 6);
  });

  it('EBL 真/相對方位換算與讀數', () => {
    expect(eblBearings({ bearing: 45, ref: 'T' }, 30)).toEqual({ trueDeg: 45, relDeg: 15 });
    expect(eblBearings({ bearing: 350, ref: 'R' }, 30)).toEqual({ trueDeg: 20, relDeg: 350 });
    expect(eblReadout({ bearing: 45, ref: 'T' }, 30)).toBe('045.0° T');
    expect(eblReadout({ bearing: 350, ref: 'R' }, 30)).toBe('350.0° R');
    expect(formatBearing(359.96)).toBe('000.0°');
    expect(formatBearing(5.04)).toBe('005.0°');
    expect(formatBearing(-0.5)).toBe('359.5°');
    expect(vrmReadout({ rangeNm: 1.5 })).toBe('1.50 NM');
    expect(vrmReadout({ rangeNm: 0.25 })).toBe('0.250 NM');
    expect(formatRangeNm(12.34)).toBe('12.3');
  });

  it('點選設定 EBL/VRM:艏向上畫面正上方 = 相對 000 = 真方位 = 航向', () => {
    const v = view(orientationRotation('head-up', 80, 0));
    const r = eblVrmFromScreen(v, 500, 300, 'R', 80);
    expect(r.bearing).toBeCloseTo(0, 9);
    expect(r.rangeNm).toBeCloseTo(2, 9);
    const t = eblVrmFromScreen(v, 500, 300, 'T', 80);
    expect(t.bearing).toBeCloseTo(80, 9);
    // 北向上右方 = 真 090 = 相對 090 − 航向
    const n = eblVrmFromScreen(view(0), 600, 500, 'R', 80);
    expect(n.bearing).toBeCloseTo(10, 9);
  });

  it('平行索引線與方位平行且垂距正確(正 = 右側)', () => {
    for (const rot of [0, 45]) {
      const v = view(rot);
      const seg = parallelIndexLine(v, 30, 1)!;
      expect(seg).not.toBeNull();
      // 端點在圓上
      expect(Math.hypot(seg.from.x - 500, seg.from.y - 500)).toBeCloseTo(400, 6);
      expect(Math.hypot(seg.to.x - 500, seg.to.y - 500)).toBeCloseTo(400, 6);
      // 方向 = 030 真方位
      const dir = screenToPolar({ ...v, ownX: seg.from.x, ownY: seg.from.y }, seg.to.x, seg.to.y);
      expect(dir.bearing).toBeCloseTo(30, 6);
      // 自船到線的有號距離 = −1 nm(自船在線的左側)
      const own = { x: v.ownX, y: v.ownY };
      expect(signedDistanceToLine(seg.from, seg.to, own) / 100).toBeCloseTo(-1, 6);
    }
    expect(parallelIndexLine(view(0), 0, 5)).toBeNull();
  });
});

describe('保護區、真運動', () => {
  it('保護區相對方位換為真方位;全周判定;扇區跨 000', () => {
    expect(guardZoneTrueSector({ from: 300, to: 60, relative: true }, 30)).toEqual({ from: 330, to: 90, full: false });
    expect(guardZoneTrueSector({ from: 0, to: 360, relative: true }, 30).full).toBe(true);
    expect(guardZoneTrueSector({ from: 10, to: 20, relative: false }, 30)).toEqual({ from: 10, to: 20, full: false });
    expect(bearingInSector(350, 330, 90)).toBe(true);
    expect(bearingInSector(100, 330, 90)).toBe(false);
    expect(bearingInSector(45, 10, 90)).toBe(true);
  });

  it('真運動重設:超出 0.7 R 時把自船放到航向反側 0.6 R', () => {
    expect(trueMotionReset(0.3, -0.2, 0)).toBeNull();
    const r = trueMotionReset(0, -0.75, 0)!;
    expect(r.x).toBeCloseTo(0, 9);
    expect(r.y).toBeCloseTo(0.6, 9);
    const e = trueMotionReset(0.8, 0, 90)!;
    expect(e.x).toBeCloseTo(-0.6, 9);
    expect(e.y).toBeCloseTo(0, 9);
  });
});
